import {
  applyFindHighlights,
  clearFindHighlights,
  findCaseInsensitiveMatchOffsets,
  type FindNavigationDirection,
  type FindProvider,
  type FindProviderStatus,
  type FindProviderView,
} from "./findBar";
import type { DocumentWindowModel } from "./documentWindow";
import {
  renderWindowTargetThenAct,
  type WindowTargetDescriptor,
  type WindowTargetController,
  type WindowTargetOperation,
} from "./windowTargetResolver";

export type FindMatchDescriptor = {
  matchId: string;
  blockIndex: number;
  startBlockIndex?: number;
  endBlockIndex?: number;
  blockLocalOffset: number;
  length: number;
  normalizedText: string;
  ordinal: number;
};

export type FindQueryMessage = {
  type: "find-query";
  requestId: number;
  query: string;
  renderId?: number | null;
};

export type FindResultsMessage = {
  type: "find-results";
  requestId: number;
  query: string;
  renderId?: number | null;
  totalCount: number;
  matches: FindMatchDescriptor[];
  stale?: boolean;
};

export type VirtualizedFindContext = {
  beginNavigationOperation: () => WindowTargetOperation | null;
  completeNavigationOperation: (operation: WindowTargetOperation) => void;
  virtualizationEnabled: boolean;
  model: DocumentWindowModel | null;
  controller: WindowTargetController | null;
  main: HTMLElement | null;
  root: Element & { scrollTop: number; scrollHeight: number; clientHeight: number };
  ownerWindow: Window & typeof globalThis;
  renderId: number | null;
};

export type VirtualizedFindProvider = FindProvider & {
  handleFindResults: (message: FindResultsMessage) => void;
  refreshVisibleHighlights: () => void;
};

export type VirtualizedFindProviderDeps = {
  postHostMessage: (message: FindQueryMessage) => void;
  readContext: () => VirtualizedFindContext;
};

const FIND_VISIBLE_SKIP_SELECTOR = [
  "aside",
  ".mm-minimap",
  ".mm-minimap-viewport",
  ".mm-width-handle",
  ".mm-drop-overlay",
  ".katex-mathml",
  "pre.mm-mermaid.is-rendered",
  ".mm-find-bar",
].join(",");

export function createVirtualizedFindProvider(deps: VirtualizedFindProviderDeps): VirtualizedFindProvider {
  let view: FindProviderView | null = null;
  let requestSequence = 0;
  let latestRequestId = 0;
  let currentQuery = "";
  let matches: FindMatchDescriptor[] = [];
  let totalCount = 0;
  let currentIndex = -1;
  let navigationSequence = 0;

  const updateStatus = (): void => {
    const status: FindProviderStatus = {
      currentIndex,
      query: currentQuery,
      totalCount,
    };
    view?.updateStatus(status);
  };

  const resetResults = (query = ""): void => {
    currentQuery = query;
    matches = [];
    totalCount = 0;
    currentIndex = -1;
    clearFindHighlights();
    updateStatus();
  };

  const paintVisibleHighlights = (): Range | null => {
    const allRanges: Range[] = [];
    let currentRange: Range | null = null;
    const currentMatch = currentIndex >= 0 ? matches[currentIndex] : undefined;
    for (const match of matches) {
      const range = resolveLiveRangeForMatch(match);
      if (range === null) {
        continue;
      }

      allRanges.push(range);
      if (currentMatch !== undefined && match.matchId === currentMatch.matchId) {
        currentRange = range;
      }
    }

    applyFindHighlights(allRanges, currentRange ?? undefined);
    return currentRange;
  };

  const search = (query: string): void => {
    currentQuery = query;
    navigationSequence++;
    currentIndex = -1;
    totalCount = 0;
    matches = [];
    clearFindHighlights();
    updateStatus();
    if (query.length === 0) {
      return;
    }

    const context = deps.readContext();
    const request: FindQueryMessage = {
      query,
      requestId: ++requestSequence,
      type: "find-query",
    };
    if (context.renderId !== null) {
      request.renderId = context.renderId;
    }
    latestRequestId = request.requestId;
    deps.postHostMessage(request);
  };

  const handleFindResults = (message: FindResultsMessage): void => {
    if (
      message.stale === true
      || message.requestId !== latestRequestId
      || message.query !== currentQuery
    ) {
      return;
    }

    const context = deps.readContext();
    if (
      context.renderId !== null
      && message.renderId !== undefined
      && message.renderId !== null
      && message.renderId !== context.renderId
    ) {
      return;
    }

    matches = message.matches
      .filter(isUsableDescriptor)
      .slice()
      .sort((left, right) => left.ordinal - right.ordinal);
    totalCount = Math.max(0, Math.floor(message.totalCount));
    currentIndex = selectInitialMatchIndex(matches, context);
    paintVisibleHighlights();
    if (currentIndex >= 0) {
      const sequence = ++navigationSequence;
      void renderMatchThenAct(matches[currentIndex]!, sequence);
    }
    updateStatus();
  };

  const navigate = (direction: FindNavigationDirection): void => {
    if (matches.length === 0) {
      paintVisibleHighlights();
      updateStatus();
      return;
    }

    if (currentIndex < 0) {
      currentIndex = direction === "next" ? 0 : matches.length - 1;
    } else {
      currentIndex = (currentIndex + (direction === "next" ? 1 : -1) + matches.length) % matches.length;
    }

    const match = matches[currentIndex]!;
    const sequence = ++navigationSequence;
    void renderMatchThenAct(match, sequence);
    updateStatus();
  };

  const renderMatchThenAct = async (match: FindMatchDescriptor, sequence: number): Promise<void> => {
    const context = deps.readContext();
    const operation = context.virtualizationEnabled ? context.beginNavigationOperation() : null;
    if (
      !context.virtualizationEnabled
      || operation === null
    ) {
      if (sequence !== navigationSequence) {
        return;
      }
      const currentRange = paintVisibleHighlights();
      updateStatus();
      return;
    }

    const descriptor: WindowTargetDescriptor = {
      blockIndex: match.blockIndex,
      kind: "find-match",
      matchId: match.matchId,
    };
    if (match.startBlockIndex !== undefined) {
      descriptor.startBlockIndex = match.startBlockIndex;
    }
    if (match.endBlockIndex !== undefined) {
      descriptor.endBlockIndex = match.endBlockIndex;
    }

    const pendingRender = renderWindowTargetThenAct({
      action: ({ element, targetElement }) => {
        if (sequence !== navigationSequence || !operation.isCurrent()) {
          return;
        }
        const currentRange = paintVisibleHighlights();
        requestRangeLanding(context, operation, currentRange, targetElement ?? element);
      },
      actionKind: "navigate",
      controller: context.controller,
      descriptor,
      legacyAction: () => {
        if (sequence !== navigationSequence || !operation.isCurrent()) {
          return;
        }
        const currentRange = paintVisibleHighlights();
        operation.scheduleFrameTransaction(() => {
          if (sequence === navigationSequence && operation.isCurrent()) {
            requestRangeLanding(context, operation, currentRange, findLiveBlockElement(match.blockIndex));
          }
        });
      },
      main: context.main ?? document.body,
      model: context.model,
      operation,
      ownerWindow: context.ownerWindow,
      root: context.root,
      virtualizationEnabled: context.virtualizationEnabled,
    });
    await pendingRender;
    if (!operation.isCurrent()) {
      return;
    }
    if (sequence === navigationSequence) {
      updateStatus();
    }
    context.completeNavigationOperation(operation);
  };

  return {
    close: () => {
      latestRequestId = ++requestSequence;
      navigationSequence++;
      resetResults("");
    },
    handleFindResults,
    navigate,
    refreshVisibleHighlights: () => {
      paintVisibleHighlights();
    },
    search,
    setView: nextView => {
      view = nextView;
      updateStatus();
    },
  };
}

function isUsableDescriptor(match: FindMatchDescriptor): boolean {
  return typeof match.matchId === "string"
    && Number.isFinite(match.blockIndex)
    && Number.isFinite(match.blockLocalOffset)
    && match.blockLocalOffset >= 0
    && Number.isFinite(match.length)
    && match.length > 0
    && typeof match.normalizedText === "string"
    && Number.isFinite(match.ordinal)
    && match.ordinal > 0;
}

function resolveLiveRangeForMatch(match: FindMatchDescriptor): Range | null {
  const block = findLiveBlockElement(match.blockIndex);
  if (block === null) {
    return null;
  }

  return rangeFromBlockLocalOffset(block, match.blockLocalOffset, match.length)
    ?? rangeFromNormalizedText(block, match.normalizedText);
}

function findLiveBlockElement(blockIndex: number): HTMLElement | null {
  return document.querySelector<HTMLElement>(`body > main.mm-document [data-mm-block-index="${blockIndex}"]`);
}

function selectInitialMatchIndex(
  matches: readonly FindMatchDescriptor[],
  context: VirtualizedFindContext
): number {
  if (matches.length === 0) {
    return -1;
  }

  const model = context.model;
  if (model === null) {
    return 0;
  }

  const readingTop = Math.max(0, context.root.scrollTop);
  for (let index = 0; index < matches.length; index++) {
    const match = matches[index]!;
    const entry = model.getEntryContainingBlockIndex(match.startBlockIndex ?? match.blockIndex)
      ?? model.getEntryByBlockIndex(match.blockIndex);
    if (entry === undefined) {
      continue;
    }

    const sectionTop = model.sectionTop(entry.sectionIndex);
    const sectionBottom = sectionTop + model.sectionEffectiveHeight(entry.sectionIndex);
    if (sectionBottom >= readingTop) {
      return index;
    }
  }

  return 0;
}

function rangeFromBlockLocalOffset(block: HTMLElement, offset: number, length: number): Range | null {
  const endOffset = offset + length;
  if (!Number.isFinite(offset) || !Number.isFinite(length) || offset < 0 || length <= 0) {
    return null;
  }

  let cursor = 0;
  let startNode: Text | null = null;
  let startInNode = 0;
  let endNode: Text | null = null;
  let endInNode = 0;
  for (const node of visibleTextNodes(block)) {
    const textLength = node.nodeValue?.length ?? 0;
    const nextCursor = cursor + textLength;
    if (startNode === null && offset >= cursor && offset <= nextCursor) {
      startNode = node;
      startInNode = offset - cursor;
    }
    if (startNode !== null && endOffset >= cursor && endOffset <= nextCursor) {
      endNode = node;
      endInNode = endOffset - cursor;
      break;
    }
    cursor = nextCursor;
  }

  if (startNode === null || endNode === null) {
    return null;
  }

  const range = document.createRange();
  range.setStart(startNode, startInNode);
  range.setEnd(endNode, endInNode);
  return range.toString().length === length ? range : null;
}

function rangeFromNormalizedText(block: HTMLElement, normalizedText: string): Range | null {
  if (normalizedText.length === 0) {
    return null;
  }

  for (const node of visibleTextNodes(block)) {
    for (const [start, end] of findCaseInsensitiveMatchOffsets(node.nodeValue ?? "", normalizedText)) {
      const range = document.createRange();
      range.setStart(node, start);
      range.setEnd(node, end);
      return range;
    }
  }

  return null;
}

function visibleTextNodes(root: HTMLElement): Text[] {
  const out: Text[] = [];
  const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, {
    acceptNode(node: Node): number {
      const parent = node.parentElement;
      if (parent === null || parent.closest(FIND_VISIBLE_SKIP_SELECTOR) !== null) {
        return NodeFilter.FILTER_REJECT;
      }
      return NodeFilter.FILTER_ACCEPT;
    },
  });

  for (let current = walker.nextNode(); current !== null; current = walker.nextNode()) {
    if (current.nodeType === Node.TEXT_NODE) {
      out.push(current as Text);
    }
  }
  return out;
}

function requestRangeLanding(
  context: VirtualizedFindContext,
  operation: WindowTargetOperation,
  range: Range | null,
  fallback: HTMLElement | null
): void {
  const host = range?.startContainer.parentElement?.closest<HTMLElement>("[data-mm-block-index]") ?? fallback;
  if (host === null || !operation.isCurrent()) {
    return;
  }
  const rect = host.getBoundingClientRect();
  const target = context.root.scrollTop
    + rect.top
    - Math.max(0, (context.root.clientHeight - Math.max(0, rect.height)) / 2);
  operation.requestScrollTop(Math.max(0, target), "find-navigation");
}

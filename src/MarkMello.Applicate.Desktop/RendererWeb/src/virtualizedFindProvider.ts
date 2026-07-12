import {
  applyFindHighlights,
  clearFindHighlights,
  type FindNavigationDirection,
  type FindProvider,
  type FindProviderStatus,
  type FindProviderView,
} from "./findBar";
import type { DocumentWindowModel } from "./documentWindow";
import { walkVisibleTextNodes } from "./findVisibleText";
import { RENDERED_FIND_TEXT_DOMAIN } from "./renderedFindProjection";
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
  textDomain: "rendered-dom-v1";
};

export type FindResultsMessage = {
  type: "find-results";
  requestId: number;
  query: string;
  renderId?: number | null;
  totalCount: number;
  truncated?: boolean;
  matches: FindMatchDescriptor[];
  status: "pending" | "ready" | "unavailable";
  stale?: boolean;
  textDomain: "rendered-dom-v1";
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

const FIND_RANGE_REAIM_FRAME_LIMIT = 3;
const FIND_SCROLL_EPSILON = 0.5;

export function createVirtualizedFindProvider(deps: VirtualizedFindProviderDeps): VirtualizedFindProvider {
  let view: FindProviderView | null = null;
  let requestSequence = 0;
  let latestRequestId = 0;
  let currentQuery = "";
  let matches: FindMatchDescriptor[] = [];
  let totalCount = 0;
  let shownCount = 0;
  let skippedCount = 0;
  let truncated = false;
  let currentIndex = -1;
  let navigationSequence = 0;

  const updateStatus = (): void => {
    const status: FindProviderStatus = {
      currentIndex,
      query: currentQuery,
      shownCount,
      skippedCount,
      totalCount,
      truncated,
    };
    view?.updateStatus(status);
  };

  const resetResults = (query = ""): void => {
    currentQuery = query;
    matches = [];
    totalCount = 0;
    shownCount = 0;
    skippedCount = 0;
    truncated = false;
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
    shownCount = 0;
    skippedCount = 0;
    truncated = false;
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
      textDomain: RENDERED_FIND_TEXT_DOMAIN,
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
      || message.textDomain !== RENDERED_FIND_TEXT_DOMAIN
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

    if (message.status !== "ready") {
      resetResults(currentQuery);
      return;
    }

    const usableMatches = message.matches
      .filter(isUsableDescriptor)
      .filter(hasUsableRenderedOffsetWhenLive)
      .slice()
      .sort((left, right) => left.ordinal - right.ordinal);
    matches = usableMatches;
    totalCount = Math.max(0, Math.floor(message.totalCount));
    shownCount = matches.length;
    skippedCount = Math.max(0, message.matches.length - shownCount);
    truncated = message.truncated === true;
    currentIndex = matches.length === 0 ? -1 : 0;
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
        paintVisibleHighlights();
        requestElementLanding(context, operation, element ?? targetElement);
        return scheduleRangeReaim(context, operation, () => {
          if (sequence !== navigationSequence || !operation.isCurrent()) {
            return null;
          }
          return paintVisibleHighlights();
        });
      },
      actionKind: "navigate",
      controller: context.controller,
      descriptor,
      legacyAction: () => {
        if (sequence !== navigationSequence || !operation.isCurrent()) {
          return;
        }
        return new Promise<void>(resolve => {
          const scheduled = operation.scheduleFrameTransaction(() => {
            if (sequence !== navigationSequence || !operation.isCurrent()) {
              resolve();
              return;
            }
            paintVisibleHighlights();
            requestElementLanding(
              context,
              operation,
              findLiveTopLevelBlockElement(match.blockIndex) ?? findLiveBlockElement(match.blockIndex)
            );
            void scheduleRangeReaim(context, operation, () => {
              if (sequence !== navigationSequence || !operation.isCurrent()) {
                return null;
              }
              return paintVisibleHighlights();
            }).then(resolve);
          });
          if (!scheduled) {
            resolve();
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

function hasUsableRenderedOffsetWhenLive(match: FindMatchDescriptor): boolean {
  const block = findLiveBlockElement(match.blockIndex);
  return block === null
    || rangeFromBlockLocalOffset(block, match.blockLocalOffset, match.length) !== null;
}

function resolveLiveRangeForMatch(match: FindMatchDescriptor): Range | null {
  const block = findLiveBlockElement(match.blockIndex);
  if (block === null) {
    return null;
  }

  return rangeFromBlockLocalOffset(block, match.blockLocalOffset, match.length);
}

function findLiveBlockElement(blockIndex: number): HTMLElement | null {
  return document.querySelector<HTMLElement>(`body > main.mm-document [data-mm-block-index="${blockIndex}"]`);
}

function findLiveTopLevelBlockElement(blockIndex: number): HTMLElement | null {
  return findLiveBlockElement(blockIndex)?.closest<HTMLElement>("main.mm-document > *") ?? null;
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

function visibleTextNodes(root: HTMLElement): Text[] {
  return walkVisibleTextNodes(root);
}

function requestRangeLanding(
  context: VirtualizedFindContext,
  operation: WindowTargetOperation,
  range: Range | null
): boolean {
  if (range === null || !operation.isCurrent()) {
    return false;
  }
  const rect = range.getBoundingClientRect();
  if (rect.height <= 0 && rect.width <= 0) {
    return false;
  }
  return requestLandingForRect(context, operation, rect);
}

function requestElementLanding(
  context: VirtualizedFindContext,
  operation: WindowTargetOperation,
  element: HTMLElement | null
): boolean {
  if (element === null || !operation.isCurrent()) {
    return false;
  }
  return requestLandingForRect(context, operation, element.getBoundingClientRect());
}

function requestLandingForRect(
  context: VirtualizedFindContext,
  operation: WindowTargetOperation,
  rect: DOMRect
): boolean {
  const target = context.root.scrollTop
    + rect.top
    - Math.max(0, (context.root.clientHeight - Math.max(0, rect.height)) / 2);
  const scrollTop = Math.max(0, target);
  if (Math.abs(scrollTop - context.root.scrollTop) <= FIND_SCROLL_EPSILON || !operation.isCurrent()) {
    return false;
  }
  operation.requestScrollTop(scrollTop, "find-navigation");
  return true;
}

function scheduleRangeReaim(
  context: VirtualizedFindContext,
  operation: WindowTargetOperation,
  readRange: () => Range | null
): Promise<void> {
  return new Promise(resolve => {
    let attempts = 0;
    const scheduleNext = (): void => {
      if (!operation.isCurrent() || attempts >= FIND_RANGE_REAIM_FRAME_LIMIT) {
        resolve();
        return;
      }
      const scheduled = operation.scheduleFrameTransaction(() => {
        if (!operation.isCurrent()) {
          resolve();
          return;
        }
        attempts++;
        const range = readRange();
        const requested = requestRangeLanding(context, operation, range);
        if ((requested || range === null) && attempts < FIND_RANGE_REAIM_FRAME_LIMIT) {
          scheduleNext();
          return;
        }
        resolve();
      });
      if (!scheduled) {
        resolve();
      }
    };
    scheduleNext();
  });
}

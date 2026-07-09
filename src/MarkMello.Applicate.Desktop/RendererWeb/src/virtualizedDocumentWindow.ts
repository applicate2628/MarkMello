import {
  DEFAULT_RENDER_AHEAD,
  collectLiveDocumentSectionElements,
  effectiveHeight,
  readLiveBlockOffsetMeasuredHeights,
  type DocumentWindowModel,
  type MeasuredHeightUpdate,
  type MeasuredHeightUpdateResult,
  type RenderAheadConfig,
  type SectionModelEntry,
  type WindowRange,
} from "./documentWindow";

const TOP_SPACER = "top";
const BOTTOM_SPACER = "bottom";
const SPACER_CLASS = "mm-virtual-spacer";

export type VirtualizedDocumentWindowDeps = {
  ownerWindow: Window;
  main: HTMLElement;
  root: Element & { scrollTop: number; scrollHeight: number; clientHeight: number };
  model: DocumentWindowModel;
  renderAhead?: RenderAheadConfig;
  prepareInsertedContent?: (root: ParentNode) => void;
  readMeasuredHeights?: (blocks: readonly HTMLElement[]) => MeasuredHeightUpdate[];
};

export type VirtualizedDocumentWindowController = {
  updateWindowForScroll: (options?: UpdateWindowForScrollOptions) => boolean;
  adoptRenderedHeights: (options?: AdoptRenderedHeightsOptions) => MeasuredHeightUpdateResult;
  getCurrentRange: () => WindowRange | null;
  ensureSectionRendered: (sectionIndex: number, options?: EnsureSectionRenderedOptions) => boolean;
  ensureSectionRangeRendered: (start: number, end: number, options?: EnsureSectionRenderedOptions) => boolean;
  isSectionRendered: (sectionIndex: number) => boolean;
};

export type UpdateWindowForScrollOptions = {
  force?: boolean;
};

export type EnsureSectionRenderedOptions = {
  force?: boolean;
  preserveAnchor?: boolean;
};

export type AdoptRenderedHeightsOptions = {
  preserveSectionIndex?: number;
};

type LiveBlockAnchor = {
  blockIndex: number;
  viewportTop: number;
};

const EMPTY_HEIGHT_UPDATE: MeasuredHeightUpdateResult = {
  maxAbsDelta: 0,
  totalDelta: 0,
  updatedCount: 0,
};

export function createVirtualizedDocumentWindowController(
  deps: VirtualizedDocumentWindowDeps
): VirtualizedDocumentWindowController {
  let currentRange: WindowRange | null = null;
  const renderAhead = deps.renderAhead ?? DEFAULT_RENDER_AHEAD;

  const renderRange = (range: WindowRange): void => {
    const existingByBlockIndex = collectExistingSections(deps.main);
    const nodes: Node[] = [];
    let insertedCount = 0;
    const topSpacer = createSpacer(deps.ownerWindow.document, TOP_SPACER);
    const bottomSpacer = createSpacer(deps.ownerWindow.document, BOTTOM_SPACER);
    const spacers = deps.model.computeSpacerHeights(range);
    topSpacer.style.height = `${Math.round(spacers.topSpacer)}px`;
    bottomSpacer.style.height = `${Math.round(spacers.bottomSpacer)}px`;
    nodes.push(topSpacer);

    for (let index = range.start; index <= range.end; index++) {
      const entry = deps.model.sections[index];
      if (!entry) {
        continue;
      }

      const existing = existingByBlockIndex.get(entry.blockIndex);
      if (existing) {
        nodes.push(existing);
        continue;
      }

      const created = createSectionNode(deps.ownerWindow.document, entry);
      if (created) {
        insertedCount++;
        nodes.push(created);
      }
    }

    nodes.push(bottomSpacer);
    deps.main.replaceChildren(...nodes);
    currentRange = { ...range };
    if (insertedCount > 0) {
      deps.prepareInsertedContent?.(deps.main);
    }
  };

  const computeRange = (): WindowRange =>
    deps.model.computeWindowRange(deps.root.scrollTop, deps.root.clientHeight, renderAhead);

  const isSectionRendered = (sectionIndex: number): boolean =>
    currentRange !== null && sectionIndex >= currentRange.start && sectionIndex <= currentRange.end;

  const ensureRangeRendered = (
    requestedRange: WindowRange,
    options: EnsureSectionRenderedOptions = {}
  ): boolean => {
    const range = normalizeRequestedRange(requestedRange, deps.model.getSectionCount());
    if (range === null) {
      return false;
    }

    if (options.force !== true && currentRange !== null && rangesEqual(currentRange, range)) {
      return false;
    }

    const anchor = options.preserveAnchor === false ? null : deps.model.captureAnchor(deps.root.scrollTop);
    renderRange(range);
    if (anchor !== null) {
      deps.root.scrollTop = deps.model.scrollTopForAnchor(anchor);
    }
    return true;
  };

  return {
    adoptRenderedHeights: (options = {}) => {
      const preserveSectionIndex = normalizeSectionIndex(options.preserveSectionIndex, deps.model.getSectionCount());
      const anchor = preserveSectionIndex === null ? deps.model.captureAnchor(deps.root.scrollTop) : null;
      const blocks = collectLiveDocumentSectionElements(deps.main);
      const liveAnchor = preserveSectionIndex === null ? captureFirstVisibleLiveBlockAnchor(blocks) : null;
      const updates = deps.readMeasuredHeights
        ? deps.readMeasuredHeights(blocks)
        : readLiveBlockOffsetMeasuredHeights(blocks);
      const result = deps.model.updateMeasuredHeightsByBlockIndex(updates);
      if (result.updatedCount === 0) {
        return EMPTY_HEIGHT_UPDATE;
      }

      if (preserveSectionIndex !== null) {
        deps.root.scrollTop = deps.model.sectionTop(preserveSectionIndex);
        renderRange(computeRange());
        deps.root.scrollTop = deps.model.sectionTop(preserveSectionIndex);
        return result;
      }

      restoreLiveBlockAnchor(deps.model, deps.root, liveAnchor)
        || (deps.root.scrollTop = deps.model.scrollTopForAnchor(anchor!));
      renderRange(computeRange());
      restoreLiveBlockAnchor(deps.model, deps.root, liveAnchor)
        || (deps.root.scrollTop = deps.model.scrollTopForAnchor(anchor!));
      return result;
    },
    ensureSectionRangeRendered: (start, end, options = {}) =>
      ensureRangeRendered({ end, start }, options),
    ensureSectionRendered: (sectionIndex, options = {}) =>
      ensureRangeRendered({ end: sectionIndex, start: sectionIndex }, options),
    getCurrentRange: () => currentRange === null ? null : { ...currentRange },
    isSectionRendered,
    updateWindowForScroll: (options = {}) => {
      const nextRange = computeRange();
      if (options.force !== true && currentRange !== null && rangesEqual(currentRange, nextRange)) {
        return false;
      }

      const anchor = deps.model.captureAnchor(deps.root.scrollTop);
      renderRange(nextRange);
      deps.root.scrollTop = deps.model.scrollTopForAnchor(anchor);
      return true;
    },
  };
}

export function createFullDocumentFragmentFromWindowModel(
  ownerDocument: Document,
  model: DocumentWindowModel
): DocumentFragment {
  const fragment = ownerDocument.createDocumentFragment();
  for (const entry of model.sections) {
    const created = createSectionNode(ownerDocument, entry, effectiveHeight(entry));
    if (created) {
      fragment.append(created);
    }
  }
  return fragment;
}

function collectExistingSections(main: HTMLElement): Map<number, HTMLElement> {
  const result = new Map<number, HTMLElement>();
  for (const element of collectLiveDocumentSectionElements(main)) {
    const raw = element.dataset["mmBlockIndex"];
    const blockIndex = raw === undefined ? Number.NaN : Number.parseInt(raw, 10);
    if (Number.isFinite(blockIndex)) {
      result.set(blockIndex, element);
    }
  }
  return result;
}

function captureFirstVisibleLiveBlockAnchor(blocks: readonly HTMLElement[]): LiveBlockAnchor | null {
  for (const block of blocks) {
    const blockIndex = readBlockIndex(block);
    if (blockIndex === null) {
      continue;
    }

    const rect = block.getBoundingClientRect();
    if (!Number.isFinite(rect.height) || rect.height <= 0 || !Number.isFinite(rect.bottom) || rect.bottom < 0) {
      continue;
    }

    return { blockIndex, viewportTop: rect.top };
  }
  return null;
}

function restoreLiveBlockAnchor(
  model: DocumentWindowModel,
  root: Element & { scrollTop: number },
  anchor: LiveBlockAnchor | null
): boolean {
  if (anchor === null || !Number.isFinite(anchor.viewportTop)) {
    return false;
  }

  const entry = model.getEntryByBlockIndex(anchor.blockIndex);
  if (entry === undefined) {
    return false;
  }

  root.scrollTop = Math.max(0, model.sectionTop(entry.sectionIndex) - anchor.viewportTop);
  return true;
}

function readBlockIndex(element: HTMLElement): number | null {
  const raw = element.dataset["mmBlockIndex"];
  if (raw === undefined || raw.trim() === "") {
    return null;
  }

  const parsed = Number.parseInt(raw, 10);
  return Number.isFinite(parsed) ? parsed : null;
}

function createSpacer(ownerDocument: Document, kind: typeof TOP_SPACER | typeof BOTTOM_SPACER): HTMLElement {
  const spacer = ownerDocument.createElement("div");
  spacer.className = `${SPACER_CLASS} ${SPACER_CLASS}-${kind}`;
  spacer.dataset["mmVirtualSpacer"] = kind;
  spacer.setAttribute("aria-hidden", "true");
  spacer.style.display = "block";
  spacer.style.flex = "0 0 auto";
  spacer.style.pointerEvents = "none";
  return spacer;
}

function createSectionNode(
  ownerDocument: Document,
  entry: SectionModelEntry,
  settledHeightPx?: number
): HTMLElement | null {
  if (!entry.html) {
    return null;
  }

  const template = ownerDocument.createElement("template");
  template.innerHTML = entry.html;
  const firstElement = Array.from(template.content.childNodes)
    .find((node): node is HTMLElement => node instanceof HTMLElement);
  if (firstElement && settledHeightPx !== undefined && Number.isFinite(settledHeightPx) && settledHeightPx > 0) {
    firstElement.style.containIntrinsicSize = `auto ${settledHeightPx}px`;
  }
  return firstElement ?? null;
}

function rangesEqual(left: WindowRange, right: WindowRange): boolean {
  return left.start === right.start && left.end === right.end;
}

function normalizeRequestedRange(range: WindowRange, sectionCount: number): WindowRange | null {
  if (sectionCount <= 0 || !Number.isFinite(range.start) || !Number.isFinite(range.end)) {
    return null;
  }

  const rawStart = Math.floor(Math.min(range.start, range.end));
  const rawEnd = Math.floor(Math.max(range.start, range.end));
  const start = Math.max(0, Math.min(sectionCount - 1, rawStart));
  const end = Math.max(start, Math.min(sectionCount - 1, rawEnd));
  return { end, start };
}

function normalizeSectionIndex(sectionIndex: number | undefined, sectionCount: number): number | null {
  if (sectionIndex === undefined || sectionCount <= 0 || !Number.isFinite(sectionIndex)) {
    return null;
  }

  return Math.max(0, Math.min(sectionCount - 1, Math.floor(sectionIndex)));
}

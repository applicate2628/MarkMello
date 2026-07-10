import {
  DEFAULT_RENDER_AHEAD,
  collectLiveDocumentSectionElements,
  elementDocumentTop,
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
  realization?: VirtualizedRealizationOptions;
};

export type VirtualizedDocumentWindowController = {
  updateWindowForScroll: (options?: UpdateWindowForScrollOptions) => boolean;
  adoptRenderedHeights: (options?: AdoptRenderedHeightsOptions) => MeasuredHeightUpdateResult;
  dispose: () => void;
  getCurrentRange: () => WindowRange | null;
  ensureSectionRendered: (sectionIndex: number, options?: EnsureSectionRenderedOptions) => boolean;
  ensureSectionRangeRendered: (start: number, end: number, options?: EnsureSectionRenderedOptions) => boolean;
  isSectionRendered: (sectionIndex: number) => boolean;
};

export type VirtualizedRealizationOptions = {
  enabled: true;
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
  reanchor?: boolean;
};

type LiveBlockAnchor = {
  blockIndex: number;
  viewportTop: number;
};

type ContentVisibilityAutoStateChangeLike = Event & {
  skipped?: boolean;
};

type RealizationWatchState =
  | "placeholder-not-intersecting"
  | "intersecting-await-event"
  | "event-observed-settling"
  | "real-ready"
  | "event-equal-fallback-noop"
  | "realized-then-skipped"
  | "expired-nonconvergent";

type RealizationWatch = {
  element: HTMLElement;
  blockIndex: number;
  mountGeneration: number;
  state: RealizationWatchState;
  frameBudget: number;
  frameRequested: boolean;
  skipped: boolean;
  stableFrameCount: number;
  lastOffsetHeight: number | null;
  lastOccupiedHeight: number | null;
  readyMeasuredHeight: number | null;
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
  const realizationTracker = deps.realization?.enabled === true
    ? createRealizationTracker(deps)
    : null;

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
    const mountedBlocks = collectLiveDocumentSectionElements(deps.main);
    reconcileMountedNonContentMetadata(deps.model, mountedBlocks);
    realizationTracker?.syncMountedSections(mountedBlocks);
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
      const reanchor = options.reanchor !== false;
      const anchor = preserveSectionIndex === null ? deps.model.captureAnchor(deps.root.scrollTop) : null;
      const blocks = collectLiveDocumentSectionElements(deps.main);
      const liveAnchor = preserveSectionIndex === null ? captureFirstVisibleLiveBlockAnchor(blocks) : null;
      const updates = deps.readMeasuredHeights
        ? deps.readMeasuredHeights(blocks)
        : readLiveBlockOffsetMeasuredHeights(blocks);
      const result = deps.model.updateMeasuredHeightsByBlockIndex(
        realizationTracker?.filterRealizedUpdates(blocks, updates) ?? updates
      );
      if (result.updatedCount === 0) {
        return EMPTY_HEIGHT_UPDATE;
      }

      if (preserveSectionIndex !== null) {
        if (reanchor) {
          deps.root.scrollTop = deps.model.sectionTop(preserveSectionIndex);
        }
        renderRange(computeRange());
        if (reanchor) {
          deps.root.scrollTop = deps.model.sectionTop(preserveSectionIndex);
        }
        return result;
      }

      if (reanchor) {
        restoreLiveBlockAnchor(deps.model, deps.root, liveAnchor)
          || (deps.root.scrollTop = deps.model.scrollTopForAnchor(anchor!));
      }
      renderRange(computeRange());
      if (reanchor) {
        restoreLiveBlockAnchor(deps.model, deps.root, liveAnchor)
          || (deps.root.scrollTop = deps.model.scrollTopForAnchor(anchor!));
      }
      return result;
    },
    dispose: () => {
      realizationTracker?.dispose();
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
    const created = createSectionNode(ownerDocument, entry);
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
  entry: SectionModelEntry
): HTMLElement | null {
  if (!entry.html) {
    return null;
  }

  const template = ownerDocument.createElement("template");
  template.innerHTML = entry.html;
  const firstElement = Array.from(template.content.childNodes)
    .find((node): node is HTMLElement => node instanceof HTMLElement);
  if (firstElement) {
    writeIntrinsicSizeStamp(firstElement, entry);
  }
  return firstElement ?? null;
}

function writeIntrinsicSizeStamp(element: HTMLElement, entry: SectionModelEntry): void {
  const stamp = readIntrinsicSizeStamp(entry);
  if (stamp === null) {
    element.style.removeProperty("contain-intrinsic-size");
    return;
  }

  element.style.containIntrinsicSize = `auto ${stamp}px`;
}

function readIntrinsicSizeStamp(entry: SectionModelEntry): number | null {
  const occupiedNonContentHeight = entry.occupiedNonContentHeight;
  if (!Number.isFinite(occupiedNonContentHeight)) {
    return null;
  }

  const stamp = Math.max(0, effectiveHeight(entry) - occupiedNonContentHeight!);
  return Number.isFinite(stamp) ? stamp : null;
}

function reconcileMountedNonContentMetadata(
  model: DocumentWindowModel,
  blocks: readonly HTMLElement[]
): void {
  const updates = readLiveBlockOffsetMeasuredHeights(blocks);
  for (const update of updates) {
    const occupiedNonContentHeight = update.occupiedNonContentHeight;
    if (typeof occupiedNonContentHeight !== "number" || !Number.isFinite(occupiedNonContentHeight)) {
      continue;
    }

    const entry = model.getEntryByBlockIndex(update.blockIndex);
    if (entry === undefined) {
      continue;
    }

    entry.occupiedNonContentHeight = occupiedNonContentHeight;
    const block = blocks.find(candidate => readBlockIndex(candidate) === update.blockIndex);
    if (block !== undefined) {
      writeIntrinsicSizeStamp(block, entry);
    }
  }
}

function createRealizationTracker(deps: VirtualizedDocumentWindowDeps): {
  dispose: () => void;
  filterRealizedUpdates: (
    blocks: readonly HTMLElement[],
    updates: readonly MeasuredHeightUpdate[]
  ) => MeasuredHeightUpdate[];
  syncMountedSections: (blocks: readonly HTMLElement[]) => void;
} {
  const watches = new Map<HTMLElement, RealizationWatch>();
  let mountGeneration = 0;
  let disposed = false;

  const eventOptions: AddEventListenerOptions = { capture: true };

  const handleContentVisibilityStateChange = (event: Event): void => {
    const target = event.target;
    if (!(target instanceof HTMLElement)) {
      return;
    }

    const watch = watches.get(target);
    if (watch === undefined || watch.element !== target || !deps.main.contains(target)) {
      return;
    }

    const stateEvent = event as ContentVisibilityAutoStateChangeLike;
    if (stateEvent.skipped === true) {
      watch.skipped = true;
      watch.readyMeasuredHeight = null;
      watch.stableFrameCount = 0;
      watch.lastOffsetHeight = null;
      watch.lastOccupiedHeight = null;
      watch.state = watch.state === "real-ready" ? "realized-then-skipped" : "placeholder-not-intersecting";
      return;
    }

    watch.skipped = false;
    watch.frameBudget = 120;
    watch.stableFrameCount = 0;
    watch.lastOffsetHeight = null;
    watch.lastOccupiedHeight = null;
    watch.readyMeasuredHeight = null;
    watch.state = "event-observed-settling";
    scheduleSample(watch);
  };

  deps.main.addEventListener("contentvisibilityautostatechange", handleContentVisibilityStateChange, eventOptions);

  const dispose = (): void => {
    if (disposed) {
      return;
    }

    disposed = true;
    watches.clear();
    deps.main.removeEventListener("contentvisibilityautostatechange", handleContentVisibilityStateChange, eventOptions);
  };

  const syncMountedSections = (blocks: readonly HTMLElement[]): void => {
    if (disposed) {
      return;
    }

    mountGeneration++;
    for (const block of blocks) {
      if (!isContentVisibilityAutoOwner(block)) {
        watches.delete(block);
        continue;
      }

      const blockIndex = readBlockIndex(block);
      if (blockIndex === null) {
        continue;
      }

      const existing = watches.get(block);
      if (existing !== undefined) {
        existing.blockIndex = blockIndex;
        existing.mountGeneration = mountGeneration;
        if (existing.state !== "real-ready" && existing.state !== "event-equal-fallback-noop") {
          existing.state = isStrictlyIntersecting(block)
            ? "intersecting-await-event"
            : "placeholder-not-intersecting";
        }
        continue;
      }

      watches.set(block, {
        blockIndex,
        element: block,
        frameBudget: 120,
        frameRequested: false,
        lastOccupiedHeight: null,
        lastOffsetHeight: null,
        mountGeneration,
        readyMeasuredHeight: null,
        skipped: true,
        stableFrameCount: 0,
        state: isStrictlyIntersecting(block)
          ? "intersecting-await-event"
          : "placeholder-not-intersecting",
      });
    }

    for (const [element, watch] of watches) {
      if (watch.mountGeneration !== mountGeneration || !deps.main.contains(element)) {
        watches.delete(element);
      }
    }
  };

  const filterRealizedUpdates = (
    blocks: readonly HTMLElement[],
    updates: readonly MeasuredHeightUpdate[]
  ): MeasuredHeightUpdate[] => {
    const accepted: MeasuredHeightUpdate[] = [];
    const blocksByBlockIndex = mapBlocksByBlockIndex(blocks);
    for (const update of updates) {
      const block = blocksByBlockIndex.get(update.blockIndex);
      if (block === undefined || block === null || readBlockIndex(block) !== update.blockIndex) {
        continue;
      }

      const watch = watches.get(block);
      if (watch === undefined) {
        accepted.push(update);
        continue;
      }

      if (
        watch.element !== block
        || watch.blockIndex !== update.blockIndex
        || watch.mountGeneration !== mountGeneration
        || !deps.main.contains(block)
        || watch.state !== "real-ready"
        || watch.readyMeasuredHeight === null
      ) {
        continue;
      }

      const acceptedUpdate: MeasuredHeightUpdate = {
        ...update,
        measuredHeight: watch.readyMeasuredHeight,
      };
      delete acceptedUpdate.measuredHeightPlaceholder;
      accepted.push(acceptedUpdate);
    }
    return accepted;
  };

  function scheduleSample(watch: RealizationWatch): void {
    if (disposed || watch.frameRequested || watch.state !== "event-observed-settling") {
      return;
    }

    const expectedGeneration = watch.mountGeneration;
    watch.frameRequested = true;
    deps.ownerWindow.requestAnimationFrame(() => {
      watch.frameRequested = false;
      if (
        disposed
        || watch.mountGeneration !== expectedGeneration
        || watches.get(watch.element) !== watch
        || !deps.main.contains(watch.element)
      ) {
        return;
      }

      sampleWatch(watch);
    });
  }

  function sampleWatch(watch: RealizationWatch): void {
    if (watch.skipped || watch.state !== "event-observed-settling") {
      return;
    }

    const sample = readRealizationSample(watch.element);
    if (sample === null) {
      expireOrContinue(watch);
      return;
    }

    const offsetStable = watch.lastOffsetHeight === null
      || Math.abs(sample.offsetHeight - watch.lastOffsetHeight) <= 1;
    const occupiedStable = watch.lastOccupiedHeight === null
      || Math.abs(sample.occupiedHeight - watch.lastOccupiedHeight) <= 1;
    watch.stableFrameCount = offsetStable && occupiedStable ? watch.stableFrameCount + 1 : 1;
    watch.lastOffsetHeight = sample.offsetHeight;
    watch.lastOccupiedHeight = sample.occupiedHeight;

    if (watch.stableFrameCount >= 2) {
      if (Math.abs(sample.offsetHeight - sample.fallbackBorderBoxHeight) <= 1) {
        watch.state = "event-equal-fallback-noop";
        watch.readyMeasuredHeight = null;
        return;
      }

      if (Math.abs(sample.offsetHeight - sample.fallbackBorderBoxHeight) > 1) {
        watch.state = "real-ready";
        watch.readyMeasuredHeight = Math.max(0, sample.occupiedHeight);
        return;
      }
    }

    expireOrContinue(watch);
  }

  function expireOrContinue(watch: RealizationWatch): void {
    watch.frameBudget--;
    if (watch.frameBudget <= 0) {
      watch.state = "expired-nonconvergent";
      watch.readyMeasuredHeight = null;
      return;
    }

    scheduleSample(watch);
  }

  return { dispose, filterRealizedUpdates, syncMountedSections };
}

function mapBlocksByBlockIndex(blocks: readonly HTMLElement[]): Map<number, HTMLElement | null> {
  const blocksByBlockIndex = new Map<number, HTMLElement | null>();
  for (const block of blocks) {
    const blockIndex = readBlockIndex(block);
    if (blockIndex === null) {
      continue;
    }

    blocksByBlockIndex.set(
      blockIndex,
      blocksByBlockIndex.has(blockIndex) ? null : block
    );
  }
  return blocksByBlockIndex;
}

function readRealizationSample(element: HTMLElement): {
  fallbackBorderBoxHeight: number;
  occupiedHeight: number;
  offsetHeight: number;
} | null {
  const offsetHeight = element.offsetHeight;
  const occupiedHeight = readOccupiedHeight(element);
  const fallbackBorderBoxHeight = readFallbackBorderBoxHeight(element);
  if (
    !Number.isFinite(offsetHeight)
    || occupiedHeight === null
    || !Number.isFinite(occupiedHeight)
    || fallbackBorderBoxHeight === null
  ) {
    return null;
  }

  return { fallbackBorderBoxHeight, occupiedHeight, offsetHeight };
}

function readOccupiedHeight(element: HTMLElement): number | null {
  const top = elementDocumentTop(element);
  const nextTop = readNextSiblingTop(element);
  if (
    !Number.isFinite(top)
    || nextTop === null
    || !Number.isFinite(nextTop)
    || nextTop <= top
  ) {
    return null;
  }
  return nextTop - top;
}

function readNextSiblingTop(element: HTMLElement): number | null {
  let sibling = element.nextElementSibling;
  while (sibling instanceof HTMLElement) {
    const top = elementDocumentTop(sibling);
    if (Number.isFinite(top)) {
      return top;
    }
    sibling = sibling.nextElementSibling;
  }
  return null;
}

function readFallbackBorderBoxHeight(element: HTMLElement): number | null {
  const intrinsicSize = readContainIntrinsicBlockSizePx(element);
  const nonContent = readBlockAxisPaddingBorderHeightPx(element);
  if (intrinsicSize === null || nonContent === null) {
    return null;
  }
  return intrinsicSize + nonContent;
}

function isContentVisibilityAutoOwner(element: HTMLElement): boolean {
  const inlineValue = element.style.getPropertyValue("content-visibility");
  if (inlineValue.trim().length > 0) {
    return inlineValue.trim() === "auto";
  }

  return element.ownerDocument.defaultView
    ?.getComputedStyle(element)
    .getPropertyValue("content-visibility")
    .trim() === "auto";
}

function readContainIntrinsicBlockSizePx(element: HTMLElement): number | null {
  const raw = element.style.getPropertyValue("contain-intrinsic-size")
    || element.ownerDocument.defaultView?.getComputedStyle(element).getPropertyValue("contain-intrinsic-size")
    || "";
  const matches = Array.from(raw.matchAll(/(-?\d+(?:\.\d+)?)px/g));
  const lastMatch = matches[matches.length - 1];
  if (!lastMatch) {
    return null;
  }

  const parsed = Number.parseFloat(lastMatch[1]!);
  return Number.isFinite(parsed) && parsed >= 0 ? parsed : null;
}

function readBlockAxisPaddingBorderHeightPx(element: HTMLElement): number | null {
  const styles = element.ownerDocument.defaultView?.getComputedStyle(element);
  if (!styles) {
    return 0;
  }

  let total = 0;
  for (const propertyName of ["padding-top", "padding-bottom", "border-top-width", "border-bottom-width"]) {
    const value = readCssPixelLength(styles.getPropertyValue(propertyName));
    if (value === null) {
      return null;
    }
    total += value;
  }
  return total;
}

function readCssPixelLength(raw: string): number | null {
  const value = raw.trim();
  if (value === "" || value === "0") {
    return 0;
  }
  if (!value.endsWith("px")) {
    return null;
  }

  const parsed = Number.parseFloat(value);
  return Number.isFinite(parsed) ? parsed : null;
}

function isStrictlyIntersecting(element: HTMLElement): boolean {
  const root = element.ownerDocument.scrollingElement ?? element.ownerDocument.documentElement;
  const top = elementDocumentTop(element);
  const height = element.offsetHeight;
  const viewportTop = Number.isFinite(root.scrollTop) ? root.scrollTop : 0;
  const viewportHeight = root.clientHeight || element.ownerDocument.defaultView?.innerHeight || 0;
  if (!Number.isFinite(top) || !Number.isFinite(height) || !Number.isFinite(viewportHeight) || viewportHeight <= 0) {
    return false;
  }

  return top < viewportTop + viewportHeight && top + height > viewportTop;
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

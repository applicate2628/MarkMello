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
import { readReadyMermaidProxy } from "./mermaidRender";

const TOP_SPACER = "top";
const BOTTOM_SPACER = "bottom";
const SPACER_CLASS = "mm-virtual-spacer";
const REALIZATION_FRAME_BUDGET = 120;
const REALIZATION_QUARANTINE_CYCLES = 3;
const REALIZATION_TRACE_IDS = {
  expired: "mm-virt-realization-expired",
  quarantined: "mm-virt-realization-quarantined",
} as const;

export type VirtualizedRealizationTraceEvent = {
  id: typeof REALIZATION_TRACE_IDS[keyof typeof REALIZATION_TRACE_IDS];
  details: Readonly<Record<string, unknown>>;
};

export type VirtualizedDocumentWindowDeps = {
  beginWindowGeometryWork?: (mountGeneration: number) => VirtualizedWindowGeometryWork | null;
  documentEpoch?: number;
  isCurrentDocumentEpoch?: (epoch: number) => boolean;
  ownerWindow: Window;
  main: HTMLElement;
  root: Element & { scrollTop: number; scrollHeight: number; clientHeight: number };
  model: DocumentWindowModel;
  renderAhead?: RenderAheadConfig;
  prepareInsertedContent?: (root: ParentNode, mountGeneration: number) => void;
  onRealizationReady?: (mountGeneration: number) => void;
  onWindowMounted?: (mountGeneration: number) => void;
  readMeasuredHeights?: (blocks: readonly HTMLElement[]) => MeasuredHeightUpdate[];
  realization?: VirtualizedRealizationOptions;
  trace?: (event: VirtualizedRealizationTraceEvent) => void;
};

export type VirtualizedDocumentWindowController = {
  updateWindowForScroll: (options?: UpdateWindowForScrollOptions) => boolean;
  adoptRenderedHeights: (options?: AdoptRenderedHeightsOptions) => MeasuredHeightUpdateResult;
  dispose: () => void;
  getCurrentRange: () => WindowRange | null;
  ensureSectionRendered: (sectionIndex: number, options?: EnsureSectionRenderedOptions) => boolean;
  ensureSectionRangeRendered: (start: number, end: number, options?: EnsureSectionRenderedOptions) => boolean;
  isSectionRendered: (sectionIndex: number) => boolean;
  recensusRealizationWatches: () => boolean;
};

export type VirtualizedWindowGeometryWork = {
  end: () => void;
  mutated: () => void;
};

export type VirtualizedRealizationOptions = {
  enabled: true;
};

export type UpdateWindowForScrollOptions = {
  desiredScrollTop?: number;
  force?: boolean;
  operation?: VirtualizedWindowOperation;
};

export type EnsureSectionRenderedOptions = {
  force?: boolean;
  operation?: VirtualizedWindowOperation;
  preserveAnchor?: boolean;
};

export type AdoptRenderedHeightsOptions = {
  operation?: VirtualizedWindowOperation;
  preserveSectionIndex?: number;
  reanchor?: boolean;
};

export type ReadingAnchor = {
  blockIndex: number;
  intraOffsetPx: number;
};

export type VirtualizedWindowOperation = {
  requestScrollTop: (target: number, writer: string) => void;
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
  | "expired-nonconvergent"
  | "quarantined-nonconvergent";

type RealizationWatch = {
  element: HTMLElement;
  blockIndex: number;
  mountGeneration: number;
  nonconvergentCycles: number;
  state: RealizationWatchState;
  frameBudget: number;
  frameRequested: boolean;
  skipped: boolean;
  stableFrameCount: number;
  lastOffsetHeight: number | null;
  lastOccupiedHeight: number | null;
  readyMeasuredHeight: number | null;
};

type ExistingSectionUnit = {
  source: HTMLElement;
  proxy: HTMLElement | null;
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
  let windowMountGeneration = 0;
  const renderAhead = deps.renderAhead ?? DEFAULT_RENDER_AHEAD;
  const realizationTracker = deps.realization?.enabled === true
    ? createRealizationTracker(deps)
    : null;

  const renderRange = (range: WindowRange): void => {
    const mountGeneration = ++windowMountGeneration;
    const geometryWork = deps.beginWindowGeometryWork?.(mountGeneration) ?? null;
    const existingByBlockIndex = collectExistingSections(deps.main);
    const nodes: Node[] = [];
    let insertedCount = 0;
    let repairedMermaidCount = 0;
    const repairedMermaidBlockIndexes = new Set<number>();
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
        if (appendSectionUnit(nodes, existing.source, existing.proxy, entry)) {
          repairedMermaidCount++;
          repairedMermaidBlockIndexes.add(entry.blockIndex);
        }
        continue;
      }

      const created = createSectionNode(deps.ownerWindow.document, entry);
      if (created) {
        insertedCount++;
        appendSectionUnit(nodes, created, null, entry);
      }
    }

    nodes.push(bottomSpacer);
    try {
      deps.main.replaceChildren(...nodes);
      geometryWork?.mutated();
      currentRange = { ...range };
      const mountedBlocks = collectLiveDocumentSectionElements(deps.main);
      reconcileMountedNonContentMetadata(deps.model, mountedBlocks, repairedMermaidBlockIndexes);
      realizationTracker?.syncMountedSections(mountedBlocks, mountGeneration);
      deps.onWindowMounted?.(mountGeneration);
      if (insertedCount > 0 || repairedMermaidCount > 0) {
        deps.prepareInsertedContent?.(deps.main, mountGeneration);
      }
    } finally {
      geometryWork?.end();
    }
  };

  const computeRange = (scrollTop = deps.root.scrollTop): WindowRange =>
    deps.model.computeWindowRange(scrollTop, deps.root.clientHeight, renderAhead);

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
      options.operation?.requestScrollTop(
        deps.model.scrollTopForAnchor(anchor),
        "target-window-reanchor"
      );
    }
    return true;
  };

  return {
    adoptRenderedHeights: (options = {}) => {
      const preserveSectionIndex = normalizeSectionIndex(options.preserveSectionIndex, deps.model.getSectionCount());
      const reanchor = options.reanchor !== false;
      const anchor = preserveSectionIndex === null ? deps.model.captureAnchor(deps.root.scrollTop) : null;
      const blocks = collectLiveDocumentSectionElements(deps.main);
      const liveAnchor = preserveSectionIndex === null ? captureReadingAnchor(blocks) : null;
      const updates = deps.readMeasuredHeights
        ? deps.readMeasuredHeights(blocks)
        : readLiveBlockOffsetMeasuredHeights(blocks);
      const result = deps.model.updateMeasuredHeightsByBlockIndex(
        realizationTracker?.filterRealizedUpdates(blocks, updates) ?? updates
      );
      if (result.updatedCount === 0) {
        return EMPTY_HEIGHT_UPDATE;
      }
      if (result.maxAbsDelta <= Number.EPSILON && Math.abs(result.totalDelta) <= Number.EPSILON) {
        return result;
      }

      const desiredScrollTop = preserveSectionIndex !== null
        ? deps.model.sectionTop(preserveSectionIndex)
        : scrollTopForReadingAnchor(deps.model, liveAnchor)
          ?? deps.model.scrollTopForAnchor(anchor!);
      renderRange(computeRange(desiredScrollTop));
      if (reanchor) {
        options.operation?.requestScrollTop(desiredScrollTop, "measured-height-adoption");
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
    recensusRealizationWatches: () => realizationTracker?.recensusRealizationWatches() ?? true,
    updateWindowForScroll: (options = {}) => {
      const nextRange = computeRange(options.desiredScrollTop ?? deps.root.scrollTop);
      if (options.force !== true && currentRange !== null && rangesEqual(currentRange, nextRange)) {
        return false;
      }

      const anchor = deps.model.captureAnchor(options.desiredScrollTop ?? deps.root.scrollTop);
      renderRange(nextRange);
      options.operation?.requestScrollTop(
        deps.model.scrollTopForAnchor(anchor),
        "scroll-window-reanchor"
      );
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

function collectExistingSections(main: HTMLElement): Map<number, ExistingSectionUnit> {
  const result = new Map<number, ExistingSectionUnit>();
  for (const element of collectLiveDocumentSectionElements(main)) {
    const raw = element.dataset["mmBlockIndex"];
    const blockIndex = raw === undefined ? Number.NaN : Number.parseInt(raw, 10);
    if (Number.isFinite(blockIndex)) {
      result.set(blockIndex, {
        proxy: readReadyMermaidProxy(element),
        source: element,
      });
    }
  }
  return result;
}

function appendSectionUnit(
  nodes: Node[],
  source: HTMLElement,
  proxy: HTMLElement | null,
  entry: SectionModelEntry
): boolean {
  nodes.push(source);
  if (proxy !== null) {
    source.style.removeProperty("contain-intrinsic-size");
    nodes.push(proxy);
    return false;
  }

  if (!source.matches("pre.mm-mermaid.is-rendered")) {
    return false;
  }

  source.classList.remove("is-rendered");
  writeIntrinsicSizeStamp(source, entry);
  return true;
}

export function captureReadingAnchor(blocks: readonly HTMLElement[]): ReadingAnchor | null {
  for (const block of blocks) {
    const blockIndex = readBlockIndex(block);
    if (blockIndex === null) {
      continue;
    }

    const boxElement = readReadyMermaidProxy(block) ?? block;
    const rect = boxElement.getBoundingClientRect();
    const ownerDocument = boxElement.ownerDocument;
    const viewportHeight = ownerDocument.scrollingElement?.clientHeight
      || ownerDocument.defaultView?.innerHeight
      || 0;
    if (
      !Number.isFinite(rect.top)
      || !Number.isFinite(rect.height)
      || rect.height <= 0
      || !Number.isFinite(rect.bottom)
      || rect.bottom <= 0
      || (Number.isFinite(viewportHeight) && viewportHeight > 0 && rect.top >= viewportHeight)
    ) {
      continue;
    }

    return {
      blockIndex,
      intraOffsetPx: Math.max(0, Math.min(-rect.top, Math.max(0, rect.height - 0.5))),
    };
  }
  return null;
}

export function scrollTopForReadingAnchor(
  model: DocumentWindowModel,
  anchor: ReadingAnchor | null
): number | null {
  if (anchor === null || !Number.isFinite(anchor.intraOffsetPx)) {
    return null;
  }

  const entry = model.getEntryByBlockIndex(anchor.blockIndex);
  if (entry === undefined) {
    return 0;
  }

  return model.scrollTopForAnchor({
    blockIndex: entry.blockIndex,
    intraOffset: anchor.intraOffsetPx,
    sectionIndex: entry.sectionIndex,
  });
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
    if (firstElement.matches("pre.mm-mermaid.is-rendered")) {
      firstElement.classList.remove("is-rendered");
    }
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
  blocks: readonly HTMLElement[],
  excludedBlockIndexes: ReadonlySet<number>
): void {
  const updates = readLiveBlockOffsetMeasuredHeights(blocks);
  for (const update of updates) {
    if (excludedBlockIndexes.has(update.blockIndex)) {
      continue;
    }
    const block = blocks.find(candidate => readBlockIndex(candidate) === update.blockIndex);
    if (update.geometryOwner === "mermaid-proxy") {
      block?.style.removeProperty("contain-intrinsic-size");
      continue;
    }

    const occupiedNonContentHeight = update.occupiedNonContentHeight;
    if (typeof occupiedNonContentHeight !== "number" || !Number.isFinite(occupiedNonContentHeight)) {
      continue;
    }

    const entry = model.getEntryByBlockIndex(update.blockIndex);
    if (entry === undefined) {
      continue;
    }

    entry.occupiedNonContentHeight = occupiedNonContentHeight;
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
  recensusRealizationWatches: () => boolean;
  syncMountedSections: (blocks: readonly HTMLElement[], mountGeneration: number) => void;
} {
  const watches = new Map<HTMLElement, RealizationWatch>();
  let currentMountGeneration = 0;
  let disposed = false;

  const eventOptions: AddEventListenerOptions = { capture: true };
  const documentEpoch = deps.documentEpoch;
  const isCurrentDocument = (): boolean => documentEpoch === undefined
    || deps.isCurrentDocumentEpoch === undefined
    || deps.isCurrentDocumentEpoch(documentEpoch);

  const handleContentVisibilityStateChange = (event: Event): void => {
    if (!isCurrentDocument()) {
      return;
    }
    const target = event.target;
    if (!(target instanceof HTMLElement)) {
      return;
    }

    const watch = watches.get(target);
    if (watch === undefined || watch.element !== target || !deps.main.contains(target)) {
      return;
    }
    if (watch.state === "quarantined-nonconvergent") {
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
    watch.frameBudget = REALIZATION_FRAME_BUDGET;
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

  const syncMountedSections = (
    blocks: readonly HTMLElement[],
    mountGeneration: number
  ): void => {
    if (disposed) {
      return;
    }

    currentMountGeneration = mountGeneration;
    for (const block of blocks) {
      if (readReadyMermaidProxy(block) !== null) {
        watches.delete(block);
        continue;
      }

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
        existing.mountGeneration = currentMountGeneration;
        if (
          existing.state === "placeholder-not-intersecting"
          || existing.state === "realized-then-skipped"
        ) {
          existing.state = isStrictlyIntersecting(block)
            ? "intersecting-await-event"
            : "placeholder-not-intersecting";
        }
        continue;
      }

      watches.set(block, {
        blockIndex,
        element: block,
        frameBudget: REALIZATION_FRAME_BUDGET,
        frameRequested: false,
        lastOccupiedHeight: null,
        lastOffsetHeight: null,
        mountGeneration: currentMountGeneration,
        nonconvergentCycles: 0,
        readyMeasuredHeight: null,
        skipped: true,
        stableFrameCount: 0,
        state: isStrictlyIntersecting(block)
          ? "intersecting-await-event"
          : "placeholder-not-intersecting",
      });
    }

    for (const [element, watch] of watches) {
      if (watch.mountGeneration !== currentMountGeneration || !deps.main.contains(element)) {
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

      if (update.geometryOwner === "mermaid-proxy") {
        const proxy = readReadyMermaidProxy(block);
        if (proxy !== null && deps.main.contains(proxy)) {
          accepted.push(update);
        }
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
        || watch.mountGeneration !== currentMountGeneration
        || !deps.main.contains(block)
        || watch.state !== "real-ready"
        || watch.readyMeasuredHeight === null
      ) {
        continue;
      }

      if (isStrictlyIntersecting(block)) {
        watch.readyMeasuredHeight = Math.max(0, update.measuredHeight);
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
        || !isCurrentDocument()
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
        deps.onRealizationReady?.(watch.mountGeneration);
        return;
      }

      if (Math.abs(sample.offsetHeight - sample.fallbackBorderBoxHeight) > 1) {
        watch.state = "real-ready";
        watch.readyMeasuredHeight = Math.max(0, sample.occupiedHeight);
        deps.onRealizationReady?.(watch.mountGeneration);
        return;
      }
    }

    expireOrContinue(watch);
  }

  function expireOrContinue(watch: RealizationWatch): void {
    watch.frameBudget--;
    if (watch.frameBudget <= 0) {
      watch.nonconvergentCycles++;
      watch.state = "expired-nonconvergent";
      watch.readyMeasuredHeight = null;
      deps.trace?.({
        id: REALIZATION_TRACE_IDS.expired,
        details: {
          blockIndex: watch.blockIndex,
          cycles: watch.nonconvergentCycles,
        },
      });
      return;
    }

    scheduleSample(watch);
  }

  const recensusRealizationWatches = (): boolean => {
    if (disposed || !isCurrentDocument()) {
      return false;
    }
    syncMountedSections(collectLiveDocumentSectionElements(deps.main), currentMountGeneration);
    let ready = true;
    for (const watch of watches.values()) {
      const intersecting = isStrictlyIntersecting(watch.element);
      if (intersecting && watch.state === "expired-nonconvergent") {
        if (watch.nonconvergentCycles < REALIZATION_QUARANTINE_CYCLES) {
          watch.frameBudget = REALIZATION_FRAME_BUDGET;
          watch.stableFrameCount = 0;
          watch.lastOffsetHeight = null;
          watch.lastOccupiedHeight = null;
          watch.readyMeasuredHeight = null;
          watch.state = "event-observed-settling";
          scheduleSample(watch);
          ready = false;
        } else {
          watch.state = "quarantined-nonconvergent";
          deps.trace?.({
            id: REALIZATION_TRACE_IDS.quarantined,
            details: {
              blockIndex: watch.blockIndex,
              cycles: watch.nonconvergentCycles,
              mountGeneration: watch.mountGeneration,
            },
          });
        }
        continue;
      }
      if (
        intersecting
        && watch.state !== "real-ready"
        && watch.state !== "event-equal-fallback-noop"
        && watch.state !== "quarantined-nonconvergent"
      ) {
        if (
          watch.state === "placeholder-not-intersecting"
          || watch.state === "realized-then-skipped"
        ) {
          watch.state = "intersecting-await-event";
        }
        ready = false;
      }
    }
    return ready;
  };

  return {
    dispose,
    filterRealizedUpdates,
    recensusRealizationWatches,
    syncMountedSections,
  };
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

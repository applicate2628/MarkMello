import {
  normalizeSectionKind,
  readSectionIntrinsicCalibrationTarget,
  type SectionIntrinsicCalibrationTarget,
  type SectionIntrinsicCalibrator,
  type IntrinsicSizeMetrics,
  type SectionKind,
} from "./sectionIntrinsicSize";

export type SectionModelEntry = {
  sectionIndex: number;
  blockIndex: number;
  kind: SectionKind;
  estimatedHeight: number;
  measuredHeight: number | undefined;
  measuredHeightPlaceholder?: boolean;
  headingLevel: number;
  cumulativeTop: number;
  html?: string;
  hasMermaid?: boolean;
  intrinsicSize?: SectionIntrinsicCalibrationTarget;
  needsRichPrep?: boolean;
};

export type EstimateHeightErrorKind = SectionKind | "mermaid";

export type EstimateHeightErrorOffender = {
  sectionIndex: number;
  blockIndex: number;
  kind: EstimateHeightErrorKind;
  estimatedHeight: number;
  measuredHeight: number;
  signedError: number;
  absError: number;
};

export type EstimateHeightErrorBucket = {
  kind: EstimateHeightErrorKind;
  count: number;
  meanAbsError: number;
  maxAbsError: number;
  placeholderCount: number;
  worstOffenders: EstimateHeightErrorOffender[];
};

export type EstimateHeightErrorSummary = {
  count: number;
  meanAbsError: number;
  maxAbsError: number;
  placeholderCount: number;
  byKind: Record<string, EstimateHeightErrorBucket>;
  worstOffenders: EstimateHeightErrorOffender[];
};

export type LiveDocumentWindowModels = {
  measuredModel: DocumentWindowModel;
  estimateOnlyModel: DocumentWindowModel;
  estimateHeightError: EstimateHeightErrorSummary;
};

export type BuildDocumentWindowModelOptions = {
  intrinsicSizeCalibrator?: SectionIntrinsicCalibrator;
};

export type WindowRange = {
  start: number;
  end: number;
};

export type ScrollAnchor = {
  sectionIndex: number;
  blockIndex: number;
  intraOffset: number;
};

export type RenderAheadConfig = {
  aboveViewports: number;
  belowViewports: number;
  minAbovePx: number;
  minBelowPx: number;
};

export type MeasuredHeightUpdate = {
  blockIndex: number;
  measuredHeight: number;
  measuredHeightPlaceholder?: boolean;
};

export type MeasuredHeightUpdateResult = {
  updatedCount: number;
  maxAbsDelta: number;
  totalDelta: number;
};

export type SpacerHeights = {
  topSpacer: number;
  windowHeight: number;
  bottomSpacer: number;
  totalHeight: number;
};

const ESTIMATE_ERROR_KIND_ORDER: EstimateHeightErrorKind[] = [
  "heading",
  "paragraph",
  "math",
  "code",
  "table",
  "list",
  "quote",
  "mermaid",
  "rule",
  "image",
  "unknown",
];

const ESTIMATE_ERROR_WORST_OFFENDER_LIMIT = 5;

export const DEFAULT_RENDER_AHEAD: RenderAheadConfig = {
  aboveViewports: 1.5,
  belowViewports: 2.0,
  minAbovePx: 2400,
  minBelowPx: 3600,
};

export function effectiveHeight(entry: SectionModelEntry): number {
  return entry.measuredHeight ?? entry.estimatedHeight;
}

export class DocumentWindowModel {
  readonly sections: SectionModelEntry[];
  private readonly sectionIndexByBlockIndex = new Map<number, number>();
  private readonly leadingOffset: number;
  private totalHeight = 0;

  constructor(entries: readonly SectionModelEntry[], options: { leadingOffset?: number } = {}) {
    this.leadingOffset = options.leadingOffset ?? 0;
    this.sections = entries
      .slice()
      .sort((a, b) => a.sectionIndex - b.sectionIndex)
      .map(entry => ({ ...entry }));
    for (let index = 0; index < this.sections.length; index++) {
      const entry = this.sections[index]!;
      if (!this.sectionIndexByBlockIndex.has(entry.blockIndex)) {
        this.sectionIndexByBlockIndex.set(entry.blockIndex, index);
      }
    }
    this.refreshHeightModel();
  }

  getSectionCount(): number {
    return this.sections.length;
  }

  getTotalHeight(): number {
    return this.totalHeight;
  }

  sectionTop(sectionIndex: number): number {
    return this.sections[sectionIndex]?.cumulativeTop ?? this.leadingOffset;
  }

  sectionEffectiveHeight(sectionIndex: number): number {
    const entry = this.sections[sectionIndex];
    return entry ? effectiveHeight(entry) : 0;
  }

  getEntryByBlockIndex(blockIndex: number): SectionModelEntry | undefined {
    const sectionIndex = this.sectionIndexByBlockIndex.get(blockIndex);
    return sectionIndex === undefined ? undefined : this.sections[sectionIndex];
  }

  refreshHeightModel(): void {
    let cumulative = this.leadingOffset;
    for (const entry of this.sections) {
      entry.cumulativeTop = cumulative;
      cumulative += effectiveHeight(entry);
    }
    this.totalHeight = cumulative;
  }

  updateMeasuredHeightsByBlockIndex(updates: Iterable<MeasuredHeightUpdate>): MeasuredHeightUpdateResult {
    let updatedCount = 0;
    let maxAbsDelta = 0;
    let totalDelta = 0;
    for (const update of updates) {
      const index = this.sectionIndexByBlockIndex.get(update.blockIndex);
      if (index === undefined) {
        continue;
      }

      const entry = this.sections[index]!;
      if (!Number.isFinite(update.measuredHeight) || update.measuredHeight < 0) {
        continue;
      }

      const previous = effectiveHeight(entry);
      entry.measuredHeight = update.measuredHeight;
      if (update.measuredHeightPlaceholder === true) {
        entry.measuredHeightPlaceholder = true;
      } else {
        delete entry.measuredHeightPlaceholder;
      }
      const delta = update.measuredHeight - previous;
      updatedCount++;
      maxAbsDelta = Math.max(maxAbsDelta, Math.abs(delta));
      totalDelta += delta;
    }
    if (updatedCount > 0) {
      this.refreshHeightModel();
    }
    return { maxAbsDelta, totalDelta, updatedCount };
  }

  recordIntrinsicSizeCalibrationSamples(calibrator: SectionIntrinsicCalibrator): number {
    let recordedCount = 0;
    for (const entry of this.sections) {
      if (entry.measuredHeight === undefined || entry.intrinsicSize === undefined) {
        continue;
      }

      const sample = {
        ...entry.intrinsicSize,
        blockIndex: entry.blockIndex,
        measuredHeight: entry.measuredHeight,
      };
      if (entry.measuredHeightPlaceholder === true) {
        Object.assign(sample, { measuredHeightPlaceholder: true });
      }

      if (calibrator.recordSample(sample)) {
        recordedCount++;
      }
    }
    return recordedCount;
  }

  updateEstimatedHeightsFromCalibration(calibrator: SectionIntrinsicCalibrator): MeasuredHeightUpdateResult {
    let updatedCount = 0;
    let maxAbsDelta = 0;
    let totalDelta = 0;
    for (const entry of this.sections) {
      if (entry.intrinsicSize === undefined) {
        continue;
      }

      const nextHeight = calibrator.estimateTargetHeight(entry.intrinsicSize);
      if (!Number.isFinite(nextHeight) || nextHeight < 0) {
        continue;
      }

      const previous = entry.estimatedHeight;
      if (Object.is(previous, nextHeight)) {
        continue;
      }

      entry.estimatedHeight = nextHeight;
      const delta = nextHeight - previous;
      updatedCount++;
      maxAbsDelta = Math.max(maxAbsDelta, Math.abs(delta));
      totalDelta += delta;
    }
    if (updatedCount > 0) {
      this.refreshHeightModel();
    }
    return { maxAbsDelta, totalDelta, updatedCount };
  }

  sectionIndexAtDocumentY(y: number): number {
    const count = this.sections.length;
    if (count === 0) {
      return 0;
    }

    let lo = 0;
    let hi = count - 1;
    let result = 0;
    while (lo <= hi) {
      const mid = lo + ((hi - lo) >> 1);
      const entry = this.sections[mid]!;
      if (entry.cumulativeTop <= y) {
        result = mid;
        lo = mid + 1;
      } else {
        hi = mid - 1;
      }
    }
    return result;
  }

  computeWindowRange(
    scrollTop: number,
    viewportHeight: number,
    config: RenderAheadConfig = DEFAULT_RENDER_AHEAD
  ): WindowRange {
    if (this.sections.length === 0) {
      return { start: 0, end: -1 };
    }

    const above = Math.max(config.minAbovePx, viewportHeight * config.aboveViewports);
    const below = Math.max(config.minBelowPx, viewportHeight * config.belowViewports);
    const topY = Math.max(0, scrollTop - above);
    const bottomY = scrollTop + viewportHeight + below;
    const start = this.sectionIndexAtDocumentY(topY);
    const end = Math.max(start, this.sectionIndexAtDocumentY(bottomY));
    return { start, end };
  }

  captureAnchor(scrollTop: number): ScrollAnchor {
    const sectionIndex = this.sectionIndexAtDocumentY(scrollTop);
    const entry = this.sections[sectionIndex];
    const top = entry?.cumulativeTop ?? this.leadingOffset;
    return {
      blockIndex: entry?.blockIndex ?? -1,
      intraOffset: Math.max(0, scrollTop - top),
      sectionIndex,
    };
  }

  scrollTopForAnchor(anchor: ScrollAnchor): number {
    const byBlock = anchor.blockIndex >= 0 ? this.getEntryByBlockIndex(anchor.blockIndex) : undefined;
    const entry = byBlock ?? this.sections[anchor.sectionIndex];
    return (entry?.cumulativeTop ?? this.leadingOffset) + anchor.intraOffset;
  }

  computeSpacerHeights(range: WindowRange): SpacerHeights {
    if (this.sections.length === 0 || range.end < range.start) {
      return {
        bottomSpacer: 0,
        topSpacer: 0,
        totalHeight: this.totalHeight,
        windowHeight: 0,
      };
    }

    const start = Math.max(0, Math.min(range.start, this.sections.length - 1));
    const end = Math.max(start, Math.min(range.end, this.sections.length - 1));
    const topSpacer = this.sectionTop(start);
    let windowHeight = 0;
    for (let index = start; index <= end; index++) {
      windowHeight += effectiveHeight(this.sections[index]!);
    }
    return {
      bottomSpacer: Math.max(0, this.totalHeight - topSpacer - windowHeight),
      topSpacer,
      totalHeight: this.totalHeight,
      windowHeight,
    };
  }
}

export function buildDocumentWindowModelFromLiveBlocks(
  blocks: readonly HTMLElement[],
  metrics: IntrinsicSizeMetrics,
  documentScrollHeight: number,
  options: BuildDocumentWindowModelOptions = {}
): DocumentWindowModel {
  return buildDocumentWindowModelsFromLiveBlocks(blocks, metrics, documentScrollHeight, options).measuredModel;
}

export function buildDocumentWindowModelsFromLiveBlocks(
  blocks: readonly HTMLElement[],
  metrics: IntrinsicSizeMetrics,
  documentScrollHeight: number,
  options: BuildDocumentWindowModelOptions = {}
): LiveDocumentWindowModels {
  const measuredEntries = readLiveSectionModelEntries(blocks, metrics, documentScrollHeight, true, options);
  const estimateEntries = measuredEntries.map((entry): SectionModelEntry => {
    const { measuredHeightPlaceholder: _placeholder, ...estimateEntry } = entry;
    return {
      ...estimateEntry,
      measuredHeight: undefined,
    };
  });
  const leadingOffset = measuredEntries[0]?.cumulativeTop ?? 0;
  const measuredModel = new DocumentWindowModel(measuredEntries, { leadingOffset });
  const estimateOnlyModel = new DocumentWindowModel(estimateEntries, { leadingOffset });
  return {
    estimateHeightError: summarizeEstimateHeightErrors(estimateOnlyModel, measuredModel),
    estimateOnlyModel,
    measuredModel,
  };
}

export function collectLiveDocumentSectionElements(main: HTMLElement): HTMLElement[] {
  return Array.from(main.children).filter((child): child is HTMLElement =>
    child instanceof HTMLElement && child.hasAttribute("data-mm-block-index"));
}

export function readLiveBlockMeasuredHeights(
  blocks: readonly HTMLElement[],
  documentScrollHeight: number
): MeasuredHeightUpdate[] {
  return readLiveBlockMeasurements(blocks, documentScrollHeight).map(measurement => {
    const update: MeasuredHeightUpdate = {
      blockIndex: measurement.blockIndex,
      measuredHeight: measurement.measuredHeight,
    };
    if (measurement.measuredHeightPlaceholder) {
      update.measuredHeightPlaceholder = true;
    }
    return update;
  });
}

export function computeLiveBlockWindowRange(
  blocks: readonly HTMLElement[],
  scrollTop: number,
  viewportHeight: number,
  config: RenderAheadConfig = DEFAULT_RENDER_AHEAD
): WindowRange {
  const visibleBlocks = readVisibleBlockGeometry(blocks);
  if (visibleBlocks.length === 0) {
    return { start: 0, end: -1 };
  }

  const above = Math.max(config.minAbovePx, viewportHeight * config.aboveViewports);
  const below = Math.max(config.minBelowPx, viewportHeight * config.belowViewports);
  const topY = Math.max(0, scrollTop - above);
  const bottomY = scrollTop + viewportHeight + below;
  let start = visibleBlocks.length - 1;
  let end = 0;
  let found = false;
  for (let index = 0; index < visibleBlocks.length; index++) {
    const block = visibleBlocks[index]!;
    const top = block.top;
    const bottom = top + block.height;
    if (bottom > topY && top <= bottomY) {
      if (!found) {
        start = index;
      }
      end = index;
      found = true;
    }
  }

  return found ? { start, end } : { start: 0, end: -1 };
}

export function elementDocumentTop(element: HTMLElement): number {
  let top = 0;
  let current: HTMLElement | null = element;
  while (current !== null) {
    if (!Number.isFinite(current.offsetTop)) {
      return Number.NaN;
    }
    top += current.offsetTop;
    const parent: Element | null = current.offsetParent;
    current = parent instanceof HTMLElement ? parent : null;
  }
  return top;
}

export function summarizeEstimateHeightErrors(
  estimateOnlyModel: DocumentWindowModel,
  measuredModel: DocumentWindowModel
): EstimateHeightErrorSummary {
  const mutableBuckets = new Map<EstimateHeightErrorKind, EstimateHeightErrorBucket & { totalAbsError: number }>();
  for (const kind of ESTIMATE_ERROR_KIND_ORDER) {
    mutableBuckets.set(kind, {
      count: 0,
      kind,
      maxAbsError: 0,
      meanAbsError: 0,
      placeholderCount: 0,
      totalAbsError: 0,
      worstOffenders: [],
    });
  }

  let count = 0;
  let totalAbsError = 0;
  let maxAbsError = 0;
  let placeholderCount = 0;
  const worstOffenders: EstimateHeightErrorOffender[] = [];
  for (const measuredEntry of measuredModel.sections) {
    if (measuredEntry.measuredHeight === undefined) {
      continue;
    }

    const kind = estimateErrorKind(measuredEntry);
    if (measuredEntry.measuredHeightPlaceholder === true) {
      const bucket = mutableBuckets.get(kind) ?? {
        count: 0,
        kind,
        maxAbsError: 0,
        meanAbsError: 0,
        placeholderCount: 0,
        totalAbsError: 0,
        worstOffenders: [],
      };
      bucket.placeholderCount++;
      mutableBuckets.set(kind, bucket);
      placeholderCount++;
      continue;
    }

    const estimateEntry = estimateOnlyModel.getEntryByBlockIndex(measuredEntry.blockIndex);
    if (!estimateEntry) {
      continue;
    }

    const signedError = estimateEntry.estimatedHeight - measuredEntry.measuredHeight;
    const absError = Math.abs(signedError);
    const offender: EstimateHeightErrorOffender = {
      absError,
      blockIndex: measuredEntry.blockIndex,
      estimatedHeight: estimateEntry.estimatedHeight,
      kind,
      measuredHeight: measuredEntry.measuredHeight,
      sectionIndex: measuredEntry.sectionIndex,
      signedError,
    };
    const bucket = mutableBuckets.get(kind) ?? {
      count: 0,
      kind,
      maxAbsError: 0,
      meanAbsError: 0,
      placeholderCount: 0,
      totalAbsError: 0,
      worstOffenders: [],
    };
    bucket.count++;
    bucket.totalAbsError += absError;
    bucket.maxAbsError = Math.max(bucket.maxAbsError, absError);
    insertWorstOffender(bucket.worstOffenders, offender);
    mutableBuckets.set(kind, bucket);

    count++;
    totalAbsError += absError;
    maxAbsError = Math.max(maxAbsError, absError);
    insertWorstOffender(worstOffenders, offender);
  }

  const byKind: Record<string, EstimateHeightErrorBucket> = {};
  for (const [kind, bucket] of mutableBuckets) {
    byKind[kind] = {
      count: bucket.count,
      kind,
      maxAbsError: bucket.maxAbsError,
      meanAbsError: bucket.count === 0 ? 0 : bucket.totalAbsError / bucket.count,
      placeholderCount: bucket.placeholderCount,
      worstOffenders: bucket.worstOffenders,
    };
  }

  return {
    byKind,
    count,
    maxAbsError,
    meanAbsError: count === 0 ? 0 : totalAbsError / count,
    placeholderCount,
    worstOffenders,
  };
}

type VisibleBlockGeometry = {
  element: HTMLElement;
  sourceIndex: number;
  top: number;
  height: number;
};

type LiveBlockMeasurement = VisibleBlockGeometry & {
  blockIndex: number;
  measuredHeight: number;
  measuredHeightPlaceholder: boolean;
};

function readLiveSectionModelEntries(
  blocks: readonly HTMLElement[],
  metrics: IntrinsicSizeMetrics,
  documentScrollHeight: number,
  measured: boolean,
  options: BuildDocumentWindowModelOptions
): SectionModelEntry[] {
  return readLiveBlockMeasurements(blocks, documentScrollHeight).map((measurement, sectionIndex): SectionModelEntry => {
    const intrinsicSize = readSectionIntrinsicCalibrationTarget(measurement.element, metrics);
    const entry: SectionModelEntry = {
      blockIndex: measurement.blockIndex,
      cumulativeTop: measurement.top,
      estimatedHeight: options.intrinsicSizeCalibrator?.estimateTargetHeight(intrinsicSize) ?? intrinsicSize.defaultHeight,
      hasMermaid: hasMermaidContent(measurement.element),
      headingLevel: readHeadingLevel(measurement.element),
      intrinsicSize,
      kind: normalizeSectionKind(measurement.element.dataset["mmBlockKind"]),
      measuredHeight: measured ? measurement.measuredHeight : undefined,
      sectionIndex,
    };
    if (measured && measurement.measuredHeightPlaceholder) {
      entry.measuredHeightPlaceholder = true;
    }
    return entry;
  });
}

function readLiveBlockMeasurements(
  blocks: readonly HTMLElement[],
  documentScrollHeight: number
): LiveBlockMeasurement[] {
  const geometry = readVisibleBlockGeometry(blocks);
  const safeDocumentScrollHeight = Number.isFinite(documentScrollHeight) ? documentScrollHeight : 0;
  return geometry.map((item, index): LiveBlockMeasurement => {
    const nextTop = geometry[index + 1]?.top;
    const measuredHeight = nextTop === undefined
      ? Math.max(0, item.height, safeDocumentScrollHeight - item.top)
      : Math.max(0, nextTop - item.top);
    return {
      ...item,
      blockIndex: readBlockIndex(item.element, item.sourceIndex),
      measuredHeight,
      measuredHeightPlaceholder: isContentVisibilityPlaceholderMeasurement(item),
    };
  });
}

function readVisibleBlockGeometry(blocks: readonly HTMLElement[]): VisibleBlockGeometry[] {
  const geometry: VisibleBlockGeometry[] = [];
  for (let sourceIndex = 0; sourceIndex < blocks.length; sourceIndex++) {
    const element = blocks[sourceIndex]!;
    const top = elementDocumentTop(element);
    const height = element.offsetHeight;
    if (!Number.isFinite(top) || !Number.isFinite(height) || height <= 0) {
      continue;
    }
    geometry.push({ element, height, sourceIndex, top });
  }
  return geometry;
}

const CONTENT_VISIBILITY_PLACEHOLDER_TOLERANCE_PX = 1;

function isContentVisibilityPlaceholderMeasurement(item: VisibleBlockGeometry): boolean {
  if (readCssProperty(item.element, "content-visibility").trim() !== "auto") {
    return false;
  }

  const intrinsicSize = readContainIntrinsicBlockSizePx(item.element);
  if (intrinsicSize === null || Math.abs(item.height - intrinsicSize) > CONTENT_VISIBILITY_PLACEHOLDER_TOLERANCE_PX) {
    return false;
  }

  const viewport = readDocumentViewport(item.element);
  if (viewport === null) {
    return false;
  }

  const bottom = item.top + item.height;
  return bottom < viewport.top - CONTENT_VISIBILITY_PLACEHOLDER_TOLERANCE_PX
    || item.top > viewport.bottom + CONTENT_VISIBILITY_PLACEHOLDER_TOLERANCE_PX;
}

function readContainIntrinsicBlockSizePx(element: HTMLElement): number | null {
  const raw = readCssProperty(element, "contain-intrinsic-size");
  const matches = Array.from(raw.matchAll(/(-?\d+(?:\.\d+)?)px/g));
  const lastMatch = matches[matches.length - 1];
  if (!lastMatch) {
    return null;
  }

  const parsed = Number.parseFloat(lastMatch[1]!);
  return Number.isFinite(parsed) && parsed >= 0 ? parsed : null;
}

function readCssProperty(element: HTMLElement, propertyName: string): string {
  const inlineValue = element.style.getPropertyValue(propertyName);
  if (inlineValue.trim().length > 0) {
    return inlineValue;
  }

  const view = element.ownerDocument.defaultView;
  return view?.getComputedStyle(element).getPropertyValue(propertyName) ?? "";
}

function readDocumentViewport(element: HTMLElement): { top: number; bottom: number } | null {
  const doc = element.ownerDocument;
  const view = doc.defaultView;
  const root = doc.scrollingElement ?? doc.documentElement;
  const top = Number.isFinite(root.scrollTop) ? root.scrollTop : view?.scrollY ?? 0;
  const height = root.clientHeight || view?.innerHeight || doc.documentElement.clientHeight;
  if (!Number.isFinite(top) || !Number.isFinite(height) || height <= 0) {
    return null;
  }

  return { bottom: top + height, top };
}

function estimateErrorKind(entry: SectionModelEntry): EstimateHeightErrorKind {
  return entry.hasMermaid ? "mermaid" : entry.kind;
}

function insertWorstOffender(
  offenders: EstimateHeightErrorOffender[],
  offender: EstimateHeightErrorOffender
): void {
  offenders.push(offender);
  offenders.sort((a, b) => b.absError - a.absError);
  if (offenders.length > ESTIMATE_ERROR_WORST_OFFENDER_LIMIT) {
    offenders.length = ESTIMATE_ERROR_WORST_OFFENDER_LIMIT;
  }
}

function hasMermaidContent(element: HTMLElement): boolean {
  return element.classList.contains("mm-mermaid") || element.querySelector("[data-mm-mermaid]") !== null;
}

function readBlockIndex(element: HTMLElement, fallback: number): number {
  const raw = element.dataset["mmBlockIndex"];
  const parsed = raw === undefined ? Number.NaN : Number.parseInt(raw, 10);
  return Number.isFinite(parsed) ? parsed : fallback;
}

function readHeadingLevel(element: HTMLElement): number {
  const tag = element.tagName.toUpperCase();
  return /^H[1-6]$/.test(tag) ? Number.parseInt(tag.slice(1), 10) : 0;
}

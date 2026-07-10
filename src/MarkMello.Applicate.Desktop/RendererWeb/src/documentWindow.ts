import {
  normalizeSectionKind,
  readSectionIntrinsicCalibrationTarget,
  type SectionIntrinsicCalibrationTarget,
  type SectionIntrinsicCalibrator,
  type IntrinsicSizeMetrics,
  type SectionKind,
} from "./sectionIntrinsicSize";
import { readReadyMermaidProxy } from "./mermaidRender";

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
  geometryOwner?: SectionGeometryOwner;
  occupiedNonContentHeight?: number;
  needsRichPrep?: boolean;
  containedBlockIndexes?: readonly number[];
  headingAnchors?: readonly string[];
  sourceLineSpans?: readonly SourceLineModelSpan[];
};

export type SectionGeometryOwner = "mermaid-proxy";

export type SourceLineModelSpan = {
  sourceLine: number;
  endLine: number;
};

export type SourceLineModelAnchor = SourceLineModelSpan & {
  sectionIndex: number;
  blockIndex: number;
  top: number;
};

export type ModelMinimapBlockProjection = {
  kind: SectionKind;
  headingLevel: number;
  top: number;
  height: number;
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
  geometryOwner?: SectionGeometryOwner;
  measuredHeightPlaceholder?: boolean;
  occupiedNonContentHeight?: number;
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
  private readonly containingSectionIndexByBlockIndex = new Map<number, number>();
  private readonly sectionIndexByHeadingAnchor = new Map<string, number>();
  private readonly sourceLineSpans: Array<SourceLineModelSpan & { sectionIndex: number }> = [];
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
      const metadata = readSectionModelEntryMetadata(entry);
      entry.containedBlockIndexes = metadata.containedBlockIndexes;
      entry.headingAnchors = metadata.headingAnchors;
      entry.sourceLineSpans = metadata.sourceLineSpans;
      if (!this.sectionIndexByBlockIndex.has(entry.blockIndex)) {
        this.sectionIndexByBlockIndex.set(entry.blockIndex, index);
      }
      for (const blockIndex of entry.containedBlockIndexes) {
        if (!this.containingSectionIndexByBlockIndex.has(blockIndex)) {
          this.containingSectionIndexByBlockIndex.set(blockIndex, index);
        }
      }
      for (const anchor of entry.headingAnchors) {
        if (!this.sectionIndexByHeadingAnchor.has(anchor)) {
          this.sectionIndexByHeadingAnchor.set(anchor, index);
        }
      }
      for (const span of entry.sourceLineSpans) {
        this.sourceLineSpans.push({ ...span, sectionIndex: index });
      }
    }
    this.sourceLineSpans.sort((left, right) => {
      const sourceComparison = left.sourceLine - right.sourceLine;
      return sourceComparison !== 0 ? sourceComparison : left.sectionIndex - right.sectionIndex;
    });
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

  getEntryContainingBlockIndex(blockIndex: number): SectionModelEntry | undefined {
    const sectionIndex = this.containingSectionIndexByBlockIndex.get(blockIndex);
    return sectionIndex === undefined ? undefined : this.sections[sectionIndex];
  }

  getEntryByHeadingAnchor(anchor: string): SectionModelEntry | undefined {
    const normalized = normalizeHeadingAnchor(anchor);
    if (normalized.length === 0) {
      return undefined;
    }

    const sectionIndex = this.sectionIndexByHeadingAnchor.get(normalized);
    return sectionIndex === undefined ? undefined : this.sections[sectionIndex];
  }

  getEntryBySourceLine(sourceLine: number): SectionModelEntry | undefined {
    if (this.sourceLineSpans.length === 0 || !Number.isFinite(sourceLine)) {
      return undefined;
    }

    const normalizedLine = Math.max(0, Math.floor(sourceLine));
    let low = 0;
    let high = this.sourceLineSpans.length - 1;
    let selectedIndex = -1;
    while (low <= high) {
      const mid = low + Math.floor((high - low) / 2);
      if (this.sourceLineSpans[mid]!.sourceLine <= normalizedLine) {
        selectedIndex = mid;
        low = mid + 1;
      } else {
        high = mid - 1;
      }
    }

    const selected = selectedIndex >= 0 ? this.sourceLineSpans[selectedIndex] : this.sourceLineSpans[0];
    return selected === undefined ? undefined : this.sections[selected.sectionIndex];
  }

  getSourceLineAnchors(): SourceLineModelAnchor[] {
    return this.sourceLineSpans.map(span => {
      const entry = this.sections[span.sectionIndex]!;
      return {
        blockIndex: entry.blockIndex,
        endLine: span.endLine,
        sectionIndex: span.sectionIndex,
        sourceLine: span.sourceLine,
        top: entry.cumulativeTop,
      };
    });
  }

  getMinimapBlockProjection(): ModelMinimapBlockProjection[] {
    return this.sections.map(entry => ({
      height: effectiveHeight(entry),
      headingLevel: entry.headingLevel,
      kind: entry.kind,
      top: entry.cumulativeTop,
    }));
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
      const occupiedNonContentHeight = update.occupiedNonContentHeight;
      if (typeof occupiedNonContentHeight === "number" && Number.isFinite(occupiedNonContentHeight)) {
        entry.occupiedNonContentHeight = occupiedNonContentHeight;
      }
      if (update.measuredHeightPlaceholder === true) {
        continue;
      }
      if (!Number.isFinite(update.measuredHeight) || update.measuredHeight < 0) {
        continue;
      }

      const previous = effectiveHeight(entry);
      entry.measuredHeight = update.measuredHeight;
      if (update.geometryOwner === undefined) {
        delete entry.geometryOwner;
      } else {
        entry.geometryOwner = update.geometryOwner;
      }
      delete entry.measuredHeightPlaceholder;
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
      if (entry.measuredHeightPlaceholder === true) {
        continue;
      }
      if (entry.geometryOwner === "mermaid-proxy") {
        continue;
      }

      const sample = {
        ...entry.intrinsicSize,
        blockIndex: entry.blockIndex,
        measuredHeight: entry.measuredHeight,
      };
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
    const firstTop = this.sections[0]?.cumulativeTop ?? this.leadingOffset;
    if (this.sections.length === 0 || scrollTop < firstTop) {
      return {
        blockIndex: -1,
        intraOffset: Math.max(0, scrollTop),
        sectionIndex: -1,
      };
    }

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
    if (anchor.sectionIndex < 0 || anchor.blockIndex < 0) {
      return Math.max(0, anchor.intraOffset);
    }

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
    const windowTop = this.sectionTop(start);
    const topSpacer = Math.max(0, windowTop - this.leadingOffset);
    let windowHeight = 0;
    for (let index = start; index <= end; index++) {
      windowHeight += effectiveHeight(this.sections[index]!);
    }
    return {
      bottomSpacer: Math.max(0, this.totalHeight - windowTop - windowHeight),
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
    if (measurement.geometryOwner !== undefined) {
      update.geometryOwner = measurement.geometryOwner;
    }
    if (measurement.measuredHeightPlaceholder) {
      update.measuredHeightPlaceholder = true;
    }
    if (measurement.occupiedNonContentHeight !== undefined) {
      update.occupiedNonContentHeight = measurement.occupiedNonContentHeight;
    }
    return update;
  });
}

export function readLiveBlockOffsetMeasuredHeights(blocks: readonly HTMLElement[]): MeasuredHeightUpdate[] {
  const geometry = readVisibleBlockGeometry(blocks);
  return geometry.map((item, index): MeasuredHeightUpdate => {
    const nextItem = geometry[index + 1];
    const nextTop = hasInvalidRenderedMermaidBetween(blocks, item.sourceIndex, nextItem?.sourceIndex)
      ? readNextSiblingDocumentTop(item.boxElement)
      : nextItem?.top ?? readNextSiblingDocumentTop(item.boxElement);
    const measuredHeight = nextTop !== undefined && nextTop > item.top
      ? nextTop - item.top
      : item.height;
    const update: MeasuredHeightUpdate = {
      blockIndex: readBlockIndex(item.semanticElement, item.sourceIndex),
      measuredHeight: Math.max(0, measuredHeight),
    };
    if (item.geometryOwner !== undefined) {
      update.geometryOwner = item.geometryOwner;
    }
    if (isContentVisibilityPlaceholderMeasurement(item)) {
      update.measuredHeightPlaceholder = true;
    }
    const occupiedNonContentHeight = readOccupiedNonContentHeight(item, update.measuredHeight);
    if (occupiedNonContentHeight !== null) {
      update.occupiedNonContentHeight = occupiedNonContentHeight;
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

function readNextSiblingDocumentTop(element: HTMLElement): number | undefined {
  let sibling = element.nextElementSibling;
  while (sibling instanceof HTMLElement) {
    const top = elementDocumentTop(sibling);
    if (Number.isFinite(top)) {
      return top;
    }

    sibling = sibling.nextElementSibling;
  }

  return undefined;
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
    if (measuredEntry.measuredHeight === undefined) {
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
  semanticElement: HTMLElement;
  boxElement: HTMLElement;
  geometryOwner?: SectionGeometryOwner;
  sourceIndex: number;
  top: number;
  height: number;
};

type LiveBlockMeasurement = VisibleBlockGeometry & {
  blockIndex: number;
  measuredHeight: number;
  measuredHeightPlaceholder: boolean;
  occupiedNonContentHeight?: number;
};

function readLiveSectionModelEntries(
  blocks: readonly HTMLElement[],
  metrics: IntrinsicSizeMetrics,
  documentScrollHeight: number,
  measured: boolean,
  options: BuildDocumentWindowModelOptions
): SectionModelEntry[] {
  return readLiveBlockMeasurements(blocks, documentScrollHeight).map((measurement, sectionIndex): SectionModelEntry => {
    const semanticElement = measurement.semanticElement;
    const intrinsicSize = readSectionIntrinsicCalibrationTarget(semanticElement, metrics);
    const entry: SectionModelEntry = {
      blockIndex: measurement.blockIndex,
      cumulativeTop: measurement.top,
      estimatedHeight: options.intrinsicSizeCalibrator?.estimateTargetHeight(intrinsicSize) ?? intrinsicSize.defaultHeight,
      hasMermaid: hasMermaidContent(semanticElement),
      headingLevel: readHeadingLevel(semanticElement),
      html: semanticElement.outerHTML,
      intrinsicSize,
      kind: normalizeSectionKind(semanticElement.dataset["mmBlockKind"]),
      measuredHeight: measured ? measurement.measuredHeight : undefined,
      sectionIndex,
    };
    if (measurement.geometryOwner !== undefined) {
      entry.geometryOwner = measurement.geometryOwner;
    }
    if (measurement.occupiedNonContentHeight !== undefined) {
      entry.occupiedNonContentHeight = measurement.occupiedNonContentHeight;
    }
    if (measured && measurement.measuredHeightPlaceholder) {
      entry.measuredHeight = undefined;
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
    const nextItem = geometry[index + 1];
    const invalidMermaidBoundary = hasInvalidRenderedMermaidBetween(
      blocks,
      item.sourceIndex,
      nextItem?.sourceIndex
    );
    const nextTop = invalidMermaidBoundary
      ? readNextSiblingDocumentTop(item.boxElement)
      : nextItem?.top;
    const measuredHeight = nextTop !== undefined && nextTop > item.top
      ? Math.max(0, nextTop - item.top)
      : invalidMermaidBoundary
        ? Math.max(0, item.height)
        : Math.max(0, item.height, safeDocumentScrollHeight - item.top);
    const measurement: LiveBlockMeasurement = {
      ...item,
      blockIndex: readBlockIndex(item.semanticElement, item.sourceIndex),
      measuredHeight,
      measuredHeightPlaceholder: isContentVisibilityPlaceholderMeasurement(item),
    };
    const occupiedNonContentHeight = readOccupiedNonContentHeight(item, measuredHeight);
    if (occupiedNonContentHeight !== null) {
      measurement.occupiedNonContentHeight = occupiedNonContentHeight;
    }
    return measurement;
  });
}

function readVisibleBlockGeometry(blocks: readonly HTMLElement[]): VisibleBlockGeometry[] {
  const geometry: VisibleBlockGeometry[] = [];
  for (let sourceIndex = 0; sourceIndex < blocks.length; sourceIndex++) {
    const semanticElement = blocks[sourceIndex]!;
    const mermaidProxy = readReadyMermaidProxy(semanticElement);
    if (semanticElement.matches("pre.mm-mermaid.is-rendered") && mermaidProxy === null) {
      continue;
    }

    const boxElement = mermaidProxy ?? semanticElement;
    const top = elementDocumentTop(boxElement);
    const height = boxElement.offsetHeight;
    if (!Number.isFinite(top) || !Number.isFinite(height) || height <= 0) {
      continue;
    }
    const item: VisibleBlockGeometry = {
      boxElement,
      height,
      semanticElement,
      sourceIndex,
      top,
    };
    if (mermaidProxy !== null) {
      item.geometryOwner = "mermaid-proxy";
    }
    geometry.push(item);
  }
  return geometry;
}

function hasInvalidRenderedMermaidBetween(
  blocks: readonly HTMLElement[],
  sourceIndex: number,
  nextSourceIndex: number | undefined
): boolean {
  const end = nextSourceIndex ?? blocks.length;
  for (let index = sourceIndex + 1; index < end; index++) {
    const candidate = blocks[index];
    if (
      candidate?.matches("pre.mm-mermaid.is-rendered")
      && readReadyMermaidProxy(candidate) === null
    ) {
      return true;
    }
  }
  return false;
}

const CONTENT_VISIBILITY_PLACEHOLDER_TOLERANCE_PX = 1;

function isContentVisibilityPlaceholderMeasurement(item: VisibleBlockGeometry): boolean {
  if (readCssProperty(item.boxElement, "content-visibility").trim() !== "auto") {
    return false;
  }

  const viewport = readDocumentViewport(item.boxElement);
  if (viewport === null) {
    return false;
  }

  const bottom = item.top + item.height;
  return bottom <= viewport.top + CONTENT_VISIBILITY_PLACEHOLDER_TOLERANCE_PX
    || item.top >= viewport.bottom - CONTENT_VISIBILITY_PLACEHOLDER_TOLERANCE_PX;
}

function readOccupiedNonContentHeight(item: VisibleBlockGeometry, occupiedHeight: number): number | null {
  if (item.geometryOwner === "mermaid-proxy") {
    return null;
  }
  if (!Number.isFinite(occupiedHeight)) {
    return null;
  }

  const contentBoxHeight = readContentBoxContributionHeight(item);
  if (contentBoxHeight === null) {
    return null;
  }

  const occupiedNonContentHeight = occupiedHeight - contentBoxHeight;
  return Number.isFinite(occupiedNonContentHeight) ? occupiedNonContentHeight : null;
}

function readContentBoxContributionHeight(item: VisibleBlockGeometry): number | null {
  if (isContentVisibilityPlaceholderMeasurement(item)) {
    return readContainIntrinsicBlockSizePx(item.boxElement);
  }

  const blockAxisNonContent = readBlockAxisPaddingBorderHeightPx(item.boxElement);
  if (blockAxisNonContent === null) {
    return null;
  }

  const contentBoxHeight = item.height - blockAxisNonContent;
  return Number.isFinite(contentBoxHeight) && contentBoxHeight >= 0 ? contentBoxHeight : null;
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

type SectionModelEntryMetadata = {
  containedBlockIndexes: number[];
  headingAnchors: string[];
  sourceLineSpans: SourceLineModelSpan[];
};

function readSectionModelEntryMetadata(entry: SectionModelEntry): SectionModelEntryMetadata {
  const parsed = entry.html ? readSectionHtmlMetadata(entry.html) : EMPTY_SECTION_METADATA;
  return {
    containedBlockIndexes: uniqueNumbers([
      entry.blockIndex,
      ...(entry.containedBlockIndexes ?? []),
      ...parsed.containedBlockIndexes,
    ]),
    headingAnchors: uniqueStrings([
      ...(entry.headingAnchors ?? []),
      ...parsed.headingAnchors,
    ]),
    sourceLineSpans: uniqueSourceLineSpans([
      ...(entry.sourceLineSpans ?? []),
      ...parsed.sourceLineSpans,
    ]),
  };
}

const EMPTY_SECTION_METADATA: SectionModelEntryMetadata = {
  containedBlockIndexes: [],
  headingAnchors: [],
  sourceLineSpans: [],
};

function readSectionHtmlMetadata(html: string): SectionModelEntryMetadata {
  if (typeof document === "undefined") {
    return EMPTY_SECTION_METADATA;
  }

  const template = document.createElement("template");
  template.innerHTML = html;
  const elements = Array.from(template.content.querySelectorAll<HTMLElement>("*"));
  return readSectionElementMetadata(elements);
}

function readSectionElementMetadata(elements: readonly HTMLElement[]): SectionModelEntryMetadata {
  const containedBlockIndexes: number[] = [];
  const headingAnchors: string[] = [];
  const sourceLineSpans: SourceLineModelSpan[] = [];
  for (const element of elements) {
    const blockIndex = parseFiniteInt(element.dataset["mmBlockIndex"]);
    if (blockIndex !== null) {
      containedBlockIndexes.push(blockIndex);
    }

    if (/^H[1-6]$/i.test(element.tagName) && element.id.trim().length > 0) {
      headingAnchors.push(element.id);
    }

    const sourceLine = parseNonNegativeInt(element.dataset["mmSourceLine"]);
    if (sourceLine !== null) {
      const rawEndLine = parseNonNegativeInt(element.dataset["mmSourceEndLine"]);
      sourceLineSpans.push({
        endLine: Math.max(sourceLine, rawEndLine ?? sourceLine),
        sourceLine,
      });
    }
  }

  return {
    containedBlockIndexes,
    headingAnchors,
    sourceLineSpans,
  };
}

function uniqueNumbers(values: readonly number[]): number[] {
  const result: number[] = [];
  const seen = new Set<number>();
  for (const value of values) {
    if (!Number.isFinite(value) || seen.has(value)) {
      continue;
    }

    seen.add(value);
    result.push(value);
  }
  return result;
}

function uniqueStrings(values: readonly string[]): string[] {
  const result: string[] = [];
  const seen = new Set<string>();
  for (const value of values) {
    if (value.length === 0 || seen.has(value)) {
      continue;
    }

    seen.add(value);
    result.push(value);
  }
  return result;
}

function uniqueSourceLineSpans(values: readonly SourceLineModelSpan[]): SourceLineModelSpan[] {
  const result: SourceLineModelSpan[] = [];
  const seen = new Set<string>();
  for (const span of values) {
    if (!Number.isFinite(span.sourceLine) || !Number.isFinite(span.endLine)) {
      continue;
    }

    const sourceLine = Math.max(0, Math.floor(span.sourceLine));
    const endLine = Math.max(sourceLine, Math.floor(span.endLine));
    const key = `${sourceLine}:${endLine}`;
    if (seen.has(key)) {
      continue;
    }

    seen.add(key);
    result.push({ endLine, sourceLine });
  }
  return result;
}

function normalizeHeadingAnchor(anchor: string): string {
  return anchor.startsWith("#") ? anchor.slice(1) : anchor;
}

function parseFiniteInt(value: string | undefined): number | null {
  if (value === undefined || value.trim() === "") {
    return null;
  }

  const parsed = Number.parseInt(value, 10);
  return Number.isFinite(parsed) ? parsed : null;
}

function parseNonNegativeInt(value: string | undefined): number | null {
  const parsed = parseFiniteInt(value);
  return parsed !== null && parsed >= 0 ? parsed : null;
}

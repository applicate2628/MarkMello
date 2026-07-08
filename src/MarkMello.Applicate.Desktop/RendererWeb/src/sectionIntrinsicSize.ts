export type SectionKind =
  | "heading"
  | "paragraph"
  | "quote"
  | "list"
  | "rule"
  | "code"
  | "table"
  | "image"
  | "math"
  | "unknown";

export type IntrinsicSizeMetrics = {
  charsPerLine: number;
  lineHeightPx: number;
  fontSizePx: number;
};

export type SectionIntrinsicInputs = {
  textLength: number;
  newlineCount: number;
  listItemCount: number;
  tableRowCount: number;
  headingLevel: number;
};

export type SectionIntrinsicCalibrationTarget = {
  kind: SectionKind;
  input: SectionIntrinsicInputs;
  defaultHeight: number;
  sourceText?: string;
};

export type SectionIntrinsicCalibrationSample = SectionIntrinsicCalibrationTarget & {
  blockIndex: number;
  measuredHeight: number;
  measuredHeightPlaceholder?: boolean;
};

export type SectionIntrinsicCalibrationOptions = {
  minSamplesPerBucket?: number;
};

export type SectionIntrinsicCalibrationBucketSummary = {
  calibratedBucketCount: number;
  sampleCount: number;
};

export type SectionIntrinsicCalibrationSummary = {
  bucketCount: number;
  calibratedBucketCount: number;
  sampleCount: number;
  byKind: Record<string, SectionIntrinsicCalibrationBucketSummary>;
};

const MODEL_GAP_PX = 44;
const DEFAULT_LINE_HEIGHT_PX = 30;
const DEFAULT_DISPLAY_MATH_CONTENT_PX = 120;
const HEADING_CONTENT_HEIGHT_BY_LEVEL: Record<number, number> = {
  1: 56,
  2: 35,
  3: 32,
  4: 30,
  5: 28,
  6: 26,
};
const DEFAULT_MIN_SAMPLES_PER_BUCKET = 3;

export function readIntrinsicSizeMetrics(main: HTMLElement): IntrinsicSizeMetrics {
  const styles = getComputedStyle(main);
  const fontSizePx = Number.parseFloat(styles.fontSize) || 18;
  const lineHeightPx = readLineHeightPx(styles.lineHeight, fontSizePx);
  const contentWidth = main.clientWidth || 820;
  const charsPerLine = Math.max(8, Math.floor(contentWidth / (fontSizePx * 0.61)));
  return { charsPerLine, lineHeightPx, fontSizePx };
}

function readLineHeightPx(lineHeight: string, fontSizePx: number): number {
  if (lineHeight.endsWith("px")) {
    return Number.parseFloat(lineHeight) || fontSizePx * 1.6;
  }

  const ratio = Number.parseFloat(lineHeight);
  return Number.isFinite(ratio) && ratio > 0 ? fontSizePx * ratio : fontSizePx * 1.6;
}

export function normalizeSectionKind(raw: string | null | undefined): SectionKind {
  switch (raw) {
    case "heading":
    case "paragraph":
    case "quote":
    case "list":
    case "rule":
    case "code":
    case "table":
    case "image":
    case "math":
      return raw;
    default:
      return "unknown";
  }
}

export function readSectionIntrinsicInputs(element: HTMLElement): SectionIntrinsicInputs {
  const text = element.textContent ?? "";
  return {
    headingLevel: readHeadingLevel(element),
    listItemCount: element.querySelectorAll("li").length,
    newlineCount: countNewlines(text),
    tableRowCount: element.querySelectorAll("tr").length,
    textLength: text.length,
  };
}

function readHeadingLevel(element: HTMLElement): number {
  const tag = element.tagName.toUpperCase();
  return /^H[1-6]$/.test(tag) ? Number.parseInt(tag.slice(1), 10) : 0;
}

function countNewlines(text: string): number {
  let count = 0;
  for (let index = 0; index < text.length; index++) {
    if (text.charCodeAt(index) === 10) {
      count++;
    }
  }
  return count;
}

export function estimateSectionIntrinsicHeightFromElement(
  element: HTMLElement,
  metrics: IntrinsicSizeMetrics,
  calibrator?: SectionIntrinsicCalibrator
): number {
  const target = readSectionIntrinsicCalibrationTarget(element, metrics);
  return calibrator?.estimateTargetHeight(target) ?? target.defaultHeight;
}

export function readSectionIntrinsicCalibrationTarget(
  element: HTMLElement,
  metrics: IntrinsicSizeMetrics
): SectionIntrinsicCalibrationTarget {
  const kind = normalizeSectionKind(element.dataset["mmBlockKind"]);
  const input = readSectionIntrinsicInputs(element);
  const sourceText = element.textContent ?? "";
  return {
    defaultHeight: estimateSectionIntrinsicHeight(kind, input, metrics, sourceText),
    input,
    kind,
    sourceText,
  };
}

export function estimateSectionIntrinsicHeight(
  kind: SectionKind,
  input: SectionIntrinsicInputs,
  metrics: IntrinsicSizeMetrics,
  sourceText = ""
): number {
  const wrappedLines = Math.max(1, Math.ceil(input.textLength / metrics.charsPerLine));
  switch (kind) {
    case "heading": {
      const level = input.headingLevel >= 1 && input.headingLevel <= 6 ? input.headingLevel : 2;
      const baseContentHeight = scaleDefaultPx(HEADING_CONTENT_HEIGHT_BY_LEVEL[level]!, metrics);
      const wrapExtraHeight = Math.max(0, wrappedLines - 1) * metrics.lineHeightPx * 1.15;
      return withModelGap(baseContentHeight + wrapExtraHeight);
    }
    case "paragraph":
      return withModelGap((wrappedLines * metrics.lineHeightPx + metrics.lineHeightPx * 0.6) * 0.95);
    case "code":
      return withModelGap((input.newlineCount + 1) * metrics.lineHeightPx * 0.95 + metrics.lineHeightPx * 1.4);
    case "quote":
      return withModelGap(wrappedLines * metrics.lineHeightPx + metrics.lineHeightPx * 0.9);
    case "list":
      return withModelGap((input.listItemCount || 1) * metrics.lineHeightPx * 1.3 + metrics.lineHeightPx * 0.5);
    case "table": {
      const rows = input.tableRowCount || 2;
      return withModelGap(rows * metrics.lineHeightPx * 1.0 + metrics.lineHeightPx * 0.8);
    }
    case "math": {
      const rowCount = countMathRows(sourceText);
      const baseContentHeight = scaleDefaultPx(DEFAULT_DISPLAY_MATH_CONTENT_PX, metrics);
      return withModelGap(baseContentHeight + Math.max(0, rowCount - 1) * metrics.lineHeightPx * 1.35);
    }
    case "image":
      return withModelGap(320);
    case "rule":
      return withModelGap(metrics.lineHeightPx);
    default:
      return withModelGap(Math.max(metrics.lineHeightPx, wrappedLines * metrics.lineHeightPx));
  }
}

function withModelGap(contentBoxHeight: number): number {
  return contentBoxHeight + MODEL_GAP_PX;
}

function scaleDefaultPx(defaultPx: number, metrics: IntrinsicSizeMetrics): number {
  return defaultPx * (metrics.lineHeightPx / DEFAULT_LINE_HEIGHT_PX);
}

function countMathRows(sourceText: string): number {
  const rowSeparators = sourceText.match(/\\\\/g)?.length ?? 0;
  return Math.max(1, rowSeparators + 1);
}

type CalibrationBucket = {
  kind: SectionKind;
  samplesByBlockIndex: Map<number, number>;
};

export class SectionIntrinsicCalibrator {
  private readonly buckets = new Map<string, CalibrationBucket>();
  private readonly bucketKeyByBlockIndex = new Map<number, string>();
  private readonly minSamplesPerBucket: number;

  constructor(options: SectionIntrinsicCalibrationOptions = {}) {
    this.minSamplesPerBucket = Math.max(1, Math.floor(options.minSamplesPerBucket ?? DEFAULT_MIN_SAMPLES_PER_BUCKET));
  }

  reset(): void {
    this.buckets.clear();
    this.bucketKeyByBlockIndex.clear();
  }

  recordSample(sample: SectionIntrinsicCalibrationSample): boolean {
    if (sample.measuredHeightPlaceholder === true
      || !Number.isFinite(sample.blockIndex)
      || !Number.isFinite(sample.measuredHeight)
      || sample.measuredHeight < 0
      || !Number.isFinite(sample.defaultHeight)
      || sample.defaultHeight <= 0) {
      return false;
    }

    const bucketKey = sectionIntrinsicCalibrationBucketKey(sample.kind, sample.input, sample.sourceText ?? "");
    const previousBucketKey = this.bucketKeyByBlockIndex.get(sample.blockIndex);
    if (previousBucketKey !== undefined && previousBucketKey !== bucketKey) {
      this.buckets.get(previousBucketKey)?.samplesByBlockIndex.delete(sample.blockIndex);
    }

    const bucket = this.readOrCreateBucket(bucketKey, sample.kind);
    const hadSample = bucket.samplesByBlockIndex.has(sample.blockIndex);
    bucket.samplesByBlockIndex.set(sample.blockIndex, sample.measuredHeight);
    this.bucketKeyByBlockIndex.set(sample.blockIndex, bucketKey);
    return !hadSample;
  }

  estimateHeight(
    kind: SectionKind,
    input: SectionIntrinsicInputs,
    metrics: IntrinsicSizeMetrics,
    sourceText = ""
  ): number {
    return this.estimateTargetHeight({
      defaultHeight: estimateSectionIntrinsicHeight(kind, input, metrics, sourceText),
      input,
      kind,
      sourceText,
    });
  }

  estimateTargetHeight(target: SectionIntrinsicCalibrationTarget): number {
    const bucket = this.buckets.get(sectionIntrinsicCalibrationBucketKey(
      target.kind,
      target.input,
      target.sourceText ?? ""));
    if (bucket === undefined || bucket.samplesByBlockIndex.size < this.minSamplesPerBucket) {
      return target.defaultHeight;
    }

    return median(Array.from(bucket.samplesByBlockIndex.values()));
  }

  getSummary(): SectionIntrinsicCalibrationSummary {
    const byKind: Record<string, SectionIntrinsicCalibrationBucketSummary> = {};
    let bucketCount = 0;
    let calibratedBucketCount = 0;
    let sampleCount = 0;
    for (const bucket of this.buckets.values()) {
      const kindSummary = byKind[bucket.kind] ?? {
        calibratedBucketCount: 0,
        sampleCount: 0,
      };
      const bucketSampleCount = bucket.samplesByBlockIndex.size;
      bucketCount++;
      sampleCount += bucketSampleCount;
      kindSummary.sampleCount += bucketSampleCount;
      if (bucketSampleCount >= this.minSamplesPerBucket) {
        calibratedBucketCount++;
        kindSummary.calibratedBucketCount++;
      }
      byKind[bucket.kind] = kindSummary;
    }
    return { bucketCount, calibratedBucketCount, sampleCount, byKind };
  }

  private readOrCreateBucket(bucketKey: string, kind: SectionKind): CalibrationBucket {
    const existing = this.buckets.get(bucketKey);
    if (existing !== undefined) {
      return existing;
    }

    const created: CalibrationBucket = {
      kind,
      samplesByBlockIndex: new Map<number, number>(),
    };
    this.buckets.set(bucketKey, created);
    return created;
  }
}

export function createSectionIntrinsicCalibrator(
  options: SectionIntrinsicCalibrationOptions = {}
): SectionIntrinsicCalibrator {
  return new SectionIntrinsicCalibrator(options);
}

function sectionIntrinsicCalibrationBucketKey(
  kind: SectionKind,
  input: SectionIntrinsicInputs,
  sourceText: string
): string {
  switch (kind) {
    case "heading": {
      const level = input.headingLevel >= 1 && input.headingLevel <= 6 ? input.headingLevel : 2;
      return `${kind}:level:${level}`;
    }
    case "paragraph":
    case "quote":
      return `${kind}:text:${bucketByThreshold(input.textLength, [80, 160, 320, 640, 1280, 2560])}`;
    case "math":
      return `${kind}:rows:${bucketByThreshold(countMathRows(sourceText), [1, 2, 4, 8, 16])}`;
    case "code":
      return `${kind}:lines:${bucketByThreshold(input.newlineCount + 1, [1, 3, 8, 16, 32, 64])}`;
    case "list":
      return `${kind}:items:${bucketByThreshold(input.listItemCount || 1, [1, 3, 6, 12, 24, 48])}`;
    case "table":
      return `${kind}:rows:${bucketByThreshold(input.tableRowCount || 2, [2, 4, 8, 16, 32, 64])}`;
    default:
      return kind;
  }
}

function bucketByThreshold(value: number, thresholds: readonly number[]): string {
  for (const threshold of thresholds) {
    if (value <= threshold) {
      return `le-${threshold}`;
    }
  }
  return `gt-${thresholds[thresholds.length - 1] ?? 0}`;
}

function median(values: readonly number[]): number {
  if (values.length === 0) {
    return 0;
  }

  const sorted = values.slice().sort((a, b) => a - b);
  const middle = Math.floor(sorted.length / 2);
  if (sorted.length % 2 === 1) {
    return sorted[middle]!;
  }

  return (sorted[middle - 1]! + sorted[middle]!) / 2;
}

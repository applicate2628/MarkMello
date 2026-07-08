import {
  DEFAULT_RENDER_AHEAD,
  DocumentWindowModel,
  buildDocumentWindowModelsFromLiveBlocks,
  collectLiveDocumentSectionElements,
  computeLiveBlockWindowRange,
  elementDocumentTop,
  readLiveBlockMeasuredHeights,
  summarizeEstimateHeightErrors,
  type EstimateHeightErrorSummary,
  type RenderAheadConfig,
} from "./documentWindow";
import { readIntrinsicSizeMetrics } from "./sectionIntrinsicSize";
import {
  collectLiveDocumentBlockElements,
  findTopVisibleBlockIndexFromBlocks,
} from "./topVisibleBlockIndex";
import {
  createSectionIntrinsicCalibrator,
  type SectionIntrinsicCalibrationSummary,
} from "./sectionIntrinsicSize";

const SHADOW_FLAG_NAME = "MARKMELLO_VIRT_SHADOW";

type ShadowFlagWindow = Window & {
  MARKMELLO_VIRT_SHADOW?: unknown;
};

export type VirtualizationShadowValidation = {
  sectionCount: number;
  predictedTotalHeight: number;
  estimatedTotalHeight: number;
  realScrollHeight: number;
  scrollHeightGrowth: number;
  totalHeightDelta: number;
  estimatedTotalHeightDelta: number;
  estimateHeightError: EstimateHeightErrorSummary;
  estimateCalibration: SectionIntrinsicCalibrationSummary;
  predictedTopSectionIndex: number;
  predictedTopBlockIndex: number | null;
  predictedIntraOffset: number;
  realTopSectionIndex: number | null;
  realTopBlockIndex: number | null;
  productionTopSectionIndex: number | null;
  productionTopBlockIndex: number | null;
  nestedTopVisibleAnchor: boolean;
  realIntraOffset: number | null;
  intraOffsetDelta: number | null;
  topOffsetDelta: number | null;
  anchorBlockIndexMatches: boolean;
  predictedWindowStart: number;
  predictedWindowEnd: number;
  actualWindowStart: number;
  actualWindowEnd: number;
  windowStartDelta: number;
  windowEndDelta: number;
  maxAbsPxError: number;
  maxAbsIndexDelta: number;
  maxAbsError: number;
  elapsedMs: number;
};

export type VirtualizationShadowValidator = {
  invalidate: () => void;
  schedule: () => void;
  validateNow: () => VirtualizationShadowValidation | null;
};

export type VirtualizationShadowDeps = {
  ownerDocument: Document;
  ownerWindow: Window;
  isDocumentFinal?: () => boolean;
  postPerfMark: (name: string, detail?: Record<string, unknown>) => void;
  postDebugLog: (text: string) => void;
};

export function readVirtualizationShadowFlag(
  ownerWindow: Window = window,
  ownerDocument: Document = document
): boolean {
  const shadowWindow = ownerWindow as ShadowFlagWindow;
  return isTrueFlagValue(shadowWindow.MARKMELLO_VIRT_SHADOW)
    || isTrueFlagValue(ownerDocument.documentElement.dataset["markmelloVirtShadow"])
    || isTrueFlagValue(readLocalStorageFlag(ownerWindow));
}

function readLocalStorageFlag(ownerWindow: Window): string | null {
  try {
    return ownerWindow.localStorage.getItem(SHADOW_FLAG_NAME);
  } catch {
    return null;
  }
}

function isTrueFlagValue(value: unknown): boolean {
  if (value === true) {
    return true;
  }
  if (typeof value !== "string") {
    return false;
  }

  switch (value.trim().toLowerCase()) {
    case "1":
    case "true":
    case "yes":
    case "on":
      return true;
    default:
      return false;
  }
}

export function validateVirtualizationShadowGeometry(input: {
  model: DocumentWindowModel;
  estimateModel?: DocumentWindowModel;
  blocks: readonly HTMLElement[];
  productionBlocks?: readonly HTMLElement[];
  scrollTop: number;
  viewportHeight: number;
  realScrollHeight: number;
  realTopBlockIndex: number | null;
  productionTopBlockIndex?: number | null;
  estimateCalibration?: SectionIntrinsicCalibrationSummary;
  config?: RenderAheadConfig;
}): VirtualizationShadowValidation {
  const estimateModel = input.estimateModel ?? input.model;
  const predictedAnchor = input.model.captureAnchor(input.scrollTop);
  const predictedRange = input.model.computeWindowRange(
    input.scrollTop,
    input.viewportHeight,
    input.config ?? DEFAULT_RENDER_AHEAD);
  const actualRange = computeLiveBlockWindowRange(
    input.blocks,
    input.scrollTop,
    input.viewportHeight,
    input.config ?? DEFAULT_RENDER_AHEAD);
  const productionTopBlockIndex = input.productionTopBlockIndex ?? input.realTopBlockIndex;
  const productionBlocks = input.productionBlocks ?? input.blocks;
  const realTop = findBlockByIndex(input.blocks, input.realTopBlockIndex);
  const realTopSectionIndex = input.realTopBlockIndex === null
    ? null
    : input.model.getEntryByBlockIndex(input.realTopBlockIndex)?.sectionIndex ?? null;
  const productionTopSectionIndex = productionTopBlockIndex === null
    ? null
    : input.model.getEntryByBlockIndex(productionTopBlockIndex)?.sectionIndex ?? null;
  const productionTop = findBlockByIndex(productionBlocks, productionTopBlockIndex);
  const nestedTopVisibleAnchor = productionTopBlockIndex !== null
    && productionTop !== null
    && findBlockByIndex(input.blocks, productionTopBlockIndex) === null;
  const realTopDocumentTop = realTop === null ? null : elementDocumentTop(realTop);
  const realIntraOffset = realTop === null
    ? null
    : Math.max(0, input.scrollTop - realTopDocumentTop!);
  const anchorBlockIndexMatches = blockIndexMatchesExpectedTop({
    model: input.model,
    predictedSectionIndex: predictedAnchor.sectionIndex,
    predictedTopBlockIndex: predictedAnchor.blockIndex >= 0 ? predictedAnchor.blockIndex : null,
    realIntraOffset,
    realTopBlockIndex: input.realTopBlockIndex,
  });
  const intraOffsetDelta = realIntraOffset === null || predictedAnchor.blockIndex !== input.realTopBlockIndex
    ? null
    : predictedAnchor.intraOffset - realIntraOffset;
  const topOffsetDelta = realTopDocumentTop === null || realTopSectionIndex === null
    ? null
    : input.model.sectionTop(realTopSectionIndex) - realTopDocumentTop;
  const totalHeightDelta = input.model.getTotalHeight() - input.realScrollHeight;
  const estimatedTotalHeightDelta = estimateModel.getTotalHeight() - input.realScrollHeight;
  const windowStartDelta = predictedRange.start - actualRange.start;
  const windowEndDelta = predictedRange.end - actualRange.end;
  const maxAbsPxError = Math.max(
    Math.abs(totalHeightDelta),
    Math.abs(topOffsetDelta ?? 0),
    Math.abs(intraOffsetDelta ?? 0));
  const maxAbsIndexDelta = Math.max(
    Math.abs(windowStartDelta),
    Math.abs(windowEndDelta));
  const estimateHeightError = summarizeEstimateHeightErrors(estimateModel, input.model);

  return {
    actualWindowEnd: actualRange.end,
    actualWindowStart: actualRange.start,
    anchorBlockIndexMatches,
    elapsedMs: 0,
    estimatedTotalHeight: estimateModel.getTotalHeight(),
    estimatedTotalHeightDelta,
    estimateCalibration: input.estimateCalibration ?? emptyEstimateCalibrationSummary(),
    estimateHeightError,
    intraOffsetDelta,
    maxAbsError: maxAbsPxError,
    maxAbsIndexDelta,
    maxAbsPxError,
    nestedTopVisibleAnchor,
    predictedIntraOffset: predictedAnchor.intraOffset,
    predictedTopBlockIndex: predictedAnchor.blockIndex >= 0 ? predictedAnchor.blockIndex : null,
    predictedTopSectionIndex: predictedAnchor.sectionIndex,
    predictedTotalHeight: input.model.getTotalHeight(),
    predictedWindowEnd: predictedRange.end,
    predictedWindowStart: predictedRange.start,
    productionTopBlockIndex,
    productionTopSectionIndex,
    realIntraOffset,
    realScrollHeight: input.realScrollHeight,
    realTopBlockIndex: input.realTopBlockIndex,
    realTopSectionIndex,
    scrollHeightGrowth: 0,
    sectionCount: input.model.getSectionCount(),
    topOffsetDelta,
    totalHeightDelta,
    windowEndDelta,
    windowStartDelta,
  };
}

export function createVirtualizationShadowValidator(
  deps: VirtualizationShadowDeps
): VirtualizationShadowValidator {
  let model: DocumentWindowModel | null = null;
  let estimateModel: DocumentWindowModel | null = null;
  let scheduled = false;
  let validationCount = 0;
  let nestedTopVisibleAnchorCount = 0;
  let previousScrollHeight: number | null = null;
  let scrollHeightGrowth = 0;
  const intrinsicSizeCalibrator = createSectionIntrinsicCalibrator();

  const validateNow = (): VirtualizationShadowValidation | null => {
    const startedAt = nowMs(deps.ownerWindow);
    if (deps.isDocumentFinal?.() === false) {
      deps.postPerfMark("mm-virt-shadow-validation-skipped", {
        reason: "progressive-append-pending",
      });
      return null;
    }

    const main = deps.ownerDocument.querySelector<HTMLElement>("main.mm-document");
    const root = deps.ownerDocument.scrollingElement ?? deps.ownerDocument.documentElement;
    const realScrollHeight = root.scrollHeight;
    if (previousScrollHeight !== null) {
      scrollHeightGrowth += Math.max(0, realScrollHeight - previousScrollHeight);
    }
    previousScrollHeight = realScrollHeight;
    const blocks = main ? collectLiveDocumentSectionElements(main) : [];
    if (!main || blocks.length === 0) {
      model = null;
      estimateModel = null;
      intrinsicSizeCalibrator.reset();
      previousScrollHeight = null;
      scrollHeightGrowth = 0;
      return null;
    }

    if (model === null || estimateModel === null) {
      const models = buildDocumentWindowModelsFromLiveBlocks(
        blocks,
        readIntrinsicSizeMetrics(main),
        realScrollHeight,
        { intrinsicSizeCalibrator });
      model = models.measuredModel;
      estimateModel = models.estimateOnlyModel;
      if (model.getSectionCount() === 0) {
        model = null;
        estimateModel = null;
        intrinsicSizeCalibrator.reset();
        return null;
      }
      deps.postPerfMark("mm-virt-shadow-model-built", {
        estimatedTotalHeight: estimateModel.getTotalHeight(),
        estimateHeightError: models.estimateHeightError,
        sectionCount: model.getSectionCount(),
        totalHeight: model.getTotalHeight(),
      });
    }

    const productionBlocks = collectLiveDocumentBlockElements(deps.ownerDocument);
    const validation = validateVirtualizationShadowGeometry({
      blocks,
      estimateModel,
      model,
      productionBlocks,
      productionTopBlockIndex: findTopVisibleBlockIndexFromBlocks(productionBlocks, root.scrollTop),
      realScrollHeight,
      realTopBlockIndex: findTopVisibleBlockIndexFromBlocks(blocks, root.scrollTop),
      scrollTop: root.scrollTop,
      viewportHeight: root.clientHeight,
    });
    const adopted = model.updateMeasuredHeightsByBlockIndex(
      readLiveBlockMeasuredHeights(blocks, realScrollHeight));
    const calibrationRecordedCount = model.recordIntrinsicSizeCalibrationSamples(intrinsicSizeCalibrator);
    const calibratedEstimate = estimateModel.updateEstimatedHeightsFromCalibration(intrinsicSizeCalibrator);
    const estimateCalibration = intrinsicSizeCalibrator.getSummary();
    validationCount++;
    if (validation.nestedTopVisibleAnchor) {
      nestedTopVisibleAnchorCount++;
    }
    const elapsedMs = Math.max(0, nowMs(deps.ownerWindow) - startedAt);
    const estimatedTotalHeight = estimateModel.getTotalHeight();
    const measuredValidation: VirtualizationShadowValidation = {
      ...validation,
      elapsedMs,
      estimatedTotalHeight,
      estimatedTotalHeightDelta: estimatedTotalHeight - realScrollHeight,
      estimateCalibration,
      estimateHeightError: summarizeEstimateHeightErrors(estimateModel, model),
      scrollHeightGrowth,
    };
    const detail = {
      ...measuredValidation,
      adoptedMaxAbsDelta: adopted.maxAbsDelta,
      adoptedTotalDelta: adopted.totalDelta,
      adoptedUpdatedCount: adopted.updatedCount,
      calibratedEstimateMaxAbsDelta: calibratedEstimate.maxAbsDelta,
      calibratedEstimateTotalDelta: calibratedEstimate.totalDelta,
      calibratedEstimateUpdatedCount: calibratedEstimate.updatedCount,
      calibrationRecordedCount,
      nestedTopVisibleAnchorCount,
      validationCount,
    };
    deps.postPerfMark("mm-virt-shadow-validation", detail);
    deps.postDebugLog(
      `virt-shadow sections=${measuredValidation.sectionCount} totalDelta=${Math.round(measuredValidation.totalHeightDelta)} ` +
      `estimateDelta=${Math.round(measuredValidation.estimatedTotalHeightDelta)} ` +
      `topModel=${measuredValidation.predictedTopBlockIndex ?? "null"} ` +
      `topReal=${measuredValidation.realTopBlockIndex ?? "null"} ` +
      `topProd=${measuredValidation.productionTopBlockIndex ?? "null"} ` +
      `nested=${nestedTopVisibleAnchorCount}/${validationCount} ` +
      `topDelta=${measuredValidation.topOffsetDelta === null ? "null" : Math.round(measuredValidation.topOffsetDelta)} ` +
      `intraDelta=${measuredValidation.intraOffsetDelta === null ? "null" : Math.round(measuredValidation.intraOffsetDelta)} ` +
      `estimateMeanErr=${Math.round(measuredValidation.estimateHeightError.meanAbsError)} ` +
      `estimateMaxErr=${Math.round(measuredValidation.estimateHeightError.maxAbsError)} ` +
      `window=${measuredValidation.predictedWindowStart}..${measuredValidation.predictedWindowEnd}/` +
      `${measuredValidation.actualWindowStart}..${measuredValidation.actualWindowEnd} ` +
      `maxPx=${Math.round(measuredValidation.maxAbsPxError)} ` +
      `maxIndex=${Math.round(measuredValidation.maxAbsIndexDelta)} ` +
      `scrollGrowth=${Math.round(measuredValidation.scrollHeightGrowth)} ` +
      `elapsedMs=${Math.round(elapsedMs)}`);
    return measuredValidation;
  };

  return {
    invalidate: () => {
      model = null;
      estimateModel = null;
      intrinsicSizeCalibrator.reset();
      previousScrollHeight = null;
      scrollHeightGrowth = 0;
    },
    schedule: () => {
      if (scheduled) {
        return;
      }

      scheduled = true;
      scheduleIdle(deps.ownerWindow, () => {
        scheduled = false;
        validateNow();
      });
    },
    validateNow,
  };
}

function scheduleIdle(ownerWindow: Window, callback: () => void): void {
  const requestIdle = (ownerWindow as Window & {
    requestIdleCallback?: (cb: () => void, options?: { timeout?: number }) => number;
  }).requestIdleCallback;

  if (requestIdle) {
    requestIdle(callback, { timeout: 500 });
    return;
  }

  ownerWindow.setTimeout(callback, 120);
}

function findBlockByIndex(blocks: readonly HTMLElement[], blockIndex: number | null): HTMLElement | null {
  if (blockIndex === null) {
    return null;
  }

  for (const block of blocks) {
    const raw = block.dataset["mmBlockIndex"];
    const parsed = raw === undefined ? Number.NaN : Number.parseInt(raw, 10);
    if (parsed === blockIndex) {
      return block;
    }
  }
  return null;
}

function blockIndexMatchesExpectedTop(input: {
  model: DocumentWindowModel;
  predictedTopBlockIndex: number | null;
  predictedSectionIndex: number;
  realTopBlockIndex: number | null;
  realIntraOffset: number | null;
}): boolean {
  if (input.predictedTopBlockIndex === input.realTopBlockIndex) {
    return true;
  }
  if (input.realTopBlockIndex === null || input.realIntraOffset !== 0) {
    return false;
  }

  const realTopEntry = input.model.getEntryByBlockIndex(input.realTopBlockIndex);
  return realTopEntry !== undefined && input.predictedSectionIndex === realTopEntry.sectionIndex - 1;
}

function nowMs(ownerWindow: Window): number {
  const performanceNow = ownerWindow.performance?.now;
  return typeof performanceNow === "function" ? performanceNow.call(ownerWindow.performance) : Date.now();
}

function emptyEstimateCalibrationSummary(): SectionIntrinsicCalibrationSummary {
  return {
    bucketCount: 0,
    byKind: {},
    calibratedBucketCount: 0,
    sampleCount: 0,
  };
}

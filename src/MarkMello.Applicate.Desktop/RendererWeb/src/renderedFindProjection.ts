import {
  walkVisibleTextNodesSliced,
  type VisibleTextTraversalCheckpoint,
} from "./findVisibleText";

export const RENDERED_FIND_TEXT_DOMAIN = "rendered-dom-v1";
export const RENDERED_FIND_SCHEMA_VERSION = 1;
export const RENDERED_FIND_MAX_MESSAGE_UTF8_BYTES = 262_144;
export const RENDERED_FIND_MAX_MESSAGE_CODE_UNITS = 262_144;
export const RENDERED_FIND_MAX_CHUNK_PARTS = 4_096;
export const RENDERED_FIND_MAX_TEXT_PART_CODE_UNITS = 65_536;
export const RENDERED_FIND_MAX_PROJECTION_CODE_UNITS = 16_777_216;
export const RENDERED_FIND_MAX_SEMANTIC_SEGMENTS = 524_288;
export const RENDERED_FIND_MAX_TRANSFER_PARTS = 1_048_576;
export const RENDERED_FIND_PRODUCER_SLICE_BUDGET_MS = 7;
// Producer packing measures the JSON.stringify payload this module emits. The
// host raw-message limits remain authoritative; this reserve does not model any
// undocumented WebView bridge escaping.
export const RENDERED_FIND_PRODUCER_MESSAGE_LIMIT_NUMERATOR = 9;
export const RENDERED_FIND_PRODUCER_MESSAGE_LIMIT_DENOMINATOR = 10;
export const RENDERED_FIND_MAX_PACKED_CHUNK_MESSAGE_UTF8_BYTES = Math.floor(
  RENDERED_FIND_MAX_MESSAGE_UTF8_BYTES
  * RENDERED_FIND_PRODUCER_MESSAGE_LIMIT_NUMERATOR
  / RENDERED_FIND_PRODUCER_MESSAGE_LIMIT_DENOMINATOR
);
export const RENDERED_FIND_MAX_PACKED_CHUNK_MESSAGE_CODE_UNITS = Math.floor(
  RENDERED_FIND_MAX_MESSAGE_CODE_UNITS
  * RENDERED_FIND_PRODUCER_MESSAGE_LIMIT_NUMERATOR
  / RENDERED_FIND_PRODUCER_MESSAGE_LIMIT_DENOMINATOR
);

export type RenderedFindTextSegment = {
  segmentOrdinal: number;
  blockIndex: number;
  blockLocalStart: number;
  segmentCodeUnitLength: number;
  text: string;
};

export type RenderedFindDomainBeginMessage = {
  type: "find-domain-begin";
  schemaVersion: 1;
  textDomain: "rendered-dom-v1";
  renderId: number;
};

export type RenderedFindTextPart = {
  segmentOrdinal: number;
  blockIndex: number;
  blockLocalStart: number;
  segmentCodeUnitLength: number;
  partOffset: number;
  text: string;
};

export type RenderedFindTextIndexStartMessage = {
  type: "find-text-index-start";
  schemaVersion: 1;
  textDomain: "rendered-dom-v1";
  renderId: number;
  projectionRevision: number;
  transferId: string;
  semanticSegmentCount: number;
  totalCodeUnits: number;
  chunkCount: number;
  partCount: number;
};

export type RenderedFindTextIndexChunkMessage = {
  type: "find-text-index-chunk";
  schemaVersion: 1;
  textDomain: "rendered-dom-v1";
  renderId: number;
  projectionRevision: number;
  transferId: string;
  chunkIndex: number;
  parts: RenderedFindTextPart[];
};

export type RenderedFindTextIndexCompleteMessage = {
  type: "find-text-index-complete";
  schemaVersion: 1;
  textDomain: "rendered-dom-v1";
  renderId: number;
  projectionRevision: number;
  transferId: string;
  semanticSegmentCount: number;
  totalCodeUnits: number;
  chunkCount: number;
  partCount: number;
};

export type RenderedFindUnavailableReason =
  | "lease-unavailable"
  | "projection-build-failed"
  | "rendered-content-unavailable"
  | "retry-exhausted";

export type RenderedFindUnavailableMessage = {
  type: "rendered-find-unavailable";
  schemaVersion: 1;
  textDomain: "rendered-dom-v1";
  renderId: number;
  reason: RenderedFindUnavailableReason;
};

export type RenderedFindTransferMessage =
  | RenderedFindTextIndexStartMessage
  | RenderedFindTextIndexChunkMessage
  | RenderedFindTextIndexCompleteMessage;

export type RenderedFindTransferCheckpoint = "before-slice" | "after-yield" | "before-post";

export type RenderedFindTransferOptions = {
  renderId: number;
  projectionRevision: number;
  emit: (message: RenderedFindTransferMessage) => void;
  shouldCancel: (checkpoint: RenderedFindTransferCheckpoint) => boolean;
  yieldControl: () => Promise<void>;
  now?: () => number;
};

export type RenderedFindTransferResult = "complete" | "cancelled" | "unavailable";

export type RenderedFindProjectionOptions = {
  shouldCancel?: (checkpoint: VisibleTextTraversalCheckpoint) => boolean;
  yieldControl?: () => Promise<void>;
  now?: () => number;
  onSectionProjected?: (event: RenderedFindProjectionSectionEvent) => void;
};

export type RenderedFindProjectionResult =
  | { status: "complete"; segments: RenderedFindTextSegment[] }
  | { status: "cancelled"; segments: [] };

export type RenderedFindProjectionSectionRoots = {
  sectionCount: number;
  createRoot: (sectionIndex: number) => Node | null;
};

export type RenderedFindProjectionSectionEvent = {
  sectionIndex: number;
  durationMs: number;
  overranSliceBudget: boolean;
};

export type PublishRenderedFindProjectionOptions = {
  emit: (message: RenderedFindTransferMessage) => void;
  projectionRevision: number;
  readiness: Promise<"not-needed" | "ready" | "ready-with-failures" | "cancelled" | "unavailable" | "unprepared">;
  renderId: number;
  root?: () => Node;
  sectionRoots?: RenderedFindProjectionSectionRoots;
  shouldCancel: () => boolean;
  yieldControl: () => Promise<void>;
  now?: () => number;
  onSectionProjected?: (event: RenderedFindProjectionSectionEvent) => void;
};

export async function publishRenderedFindProjection(
  options: PublishRenderedFindProjectionOptions
): Promise<RenderedFindTransferResult> {
  if (options.shouldCancel()) {
    return "cancelled";
  }

  const readiness = await options.readiness;
  if (options.shouldCancel() || readiness === "cancelled") {
    return "cancelled";
  }
  if (readiness !== "not-needed" && readiness !== "ready" && readiness !== "ready-with-failures") {
    return "unavailable";
  }

  await options.yieldControl();
  if (options.shouldCancel()) {
    return "cancelled";
  }

  const projectionOptions: RenderedFindProjectionOptions = {
    shouldCancel: () => options.shouldCancel(),
    yieldControl: options.yieldControl,
  };
  if (options.onSectionProjected !== undefined) {
    projectionOptions.onSectionProjected = options.onSectionProjected;
  }
  if (options.now !== undefined) {
    projectionOptions.now = options.now;
  }
  const projection = options.sectionRoots !== undefined
    ? await createRenderedFindProjectionFromSectionRoots(options.sectionRoots, projectionOptions)
    : await createRenderedFindProjection(readProjectionRoot(options), projectionOptions);
  if (projection.status === "cancelled" || options.shouldCancel()) {
    return "cancelled";
  }

  const transferOptions: RenderedFindTransferOptions = {
    emit: message => { options.emit(message); },
    projectionRevision: options.projectionRevision,
    renderId: options.renderId,
    shouldCancel: () => options.shouldCancel(),
    yieldControl: options.yieldControl,
  };
  if (options.now !== undefined) {
    transferOptions.now = options.now;
  }
  return emitRenderedFindProjectionTransfer(projection.segments, transferOptions);
}

function readProjectionRoot(options: PublishRenderedFindProjectionOptions): Node {
  if (options.root === undefined) {
    throw new Error("rendered find projection root is unavailable");
  }
  return options.root();
}

export async function createRenderedFindProjection(
  root: Node,
  options: RenderedFindProjectionOptions = {}
): Promise<RenderedFindProjectionResult> {
  const collector = createRenderedFindSegmentCollector();
  const result = await appendRenderedFindProjectionRoot(root, options, collector);
  if (result === "cancelled") {
    return { segments: [], status: "cancelled" };
  }

  return { segments: collector.segments, status: "complete" };
}

export async function createRenderedFindProjectionFromSectionRoots(
  sections: RenderedFindProjectionSectionRoots,
  options: RenderedFindProjectionOptions = {}
): Promise<RenderedFindProjectionResult> {
  const collector = createRenderedFindSegmentCollector();
  const now = options.now ?? (() => performance.now());
  const shouldCancel = options.shouldCancel ?? (() => false);
  const yieldControl = options.yieldControl ?? (async () => {});
  const sectionCount = Math.max(0, Math.floor(sections.sectionCount));
  let sliceActive = false;
  let sliceStart = 0;

  const beginOrContinueSectionSlice = async (): Promise<boolean> => {
    if (!sliceActive) {
      if (shouldCancel("before-work")) {
        return true;
      }
      sliceStart = now();
      sliceActive = true;
      return false;
    }
    if (now() - sliceStart < RENDERED_FIND_PRODUCER_SLICE_BUDGET_MS) {
      return false;
    }

    await yieldControl();
    if (shouldCancel("after-yield")) {
      return true;
    }
    if (shouldCancel("before-work")) {
      return true;
    }
    sliceStart = now();
    return false;
  };

  for (let sectionIndex = 0; sectionIndex < sectionCount; sectionIndex++) {
    if (await beginOrContinueSectionSlice()) {
      return { segments: [], status: "cancelled" };
    }

    const sectionStart = now();
    const root = sections.createRoot(sectionIndex);
    if (root !== null) {
      const result = await appendRenderedFindProjectionRoot(root, options, collector);
      if (result === "cancelled") {
        return { segments: [], status: "cancelled" };
      }
    }
    const durationMs = Math.max(0, now() - sectionStart);
    options.onSectionProjected?.({
      durationMs,
      overranSliceBudget: durationMs > RENDERED_FIND_PRODUCER_SLICE_BUDGET_MS,
      sectionIndex,
    });
  }

  return { segments: collector.segments, status: "complete" };
}

type RenderedFindSegmentCollector = {
  segments: RenderedFindTextSegment[];
  blockLengths: Map<number, number>;
};

function createRenderedFindSegmentCollector(): RenderedFindSegmentCollector {
  return {
    blockLengths: new Map<number, number>(),
    segments: [],
  };
}

async function appendRenderedFindProjectionRoot(
  root: Node,
  options: RenderedFindProjectionOptions,
  collector: RenderedFindSegmentCollector
): Promise<"complete" | "cancelled"> {
  const walkOptions = {
    shouldCancel: options.shouldCancel ?? (() => false),
    sliceBudgetMs: RENDERED_FIND_PRODUCER_SLICE_BUDGET_MS,
    yieldControl: options.yieldControl ?? (async () => {}),
  };

  const result = await walkVisibleTextNodesSliced(root, options.now === undefined
    ? walkOptions
    : { ...walkOptions, now: options.now }, node => {
    const text = node.nodeValue ?? "";
    if (text.length === 0) {
      return;
    }

    const block = node.parentElement?.closest<HTMLElement>("[data-mm-block-index]");
    if (block === null || block === undefined) {
      return;
    }

    const blockIndexText = block.dataset.mmBlockIndex;
    if (blockIndexText === undefined) {
      return;
    }
    const blockIndex = Number.parseInt(blockIndexText, 10);
    if (!Number.isSafeInteger(blockIndex) || blockIndex < 0) {
      return;
    }

    const blockLocalStart = collector.blockLengths.get(blockIndex) ?? 0;
    collector.segments.push({
      blockIndex,
      blockLocalStart,
      segmentCodeUnitLength: text.length,
      segmentOrdinal: collector.segments.length,
      text,
    });
    collector.blockLengths.set(blockIndex, blockLocalStart + text.length);
  });

  if (result === "cancelled") {
    return "cancelled";
  }

  return "complete";
}

export function createRenderedFindDomainBeginMessage(input: { renderId: number }): RenderedFindDomainBeginMessage {
  return {
    renderId: input.renderId,
    schemaVersion: RENDERED_FIND_SCHEMA_VERSION,
    textDomain: RENDERED_FIND_TEXT_DOMAIN,
    type: "find-domain-begin",
  };
}

export function createRenderedFindUnavailableMessage(input: {
  renderId: number;
  reason: RenderedFindUnavailableReason;
}): RenderedFindUnavailableMessage {
  return {
    reason: input.reason,
    renderId: input.renderId,
    schemaVersion: RENDERED_FIND_SCHEMA_VERSION,
    textDomain: RENDERED_FIND_TEXT_DOMAIN,
    type: "rendered-find-unavailable",
  };
}

export function createRenderedFindTextIndexChunkMessage(input: {
  renderId: number;
  projectionRevision: number;
  chunkIndex: number;
  parts: RenderedFindTextPart[];
}): RenderedFindTextIndexChunkMessage {
  return {
    chunkIndex: input.chunkIndex,
    parts: input.parts,
    projectionRevision: input.projectionRevision,
    renderId: input.renderId,
    schemaVersion: RENDERED_FIND_SCHEMA_VERSION,
    textDomain: RENDERED_FIND_TEXT_DOMAIN,
    transferId: transferId(input.renderId, input.projectionRevision),
    type: "find-text-index-chunk",
  };
}

export function measureRenderedFindMessage(message: unknown): { codeUnits: number; utf8Bytes: number } {
  const serialized = JSON.stringify(message);
  return {
    codeUnits: serialized.length,
    utf8Bytes: new TextEncoder().encode(serialized).length,
  };
}

export function assertRenderedFindMessageWithinLimits(message: unknown): void {
  const measurement = measureRenderedFindMessage(message);
  if (measurement.codeUnits > RENDERED_FIND_MAX_MESSAGE_CODE_UNITS) {
    throw new Error(`rendered find message exceeds UTF-16 limit: ${measurement.codeUnits}`);
  }
  if (measurement.utf8Bytes > RENDERED_FIND_MAX_MESSAGE_UTF8_BYTES) {
    throw new Error(`rendered find message exceeds UTF-8 limit: ${measurement.utf8Bytes}`);
  }
}

function isWithinRenderedFindPackedChunkLimit(measurement: { codeUnits: number; utf8Bytes: number }): boolean {
  return measurement.codeUnits <= RENDERED_FIND_MAX_PACKED_CHUNK_MESSAGE_CODE_UNITS
    && measurement.utf8Bytes <= RENDERED_FIND_MAX_PACKED_CHUNK_MESSAGE_UTF8_BYTES;
}

export async function emitRenderedFindProjectionTransfer(
  segments: RenderedFindTextSegment[],
  options: RenderedFindTransferOptions
): Promise<RenderedFindTransferResult> {
  const now = options.now ?? (() => performance.now());
  if (segments.length > RENDERED_FIND_MAX_SEMANTIC_SEGMENTS) {
    throw new Error(`rendered find projection exceeds semantic segment limit: ${segments.length}`);
  }
  const plan = await buildTransferPlan(segments, options, now);
  if (plan.status === "cancelled") {
    return "cancelled";
  }

  const start = createStartMessage({
    chunkCount: plan.chunks.length,
    partCount: plan.partCount,
    projectionRevision: options.projectionRevision,
    renderId: options.renderId,
    semanticSegmentCount: segments.length,
    totalCodeUnits: plan.totalCodeUnits,
  });
  const complete = createCompleteMessage({
    chunkCount: plan.chunks.length,
    partCount: plan.partCount,
    projectionRevision: options.projectionRevision,
    renderId: options.renderId,
    semanticSegmentCount: segments.length,
    totalCodeUnits: plan.totalCodeUnits,
  });
  const messages: RenderedFindTransferMessage[] = [start, ...plan.chunks, complete];

  for (const message of messages) {
    if (options.shouldCancel("before-slice")) {
      return "cancelled";
    }
    const sliceStart = now();
    if (options.shouldCancel("before-post")) {
      return "cancelled";
    }
    assertRenderedFindMessageWithinLimits(message);
    options.emit(message);
    await options.yieldControl();
    if (options.shouldCancel("after-yield")) {
      return "cancelled";
    }
  }

  return "complete";
}

type TransferPlan =
  | { status: "cancelled" }
  | {
      status: "ready";
      chunks: RenderedFindTextIndexChunkMessage[];
      partCount: number;
      totalCodeUnits: number;
    };

async function buildTransferPlan(
  segments: RenderedFindTextSegment[],
  options: RenderedFindTransferOptions,
  now: () => number
): Promise<TransferPlan> {
  const chunks: RenderedFindTextIndexChunkMessage[] = [];
  let pending: RenderedFindTextPart[] = [];
  let partCount = 0;
  let totalCodeUnits = 0;
  let sliceActive = false;
  let sliceStart = 0;

  const beginOrContinuePackingSlice = async (): Promise<boolean> => {
    if (!sliceActive) {
      if (options.shouldCancel("before-slice")) {
        return true;
      }
      sliceStart = now();
      sliceActive = true;
      return false;
    }
    if (now() - sliceStart < RENDERED_FIND_PRODUCER_SLICE_BUDGET_MS) {
      return false;
    }

    await options.yieldControl();
    if (options.shouldCancel("after-yield")) {
      return true;
    }
    if (options.shouldCancel("before-slice")) {
      return true;
    }
    sliceStart = now();
    return false;
  };

  const flush = (): void => {
    chunks.push(createRenderedFindTextIndexChunkMessage({
      chunkIndex: chunks.length,
      parts: pending,
      projectionRevision: options.projectionRevision,
      renderId: options.renderId,
    }));
    pending = [];
  };

  const appendPart = (part: RenderedFindTextPart): void => {
    const candidate = [...pending, part];
    if (candidate.length > RENDERED_FIND_MAX_CHUNK_PARTS) {
      flush();
      pending.push(part);
      return;
    }

    const candidateMessage = createRenderedFindTextIndexChunkMessage({
      chunkIndex: chunks.length,
      parts: candidate,
      projectionRevision: options.projectionRevision,
      renderId: options.renderId,
    });
    const measurement = measureRenderedFindMessage(candidateMessage);
    if (
      pending.length > 0
      && (
        !isWithinRenderedFindPackedChunkLimit(measurement)
      )
    ) {
      flush();
      appendPart(part);
      return;
    }
    if (
      pending.length === 0
      && (
        !isWithinRenderedFindPackedChunkLimit(measurement)
      )
    ) {
      throw new Error("rendered find text part cannot fit within one message");
    }

    pending = candidate;
  };

  const findPackedTextPartEnd = (
    text: string,
    offset: number,
    segment: RenderedFindTextSegment
  ): number => {
    const fitsPartEndingAt = (end: number): boolean => {
      const part: RenderedFindTextPart = {
        blockIndex: segment.blockIndex,
        blockLocalStart: segment.blockLocalStart,
        partOffset: offset,
        segmentCodeUnitLength: segment.segmentCodeUnitLength,
        segmentOrdinal: segment.segmentOrdinal,
        text: text.slice(offset, end),
      };
      const message = createRenderedFindTextIndexChunkMessage({
        chunkIndex: RENDERED_FIND_MAX_TRANSFER_PARTS,
        parts: [part],
        projectionRevision: options.projectionRevision,
        renderId: options.renderId,
      });

      return isWithinRenderedFindPackedChunkLimit(measureRenderedFindMessage(message));
    };

    let low = offset + 1;
    let high = Math.min(text.length, offset + RENDERED_FIND_MAX_TEXT_PART_CODE_UNITS);
    if (fitsPartEndingAt(high)) {
      return high;
    }

    let best = offset;

    while (low <= high) {
      const mid = low + Math.floor((high - low) / 2);
      if (fitsPartEndingAt(mid)) {
        best = mid;
        low = mid + 1;
      } else {
        high = mid - 1;
      }
    }

    if (best === offset) {
      throw new Error("rendered find text part cannot fit within one message");
    }
    return best;
  };

  for (const segment of segments) {
    if (await beginOrContinuePackingSlice()) {
      return { status: "cancelled" };
    }
    const text = segment.text;
    const segmentLength = segment.segmentCodeUnitLength;
    totalCodeUnits += segmentLength;
    if (totalCodeUnits > RENDERED_FIND_MAX_PROJECTION_CODE_UNITS) {
      throw new Error(`rendered find projection exceeds total UTF-16 limit: ${totalCodeUnits}`);
    }

    let offset = 0;
    while (offset < text.length) {
      if (await beginOrContinuePackingSlice()) {
        return { status: "cancelled" };
      }
      const partEnd = findPackedTextPartEnd(text, offset, segment);
      appendPart({
        blockIndex: segment.blockIndex,
        blockLocalStart: segment.blockLocalStart,
        partOffset: offset,
        segmentCodeUnitLength: segmentLength,
        segmentOrdinal: segment.segmentOrdinal,
        text: text.slice(offset, partEnd),
      });
      offset = partEnd;
      partCount++;
      if (partCount > RENDERED_FIND_MAX_TRANSFER_PARTS) {
        throw new Error(`rendered find projection exceeds transfer part limit: ${partCount}`);
      }
    }
  }

  if (pending.length > 0) {
    flush();
  }

  return {
    chunks,
    partCount,
    status: "ready",
    totalCodeUnits,
  };
}

function createStartMessage(input: {
  renderId: number;
  projectionRevision: number;
  semanticSegmentCount: number;
  totalCodeUnits: number;
  chunkCount: number;
  partCount: number;
}): RenderedFindTextIndexStartMessage {
  return {
    chunkCount: input.chunkCount,
    partCount: input.partCount,
    projectionRevision: input.projectionRevision,
    renderId: input.renderId,
    schemaVersion: RENDERED_FIND_SCHEMA_VERSION,
    semanticSegmentCount: input.semanticSegmentCount,
    textDomain: RENDERED_FIND_TEXT_DOMAIN,
    totalCodeUnits: input.totalCodeUnits,
    transferId: transferId(input.renderId, input.projectionRevision),
    type: "find-text-index-start",
  };
}

function createCompleteMessage(input: {
  renderId: number;
  projectionRevision: number;
  semanticSegmentCount: number;
  totalCodeUnits: number;
  chunkCount: number;
  partCount: number;
}): RenderedFindTextIndexCompleteMessage {
  return {
    chunkCount: input.chunkCount,
    partCount: input.partCount,
    projectionRevision: input.projectionRevision,
    renderId: input.renderId,
    schemaVersion: RENDERED_FIND_SCHEMA_VERSION,
    semanticSegmentCount: input.semanticSegmentCount,
    textDomain: RENDERED_FIND_TEXT_DOMAIN,
    totalCodeUnits: input.totalCodeUnits,
    transferId: transferId(input.renderId, input.projectionRevision),
    type: "find-text-index-complete",
  };
}

function transferId(renderId: number, projectionRevision: number): string {
  return `${renderId}:${projectionRevision}`;
}

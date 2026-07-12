import type { ActiveHeldOperationMode } from "./heldOperationScrollPolicy";

export const GEOMETRY_SETTLED_EVENT = "mm-virt-geometry-settled" as const;

export const SCROLL_OWNERSHIP_TRACE_IDS = {
  geometryMutated: "mm-virt-geometry-mutated",
  geometrySettled: GEOMETRY_SETTLED_EVENT,
  geometryWorkEnd: "mm-virt-geometry-work-end",
  geometryWorkStart: "mm-virt-geometry-work-start",
  frameTransactionRejected: "mm-virt-scroll-frame-transaction-rejected",
  leaseAcquired: "mm-virt-scroll-lease-acquired",
  leaseReleased: "mm-virt-scroll-lease-released",
  leaseSuperseded: "mm-virt-scroll-lease-superseded",
  observerDeliveryFailed: "mm-virt-observer-delivery-failed",
  retiredEchoQuarantined: "mm-virt-scroll-retired-echo-quarantined",
  settleTimeout: "mm-virt-geometry-settle-timeout",
  staleLease: "mm-virt-stale-callback-dropped",
  staleTicket: "mm-virt-stale-callback-dropped",
  unattributedMovement: "mm-virt-scroll-unattributed-movement",
  watchdogPaused: "mm-virt-geometry-watchdog-paused",
  watchdogResumed: "mm-virt-geometry-watchdog-resumed",
  writeCommitted: "mm-virt-scroll-write-committed",
  writeRejected: "mm-virt-scroll-write-rejected",
  writeRequest: "mm-virt-scroll-write-request",
} as const;

export type GeometrySettledPayload = {
  documentEpoch: number;
  geometryEpoch: number;
};

export type ScrollAcquirePolicy = "defer" | "supersede-programmatic" | "supersede-as-user";

const LEASE_BRAND = Symbol("mm-virt-scroll-lease");
const GEOMETRY_TICKET_BRAND = Symbol("mm-virt-geometry-ticket");

export type ScrollLease = Readonly<{
  documentEpoch: number;
  geometryEpoch: number;
  operationEpoch: number;
  owner: string;
  [LEASE_BRAND]: true;
}>;

export type GeometryWorkTicket = Readonly<{
  documentEpoch: number;
  id: number;
  mountGeneration: number | null;
  source: string;
  [GEOMETRY_TICKET_BRAND]: true;
}>;

export type DeferredLeaseOutcome =
  | { status: "acquired"; lease: ScrollLease }
  | { status: "canceled"; reason: ScrollCancellationReason };

export type LeaseAcquisition =
  | { status: "acquired"; lease: ScrollLease }
  | { status: "deferred"; ready: Promise<DeferredLeaseOutcome> };

export type MaintenanceLeaseAcquisition = {
  lease: ScrollLease;
  ownsLease: boolean;
};

export type ScrollWriteRequest = {
  composition?: "held-operation-target";
  target: number;
  writer: string;
};

export type ScrollWriteOutcome =
  | { status: "committed"; value: number }
  | { status: "rejected"; reason: ScrollWriteRejectionReason };

export type ScrollWriteReceipt = {
  afterEmission: number;
  documentEpoch: number;
  operationEpoch: number;
  result: Promise<ScrollWriteOutcome>;
};

export type GeometrySettledWaitOutcome =
  | {
    emission: number;
    payload: GeometrySettledPayload;
    status: "settled";
  }
  | {
    reason: GeometryWaitCancellationReason;
    status: "canceled";
  };

export type NativeScrollClassification =
  | { kind: "self-echo"; expected: number; value: number }
  | { kind: "stale-self-echo"; expected: number; value: number }
  | { kind: "gesture-owned"; operationEpoch: number; value: number }
  | { evidence?: HeldGestureEvidence; kind: "user-supersession"; value: number }
  | { kind: "unattributed-failure"; expected: number | null; value: number };

export type HeldGestureEvidence = Readonly<{
  kind: string;
  sequence: number;
}>;

export type ScrollOwnershipTraceEvent = {
  documentEpoch: number;
  frame: number;
  geometryEpoch: number;
  id: string;
  operationEpoch: number;
  details?: Readonly<Record<string, boolean | number | string | null>>;
};

export type ScrollRootPort = {
  clientHeight: number;
  scrollHeight: number;
  scrollTop: number;
};

export type ScrollOwnershipControlPlaneDeps = {
  cancelFrame: (handle: number) => void;
  deliveredFrameBudget?: number;
  emitGeometrySettled: (payload: GeometrySettledPayload) => void;
  hasRecentUserInput: (withinMs: number) => boolean;
  prepareGeometrySettleCandidate?: () => boolean;
  readHeldGestureEvidence?: (lease: ScrollLease) => HeldGestureEvidence | null;
  readHeldOperationMode?: (lease: ScrollLease) => ActiveHeldOperationMode | null;
  requestFrame: (callback: FrameRequestCallback) => number;
  root: ScrollRootPort;
  trace?: (event: ScrollOwnershipTraceEvent) => void;
};

export type ScrollOwnershipControlPlane = {
  acquire: (owner: string, policy: ScrollAcquirePolicy) => LeaseAcquisition;
  beginGeometryWork: (
    source: string,
    documentEpoch?: number,
    mountGeneration?: number
  ) => GeometryWorkTicket | null;
  captureDocumentEpoch: () => number;
  captureGeometryEpoch: () => number;
  classifyNativeScroll: (value: number, source?: string) => NativeScrollClassification;
  dispose: () => void;
  endGeometryWork: (ticket: GeometryWorkTicket) => boolean;
  geometryMutated: (ticket: GeometryWorkTicket) => boolean;
  holds: (lease: ScrollLease, expectedGeometryEpoch?: number) => boolean;
  invalidateDocument: () => void;
  isCurrentDocumentEpoch: (epoch: number) => boolean;
  joinMaintenance: (owner: string) => MaintenanceLeaseAcquisition | null;
  release: (lease: ScrollLease) => boolean;
  scheduleFrameTransaction: (lease: ScrollLease, work: () => void) => boolean;
  supersedeByUser: (source: string) => void;
  waitForGeometrySettled: (
    documentEpoch: number,
    afterEmission?: number
  ) => Promise<GeometrySettledWaitOutcome>;
  write: (lease: ScrollLease, request: ScrollWriteRequest) => ScrollWriteReceipt;
};

type ScrollCancellationReason =
  | "coalesced"
  | "disposed"
  | "document-invalidated"
  | "non-converged"
  | "programmatic-supersession"
  | "user-supersession";

type ScrollWriteRejectionReason =
  | "coalesced"
  | "disposed"
  | "document-invalidated"
  | "non-converged"
  | "non-finite-root-range"
  | "non-finite-target"
  | "programmatic-supersession"
  | "released"
  | "root-write-failed"
  | "stale-lease"
  | "user-supersession";

type GeometryWaitCancellationReason =
  | "disposed"
  | "document-invalidated"
  | "invalid-after-emission"
  | "non-converged"
  | "stale-document"
  | "programmatic-supersession"
  | "user-supersession";

type PendingWrite = {
  requestedTarget: number;
  lease: ScrollLease;
  resolve: (outcome: ScrollWriteOutcome) => void;
  supersessionSource: string | null;
  writer: string;
};

type DeferredAcquisition = {
  owner: string;
  resolve: (outcome: DeferredLeaseOutcome) => void;
};

type FrameTransaction = {
  heldTargetWrite: PendingWrite | null;
  lease: ScrollLease;
  works: Array<() => void>;
};

type ExpectedEcho = {
  lease: ScrollLease;
  value: number;
};

type RetiredEcho = {
  documentEpoch: number;
  expiresAfterFrame: number;
  operationEpoch: number;
  value: number;
};

type GeometryWaiter = {
  afterEmission: number;
  documentEpoch: number;
  operationEpoch: number;
  resolve: (outcome: GeometrySettledWaitOutcome) => void;
};

type QuietCandidate = {
  documentEpoch: number;
  geometryEpoch: number;
  revision: number;
  stableFrames: number;
};

const DEFAULT_DELIVERED_FRAME_BUDGET = 120;
const MAX_RETIRED_ECHOES = 4;
const RETIRED_ECHO_DELIVERED_FRAME_TTL = 2;
const RECENT_USER_INPUT_WINDOW_MS = 250;
const SELF_ECHO_TOLERANCE_PX = 0.5;

export function createScrollOwnershipControlPlane(
  deps: ScrollOwnershipControlPlaneDeps
): ScrollOwnershipControlPlane {
  const deliveredFrameBudget = readDeliveredFrameBudget(deps.deliveredFrameBudget);
  let activeLease: ScrollLease | null = null;
  let activeSupersessionSource: string | null = null;
  let deferredAcquisition: DeferredAcquisition | null = null;
  let disposed = false;
  let documentEpoch = 1;
  let expectedEcho: ExpectedEcho | null = null;
  let frameSerial = 0;
  let frameTransaction: FrameTransaction | null = null;
  let geometryEpoch = 0;
  let lastEmittedPayload: GeometrySettledPayload | null = null;
  let lastEmittedRevision = 0;
  let nextGeometryTicketId = 1;
  let operationEpoch = 0;
  let pendingWrite: PendingWrite | null = null;
  let pendingTraceFailures = 0;
  let previousFiniteNativeScrollValue: number | null = null;
  let quietCandidate: QuietCandidate | null = null;
  let retiredEchoes: RetiredEcho[] = [];
  let scheduledFrame: number | null = null;
  let settleEmission = 0;
  let settleRevision = 0;
  let watchdogDeliveredFrames = 0;
  const geometryTickets = new Map<number, GeometryWorkTicket>();
  const waiters = new Set<GeometryWaiter>();

  const createTraceEvent = (
    id: string,
    details?: Readonly<Record<string, boolean | number | string | null>>
  ): ScrollOwnershipTraceEvent => {
    const event: ScrollOwnershipTraceEvent = {
      documentEpoch,
      frame: frameSerial,
      geometryEpoch,
      id,
      operationEpoch,
    };
    if (details !== undefined) {
      event.details = details;
    }
    return event;
  };

  const deliverTrace = (event: ScrollOwnershipTraceEvent): boolean => {
    if (deps.trace === undefined) {
      return true;
    }
    try {
      deps.trace(event);
      return true;
    } catch {
      return false;
    }
  };

  const trace = (
    id: string,
    details?: Readonly<Record<string, boolean | number | string | null>>
  ): void => {
    if (deps.trace === undefined) {
      return;
    }
    if (pendingTraceFailures > 0 && id !== SCROLL_OWNERSHIP_TRACE_IDS.observerDeliveryFailed) {
      const failures = pendingTraceFailures;
      pendingTraceFailures = 0;
      if (!deliverTrace(createTraceEvent(SCROLL_OWNERSHIP_TRACE_IDS.observerDeliveryFailed, {
        channel: "trace",
        failures,
      }))) {
        pendingTraceFailures = failures + 1;
      }
    }
    if (!deliverTrace(createTraceEvent(id, details))) {
      pendingTraceFailures++;
    }
  };

  const invalidateSettleCandidate = (): void => {
    settleRevision++;
    quietCandidate = null;
  };

  const pruneRetiredEchoes = (): void => {
    retiredEchoes = retiredEchoes.filter(echo =>
      echo.documentEpoch === documentEpoch && echo.expiresAfterFrame >= frameSerial);
  };

  const retireEcho = (echo: ExpectedEcho): void => {
    if (!Number.isFinite(echo.value)) {
      return;
    }
    pruneRetiredEchoes();
    retiredEchoes.push({
      documentEpoch,
      expiresAfterFrame: frameSerial + RETIRED_ECHO_DELIVERED_FRAME_TTL,
      operationEpoch: echo.lease.operationEpoch,
      value: echo.value,
    });
    if (retiredEchoes.length > MAX_RETIRED_ECHOES) {
      retiredEchoes.splice(0, retiredEchoes.length - MAX_RETIRED_ECHOES);
    }
    trace(SCROLL_OWNERSHIP_TRACE_IDS.retiredEchoQuarantined, {
      retiredOperationEpoch: echo.lease.operationEpoch,
      value: echo.value,
    });
  };

  const consumeRetiredEcho = (value: number): RetiredEcho | null => {
    if (!Number.isFinite(value)) {
      return null;
    }
    pruneRetiredEchoes();
    for (let index = retiredEchoes.length - 1; index >= 0; index--) {
      const echo = retiredEchoes[index]!;
      if (Math.abs(value - echo.value) <= SELF_ECHO_TOLERANCE_PX) {
        retiredEchoes.splice(index, 1);
        return echo;
      }
    }
    return null;
  };

  const holds = (lease: ScrollLease, expectedGeometryEpoch?: number): boolean => {
    if (
      disposed
      || activeLease !== lease
      || lease.documentEpoch !== documentEpoch
      || lease.operationEpoch !== operationEpoch
    ) {
      return false;
    }
    return expectedGeometryEpoch === undefined
      || (Number.isFinite(expectedGeometryEpoch) && expectedGeometryEpoch === geometryEpoch);
  };

  const traceWrite = (
    id: string,
    writer: string,
    before: number | null,
    after: number | null,
    supersessionSource: string | null,
    reason?: string
  ): void => {
    const details: Record<string, boolean | number | string | null> = {
      after,
      before,
      supersessionSource,
      writer,
    };
    if (reason !== undefined) {
      details["reason"] = reason;
    }
    trace(id, details);
  };

  const rejectWrite = (pending: PendingWrite, reason: ScrollWriteRejectionReason): void => {
    pending.resolve({ reason, status: "rejected" });
    traceWrite(
      SCROLL_OWNERSHIP_TRACE_IDS.writeRejected,
      pending.writer,
      finiteOrNull(deps.root.scrollTop),
      null,
      pending.supersessionSource,
      reason
    );
  };

  const rejectPendingWrite = (reason: ScrollWriteRejectionReason): void => {
    const pending = pendingWrite;
    pendingWrite = null;
    if (pending !== null) {
      rejectWrite(pending, reason);
    }
  };

  const rejectFrameTargetWrite = (
    transaction: FrameTransaction,
    reason: ScrollWriteRejectionReason
  ): void => {
    const target = transaction.heldTargetWrite;
    transaction.heldTargetWrite = null;
    if (target !== null) {
      rejectWrite(target, reason);
    }
  };

  const cancelWaiters = (
    reason: GeometryWaitCancellationReason,
    predicate: (waiter: GeometryWaiter) => boolean = () => true
  ): void => {
    for (const waiter of [...waiters]) {
      if (!predicate(waiter)) {
        continue;
      }
      waiters.delete(waiter);
      waiter.resolve({ reason, status: "canceled" });
    }
  };

  const cancelDeferred = (reason: ScrollCancellationReason): void => {
    const deferred = deferredAcquisition;
    deferredAcquisition = null;
    deferred?.resolve({ reason, status: "canceled" });
  };

  const cancelDeferredForOperation = (reason: GeometryWaitCancellationReason): void => {
    switch (reason) {
      case "disposed":
      case "document-invalidated":
      case "non-converged":
      case "programmatic-supersession":
      case "user-supersession":
        cancelDeferred(reason);
        break;
      case "invalid-after-emission":
      case "stale-document":
        break;
    }
  };

  const clearActiveOperation = (
    reason: ScrollWriteRejectionReason,
    waiterReason: GeometryWaitCancellationReason,
    supersessionSource: string
  ): ScrollLease | null => {
    const previous = activeLease;
    if (previous === null) {
      return null;
    }
    activeLease = null;
    activeSupersessionSource = null;
    if (pendingWrite?.lease === previous) {
      rejectPendingWrite(reason);
    }
    if (frameTransaction?.lease === previous) {
      rejectFrameTargetWrite(frameTransaction, reason);
      frameTransaction = null;
    }
    if (expectedEcho?.lease === previous) {
      retireEcho(expectedEcho);
      expectedEcho = null;
    }
    cancelWaiters(waiterReason, waiter => waiter.operationEpoch === previous.operationEpoch);
    cancelDeferredForOperation(waiterReason);
    trace(SCROLL_OWNERSHIP_TRACE_IDS.leaseSuperseded, {
      owner: previous.owner,
      supersessionSource,
    });
    invalidateSettleCandidate();
    return previous;
  };

  const createLease = (owner: string, supersessionSource: string | null = null): ScrollLease => {
    operationEpoch++;
    watchdogDeliveredFrames = 0;
    const lease: ScrollLease = Object.freeze({
      [LEASE_BRAND]: true as const,
      documentEpoch,
      geometryEpoch,
      operationEpoch,
      owner,
    });
    activeLease = lease;
    activeSupersessionSource = supersessionSource;
    trace(SCROLL_OWNERSHIP_TRACE_IDS.leaseAcquired, { owner });
    return lease;
  };

  const drainDeferredAcquisition = (): void => {
    if (disposed || activeLease !== null || deferredAcquisition === null) {
      return;
    }
    const deferred = deferredAcquisition;
    deferredAcquisition = null;
    deferred.resolve({
      lease: createLease(deferred.owner, "deferred-maintenance"),
      status: "acquired",
    });
  };

  const hasSettlementBlocker = (): boolean => {
    if (
      geometryTickets.size > 0
      || pendingWrite !== null
      || frameTransaction !== null
      || expectedEcho !== null
    ) {
      return true;
    }
    if (deps.prepareGeometrySettleCandidate === undefined) {
      return false;
    }
    try {
      return deps.prepareGeometrySettleCandidate() !== true;
    } catch {
      trace(SCROLL_OWNERSHIP_TRACE_IDS.observerDeliveryFailed, {
        channel: "geometry-settle-census",
        failures: 1,
      });
      return true;
    }
  };

  const needsSettlementProgress = (): boolean =>
    hasSettlementBlocker()
    || waiters.size > 0
    || settleRevision > lastEmittedRevision;

  const ensureFrame = (): void => {
    if (disposed || scheduledFrame !== null || !needsSettlementProgress()) {
      return;
    }
    trace(SCROLL_OWNERSHIP_TRACE_IDS.watchdogPaused, { reason: "awaiting-delivered-frame" });
    scheduledFrame = deps.requestFrame(deliverFrame);
  };

  const emitSettled = (): boolean => {
    if (hasSettlementBlocker()) {
      quietCandidate = null;
      return false;
    }
    const candidateMatches = quietCandidate !== null
      && quietCandidate.documentEpoch === documentEpoch
      && quietCandidate.geometryEpoch === geometryEpoch
      && quietCandidate.revision === settleRevision;
    if (candidateMatches && quietCandidate !== null) {
      quietCandidate = { ...quietCandidate, stableFrames: quietCandidate.stableFrames + 1 };
    } else {
      quietCandidate = { documentEpoch, geometryEpoch, revision: settleRevision, stableFrames: 1 };
    }
    if (quietCandidate.stableFrames < 2) {
      return false;
    }

    const payload: GeometrySettledPayload = { documentEpoch, geometryEpoch };
    settleEmission++;
    lastEmittedPayload = payload;
    lastEmittedRevision = settleRevision;
    quietCandidate = null;
    watchdogDeliveredFrames = 0;
    for (const waiter of [...waiters]) {
      if (waiter.documentEpoch !== documentEpoch || settleEmission <= waiter.afterEmission) {
        continue;
      }
      waiters.delete(waiter);
      waiter.resolve({ emission: settleEmission, payload, status: "settled" });
    }
    try {
      deps.emitGeometrySettled(payload);
    } catch {
      trace(SCROLL_OWNERSHIP_TRACE_IDS.observerDeliveryFailed, {
        channel: "geometry-settled-emitter",
        failures: 1,
      });
    }
    trace(SCROLL_OWNERSHIP_TRACE_IDS.geometrySettled);
    return true;
  };

  const failNonConvergence = (): void => {
    trace(SCROLL_OWNERSHIP_TRACE_IDS.settleTimeout, {
      deliveredFrames: watchdogDeliveredFrames,
      pendingGeometryWork: geometryTickets.size,
    });
    clearActiveOperation("non-converged", "non-converged", "geometry-settle-timeout");
    cancelDeferred("non-converged");
    cancelWaiters("non-converged");
    geometryTickets.clear();
    frameTransaction = null;
    expectedEcho = null;
    rejectPendingWrite("non-converged");
    quietCandidate = null;
    lastEmittedRevision = settleRevision;
    watchdogDeliveredFrames = 0;
  };

  const commitPendingWrite = (lease: ScrollLease): void => {
    const pending = pendingWrite;
    if (pending === null || pending.lease !== lease) {
      return;
    }
    pendingWrite = null;
    if (!holds(lease)) {
      pending.resolve({ reason: "stale-lease", status: "rejected" });
      traceWrite(
        SCROLL_OWNERSHIP_TRACE_IDS.writeRejected,
        pending.writer,
        finiteOrNull(deps.root.scrollTop),
        null,
        pending.supersessionSource,
        "stale-lease"
      );
      return;
    }
    const maxScrollTop = readMaxScrollTop(deps.root);
    if (maxScrollTop === null) {
      pending.resolve({ reason: "non-finite-root-range", status: "rejected" });
      traceWrite(
        SCROLL_OWNERSHIP_TRACE_IDS.writeRejected,
        pending.writer,
        finiteOrNull(deps.root.scrollTop),
        null,
        pending.supersessionSource,
        "non-finite-root-range"
      );
      return;
    }
    const value = clamp(pending.requestedTarget, 0, maxScrollTop);
    const before = deps.root.scrollTop;
    const expectation: ExpectedEcho = { lease, value };
    expectedEcho = expectation;
    try {
      deps.root.scrollTop = value;
    } catch {
      expectedEcho = null;
      pending.resolve({ reason: "root-write-failed", status: "rejected" });
      traceWrite(
        SCROLL_OWNERSHIP_TRACE_IDS.writeRejected,
        pending.writer,
        finiteOrNull(before),
        null,
        pending.supersessionSource,
        "root-write-failed"
      );
      clearActiveOperation("root-write-failed", "programmatic-supersession", "root-write-failed");
      return;
    }
    const actual = deps.root.scrollTop;
    if (!Number.isFinite(actual)) {
      if (expectedEcho === expectation) {
        expectedEcho = null;
      }
      pending.resolve({ reason: "root-write-failed", status: "rejected" });
      traceWrite(
        SCROLL_OWNERSHIP_TRACE_IDS.writeRejected,
        pending.writer,
        finiteOrNull(before),
        null,
        pending.supersessionSource,
        "non-finite-root-result"
      );
      clearActiveOperation("root-write-failed", "programmatic-supersession", "root-write-failed");
      return;
    }
    if (expectedEcho === expectation) {
      expectedEcho = Number.isFinite(before)
        && Math.abs(actual - before) <= SELF_ECHO_TOLERANCE_PX
        ? null
        : { lease, value: actual };
    }
    pending.resolve({ status: "committed", value: actual });
    traceWrite(
      SCROLL_OWNERSHIP_TRACE_IDS.writeCommitted,
      pending.writer,
      finiteOrNull(before),
      actual,
      pending.supersessionSource
    );
  };

  function deliverFrame(_timestamp: number): void {
    scheduledFrame = null;
    if (disposed) {
      return;
    }
    frameSerial++;
    pruneRetiredEchoes();
    trace(SCROLL_OWNERSHIP_TRACE_IDS.watchdogResumed, { reason: "frame-delivered" });

    const transaction = frameTransaction;
    frameTransaction = null;
    if (transaction !== null && holds(transaction.lease)) {
      try {
        for (const work of transaction.works) {
          work();
        }
      } catch {
        trace(SCROLL_OWNERSHIP_TRACE_IDS.frameTransactionRejected, { reason: "frame-work-failed" });
        rejectFrameTargetWrite(transaction, "programmatic-supersession");
        clearActiveOperation(
          "programmatic-supersession",
          "programmatic-supersession",
          "frame-work-failed"
        );
      }
      if (holds(transaction.lease)) {
        if (transaction.heldTargetWrite !== null) {
          rejectPendingWrite("coalesced");
          pendingWrite = transaction.heldTargetWrite;
          transaction.heldTargetWrite = null;
        }
        commitPendingWrite(transaction.lease);
      }
    }

    const emitted = emitSettled();
    if (!emitted && needsSettlementProgress()) {
      watchdogDeliveredFrames++;
      if (watchdogDeliveredFrames >= deliveredFrameBudget) {
        failNonConvergence();
      }
    }
    ensureFrame();
  }

  const acquire = (owner: string, policy: ScrollAcquirePolicy): LeaseAcquisition => {
    if (disposed) {
      return {
        ready: Promise.resolve({ reason: "disposed", status: "canceled" }),
        status: "deferred",
      };
    }
    if (activeLease === null) {
      return { lease: createLease(owner), status: "acquired" };
    }
    if (policy === "defer") {
      cancelDeferred("coalesced");
      let resolve!: (outcome: DeferredLeaseOutcome) => void;
      const ready = new Promise<DeferredLeaseOutcome>(completed => { resolve = completed; });
      deferredAcquisition = { owner, resolve };
      return { ready, status: "deferred" };
    }

    const asUser = policy === "supersede-as-user";
    clearActiveOperation(
      asUser ? "user-supersession" : "programmatic-supersession",
      asUser ? "user-supersession" : "programmatic-supersession",
      owner
    );
    cancelDeferred(asUser ? "user-supersession" : "programmatic-supersession");
    return { lease: createLease(owner, owner), status: "acquired" };
  };

  const joinMaintenance = (owner: string): MaintenanceLeaseAcquisition | null => {
    if (disposed) {
      return null;
    }
    if (activeLease !== null) {
      return { lease: activeLease, ownsLease: false };
    }
    return { lease: createLease(owner), ownsLease: true };
  };

  const release = (lease: ScrollLease): boolean => {
    if (!holds(lease)) {
      trace(SCROLL_OWNERSHIP_TRACE_IDS.staleLease, {
        capturedOperationEpoch: lease.operationEpoch,
        reason: "stale-release",
      });
      return false;
    }
    activeLease = null;
    activeSupersessionSource = null;
    if (pendingWrite?.lease === lease) {
      rejectPendingWrite("released");
    }
    if (frameTransaction?.lease === lease) {
      rejectFrameTargetWrite(frameTransaction, "released");
      frameTransaction = null;
    }
    if (expectedEcho?.lease === lease) {
      retireEcho(expectedEcho);
      expectedEcho = null;
    }
    trace(SCROLL_OWNERSHIP_TRACE_IDS.leaseReleased, { owner: lease.owner });
    drainDeferredAcquisition();
    return true;
  };

  const write = (lease: ScrollLease, request: ScrollWriteRequest): ScrollWriteReceipt => {
    const receiptBase = {
      afterEmission: settleEmission,
      documentEpoch: lease.documentEpoch,
      operationEpoch: lease.operationEpoch,
    };
    const rejected = (reason: ScrollWriteRejectionReason): ScrollWriteReceipt => {
      traceWrite(
        SCROLL_OWNERSHIP_TRACE_IDS.writeRejected,
        request.writer,
        finiteOrNull(deps.root.scrollTop),
        null,
        activeSupersessionSource,
        reason
      );
      return {
        ...receiptBase,
        result: Promise.resolve({ reason, status: "rejected" }),
      };
    };
    if (disposed) {
      return rejected("disposed");
    }
    if (!holds(lease)) {
      return rejected("stale-lease");
    }
    if (!Number.isFinite(request.target)) {
      return rejected("non-finite-target");
    }
    const maxScrollTop = readMaxScrollTop(deps.root);
    if (maxScrollTop === null) {
      return rejected("non-finite-root-range");
    }
    if (expectedEcho?.lease === lease) {
      retireEcho(expectedEcho);
      expectedEcho = null;
    }
    let resolve!: (outcome: ScrollWriteOutcome) => void;
    const result = new Promise<ScrollWriteOutcome>(completed => { resolve = completed; });
    const writeRequest: PendingWrite = {
      requestedTarget: request.target,
      lease,
      resolve,
      supersessionSource: activeSupersessionSource,
      writer: request.writer,
    };
    const heldMode = deps.readHeldOperationMode?.(lease) ?? null;
    if (request.composition === "held-operation-target" && heldMode === "gesture") {
      if (frameTransaction === null) {
        frameTransaction = {
          heldTargetWrite: writeRequest,
          lease,
          works: [],
        };
      } else if (frameTransaction.lease === lease) {
        rejectFrameTargetWrite(frameTransaction, "coalesced");
        frameTransaction.heldTargetWrite = writeRequest;
      } else {
        rejectWrite(writeRequest, "coalesced");
        return { ...receiptBase, result };
      }
      invalidateSettleCandidate();
      traceWrite(
        SCROLL_OWNERSHIP_TRACE_IDS.writeRequest,
        request.writer,
        finiteOrNull(deps.root.scrollTop),
        writeRequest.requestedTarget,
        writeRequest.supersessionSource
      );
      ensureFrame();
      return { ...receiptBase, result };
    }
    if (pendingWrite !== null) {
      rejectPendingWrite("coalesced");
    }
    pendingWrite = writeRequest;
    invalidateSettleCandidate();
    traceWrite(
      SCROLL_OWNERSHIP_TRACE_IDS.writeRequest,
      request.writer,
      finiteOrNull(deps.root.scrollTop),
      pendingWrite.requestedTarget,
      pendingWrite.supersessionSource
    );
    return { ...receiptBase, result };
  };

  const scheduleFrameTransaction = (lease: ScrollLease, work: () => void): boolean => {
    if (!holds(lease)) {
      trace(SCROLL_OWNERSHIP_TRACE_IDS.staleLease, {
        capturedOperationEpoch: lease.operationEpoch,
        reason: "stale-frame-transaction",
      });
      return false;
    }
    if (frameTransaction !== null) {
      if (
        frameTransaction.lease === lease
        && deps.readHeldOperationMode?.(lease) === "gesture"
      ) {
        frameTransaction.works.push(work);
        invalidateSettleCandidate();
        ensureFrame();
        return true;
      }
      trace(SCROLL_OWNERSHIP_TRACE_IDS.writeRejected, { reason: "frame-transaction-already-scheduled" });
      return false;
    }
    frameTransaction = { heldTargetWrite: null, lease, works: [work] };
    invalidateSettleCandidate();
    ensureFrame();
    return true;
  };

  const supersedeByUser = (source: string): void => {
    if (disposed) {
      return;
    }
    clearActiveOperation("user-supersession", "user-supersession", source);
    cancelDeferred("user-supersession");
    cancelWaiters("user-supersession");
    operationEpoch++;
    watchdogDeliveredFrames = 0;
    invalidateSettleCandidate();
  };

  const classifyNativeScroll = (
    value: number,
    source = "native-scroll"
  ): NativeScrollClassification => {
    const finite = Number.isFinite(value);
    const previousFiniteValue = previousFiniteNativeScrollValue;
    if (finite) {
      previousFiniteNativeScrollValue = value;
    }
    const expected = expectedEcho;
    const heldMode = activeLease === null
      ? null
      : deps.readHeldOperationMode?.(activeLease) ?? null;
    if (activeLease !== null && heldMode === "gesture" && finite) {
      const evidence = deps.readHeldGestureEvidence?.(activeLease) ?? null;
      if (evidence !== null) {
        supersedeByUser(`user-input:${evidence.kind}`);
        return { evidence, kind: "user-supersession", value };
      }
      if (expectedEcho?.lease === activeLease) {
        expectedEcho = null;
      }
      ensureFrame();
      return {
        kind: "gesture-owned",
        operationEpoch: activeLease.operationEpoch,
        value,
      };
    }
    if (
      expected !== null
      && finite
      && Math.abs(value - expected.value) <= SELF_ECHO_TOLERANCE_PX
    ) {
      expectedEcho = null;
      ensureFrame();
      return { expected: expected.value, kind: "self-echo", value };
    }
    const retired = consumeRetiredEcho(value);
    if (retired !== null) {
      trace(SCROLL_OWNERSHIP_TRACE_IDS.staleLease, {
        reason: "retired-self-echo",
        retiredOperationEpoch: retired.operationEpoch,
        value,
      });
      return { expected: retired.value, kind: "stale-self-echo", value };
    }
    if (expected !== null) {
      trace(SCROLL_OWNERSHIP_TRACE_IDS.unattributedMovement, {
        expected: expected.value,
        value: finite ? value : 0,
      });
      clearActiveOperation(
        "programmatic-supersession",
        "programmatic-supersession",
        "unattributed-external-movement"
      );
      cancelDeferred("programmatic-supersession");
      operationEpoch++;
      return { expected: expected.value, kind: "unattributed-failure", value };
    }
    if (!finite) {
      trace(SCROLL_OWNERSHIP_TRACE_IDS.unattributedMovement, { expected: null, value: 0 });
      clearActiveOperation(
        "programmatic-supersession",
        "programmatic-supersession",
        "non-finite-native-scroll"
      );
      cancelDeferred("programmatic-supersession");
      operationEpoch++;
      return { expected: null, kind: "unattributed-failure", value };
    }
    if (!deps.hasRecentUserInput(RECENT_USER_INPUT_WINDOW_MS)) {
      trace(SCROLL_OWNERSHIP_TRACE_IDS.unattributedMovement, {
        delta: previousFiniteValue === null ? null : value - previousFiniteValue,
        value,
      });
    }
    supersedeByUser(source);
    return { kind: "user-supersession", value };
  };

  const beginGeometryWork = (
    source: string,
    capturedDocumentEpoch = documentEpoch,
    capturedMountGeneration?: number
  ): GeometryWorkTicket | null => {
    if (disposed || !isCurrentDocumentEpoch(capturedDocumentEpoch)) {
      trace(SCROLL_OWNERSHIP_TRACE_IDS.staleTicket, {
        capturedDocumentEpoch,
        reason: "stale-geometry-start",
      });
      return null;
    }
    const ticket: GeometryWorkTicket = Object.freeze({
      [GEOMETRY_TICKET_BRAND]: true as const,
      documentEpoch: capturedDocumentEpoch,
      id: nextGeometryTicketId++,
      mountGeneration: Number.isSafeInteger(capturedMountGeneration)
        ? capturedMountGeneration!
        : null,
      source,
    });
    geometryTickets.set(ticket.id, ticket);
    invalidateSettleCandidate();
    trace(SCROLL_OWNERSHIP_TRACE_IDS.geometryWorkStart, {
      mountGeneration: ticket.mountGeneration,
      source,
      ticket: ticket.id,
    });
    ensureFrame();
    return ticket;
  };

  const readCurrentTicket = (ticket: GeometryWorkTicket): GeometryWorkTicket | null => {
    const current = geometryTickets.get(ticket.id);
    if (
      disposed
      || current !== ticket
      || ticket.documentEpoch !== documentEpoch
      || ticket[GEOMETRY_TICKET_BRAND] !== true
    ) {
      trace(SCROLL_OWNERSHIP_TRACE_IDS.staleTicket, {
        capturedDocumentEpoch: ticket.documentEpoch,
        reason: "stale-geometry-ticket",
        ticket: ticket.id,
      });
      return null;
    }
    return current;
  };

  const geometryMutated = (ticket: GeometryWorkTicket): boolean => {
    const current = readCurrentTicket(ticket);
    if (current === null) {
      return false;
    }
    geometryEpoch++;
    invalidateSettleCandidate();
    trace(SCROLL_OWNERSHIP_TRACE_IDS.geometryMutated, {
      mountGeneration: current.mountGeneration,
      source: current.source,
      ticket: current.id,
    });
    ensureFrame();
    return true;
  };

  const endGeometryWork = (ticket: GeometryWorkTicket): boolean => {
    const current = readCurrentTicket(ticket);
    if (current === null) {
      return false;
    }
    geometryTickets.delete(current.id);
    trace(SCROLL_OWNERSHIP_TRACE_IDS.geometryWorkEnd, {
      mountGeneration: current.mountGeneration,
      source: current.source,
      ticket: current.id,
    });
    ensureFrame();
    return true;
  };

  const waitForGeometrySettled = (
    capturedDocumentEpoch: number,
    afterEmission = 0
  ): Promise<GeometrySettledWaitOutcome> => {
    if (disposed) {
      return Promise.resolve({ reason: "disposed", status: "canceled" });
    }
    if (!isCurrentDocumentEpoch(capturedDocumentEpoch)) {
      return Promise.resolve({ reason: "stale-document", status: "canceled" });
    }
    if (!Number.isSafeInteger(afterEmission) || afterEmission < 0) {
      return Promise.resolve({ reason: "invalid-after-emission", status: "canceled" });
    }
    if (
      lastEmittedPayload !== null
      && lastEmittedPayload.documentEpoch === documentEpoch
      && lastEmittedRevision === settleRevision
      && settleEmission > afterEmission
      && !hasSettlementBlocker()
    ) {
      return Promise.resolve({
        emission: settleEmission,
        payload: lastEmittedPayload,
        status: "settled",
      });
    }
    if (settleRevision === lastEmittedRevision) {
      invalidateSettleCandidate();
    }
    let resolve!: (outcome: GeometrySettledWaitOutcome) => void;
    const result = new Promise<GeometrySettledWaitOutcome>(completed => { resolve = completed; });
    waiters.add({
      afterEmission,
      documentEpoch: capturedDocumentEpoch,
      operationEpoch: activeLease?.operationEpoch ?? operationEpoch,
      resolve,
    });
    ensureFrame();
    return result;
  };

  const invalidateDocument = (): void => {
    if (disposed) {
      return;
    }
    if (scheduledFrame !== null) {
      deps.cancelFrame(scheduledFrame);
      scheduledFrame = null;
    }
    clearActiveOperation(
      "document-invalidated",
      "document-invalidated",
      "document-invalidated"
    );
    cancelDeferred("document-invalidated");
    cancelWaiters("document-invalidated");
    rejectPendingWrite("document-invalidated");
    geometryTickets.clear();
    frameTransaction = null;
    expectedEcho = null;
    previousFiniteNativeScrollValue = null;
    retiredEchoes = [];
    documentEpoch++;
    geometryEpoch = 0;
    quietCandidate = null;
    settleRevision++;
    lastEmittedRevision = settleRevision;
    lastEmittedPayload = null;
    watchdogDeliveredFrames = 0;
  };

  const dispose = (): void => {
    if (disposed) {
      return;
    }
    if (scheduledFrame !== null) {
      deps.cancelFrame(scheduledFrame);
      scheduledFrame = null;
    }
    clearActiveOperation("disposed", "disposed", "disposed");
    cancelDeferred("disposed");
    cancelWaiters("disposed");
    rejectPendingWrite("disposed");
    geometryTickets.clear();
    frameTransaction = null;
    expectedEcho = null;
    previousFiniteNativeScrollValue = null;
    retiredEchoes = [];
    quietCandidate = null;
    disposed = true;
  };

  const isCurrentDocumentEpoch = (epoch: number): boolean =>
    !disposed && Number.isSafeInteger(epoch) && epoch === documentEpoch;

  return {
    acquire,
    beginGeometryWork,
    captureDocumentEpoch: () => documentEpoch,
    captureGeometryEpoch: () => geometryEpoch,
    classifyNativeScroll,
    dispose,
    endGeometryWork,
    geometryMutated,
    holds,
    invalidateDocument,
    isCurrentDocumentEpoch,
    joinMaintenance,
    release,
    scheduleFrameTransaction,
    supersedeByUser,
    waitForGeometrySettled,
    write,
  };
}

function readDeliveredFrameBudget(input: number | undefined): number {
  if (input === undefined) {
    return DEFAULT_DELIVERED_FRAME_BUDGET;
  }
  if (!Number.isSafeInteger(input) || input <= 0) {
    throw new RangeError("deliveredFrameBudget must be a positive safe integer");
  }
  return input;
}

function readMaxScrollTop(root: ScrollRootPort): number | null {
  if (
    !Number.isFinite(root.scrollHeight)
    || !Number.isFinite(root.clientHeight)
    || root.scrollHeight < 0
    || root.clientHeight < 0
  ) {
    return null;
  }
  const maxScrollTop = Math.max(0, root.scrollHeight - root.clientHeight);
  return Number.isFinite(maxScrollTop) ? maxScrollTop : null;
}

function clamp(value: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, value));
}

function finiteOrNull(value: number): number | null {
  return Number.isFinite(value) ? value : null;
}

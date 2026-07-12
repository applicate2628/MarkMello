import type { WindowTargetOperation } from "./windowTargetResolver";
import type {
  ScrollAcquirePolicy,
  ScrollLease,
  ScrollOwnershipControlPlane,
  ScrollWriteReceipt,
} from "./scrollOwnershipControlPlane";

export type VirtualizedScrollOperation = WindowTargetOperation & {
  lease: ScrollLease;
};

export type VirtualizedMaintenanceTerminal = Readonly<{
  reason: string;
  status: "canceled" | "completed" | "failed";
}>;

export type VirtualizedMaintenanceTerminalHandler =
  (terminal: VirtualizedMaintenanceTerminal) => void;

export type PendingInitialVirtualizedWindowWork =
  (operation: VirtualizedScrollOperation) => void;

export type VirtualizedScrollCoordinatorDeps = Readonly<{
  cancelFrame: (handle: number) => void;
  getPlane: () => ScrollOwnershipControlPlane | null;
  readElementDocumentTop: (element: HTMLElement) => number;
  requestFrame: (callback: FrameRequestCallback) => number;
  trace: (name: string, detail: Record<string, unknown>) => void;
}>;

export type VirtualizedScrollCoordinator = Readonly<{
  acquireOperation: (
    owner: string,
    policy: ScrollAcquirePolicy
  ) => VirtualizedScrollOperation | null;
  releaseOperation: (operation: VirtualizedScrollOperation) => boolean;
  releaseOperationAfterWrite: (operation: VirtualizedScrollOperation) => void;
  getWriteReceipt: (operation: VirtualizedScrollOperation) => ScrollWriteReceipt | undefined;
  getWriteReceiptByEpoch: (operationEpoch: number) => ScrollWriteReceipt | undefined;
  setPendingInitialWindowWork: (work: PendingInitialVirtualizedWindowWork | null) => void;
  consumePendingInitialWindow: (operation: VirtualizedScrollOperation) => boolean;
  clearPendingInitialWindow: () => void;
  clearWriteReceipts: () => void;
  runFrameTransaction: (
    request: VirtualizedFrameTransactionRequest
  ) => VirtualizedFrameTransactionResult;
  scheduleMaintenance: (
    owner: string,
    work: (operation: VirtualizedScrollOperation) => void,
    onTerminal?: VirtualizedMaintenanceTerminalHandler | null
  ) => boolean;
  cancelPendingMaintenance: (reason: string) => void;
}>;

export type VirtualizedFrameTransactionPolicy =
  | "standalone-release-after-write"
  | "existing-release-after-write"
  | "element-landing-release-after-write"
  | "empty-commit-retain-operation";

export type VirtualizedFrameTransactionRequest =
  | Readonly<{
      acquirePolicy: ScrollAcquirePolicy;
      kind: "acquire";
      owner: string;
      policy: "standalone-release-after-write";
      work: (operation: VirtualizedScrollOperation) => void;
    }>
  | Readonly<{
      kind: "existing";
      operation: VirtualizedScrollOperation;
      policy: "existing-release-after-write";
      work: () => void;
    }>
  | Readonly<{
      element: HTMLElement | null;
      kind: "element-landing";
      operation: VirtualizedScrollOperation;
      policy: "element-landing-release-after-write";
      viewportOffsetY?: number;
      writer: string;
    }>
  | Readonly<{
      kind: "empty-commit";
      operation: VirtualizedScrollOperation;
      policy: "empty-commit-retain-operation";
    }>;

export type VirtualizedFrameTransactionResult = Readonly<{
  operation: VirtualizedScrollOperation | null;
  scheduled: boolean;
}>;

type VirtualizedMaintenancePhase = "executing" | "frame-scheduled" | "pending" | "retry-pending" | "terminal";

type VirtualizedMaintenanceRequestId = Readonly<{
  documentEpoch: number;
  requestSerial: number;
}>;

type VirtualizedMaintenanceBinding = Readonly<{
  operation: VirtualizedScrollOperation;
  operationEpoch: number;
  ownsLease: boolean;
}>;

type VirtualizedMaintenanceRequest = {
  binding: VirtualizedMaintenanceBinding | null;
  documentEpoch: number;
  executionCount: 0 | 1;
  owner: string;
  onTerminal: VirtualizedMaintenanceTerminalHandler | null;
  phase: VirtualizedMaintenancePhase;
  requestId: VirtualizedMaintenanceRequestId;
  retryFrame: number | null;
  terminal: VirtualizedMaintenanceTerminal | null;
  work: (operation: VirtualizedScrollOperation) => void;
  workRevision: number;
};

type VirtualizedMaintenanceOwnerSlot = {
  active: VirtualizedMaintenanceRequest | null;
  successor: VirtualizedMaintenanceRequest | null;
};

type VirtualizedMaintenanceReleaseHold = {
  operation: VirtualizedScrollOperation;
  releaseRequested: boolean;
  requestSerials: Set<number>;
};

type VirtualizedMaintenanceReleaseAction = {
  afterWrite: boolean;
  operation: VirtualizedScrollOperation;
};

const VIRTUALIZED_MAINTENANCE_RETRY_POLICY = "maintenance-retry-on-occupied" as const;
const VIRTUALIZED_MAINTENANCE_RETRY_REASON = "frame-transaction-occupied" as const;

export function createVirtualizedScrollCoordinator(
  deps: VirtualizedScrollCoordinatorDeps
): VirtualizedScrollCoordinator {
  const virtualizedMaintenanceByOwner = new Map<string, VirtualizedMaintenanceOwnerSlot>();
  const virtualizedMaintenanceReleaseHolds = new Map<number, VirtualizedMaintenanceReleaseHold>();
  const virtualizedMaintenanceDeferredPromotionOwners = new Set<string>();
  const virtualizedWriteReceipts = new Map<number, ScrollWriteReceipt>();
  let virtualizedMaintenanceCancellationBatchDepth = 0;
  let virtualizedMaintenanceRequestSerial = 0;
  let pendingInitialVirtualizedWindowWork: PendingInitialVirtualizedWindowWork | null = null;

  function consumePendingInitialVirtualizedWindow(operation: VirtualizedScrollOperation): boolean {
    const work = pendingInitialVirtualizedWindowWork;
    pendingInitialVirtualizedWindowWork = null;
    if (work === null || !operation.isCurrent()) {
      return false;
    }
    work(operation);
    return true;
  }

  function createVirtualizedScrollOperation(lease: ScrollLease): VirtualizedScrollOperation | null {
    const plane = deps.getPlane();
    if (plane === null) {
      return null;
    }
    return {
      documentEpoch: lease.documentEpoch,
      operationEpoch: lease.operationEpoch,
      lease,
      isCurrent: () => plane.isCurrentDocumentEpoch(lease.documentEpoch) && plane.holds(lease),
      requestScrollTop: (target, writer) => {
        if (!plane.isCurrentDocumentEpoch(lease.documentEpoch) || !plane.holds(lease)) {
          return;
        }
        const receipt = plane.write(lease, { target, writer });
        virtualizedWriteReceipts.set(lease.operationEpoch, receipt);
        void receipt.result.then(() => {
          if (virtualizedWriteReceipts.get(lease.operationEpoch) === receipt) {
            virtualizedWriteReceipts.delete(lease.operationEpoch);
          }
        });
      },
      scheduleFrameTransaction: work => plane.scheduleFrameTransaction(lease, work),
    };
  }

  function acquireVirtualizedScrollOperation(
    owner: string,
    policy: ScrollAcquirePolicy
  ): VirtualizedScrollOperation | null {
    const maintenanceCutoff = virtualizedMaintenanceRequestSerial;
    const acquired = deps.getPlane()?.acquire(owner, policy);
    if (acquired?.status !== "acquired") {
      return null;
    }
    const operation = createVirtualizedScrollOperation(acquired.lease);
    if (operation !== null && policy !== "defer") {
      cancelVirtualizedMaintenanceThrough(
        maintenanceCutoff,
        policy === "supersede-as-user" ? "user-supersession" : "programmatic-supersession"
      );
    }
    return operation;
  }

  function releaseVirtualizedScrollOperationAfterWrite(operation: VirtualizedScrollOperation): void {
    const plane = deps.getPlane();
    if (plane === null) {
      return;
    }
    const receipt = virtualizedWriteReceipts.get(operation.operationEpoch);
    if (receipt === undefined) {
      if (operation.isCurrent()) {
        releaseVirtualizedScrollOperation(operation);
      }
      return;
    }
    void receipt.result.then(() => {
      if (operation.isCurrent()) {
        releaseVirtualizedScrollOperation(operation);
      }
    });
  }

  function releaseVirtualizedScrollOperation(operation: VirtualizedScrollOperation): boolean {
    const plane = deps.getPlane();
    const hold = virtualizedMaintenanceReleaseHolds.get(operation.operationEpoch);
    if (hold !== undefined && hold.requestSerials.size > 0) {
      hold.releaseRequested = true;
      return true;
    }
    if (plane === null || !plane.release(operation.lease)) {
      return false;
    }
    return true;
  }

  function runStandaloneReleaseAfterWrite(
    request: Extract<VirtualizedFrameTransactionRequest, { kind: "acquire" }>
  ): VirtualizedFrameTransactionResult {
    const operation = acquireVirtualizedScrollOperation(request.owner, request.acquirePolicy);
    if (operation === null) {
      return { operation: null, scheduled: false };
    }
    const scheduled = operation.scheduleFrameTransaction(() => {
      if (!operation.isCurrent()) {
        return;
      }
      request.work(operation);
      releaseVirtualizedScrollOperationAfterWrite(operation);
    });
    if (!scheduled) {
      releaseVirtualizedScrollOperation(operation);
      return { operation: null, scheduled: false };
    }
    return { operation, scheduled: true };
  }

  function runExistingReleaseAfterWrite(
    request: Extract<VirtualizedFrameTransactionRequest, { kind: "existing" }>
  ): VirtualizedFrameTransactionResult {
    const operation = request.operation;
    const scheduled = operation.scheduleFrameTransaction(() => {
      if (!operation.isCurrent()) {
        return;
      }
      request.work();
      releaseVirtualizedScrollOperationAfterWrite(operation);
    });
    if (!scheduled && operation.isCurrent()) {
      releaseVirtualizedScrollOperation(operation);
    }
    return { operation, scheduled };
  }

  function runElementLandingReleaseAfterWrite(
    request: Extract<VirtualizedFrameTransactionRequest, { kind: "element-landing" }>
  ): VirtualizedFrameTransactionResult {
    const operation = request.operation;
    const element = request.element;
    if (element === null) {
      if (request.operation.isCurrent()) {
        releaseVirtualizedScrollOperation(request.operation);
      }
      return { operation, scheduled: false };
    }
    return runExistingReleaseAfterWrite({
      kind: "existing",
      operation,
      policy: "existing-release-after-write",
      work: () => {
        const target = deps.readElementDocumentTop(element) - Math.max(0, request.viewportOffsetY ?? 0);
        operation.requestScrollTop(target, request.writer);
      },
    });
  }

  function runEmptyCommitRetainOperation(
    request: Extract<VirtualizedFrameTransactionRequest, { kind: "empty-commit" }>
  ): VirtualizedFrameTransactionResult {
    const scheduled = request.operation.scheduleFrameTransaction(() => undefined);
    return { operation: request.operation, scheduled };
  }

  function runFrameTransaction(
    request: VirtualizedFrameTransactionRequest
  ): VirtualizedFrameTransactionResult {
    switch (request.kind) {
      case "acquire":
        return runStandaloneReleaseAfterWrite(request);
      case "existing":
        return runExistingReleaseAfterWrite(request);
      case "element-landing":
        return runElementLandingReleaseAfterWrite(request);
      case "empty-commit":
        return runEmptyCommitRetainOperation(request);
    }
  }

  function virtualizedMaintenanceDetail(request: VirtualizedMaintenanceRequest): Record<string, unknown> {
    return {
      documentEpoch: request.documentEpoch,
      owner: request.owner,
      requestId: request.requestId,
      requestSerial: request.requestId.requestSerial,
      workRevision: request.workRevision,
    };
  }

  function postVirtualizedMaintenanceEvent(
    name: "mm-virt-maintenance-bound"
      | "mm-virt-maintenance-coalesced"
      | "mm-virt-maintenance-requested"
      | "mm-virt-maintenance-retry",
    request: VirtualizedMaintenanceRequest,
    detail: Record<string, unknown> = {}
  ): void {
    deps.trace(name, {
      ...virtualizedMaintenanceDetail(request),
      ...detail,
    });
  }

  function isLiveVirtualizedMaintenanceRequest(request: VirtualizedMaintenanceRequest): boolean {
    if (request.terminal !== null || request.phase === "terminal") {
      return false;
    }
    const slot = virtualizedMaintenanceByOwner.get(request.owner);
    return slot?.active === request || slot?.successor === request;
  }

  function isActiveVirtualizedMaintenanceRequest(request: VirtualizedMaintenanceRequest): boolean {
    return isLiveVirtualizedMaintenanceRequest(request)
      && virtualizedMaintenanceByOwner.get(request.owner)?.active === request;
  }

  function createVirtualizedMaintenanceRequest(
    owner: string,
    documentEpoch: number,
    work: (operation: VirtualizedScrollOperation) => void,
    onTerminal: VirtualizedMaintenanceTerminalHandler | null
  ): VirtualizedMaintenanceRequest {
    const requestSerial = ++virtualizedMaintenanceRequestSerial;
    return {
      binding: null,
      documentEpoch,
      executionCount: 0,
      owner,
      onTerminal,
      phase: "pending",
      requestId: Object.freeze({ documentEpoch, requestSerial }),
      retryFrame: null,
      terminal: null,
      work,
      workRevision: 1,
    };
  }

  function postVirtualizedMaintenanceRequested(request: VirtualizedMaintenanceRequest): void {
    postVirtualizedMaintenanceEvent("mm-virt-maintenance-requested", request);
  }

  function coalesceVirtualizedMaintenanceRequest(
    request: VirtualizedMaintenanceRequest,
    work: (operation: VirtualizedScrollOperation) => void,
    onTerminal: VirtualizedMaintenanceTerminalHandler | null
  ): void {
    if (!isLiveVirtualizedMaintenanceRequest(request)) {
      return;
    }
    const replacedTerminal = request.onTerminal;
    request.onTerminal = onTerminal;
    request.work = work;
    request.workRevision++;
    replacedTerminal?.({ reason: "coalesced", status: "canceled" });
    postVirtualizedMaintenanceEvent("mm-virt-maintenance-coalesced", request);
  }

  function registerVirtualizedMaintenanceReleaseHold(
    request: VirtualizedMaintenanceRequest,
    binding: VirtualizedMaintenanceBinding
  ): void {
    let hold = virtualizedMaintenanceReleaseHolds.get(binding.operationEpoch);
    if (hold === undefined) {
      hold = {
        operation: binding.operation,
        releaseRequested: false,
        requestSerials: new Set<number>(),
      };
      virtualizedMaintenanceReleaseHolds.set(binding.operationEpoch, hold);
    }
    hold.requestSerials.add(request.requestId.requestSerial);
  }

  function detachVirtualizedMaintenanceBinding(
    request: VirtualizedMaintenanceRequest,
    terminal: VirtualizedMaintenanceTerminal
  ): VirtualizedMaintenanceReleaseAction | null {
    const binding = request.binding;
    if (binding === null) {
      return null;
    }
    if (binding.ownsLease) {
      if (terminal.status === "completed") {
        return { afterWrite: true, operation: binding.operation };
      }
      if (terminal.status === "canceled" && binding.operation.isCurrent()) {
        return { afterWrite: false, operation: binding.operation };
      }
      return null;
    }

    const hold = virtualizedMaintenanceReleaseHolds.get(binding.operationEpoch);
    if (hold === undefined) {
      return null;
    }
    hold.requestSerials.delete(request.requestId.requestSerial);
    if (hold.requestSerials.size > 0) {
      return null;
    }
    virtualizedMaintenanceReleaseHolds.delete(binding.operationEpoch);
    return hold.releaseRequested && hold.operation.isCurrent()
      ? { afterWrite: true, operation: hold.operation }
      : null;
  }

  function promoteVirtualizedMaintenanceSuccessor(owner: string): void {
    const slot = virtualizedMaintenanceByOwner.get(owner);
    if (slot === undefined || slot.active !== null) {
      return;
    }
    const successor = slot.successor;
    slot.successor = null;
    if (successor === null) {
      virtualizedMaintenanceByOwner.delete(owner);
      return;
    }
    slot.active = successor;
    attemptVirtualizedMaintenance(successor);
  }

  function flushVirtualizedMaintenancePromotions(): void {
    if (virtualizedMaintenanceCancellationBatchDepth !== 0) {
      return;
    }
    const owners = [...virtualizedMaintenanceDeferredPromotionOwners];
    virtualizedMaintenanceDeferredPromotionOwners.clear();
    for (const owner of owners) {
      promoteVirtualizedMaintenanceSuccessor(owner);
    }
  }

  function finishVirtualizedMaintenance(
    request: VirtualizedMaintenanceRequest,
    status: "canceled" | "completed" | "failed",
    reason: string
  ): boolean {
    if (request.terminal !== null || request.phase === "terminal") {
      return false;
    }
    const terminal = Object.freeze({ reason, status });
    request.terminal = terminal;
    request.phase = "terminal";
    const onTerminal = request.onTerminal;
    request.onTerminal = null;
    if (request.retryFrame !== null) {
      deps.cancelFrame(request.retryFrame);
      request.retryFrame = null;
    }
    const releaseAction = detachVirtualizedMaintenanceBinding(request, terminal);
    const slot = virtualizedMaintenanceByOwner.get(request.owner);
    if (slot?.active === request) {
      slot.active = null;
    } else if (slot?.successor === request) {
      slot.successor = null;
    }
    if (slot !== undefined && slot.active === null && slot.successor === null) {
      virtualizedMaintenanceByOwner.delete(request.owner);
    }

    deps.trace("mm-virt-maintenance-terminal", {
      ...virtualizedMaintenanceDetail(request),
      executionCount: request.executionCount,
      reason,
      status,
    });
    onTerminal?.(terminal);

    if (releaseAction !== null) {
      if (releaseAction.afterWrite) {
        releaseVirtualizedScrollOperationAfterWrite(releaseAction.operation);
      } else {
        releaseVirtualizedScrollOperation(releaseAction.operation);
      }
    }

    if (slot?.active === null && slot.successor !== null) {
      if (status === "failed") {
        finishVirtualizedMaintenance(slot.successor, "canceled", reason);
      } else if (virtualizedMaintenanceCancellationBatchDepth === 0) {
        promoteVirtualizedMaintenanceSuccessor(request.owner);
      } else {
        virtualizedMaintenanceDeferredPromotionOwners.add(request.owner);
      }
    }
    return true;
  }

  function cancelVirtualizedMaintenanceRequests(
    predicate: (request: VirtualizedMaintenanceRequest) => boolean,
    reason: string
  ): void {
    const selected: VirtualizedMaintenanceRequest[] = [];
    for (const slot of virtualizedMaintenanceByOwner.values()) {
      for (const request of [slot.active, slot.successor]) {
        if (request !== null && isLiveVirtualizedMaintenanceRequest(request) && predicate(request)) {
          selected.push(request);
        }
      }
    }
    virtualizedMaintenanceCancellationBatchDepth++;
    try {
      for (const request of selected) {
        finishVirtualizedMaintenance(request, "canceled", reason);
      }
    } finally {
      virtualizedMaintenanceCancellationBatchDepth--;
      flushVirtualizedMaintenancePromotions();
    }
  }

  function cancelPendingVirtualizedMaintenance(reason: string): void {
    cancelVirtualizedMaintenanceRequests(() => true, reason);
  }

  function cancelVirtualizedMaintenanceThrough(cutoff: number, reason: string): void {
    cancelVirtualizedMaintenanceRequests(
      request => request.requestId.requestSerial <= cutoff,
      reason
    );
  }

  function scheduleVirtualizedMaintenanceRetry(request: VirtualizedMaintenanceRequest): void {
    if (
      request.retryFrame !== null
      || !isActiveVirtualizedMaintenanceRequest(request)
      || request.phase === "terminal"
    ) {
      return;
    }
    request.phase = "retry-pending";
    request.retryFrame = deps.requestFrame(() => {
      request.retryFrame = null;
      if (!isActiveVirtualizedMaintenanceRequest(request)) {
        return;
      }
      if (deps.getPlane()?.isCurrentDocumentEpoch(request.documentEpoch) !== true) {
        finishVirtualizedMaintenance(request, "canceled", "stale-document");
        return;
      }
      request.phase = "pending";
      attemptVirtualizedMaintenance(request);
    });
    postVirtualizedMaintenanceEvent("mm-virt-maintenance-retry", request, {
      policy: VIRTUALIZED_MAINTENANCE_RETRY_POLICY,
      reason: VIRTUALIZED_MAINTENANCE_RETRY_REASON,
    });
  }

  function deliverVirtualizedMaintenance(
    request: VirtualizedMaintenanceRequest,
    operation: VirtualizedScrollOperation
  ): void {
    const binding = request.binding;
    if (
      !isActiveVirtualizedMaintenanceRequest(request)
      || request.phase !== "frame-scheduled"
      || binding === null
      || binding.operation !== operation
    ) {
      return;
    }
    if (!operation.isCurrent() || operation.documentEpoch !== request.documentEpoch) {
      finishVirtualizedMaintenance(request, "canceled", "stale-operation");
      return;
    }
    if (request.executionCount !== 0) {
      finishVirtualizedMaintenance(request, "failed", "execution-count-invariant");
      return;
    }
    request.phase = "executing";
    request.executionCount = 1;
    const work = request.work;
    try {
      work(operation);
    } catch (error) {
      finishVirtualizedMaintenance(request, "failed", "frame-work-failed");
      throw error;
    }
    finishVirtualizedMaintenance(request, "completed", "delivered");
  }

  function attemptVirtualizedMaintenance(request: VirtualizedMaintenanceRequest): void {
    const plane = deps.getPlane();
    if (
      plane === null
      || !isActiveVirtualizedMaintenanceRequest(request)
      || !plane.isCurrentDocumentEpoch(request.documentEpoch)
    ) {
      if (isLiveVirtualizedMaintenanceRequest(request)) {
        finishVirtualizedMaintenance(request, "canceled", "stale-document");
      }
      return;
    }
    const joined = plane.joinMaintenance(request.owner);
    if (joined === null) {
      finishVirtualizedMaintenance(request, "canceled", "lease-unavailable");
      return;
    }
    const operation = createVirtualizedScrollOperation(joined.lease);
    if (operation === null) {
      finishVirtualizedMaintenance(request, "canceled", "operation-unavailable");
      return;
    }
    const scheduled = operation.scheduleFrameTransaction(() => {
      deliverVirtualizedMaintenance(request, operation);
    });
    if (!scheduled) {
      if (joined.ownsLease && operation.isCurrent()) {
        releaseVirtualizedScrollOperation(operation);
      }
      scheduleVirtualizedMaintenanceRetry(request);
      return;
    }

    const binding = Object.freeze({
      operation,
      operationEpoch: operation.operationEpoch,
      ownsLease: joined.ownsLease,
    });
    request.binding = binding;
    request.phase = "frame-scheduled";
    if (!binding.ownsLease) {
      registerVirtualizedMaintenanceReleaseHold(request, binding);
    }
    postVirtualizedMaintenanceEvent("mm-virt-maintenance-bound", request, {
      operationEpoch: binding.operationEpoch,
      ownsLease: binding.ownsLease,
    });
  }

  function scheduleVirtualizedMaintenance(
    owner: string,
    work: (operation: VirtualizedScrollOperation) => void,
    onTerminal: VirtualizedMaintenanceTerminalHandler | null = null
  ): boolean {
    const plane = deps.getPlane();
    if (plane === null) {
      return false;
    }
    const documentEpoch = plane.captureDocumentEpoch();
    const staleSlot = virtualizedMaintenanceByOwner.get(owner);
    if (
      staleSlot !== undefined
      && [staleSlot.active, staleSlot.successor].some(request =>
        request !== null && request.documentEpoch !== documentEpoch)
    ) {
      cancelVirtualizedMaintenanceRequests(
        request => request.owner === owner && request.documentEpoch !== documentEpoch,
        "stale-document"
      );
    }

    let slot = virtualizedMaintenanceByOwner.get(owner);
    if (slot?.active !== null && slot?.active !== undefined) {
      if (slot.active.phase === "executing") {
        if (slot.successor !== null) {
          coalesceVirtualizedMaintenanceRequest(slot.successor, work, onTerminal);
          return true;
        }
        const successor = createVirtualizedMaintenanceRequest(
          owner,
          documentEpoch,
          work,
          onTerminal
        );
        slot.successor = successor;
        postVirtualizedMaintenanceRequested(successor);
        return true;
      }
      coalesceVirtualizedMaintenanceRequest(slot.active, work, onTerminal);
      return true;
    }

    const request = createVirtualizedMaintenanceRequest(
      owner,
      documentEpoch,
      work,
      onTerminal
    );
    if (slot === undefined) {
      slot = { active: request, successor: null };
      virtualizedMaintenanceByOwner.set(owner, slot);
    } else {
      slot.active = request;
    }
    postVirtualizedMaintenanceRequested(request);
    attemptVirtualizedMaintenance(request);
    return true;
  }

  return {
    acquireOperation: acquireVirtualizedScrollOperation,
    releaseOperation: releaseVirtualizedScrollOperation,
    releaseOperationAfterWrite: releaseVirtualizedScrollOperationAfterWrite,
    getWriteReceipt: operation => virtualizedWriteReceipts.get(operation.operationEpoch),
    getWriteReceiptByEpoch: operationEpoch => virtualizedWriteReceipts.get(operationEpoch),
    setPendingInitialWindowWork: work => { pendingInitialVirtualizedWindowWork = work; },
    consumePendingInitialWindow: consumePendingInitialVirtualizedWindow,
    clearPendingInitialWindow: () => { pendingInitialVirtualizedWindowWork = null; },
    clearWriteReceipts: () => { virtualizedWriteReceipts.clear(); },
    runFrameTransaction,
    scheduleMaintenance: scheduleVirtualizedMaintenance,
    cancelPendingMaintenance: cancelPendingVirtualizedMaintenance,
  };
}

import { describe, expect, it } from "vitest";
import {
  SCROLL_OWNERSHIP_TRACE_IDS,
  createScrollOwnershipControlPlane,
  type GeometrySettledPayload,
  type LeaseAcquisition,
  type ScrollLease,
  type ScrollOwnershipControlPlane,
  type ScrollOwnershipTraceEvent,
  type ScrollWriteReceipt,
} from "../src/scrollOwnershipControlPlane";

class FakeScrollRoot {
  clientHeight = 100;
  scrollHeight = 1000;
  writes: number[] = [];
  writeOrder: string[] | null = null;
  onWrite: ((value: number) => void) | null = null;
  normalizeWrite: (value: number) => number = value => value;
  private value = 0;

  get scrollTop(): number {
    return this.value;
  }

  set scrollTop(value: number) {
    const actual = this.normalizeWrite(value);
    this.value = actual;
    this.writes.push(actual);
    this.writeOrder?.push(`root:${actual}`);
    this.onWrite?.(actual);
  }
}

class FakeFrameQueue {
  private callbacks = new Map<number, FrameRequestCallback>();
  private nextId = 1;
  private timestamp = 0;

  readonly request = (callback: FrameRequestCallback): number => {
    const id = this.nextId++;
    this.callbacks.set(id, callback);
    return id;
  };

  readonly cancel = (id: number): void => {
    this.callbacks.delete(id);
  };

  pending(): number {
    return this.callbacks.size;
  }

  deliverFrame(paint?: () => void): boolean {
    const delivered = [...this.callbacks.values()];
    this.callbacks.clear();
    if (delivered.length === 0) {
      return false;
    }
    this.timestamp += 16;
    for (const callback of delivered) {
      callback(this.timestamp);
    }
    paint?.();
    return true;
  }
}

type Harness = {
  events: GeometrySettledPayload[];
  frames: FakeFrameQueue;
  plane: ScrollOwnershipControlPlane;
  root: FakeScrollRoot;
  traces: ScrollOwnershipTraceEvent[];
};

function createHarness(
  deliveredFrameBudget = 120,
  hasRecentUserInput: (withinMs: number) => boolean = () => true,
  heldOperation: Pick<
    Parameters<typeof createScrollOwnershipControlPlane>[0],
    "readHeldGestureEvidence" | "readHeldOperationMode"
  > = {}
): Harness {
  const events: GeometrySettledPayload[] = [];
  const frames = new FakeFrameQueue();
  const root = new FakeScrollRoot();
  const traces: ScrollOwnershipTraceEvent[] = [];
  const plane = createScrollOwnershipControlPlane({
    cancelFrame: frames.cancel,
    deliveredFrameBudget,
    emitGeometrySettled: payload => events.push(payload),
    hasRecentUserInput,
    ...heldOperation,
    requestFrame: frames.request,
    root,
    trace: event => traces.push(event),
  });
  return { events, frames, plane, root, traces };
}

function acquired(result: LeaseAcquisition): ScrollLease {
  expect(result.status).toBe("acquired");
  if (result.status !== "acquired") {
    throw new Error("Expected an acquired lease");
  }
  return result.lease;
}

describe("scroll ownership control plane", () => {
  it("rejects an invalid delivered-frame budget", () => {
    expect(() => createHarness(Number.NaN)).toThrow(RangeError);
    expect(() => createHarness(0)).toThrow(RangeError);
    expect(() => createHarness(1.5)).toThrow(RangeError);
  });

  it("holds exactly one lease per document", async () => {
    const { plane } = createHarness();
    const first = acquired(plane.acquire("navigation", "defer"));
    const maintenance = plane.joinMaintenance("height-adoption");
    const second = plane.acquire("find", "defer");

    expect(maintenance).toEqual({ lease: first, ownsLease: false });
    expect(second.status).toBe("deferred");
    expect(plane.holds(first)).toBe(true);

    expect(plane.release(first)).toBe(true);
    if (second.status !== "deferred") {
      throw new Error("Expected a deferred acquisition");
    }
    const drained = await second.ready;

    expect(drained.status).toBe("acquired");
    if (drained.status !== "acquired") {
      throw new Error("Expected the deferred acquisition to drain");
    }
    expect(drained.lease.operationEpoch).toBeGreaterThan(first.operationEpoch);
    expect(plane.holds(first)).toBe(false);
    expect(plane.holds(drained.lease)).toBe(true);
  });

  it("drains only the latest coalesced deferred maintenance request", async () => {
    const { plane } = createHarness();
    const owner = acquired(plane.acquire("navigation", "defer"));
    const first = plane.acquire("height-adoption", "defer");
    const second = plane.acquire("calibration", "defer");
    if (first.status !== "deferred" || second.status !== "deferred") {
      throw new Error("Expected deferred maintenance acquisitions");
    }

    expect(await first.ready).toEqual({ reason: "coalesced", status: "canceled" });
    expect(plane.release(owner)).toBe(true);
    const drained = await second.ready;

    expect(drained.status).toBe("acquired");
    if (drained.status !== "acquired") {
      throw new Error("Expected the latest maintenance request to drain");
    }
    expect(drained.lease.owner).toBe("calibration");
  });

  it("increments operation epochs and invalidates all leases on document teardown", async () => {
    const { plane } = createHarness();
    const oldDocumentEpoch = plane.captureDocumentEpoch();
    const first = acquired(plane.acquire("navigation", "defer"));
    const ticket = plane.beginGeometryWork("math", oldDocumentEpoch)!;
    const waiter = plane.waitForGeometrySettled(oldDocumentEpoch);

    plane.invalidateDocument();
    const canceled = await waiter;
    const second = acquired(plane.acquire("cache-restore", "defer"));

    expect(plane.captureDocumentEpoch()).toBeGreaterThan(oldDocumentEpoch);
    expect(plane.captureGeometryEpoch()).toBe(0);
    expect(second.operationEpoch).toBeGreaterThan(first.operationEpoch);
    expect(plane.holds(first)).toBe(false);
    expect(plane.geometryMutated(ticket)).toBe(false);
    expect(canceled).toMatchObject({ status: "canceled", reason: "document-invalidated" });
  });

  it("coalesces one frame transaction to one synchronous terminal commit", async () => {
    const { frames, plane, root } = createHarness();
    const lease = acquired(plane.acquire("navigation", "defer"));
    const first = plane.write(lease, { target: 400, writer: "initial" });
    const second = plane.write(lease, { target: 1200, writer: "residual" });

    expect(frames.pending()).toBe(0);
    expect(root.writes).toEqual([]);
    expect(plane.scheduleFrameTransaction(lease, () => undefined)).toBe(true);
    expect(frames.pending()).toBe(1);

    frames.deliverFrame();

    expect(root.writes).toEqual([900]);
    expect(await first.result).toMatchObject({ status: "rejected", reason: "coalesced" });
    expect(await second.result).toEqual({ status: "committed", value: 900 });
    expect(plane.classifyNativeScroll(900.5).kind).toBe("self-echo");
  });

  it("clamps the requested target once against the live post-DOM range", async () => {
    const { frames, plane, root } = createHarness();
    root.scrollHeight = 500;
    const lease = acquired(plane.acquire("navigation", "defer"));
    const receipt = plane.write(lease, { target: 800, writer: "live-range" });
    plane.scheduleFrameTransaction(lease, () => {
      root.scrollHeight = 2000;
    });

    frames.deliverFrame();

    expect(root.scrollTop).toBe(800);
    expect(await receipt.result).toEqual({ status: "committed", value: 800 });
  });

  it("DOM mutation and root commit complete before simulated paint", async () => {
    const { frames, plane, root } = createHarness();
    const lease = acquired(plane.acquire("window-render", "defer"));
    const order: string[] = [];
    root.writeOrder = order;
    let receipt: ScrollWriteReceipt | null = null;

    plane.scheduleFrameTransaction(lease, () => {
      order.push("dom");
      order.push("reconcile");
      receipt = plane.write(lease, { target: 240, writer: "anchor" });
    });
    frames.deliverFrame(() => order.push("paint"));

    expect(order).toEqual(["dom", "reconcile", "root:240", "paint"]);
    expect(await receipt!.result).toEqual({ status: "committed", value: 240 });
  });

  it("write receipt gates post-write reads but not the assignment", async () => {
    const { frames, plane, root } = createHarness();
    const lease = acquired(plane.acquire("navigation", "defer"));
    const receipt = plane.write(lease, { target: 320, writer: "navigation" });
    let completed = false;
    void receipt.result.then(() => { completed = true; });
    await Promise.resolve();

    expect(completed).toBe(false);
    expect(frames.pending()).toBe(0);
    expect(root.scrollTop).toBe(0);

    plane.scheduleFrameTransaction(lease, () => undefined);
    frames.deliverFrame();
    const result = await receipt.result;

    expect(result).toEqual({ status: "committed", value: 320 });
    expect(root.scrollTop).toBe(320);
  });

  it("records the actual post-assignment root value for receipt and self echo", async () => {
    const { frames, plane, root } = createHarness();
    root.normalizeWrite = Math.round;
    const lease = acquired(plane.acquire("navigation", "defer"));
    const receipt = plane.write(lease, { target: 123.75, writer: "navigation" });
    plane.scheduleFrameTransaction(lease, () => undefined);

    frames.deliverFrame();

    expect(root.scrollTop).toBe(124);
    expect(await receipt.result).toEqual({ status: "committed", value: 124 });
    expect(plane.classifyNativeScroll(124).kind).toBe("self-echo");
  });

  it("traces each write with complete ownership and root identity", async () => {
    const { frames, plane, traces } = createHarness();
    const lease = acquired(plane.acquire("navigation", "defer"));
    const receipt = plane.write(lease, { target: 125, writer: "navigation" });
    plane.scheduleFrameTransaction(lease, () => undefined);
    frames.deliverFrame();
    await receipt.result;

    const request = traces.find(trace => trace.id === SCROLL_OWNERSHIP_TRACE_IDS.writeRequest)!;
    const committed = traces.find(trace => trace.id === SCROLL_OWNERSHIP_TRACE_IDS.writeCommitted)!;

    expect(request).toMatchObject({
      documentEpoch: lease.documentEpoch,
      frame: 0,
      geometryEpoch: 0,
      operationEpoch: lease.operationEpoch,
      details: {
        after: 125,
        before: 0,
        supersessionSource: null,
        writer: "navigation",
      },
    });
    expect(committed).toMatchObject({
      documentEpoch: lease.documentEpoch,
      frame: 1,
      geometryEpoch: 0,
      operationEpoch: lease.operationEpoch,
      details: {
        after: 125,
        before: 0,
        supersessionSource: null,
        writer: "navigation",
      },
    });
  });

  it("classifies matching echo as self and unmatched scroll as user supersession", async () => {
    const { frames, plane } = createHarness();
    const lease = acquired(plane.acquire("navigation", "defer"));
    const receipt = plane.write(lease, { target: 180, writer: "navigation" });
    plane.scheduleFrameTransaction(lease, () => undefined);
    frames.deliverFrame();
    await receipt.result;

    expect(plane.classifyNativeScroll(180.5).kind).toBe("self-echo");
    expect(plane.holds(lease)).toBe(true);
    expect(plane.classifyNativeScroll(260).kind).toBe("user-supersession");
    expect(plane.holds(lease)).toBe(false);
  });

  it("attributes unmatched native movement to a declared held gesture until later foreign evidence", () => {
    let gestureLease: ScrollLease | null = null;
    let evidence: { kind: "wheel"; sequence: number } | null = null;
    const { plane } = createHarness(120, () => true, {
      readHeldGestureEvidence: lease => lease === gestureLease ? evidence : null,
      readHeldOperationMode: lease => lease === gestureLease ? "gesture" : null,
    });
    gestureLease = acquired(plane.acquire("minimap-gesture", "supersede-as-user"));

    expect(plane.classifyNativeScroll(333)).toEqual({
      kind: "gesture-owned",
      operationEpoch: gestureLease.operationEpoch,
      value: 333,
    });
    expect(plane.holds(gestureLease)).toBe(true);

    evidence = { kind: "wheel", sequence: 12 };
    expect(plane.classifyNativeScroll(444)).toEqual({
      evidence,
      kind: "user-supersession",
      value: 444,
    });
    expect(plane.holds(gestureLease)).toBe(false);
  });

  it("keeps declared navigation held until witness ingress explicitly supersedes it", () => {
    let navigationLease: ScrollLease | null = null;
    const { plane } = createHarness(120, () => false, {
      readHeldOperationMode: lease => lease === navigationLease ? "navigation" : null,
    });
    navigationLease = acquired(plane.acquire("block-navigation", "supersede-programmatic"));

    expect(plane.classifyNativeScroll(333)).toEqual({
      kind: "navigation-owned",
      operationEpoch: navigationLease.operationEpoch,
      value: 333,
    });
    expect(plane.holds(navigationLease)).toBe(true);

    plane.supersedeByUser("native-scroll");
    expect(plane.holds(navigationLease)).toBe(false);
  });

  it("keeps an unregistered supersede-as-user host operation on value matching", async () => {
    const { frames, plane } = createHarness(120, () => true, {
      readHeldGestureEvidence: () => null,
      readHeldOperationMode: () => null,
    });
    const host = acquired(plane.acquire("host-progress", "supersede-as-user"));
    const receipt = plane.write(host, { target: 200, writer: "host-progress" });
    plane.scheduleFrameTransaction(host, () => undefined);
    frames.deliverFrame();
    await receipt.result;

    expect(plane.classifyNativeScroll(350).kind).toBe("unattributed-failure");
    expect(plane.holds(host)).toBe(false);
  });

  it("composes gesture maintenance and the latest held target into one final write", async () => {
    let gestureLease: ScrollLease | null = null;
    const { frames, plane, root } = createHarness(120, () => true, {
      readHeldGestureEvidence: () => null,
      readHeldOperationMode: lease => lease === gestureLease ? "gesture" : null,
    });
    gestureLease = acquired(plane.acquire("minimap-gesture", "supersede-as-user"));
    const firstTarget = plane.write(gestureLease, {
      composition: "held-operation-target",
      target: 100,
      writer: "minimap-drag",
    });
    const latestTarget = plane.write(gestureLease, {
      composition: "held-operation-target",
      target: 300,
      writer: "minimap-drag",
    });
    let maintenanceReceipt: ScrollWriteReceipt | null = null;
    const workOrder: string[] = [];

    expect(plane.scheduleFrameTransaction(gestureLease, () => {
      workOrder.push("window-maintenance");
      maintenanceReceipt = plane.write(gestureLease!, {
        target: 40,
        writer: "scroll-window-reanchor",
      });
    })).toBe(true);
    expect(plane.scheduleFrameTransaction(gestureLease, () => {
      workOrder.push("adoption-maintenance");
    })).toBe(true);
    expect(frames.pending()).toBe(1);

    frames.deliverFrame();

    expect(workOrder).toEqual(["window-maintenance", "adoption-maintenance"]);
    expect(root.writes).toEqual([300]);
    expect(await firstTarget.result).toEqual({ reason: "coalesced", status: "rejected" });
    expect(await latestTarget.result).toEqual({ status: "committed", value: 300 });
    expect(maintenanceReceipt).not.toBeNull();
    expect(await maintenanceReceipt!.result).toEqual({ reason: "coalesced", status: "rejected" });
  });

  it("traces finite no-echo movement without a recent user-input witness", () => {
    const witnessWindows: number[] = [];
    const { plane, traces } = createHarness(120, withinMs => {
      witnessWindows.push(withinMs);
      return false;
    });

    const classification = plane.classifyNativeScroll(260);

    expect(classification).toEqual({ kind: "user-supersession", value: 260 });
    expect(witnessWindows).toEqual([250]);
    expect(traces.filter(trace => trace.id === SCROLL_OWNERSHIP_TRACE_IDS.unattributedMovement))
      .toHaveLength(1);
    expect(traces.at(-1)?.details).toEqual({ delta: null, value: 260 });
  });

  it("keeps finite no-echo classification unchanged when user input is recent", () => {
    const { plane, traces } = createHarness(120, () => true);

    expect(plane.classifyNativeScroll(260)).toEqual({ kind: "user-supersession", value: 260 });
    expect(traces.some(trace => trace.id === SCROLL_OWNERSHIP_TRACE_IDS.unattributedMovement))
      .toBe(false);
  });

  it("computes unattributed delta from the prior finite classified value", async () => {
    const { frames, plane, traces } = createHarness(120, () => false);
    const lease = acquired(plane.acquire("navigation", "defer"));
    const receipt = plane.write(lease, { target: 180, writer: "navigation" });
    plane.scheduleFrameTransaction(lease, () => undefined);
    frames.deliverFrame();
    await receipt.result;

    expect(plane.classifyNativeScroll(180).kind).toBe("self-echo");
    expect(plane.classifyNativeScroll(260).kind).toBe("user-supersession");
    expect(traces.findLast(trace => trace.id === SCROLL_OWNERSHIP_TRACE_IDS.unattributedMovement))
      .toMatchObject({
      details: { delta: 80, value: 260 },
      id: SCROLL_OWNERSHIP_TRACE_IDS.unattributedMovement,
    });
  });

  it("resets the prior finite native-scroll baseline on document invalidation", () => {
    const { plane, traces } = createHarness(120, () => false);

    plane.classifyNativeScroll(100);
    plane.invalidateDocument();
    plane.classifyNativeScroll(140);

    const movements = traces.filter(
      trace => trace.id === SCROLL_OWNERSHIP_TRACE_IDS.unattributedMovement
    );
    expect(movements).toHaveLength(2);
    expect(movements[1]?.details).toEqual({ delta: null, value: 140 });
  });

  it("resets the prior finite native-scroll baseline on disposal", () => {
    const { plane, traces } = createHarness(120, () => false);

    plane.classifyNativeScroll(100);
    plane.dispose();
    plane.classifyNativeScroll(140);

    const movements = traces.filter(
      trace => trace.id === SCROLL_OWNERSHIP_TRACE_IDS.unattributedMovement
    );
    expect(movements).toHaveLength(2);
    expect(movements[1]?.details).toEqual({ delta: null, value: 140 });
  });

  it("classifies an expected mismatch as unattributed failure", async () => {
    const { frames, plane, traces } = createHarness();
    const lease = acquired(plane.acquire("navigation", "defer"));
    const receipt = plane.write(lease, { target: 180, writer: "navigation" });
    plane.scheduleFrameTransaction(lease, () => undefined);
    frames.deliverFrame();
    await receipt.result;

    const classification = plane.classifyNativeScroll(180.51);

    expect(classification.kind).toBe("unattributed-failure");
    expect(plane.holds(lease)).toBe(false);
    expect(traces.some(trace => trace.id === SCROLL_OWNERSHIP_TRACE_IDS.unattributedMovement)).toBe(true);
  });

  it("cancels deferred work on unattributed failure without later resurrection", async () => {
    const { frames, plane } = createHarness();
    const lease = acquired(plane.acquire("navigation", "defer"));
    const deferred = plane.acquire("height-adoption", "defer");
    if (deferred.status !== "deferred") {
      throw new Error("Expected deferred maintenance");
    }
    const receipt = plane.write(lease, { target: 100, writer: "navigation" });
    plane.scheduleFrameTransaction(lease, () => undefined);
    frames.deliverFrame();
    await receipt.result;

    expect(plane.classifyNativeScroll(101).kind).toBe("unattributed-failure");
    let deferredOutcome: Awaited<typeof deferred.ready> | null = null;
    void deferred.ready.then(outcome => { deferredOutcome = outcome; });
    await Promise.resolve();
    expect(deferredOutcome).toEqual({
      reason: "programmatic-supersession",
      status: "canceled",
    });

    const replacement = acquired(plane.acquire("find", "defer"));
    expect(plane.release(replacement)).toBe(true);
    expect(plane.joinMaintenance("new-maintenance")?.ownsLease).toBe(true);
  });

  it("quarantines an old echo without disturbing the current expectation", async () => {
    const { frames, plane } = createHarness();
    const oldLease = acquired(plane.acquire("navigation-a", "defer"));
    const oldReceipt = plane.write(oldLease, { target: 100, writer: "a" });
    plane.scheduleFrameTransaction(oldLease, () => undefined);
    frames.deliverFrame();
    await oldReceipt.result;

    const currentLease = acquired(plane.acquire("navigation-b", "supersede-programmatic"));
    const currentReceipt = plane.write(currentLease, { target: 200, writer: "b" });
    plane.scheduleFrameTransaction(currentLease, () => undefined);
    frames.deliverFrame();
    await currentReceipt.result;

    expect(plane.classifyNativeScroll(100).kind).toBe("stale-self-echo");
    expect(plane.holds(currentLease)).toBe(true);
    expect(plane.classifyNativeScroll(200).kind).toBe("self-echo");
    expect(plane.holds(currentLease)).toBe(true);
  });

  it("resolves echo collisions current-first and quarantines one stale callback", async () => {
    const { frames, plane, root } = createHarness();
    const oldLease = acquired(plane.acquire("navigation-a", "defer"));
    const oldReceipt = plane.write(oldLease, { target: 100, writer: "a" });
    plane.scheduleFrameTransaction(oldLease, () => undefined);
    frames.deliverFrame();
    await oldReceipt.result;
    root.scrollTop = 0;

    const currentLease = acquired(plane.acquire("navigation-b", "supersede-programmatic"));
    const currentReceipt = plane.write(currentLease, { target: 100, writer: "b" });
    plane.scheduleFrameTransaction(currentLease, () => undefined);
    frames.deliverFrame();
    await currentReceipt.result;

    expect(plane.classifyNativeScroll(100).kind).toBe("self-echo");
    expect(plane.classifyNativeScroll(100).kind).toBe("stale-self-echo");
    expect(plane.holds(currentLease)).toBe(true);
    expect(plane.classifyNativeScroll(100).kind).toBe("user-supersession");
    expect(plane.holds(currentLease)).toBe(false);
  });

  it("expires retired echoes after a bounded delivered-frame quarantine", async () => {
    const { frames, plane } = createHarness();
    const oldLease = acquired(plane.acquire("navigation-a", "defer"));
    const receipt = plane.write(oldLease, { target: 100, writer: "a" });
    plane.scheduleFrameTransaction(oldLease, () => undefined);
    frames.deliverFrame();
    await receipt.result;
    const currentLease = acquired(plane.acquire("navigation-b", "supersede-programmatic"));
    const ticket = plane.beginGeometryWork("quarantine-clock", currentLease.documentEpoch)!;

    frames.deliverFrame();
    frames.deliverFrame();
    frames.deliverFrame();

    expect(plane.classifyNativeScroll(100).kind).toBe("user-supersession");
    expect(plane.holds(currentLease)).toBe(false);
    expect(plane.endGeometryWork(ticket)).toBe(true);
  });

  it("retains retired echo fencing across release but clears it on invalidation", async () => {
    const { frames, plane } = createHarness();
    const oldLease = acquired(plane.acquire("navigation-a", "defer"));
    const receipt = plane.write(oldLease, { target: 100, writer: "a" });
    plane.scheduleFrameTransaction(oldLease, () => undefined);
    frames.deliverFrame();
    await receipt.result;
    const replacement = acquired(plane.acquire("navigation-b", "supersede-programmatic"));

    expect(plane.release(replacement)).toBe(true);
    expect(plane.classifyNativeScroll(100).kind).toBe("stale-self-echo");

    const nextDocumentLease = acquired(plane.acquire("before-invalidation", "defer"));
    const nextReceipt = plane.write(nextDocumentLease, { target: 120, writer: "before-invalidation" });
    plane.scheduleFrameTransaction(nextDocumentLease, () => undefined);
    frames.deliverFrame();
    await nextReceipt.result;
    plane.invalidateDocument();
    const currentDocumentLease = acquired(plane.acquire("after-invalidation", "defer"));

    expect(plane.classifyNativeScroll(120).kind).toBe("user-supersession");
    expect(plane.holds(currentDocumentLease)).toBe(false);
  });

  it("does not quarantine an unrelated native user scroll", async () => {
    const { frames, plane } = createHarness();
    const oldLease = acquired(plane.acquire("navigation-a", "defer"));
    const receipt = plane.write(oldLease, { target: 100, writer: "a" });
    plane.scheduleFrameTransaction(oldLease, () => undefined);
    frames.deliverFrame();
    await receipt.result;
    const currentLease = acquired(plane.acquire("navigation-b", "supersede-programmatic"));

    expect(plane.classifyNativeScroll(350).kind).toBe("user-supersession");
    expect(plane.holds(currentLease)).toBe(false);
  });

  it("user supersession rejects queued writes and settle waiters", async () => {
    const { frames, plane } = createHarness();
    const lease = acquired(plane.acquire("navigation", "defer"));
    const receipt = plane.write(lease, { target: 180, writer: "navigation" });
    const waiter = plane.waitForGeometrySettled(lease.documentEpoch);
    plane.scheduleFrameTransaction(lease, () => undefined);

    plane.supersedeByUser("minimap-pointer");

    expect(await receipt.result).toEqual({ reason: "user-supersession", status: "rejected" });
    expect(await waiter).toEqual({ reason: "user-supersession", status: "canceled" });
    expect(plane.holds(lease)).toBe(false);
    frames.deliverFrame();
    expect(plane.captureGeometryEpoch()).toBe(0);
  });

  it("does not emit settled while any geometry ticket is pending", async () => {
    const { events, frames, plane } = createHarness();
    const epoch = plane.captureDocumentEpoch();
    const ticket = plane.beginGeometryWork("math", epoch)!;
    const waiter = plane.waitForGeometrySettled(epoch);

    frames.deliverFrame();
    frames.deliverFrame();
    expect(events).toEqual([]);

    expect(plane.endGeometryWork(ticket)).toBe(true);
    frames.deliverFrame();
    expect(events).toEqual([]);
    frames.deliverFrame();

    expect(events).toEqual([{ documentEpoch: epoch, geometryEpoch: 0 }]);
    expect(await waiter).toMatchObject({
      payload: { documentEpoch: epoch, geometryEpoch: 0 },
      status: "settled",
    });
  });

  it("restarts two-frame quiet count on mutation", async () => {
    const { events, frames, plane } = createHarness();
    const epoch = plane.captureDocumentEpoch();
    const waiter = plane.waitForGeometrySettled(epoch);

    frames.deliverFrame();
    const ticket = plane.beginGeometryWork("resize", epoch)!;
    expect(plane.geometryMutated(ticket)).toBe(true);
    expect(plane.endGeometryWork(ticket)).toBe(true);

    frames.deliverFrame();
    expect(events).toEqual([]);
    frames.deliverFrame();

    expect(events).toEqual([{ documentEpoch: epoch, geometryEpoch: 1 }]);
    expect(await waiter).toMatchObject({ status: "settled" });
  });

  it("emits exactly after two unchanged frames", async () => {
    const { events, frames, plane } = createHarness();
    const epoch = plane.captureDocumentEpoch();
    const waiter = plane.waitForGeometrySettled(epoch);

    expect(frames.deliverFrame()).toBe(true);
    expect(events).toEqual([]);
    expect(frames.deliverFrame()).toBe(true);
    const settled = await waiter;

    expect(events).toEqual([{ documentEpoch: epoch, geometryEpoch: 0 }]);
    expect(settled).toMatchObject({
      emission: 1,
      payload: { documentEpoch: epoch, geometryEpoch: 0 },
      status: "settled",
    });
    expect(frames.deliverFrame()).toBe(false);
    expect(events).toHaveLength(1);
  });

  it("keeps write reconciliation pending until the matching self echo", async () => {
    const { events, frames, plane } = createHarness();
    const lease = acquired(plane.acquire("navigation", "defer"));
    const receipt = plane.write(lease, { target: 90, writer: "navigation" });
    plane.scheduleFrameTransaction(lease, () => undefined);
    frames.deliverFrame();
    await receipt.result;
    const waiter = plane.waitForGeometrySettled(lease.documentEpoch, receipt.afterEmission);

    frames.deliverFrame();
    frames.deliverFrame();
    expect(events).toEqual([]);

    expect(plane.classifyNativeScroll(90).kind).toBe("self-echo");
    frames.deliverFrame();
    frames.deliverFrame();

    expect(await waiter).toMatchObject({ status: "settled" });
    expect(events).toEqual([{ documentEpoch: lease.documentEpoch, geometryEpoch: 0 }]);
  });

  it("settles a same-value write without waiting for a native echo", async () => {
    const { events, frames, plane, root } = createHarness(3);
    const lease = acquired(plane.acquire("cold-top", "defer"));
    const receipt = plane.write(lease, { target: 0, writer: "no-op" });
    plane.scheduleFrameTransaction(lease, () => undefined);
    frames.deliverFrame();
    const waiter = plane.waitForGeometrySettled(lease.documentEpoch, receipt.afterEmission);

    frames.deliverFrame();
    frames.deliverFrame();

    expect(await receipt.result).toEqual({ status: "committed", value: 0 });
    expect(await waiter).toMatchObject({ status: "settled" });
    expect(events).toEqual([{ documentEpoch: lease.documentEpoch, geometryEpoch: 0 }]);
    expect(root.scrollTop).toBe(0);
    expect(plane.holds(lease)).toBe(true);
  });

  it("consumer nominal zero waits for confirmation and retries on epoch bump", async () => {
    const { events, frames, plane } = createHarness();
    const lease = acquired(plane.acquire("navigation", "defer"));
    const firstWait = plane.waitForGeometrySettled(lease.documentEpoch);
    frames.deliverFrame();
    frames.deliverFrame();
    const nominal = await firstWait;
    expect(nominal).toMatchObject({ emission: 1, status: "settled" });
    if (nominal.status !== "settled") {
      throw new Error("Expected nominal settlement");
    }

    const confirmation = plane.waitForGeometrySettled(
      lease.documentEpoch,
      nominal.emission
    );
    const lateTicket = plane.beginGeometryWork("late-font", lease.documentEpoch)!;
    expect(plane.geometryMutated(lateTicket)).toBe(true);
    expect(plane.endGeometryWork(lateTicket)).toBe(true);
    frames.deliverFrame();
    frames.deliverFrame();
    const changed = await confirmation;
    expect(changed).toMatchObject({
      emission: 2,
      payload: { geometryEpoch: 1 },
      status: "settled",
    });
    if (changed.status !== "settled") {
      throw new Error("Expected changed settlement");
    }
    expect(plane.holds(lease, nominal.payload.geometryEpoch)).toBe(false);

    const retry = plane.waitForGeometrySettled(lease.documentEpoch, changed.emission);
    frames.deliverFrame();
    frames.deliverFrame();
    const confirmed = await retry;
    expect(confirmed).toMatchObject({
      emission: 3,
      payload: { geometryEpoch: 1 },
      status: "settled",
    });
    expect(plane.holds(lease, 1)).toBe(true);
    expect(events.map(event => event.geometryEpoch)).toEqual([0, 1, 1]);
  });

  it("pauses watchdog when zero animation frame callbacks are delivered", async () => {
    const { events, plane, traces } = createHarness(2);
    const lease = acquired(plane.acquire("navigation", "defer"));
    const ticket = plane.beginGeometryWork("math", lease.documentEpoch)!;
    const waiter = plane.waitForGeometrySettled(lease.documentEpoch);
    let resolved = false;
    void waiter.then(() => { resolved = true; });
    await Promise.resolve();

    expect(resolved).toBe(false);
    expect(plane.holds(lease)).toBe(true);
    expect(plane.endGeometryWork(ticket)).toBe(true);
    expect(events).toEqual([]);
    expect(traces.some(trace => trace.id === SCROLL_OWNERSHIP_TRACE_IDS.watchdogPaused)).toBe(true);
    expect(traces.some(trace => trace.id === SCROLL_OWNERSHIP_TRACE_IDS.settleTimeout)).toBe(false);
  });

  it("preserves one operation's delivered-frame budget across settlement and joined maintenance", async () => {
    const { events, frames, plane } = createHarness(3);
    const lease = acquired(plane.acquire("cache-restore", "defer"));
    const initial = plane.waitForGeometrySettled(lease.documentEpoch);

    frames.deliverFrame();
    frames.deliverFrame();
    expect(await initial).toMatchObject({ status: "settled" });
    expect(events).toHaveLength(1);
    expect(plane.joinMaintenance("height-adoption")).toEqual({ lease, ownsLease: false });

    plane.beginGeometryWork("joined-height-adoption", lease.documentEpoch);
    const terminal = plane.waitForGeometrySettled(lease.documentEpoch, 1);
    frames.deliverFrame();
    expect(plane.holds(lease)).toBe(true);
    frames.deliverFrame();

    expect(plane.holds(lease)).toBe(false);
    expect(await terminal).toMatchObject({ status: "canceled", reason: "non-converged" });
    expect(events).toHaveLength(1);
  });

  it("fails non-convergence only after delivered frames", async () => {
    const { events, frames, plane, traces } = createHarness(3);
    const lease = acquired(plane.acquire("cache-restore", "defer"));
    plane.beginGeometryWork("font-ready", lease.documentEpoch);
    const waiter = plane.waitForGeometrySettled(lease.documentEpoch);

    frames.deliverFrame();
    frames.deliverFrame();
    expect(plane.holds(lease)).toBe(true);

    frames.deliverFrame();
    const result = await waiter;

    expect(result).toMatchObject({ status: "canceled", reason: "non-converged" });
    expect(plane.holds(lease)).toBe(false);
    expect(await plane.waitForGeometrySettled(
      lease.documentEpoch,
      0,
      lease.operationEpoch
    )).toMatchObject({ status: "canceled", reason: "non-converged" });
    expect(events).toEqual([]);
    expect(traces.some(trace => trace.id === SCROLL_OWNERSHIP_TRACE_IDS.settleTimeout)).toBe(true);
  });

  it("cancels settle waiters on document epoch change", async () => {
    const { frames, plane } = createHarness();
    const epoch = plane.captureDocumentEpoch();
    const waiter = plane.waitForGeometrySettled(epoch);
    expect(frames.pending()).toBe(1);

    plane.invalidateDocument();

    expect(await waiter).toMatchObject({ status: "canceled", reason: "document-invalidated" });
    expect(frames.pending()).toBe(0);
  });

  it("rejects stale and non-finite inputs without root mutation", async () => {
    const { plane, root, traces } = createHarness();
    const staleEpoch = plane.captureDocumentEpoch();
    const staleLease = acquired(plane.acquire("navigation", "defer"));
    plane.invalidateDocument();
    const currentLease = acquired(plane.acquire("navigation", "defer"));

    const staleWrite = plane.write(staleLease, { target: 20, writer: "stale" });
    const nonFiniteWrite = plane.write(currentLease, { target: Number.NaN, writer: "nan" });
    root.scrollHeight = Number.POSITIVE_INFINITY;
    const invalidRangeWrite = plane.write(currentLease, { target: 20, writer: "invalid-range" });

    expect(await staleWrite.result).toMatchObject({ status: "rejected", reason: "stale-lease" });
    expect(await nonFiniteWrite.result).toMatchObject({ status: "rejected", reason: "non-finite-target" });
    expect(await invalidRangeWrite.result).toMatchObject({ status: "rejected", reason: "non-finite-root-range" });
    expect(plane.scheduleFrameTransaction(staleLease, () => undefined)).toBe(false);
    expect(plane.beginGeometryWork("stale", staleEpoch)).toBeNull();
    expect(plane.isCurrentDocumentEpoch(Number.NaN)).toBe(false);
    expect(root.writes).toEqual([]);
    expect(traces.some(trace => trace.id === SCROLL_OWNERSHIP_TRACE_IDS.writeRejected)).toBe(true);
  });

  it("defers a reentrant post-commit request to the next delivered frame", async () => {
    const { frames, plane, root } = createHarness();
    const lease = acquired(plane.acquire("navigation", "defer"));
    let secondReceipt: ScrollWriteReceipt | null = null;
    root.onWrite = value => {
      if (value !== 100 || secondReceipt !== null) {
        return;
      }
      secondReceipt = plane.write(lease, { target: 200, writer: "reentrant" });
      plane.scheduleFrameTransaction(lease, () => undefined);
    };
    const firstReceipt = plane.write(lease, { target: 100, writer: "initial" });
    plane.scheduleFrameTransaction(lease, () => undefined);

    frames.deliverFrame();
    expect(root.writes).toEqual([100]);
    expect(frames.pending()).toBe(1);

    frames.deliverFrame();
    expect(root.writes).toEqual([100, 200]);
    expect(await firstReceipt.result).toEqual({ status: "committed", value: 100 });
    expect(await secondReceipt!.result).toEqual({ status: "committed", value: 200 });
  });

  it("rejects duplicate geometry ticket completion", () => {
    const { plane, traces } = createHarness();
    const ticket = plane.beginGeometryWork("calibration")!;

    expect(plane.endGeometryWork(ticket)).toBe(true);
    expect(plane.endGeometryWork(ticket)).toBe(false);
    expect(plane.geometryMutated(ticket)).toBe(false);
    expect(traces.filter(trace => trace.id === SCROLL_OWNERSHIP_TRACE_IDS.staleTicket)).toHaveLength(2);
  });

  it("treats stale release as a traced no-op", () => {
    const { plane, traces } = createHarness();
    const oldLease = acquired(plane.acquire("navigation", "defer"));
    const currentLease = acquired(plane.acquire("find", "supersede-programmatic"));

    expect(plane.release(oldLease)).toBe(false);
    expect(plane.holds(currentLease)).toBe(true);
    expect(traces.some(trace =>
      trace.id === SCROLL_OWNERSHIP_TRACE_IDS.staleLease
      && trace.details?.["reason"] === "stale-release")).toBe(true);
  });

  it("teardown cancels every lease waiter ticket receipt and frame", async () => {
    const { events, frames, plane, root } = createHarness();
    const lease = acquired(plane.acquire("navigation", "defer"));
    const ticket = plane.beginGeometryWork("math", lease.documentEpoch)!;
    const receipt = plane.write(lease, { target: 150, writer: "navigation" });
    const waiter = plane.waitForGeometrySettled(lease.documentEpoch);
    plane.scheduleFrameTransaction(lease, () => undefined);
    expect(frames.pending()).toBe(1);

    plane.dispose();

    expect(await receipt.result).toMatchObject({ status: "rejected", reason: "disposed" });
    expect(await waiter).toMatchObject({ status: "canceled", reason: "disposed" });
    expect(plane.holds(lease)).toBe(false);
    expect(plane.endGeometryWork(ticket)).toBe(false);
    expect(frames.pending()).toBe(0);
    expect(root.writes).toEqual([]);
    expect(events).toEqual([]);
  });

  it("contains trace exceptions without orphaning an acquired lease", () => {
    const frames = new FakeFrameQueue();
    const root = new FakeScrollRoot();
    let calls = 0;
    const deliveredTraceIds: string[] = [];
    const plane = createScrollOwnershipControlPlane({
      cancelFrame: frames.cancel,
      emitGeometrySettled: () => undefined,
      hasRecentUserInput: () => true,
      requestFrame: frames.request,
      root,
      trace: event => {
        calls++;
        if (calls === 1) {
          throw new Error("trace failed");
        }
        deliveredTraceIds.push(event.id);
      },
    });
    let first: LeaseAcquisition | null = null;

    expect(() => { first = plane.acquire("navigation", "defer"); }).not.toThrow();
    const lease = acquired(first!);
    expect(plane.holds(lease)).toBe(true);
    expect(plane.release(lease)).toBe(true);
    expect(plane.acquire("find", "defer").status).toBe("acquired");
    expect(deliveredTraceIds).toContain(SCROLL_OWNERSHIP_TRACE_IDS.observerDeliveryFailed);
  });

  it("contains repeated trace exceptions without recursive delivery", async () => {
    const frames = new FakeFrameQueue();
    const root = new FakeScrollRoot();
    let calls = 0;
    const plane = createScrollOwnershipControlPlane({
      cancelFrame: frames.cancel,
      emitGeometrySettled: () => undefined,
      hasRecentUserInput: () => true,
      requestFrame: frames.request,
      root,
      trace: () => {
        calls++;
        throw new Error("trace always fails");
      },
    });
    let lease!: ScrollLease;
    expect(() => { lease = acquired(plane.acquire("navigation", "defer")); }).not.toThrow();
    const receipt = plane.write(lease, { target: 80, writer: "navigation" });
    plane.scheduleFrameTransaction(lease, () => undefined);

    expect(() => frames.deliverFrame()).not.toThrow();
    expect(await receipt.result).toEqual({ status: "committed", value: 80 });
    expect(calls).toBeGreaterThan(0);
    expect(calls).toBeLessThan(50);
  });

  it("resolves settled waiters when the event emitter throws once or repeatedly", async () => {
    for (const throwCount of [1, Number.POSITIVE_INFINITY]) {
      const frames = new FakeFrameQueue();
      const root = new FakeScrollRoot();
      let calls = 0;
      const traces: ScrollOwnershipTraceEvent[] = [];
      const plane = createScrollOwnershipControlPlane({
        cancelFrame: frames.cancel,
        emitGeometrySettled: () => {
          calls++;
          if (calls <= throwCount) {
            throw new Error("emit failed");
          }
        },
        hasRecentUserInput: () => true,
        requestFrame: frames.request,
        root,
        trace: event => traces.push(event),
      });
      const epoch = plane.captureDocumentEpoch();
      const first = plane.waitForGeometrySettled(epoch);

      expect(() => frames.deliverFrame()).not.toThrow();
      expect(() => frames.deliverFrame()).not.toThrow();
      const firstResult = await first;
      expect(firstResult).toMatchObject({ status: "settled" });

      const second = plane.waitForGeometrySettled(epoch, firstResult.status === "settled"
        ? firstResult.emission
        : 0);
      expect(() => frames.deliverFrame()).not.toThrow();
      expect(() => frames.deliverFrame()).not.toThrow();
      expect(await second).toMatchObject({ status: "settled" });
      expect(traces.some(trace =>
        trace.id === SCROLL_OWNERSHIP_TRACE_IDS.observerDeliveryFailed
        && trace.details?.["channel"] === "geometry-settled-emitter")).toBe(true);
    }
  });
});

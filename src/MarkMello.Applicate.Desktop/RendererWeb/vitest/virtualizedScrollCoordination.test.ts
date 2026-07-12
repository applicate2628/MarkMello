import { describe, expect, it } from "vitest";
import {
  createScrollOwnershipControlPlane,
  type GeometrySettledPayload,
  type LeaseAcquisition,
  type ScrollLease,
  type ScrollOwnershipControlPlane,
  type ScrollOwnershipTraceEvent,
  type ScrollWriteReceipt,
} from "../src/scrollOwnershipControlPlane";
import {
  createVirtualizedScrollCoordinator,
  type VirtualizedMaintenanceTerminal,
  type VirtualizedScrollCoordinator,
  type VirtualizedScrollOperation,
} from "../src/virtualizedScrollCoordination";

class FakeScrollRoot {
  clientHeight = 100;
  scrollHeight = 1000;
  writes: number[] = [];
  private value = 0;

  get scrollTop(): number {
    return this.value;
  }

  set scrollTop(value: number) {
    this.value = value;
    this.writes.push(value);
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

  deliverFrame(): boolean {
    const delivered = [...this.callbacks.values()];
    this.callbacks.clear();
    if (delivered.length === 0) {
      return false;
    }
    this.timestamp += 16;
    for (const callback of delivered) {
      callback(this.timestamp);
    }
    return true;
  }
}

type CoordinatorTraceEvent = {
  detail: Record<string, unknown>;
  name: string;
};

type Harness = {
  coordinator: VirtualizedScrollCoordinator;
  coordinatorTraces: CoordinatorTraceEvent[];
  frames: FakeFrameQueue;
  plane: ScrollOwnershipControlPlane;
  planeTraces: ScrollOwnershipTraceEvent[];
  root: FakeScrollRoot;
};

function createHarness(): Harness {
  const events: GeometrySettledPayload[] = [];
  const frames = new FakeFrameQueue();
  const root = new FakeScrollRoot();
  const planeTraces: ScrollOwnershipTraceEvent[] = [];
  const coordinatorTraces: CoordinatorTraceEvent[] = [];
  const plane = createScrollOwnershipControlPlane({
    cancelFrame: frames.cancel,
    emitGeometrySettled: payload => events.push(payload),
    hasRecentUserInput: () => true,
    requestFrame: frames.request,
    root,
    trace: event => planeTraces.push(event),
  });
  const coordinator = createVirtualizedScrollCoordinator({
    cancelFrame: frames.cancel,
    getPlane: () => plane,
    readElementDocumentTop: element => Number(element.dataset["top"] ?? 0),
    requestFrame: frames.request,
    trace: (name, detail) => coordinatorTraces.push({ detail, name }),
  });
  return { coordinator, coordinatorTraces, frames, plane, planeTraces, root };
}

function acquired(result: LeaseAcquisition): ScrollLease {
  expect(result.status).toBe("acquired");
  if (result.status !== "acquired") {
    throw new Error("Expected an acquired lease");
  }
  return result.lease;
}

function acquireOperation(
  coordinator: VirtualizedScrollCoordinator,
  owner: string
): VirtualizedScrollOperation {
  const operation = coordinator.acquireOperation(owner, "defer");
  expect(operation).not.toBeNull();
  if (operation === null) {
    throw new Error("Expected an acquired virtualized scroll operation");
  }
  return operation;
}

function terminalLog(
  label: string,
  terminals: string[]
): (terminal: VirtualizedMaintenanceTerminal) => void {
  return terminal => {
    terminals.push(`${label}:${terminal.status}:${terminal.reason}`);
  };
}

function traceDetails(
  traces: CoordinatorTraceEvent[],
  name: string
): Record<string, unknown>[] {
  return traces
    .filter(trace => trace.name === name)
    .map(trace => trace.detail);
}

async function flushMicrotasks(): Promise<void> {
  await Promise.resolve();
  await Promise.resolve();
}

describe("virtualized scroll coordinator", () => {
  it("coalesces owner-keyed non-executing active maintenance in place", () => {
    const { coordinator, coordinatorTraces, frames } = createHarness();
    const calls: string[] = [];
    const terminals: string[] = [];

    expect(coordinator.scheduleMaintenance(
      "height-adoption",
      () => calls.push("first"),
      terminalLog("first", terminals)
    )).toBe(true);
    expect(coordinator.scheduleMaintenance(
      "height-adoption",
      () => calls.push("latest"),
      terminalLog("latest", terminals)
    )).toBe(true);

    expect(traceDetails(coordinatorTraces, "mm-virt-maintenance-requested")).toHaveLength(1);
    expect(traceDetails(coordinatorTraces, "mm-virt-maintenance-coalesced"))
      .toMatchObject([{ owner: "height-adoption", requestSerial: 1, workRevision: 2 }]);

    expect(frames.deliverFrame()).toBe(true);

    expect(calls).toEqual(["latest"]);
    expect(terminals).toEqual([
      "first:canceled:coalesced",
      "latest:completed:delivered",
    ]);
    expect(traceDetails(coordinatorTraces, "mm-virt-maintenance-terminal"))
      .toMatchObject([{ owner: "height-adoption", requestSerial: 1, workRevision: 2 }]);
  });

  it("coalesces one executing successor and promotes it after active cleanup", () => {
    const { coordinator, coordinatorTraces, frames } = createHarness();
    const calls: string[] = [];
    const terminals: string[] = [];

    expect(coordinator.scheduleMaintenance(
      "calibration",
      () => {
        calls.push("active");
        expect(coordinator.scheduleMaintenance(
          "calibration",
          () => calls.push("successor-first"),
          terminalLog("successor-first", terminals)
        )).toBe(true);
        expect(coordinator.scheduleMaintenance(
          "calibration",
          () => calls.push("successor-latest"),
          terminalLog("successor-latest", terminals)
        )).toBe(true);
        expect(calls).toEqual(["active"]);
      },
      terminalLog("active", terminals)
    )).toBe(true);

    expect(frames.deliverFrame()).toBe(true);

    expect(calls).toEqual(["active"]);
    expect(terminals).toEqual([
      "successor-first:canceled:coalesced",
      "active:completed:delivered",
    ]);
    expect(traceDetails(coordinatorTraces, "mm-virt-maintenance-requested"))
      .toMatchObject([
        { owner: "calibration", requestSerial: 1 },
        { owner: "calibration", requestSerial: 2 },
      ]);
    expect(traceDetails(coordinatorTraces, "mm-virt-maintenance-coalesced"))
      .toMatchObject([{ owner: "calibration", requestSerial: 2, workRevision: 2 }]);

    expect(frames.deliverFrame()).toBe(true);

    expect(calls).toEqual(["active", "successor-latest"]);
    expect(terminals).toEqual([
      "successor-first:canceled:coalesced",
      "active:completed:delivered",
      "successor-latest:completed:delivered",
    ]);
  });

  it("keeps a release hold until every joined request detaches and its write resolves", async () => {
    const { coordinator, frames, plane, root } = createHarness();
    const base = acquireOperation(coordinator, "programmatic-scroll");
    const calls: string[] = [];
    let firstReceipt: ScrollWriteReceipt | undefined;
    let secondReceipt: ScrollWriteReceipt | undefined;

    expect(coordinator.scheduleMaintenance("piggyback-a", operation => {
      calls.push("first");
      expect(coordinator.scheduleMaintenance("piggyback-b", laterOperation => {
        calls.push("second");
        laterOperation.requestScrollTop(260, "piggyback-b");
        secondReceipt = coordinator.getWriteReceipt(laterOperation);
      })).toBe(true);
      operation.requestScrollTop(140, "piggyback-a");
      firstReceipt = coordinator.getWriteReceipt(operation);
    })).toBe(true);
    expect(coordinator.releaseOperation(base)).toBe(true);
    expect(plane.holds(base.lease)).toBe(true);

    expect(frames.deliverFrame()).toBe(true);

    expect(calls).toEqual(["first"]);
    expect(root.writes).toEqual([140]);
    expect(firstReceipt).toBeDefined();
    expect(await firstReceipt!.result).toEqual({ status: "committed", value: 140 });
    expect(plane.holds(base.lease)).toBe(true);

    expect(frames.deliverFrame()).toBe(true);

    expect(calls).toEqual(["first", "second"]);
    expect(root.writes).toEqual([140, 260]);
    expect(secondReceipt).toBeDefined();
    expect(plane.holds(base.lease)).toBe(true);
    expect(await secondReceipt!.result).toEqual({ status: "committed", value: 260 });
    await flushMicrotasks();
    expect(plane.holds(base.lease)).toBe(false);
  });

  it("retains request identity through an occupied-slot retry that later owns and releases its lease", () => {
    const { coordinator, coordinatorTraces, frames, plane } = createHarness();
    const blocker = acquireOperation(coordinator, "blocker");
    const calls: number[] = [];

    expect(coordinator.scheduleExistingOperation(blocker, () => {
      expect(coordinator.releaseOperation(blocker)).toBe(true);
    })).toBe(true);
    expect(coordinator.scheduleMaintenance("maintenance", operation => {
      calls.push(operation.operationEpoch);
    })).toBe(true);

    expect(traceDetails(coordinatorTraces, "mm-virt-maintenance-retry"))
      .toMatchObject([{ owner: "maintenance", requestSerial: 1 }]);

    expect(frames.deliverFrame()).toBe(true);
    expect(calls).toEqual([]);

    expect(frames.deliverFrame()).toBe(true);
    expect(calls).toHaveLength(1);
    expect(traceDetails(coordinatorTraces, "mm-virt-maintenance-bound").at(-1))
      .toMatchObject({ owner: "maintenance", ownsLease: true, requestSerial: 1 });
    expect(plane.holds(blocker.lease)).toBe(false);
    expect(traceDetails(coordinatorTraces, "mm-virt-maintenance-terminal").at(-1))
      .toMatchObject({ owner: "maintenance", requestSerial: 1, status: "completed" });
  });

  it("does not release a joined occupied-slot retry and keeps its hold", () => {
    const { coordinator, coordinatorTraces, frames, plane } = createHarness();
    const blocker = acquireOperation(coordinator, "blocker");
    const calls: number[] = [];

    expect(coordinator.scheduleExistingOperation(blocker, () => undefined)).toBe(true);
    expect(coordinator.scheduleMaintenance("joined-maintenance", operation => {
      calls.push(operation.operationEpoch);
    })).toBe(true);

    expect(plane.holds(blocker.lease)).toBe(true);
    expect(traceDetails(coordinatorTraces, "mm-virt-maintenance-retry"))
      .toMatchObject([{ owner: "joined-maintenance", requestSerial: 1 }]);

    expect(frames.deliverFrame()).toBe(true);
    expect(calls).toEqual([]);
    expect(plane.holds(blocker.lease)).toBe(true);
    expect(traceDetails(coordinatorTraces, "mm-virt-maintenance-bound").at(-1))
      .toMatchObject({ owner: "joined-maintenance", ownsLease: false, requestSerial: 1 });
    expect(coordinator.releaseOperation(blocker)).toBe(true);
    expect(plane.holds(blocker.lease)).toBe(true);

    expect(frames.deliverFrame()).toBe(true);

    expect(calls).toEqual([blocker.operationEpoch]);
    expect(plane.holds(blocker.lease)).toBe(false);
  });

  it("programmatic acquisition cancels only requests at or below its captured serial cutoff", () => {
    const { coordinator, frames, plane } = createHarness();
    const calls: string[] = [];
    const terminals: string[] = [];

    expect(coordinator.scheduleMaintenance(
      "old-maintenance",
      () => calls.push("old"),
      terminal => {
        terminals.push(`old:${terminal.status}:${terminal.reason}`);
        expect(coordinator.scheduleMaintenance(
          "late-maintenance",
          () => calls.push("late"),
          terminalLog("late", terminals)
        )).toBe(true);
      }
    )).toBe(true);

    const programmatic = coordinator.acquireOperation("programmatic-scroll", "supersede-programmatic");
    expect(programmatic).not.toBeNull();

    expect(terminals).toEqual(["old:canceled:programmatic-supersession"]);
    expect(frames.deliverFrame()).toBe(true);

    expect(calls).toEqual(["late"]);
    expect(terminals).toEqual([
      "old:canceled:programmatic-supersession",
      "late:completed:delivered",
    ]);
    if (programmatic !== null) {
      expect(plane.holds(programmatic.lease)).toBe(true);
      expect(coordinator.releaseOperation(programmatic)).toBe(true);
    }
  });

  it("terminalizes a cancellation batch before deferred successor promotion can flush", () => {
    const { coordinator, frames } = createHarness();
    const calls: string[] = [];
    const terminals: string[] = [];

    expect(coordinator.scheduleMaintenance(
      "batched-owner",
      () => {
        calls.push("active");
        expect(coordinator.scheduleMaintenance(
          "batched-owner",
          () => calls.push("successor"),
          terminalLog("successor", terminals)
        )).toBe(true);
        coordinator.cancelPendingMaintenance("batch-cancel");
      },
      terminalLog("active", terminals)
    )).toBe(true);

    expect(frames.deliverFrame()).toBe(true);
    frames.deliverFrame();

    expect(calls).toEqual(["active"]);
    expect(terminals).toEqual([
      "active:canceled:batch-cancel",
      "successor:canceled:batch-cancel",
    ]);
  });

  it("cancels stale live maintenance on document invalidation before replacement delivery", () => {
    const { coordinator, frames, plane } = createHarness();
    const calls: string[] = [];
    const terminals: string[] = [];

    expect(coordinator.scheduleMaintenance(
      "document-maintenance",
      () => calls.push("stale"),
      terminalLog("stale", terminals)
    )).toBe(true);
    plane.invalidateDocument();
    expect(coordinator.scheduleMaintenance(
      "document-maintenance",
      () => calls.push("current"),
      terminalLog("current", terminals)
    )).toBe(true);

    expect(terminals).toEqual(["stale:canceled:stale-document"]);
    expect(frames.deliverFrame()).toBe(true);

    expect(calls).toEqual(["current"]);
    expect(terminals).toEqual([
      "stale:canceled:stale-document",
      "current:completed:delivered",
    ]);
  });

  it("drops a stale retry after document invalidation without delivering work", () => {
    const { coordinator, coordinatorTraces, frames, plane } = createHarness();
    const blocker = acquireOperation(coordinator, "blocker");
    const calls: string[] = [];
    const terminals: string[] = [];

    expect(coordinator.scheduleExistingOperation(blocker, () => undefined)).toBe(true);
    expect(coordinator.scheduleMaintenance(
      "retrying-maintenance",
      () => calls.push("retrying"),
      terminalLog("retrying", terminals)
    )).toBe(true);
    expect(traceDetails(coordinatorTraces, "mm-virt-maintenance-retry"))
      .toMatchObject([{ owner: "retrying-maintenance", requestSerial: 1 }]);

    plane.invalidateDocument();
    expect(frames.deliverFrame()).toBe(true);

    expect(calls).toEqual([]);
    expect(terminals).toEqual(["retrying:canceled:stale-document"]);
  });

  it("consumes pending initial-window work once through a current operation and clears explicitly", () => {
    const { coordinator } = createHarness();
    const operation = acquireOperation(coordinator, "initial-window");
    const calls: number[] = [];

    coordinator.setPendingInitialWindowWork(current => calls.push(current.operationEpoch));

    expect(coordinator.consumePendingInitialWindow(operation)).toBe(true);
    expect(coordinator.consumePendingInitialWindow(operation)).toBe(false);

    coordinator.setPendingInitialWindowWork(current => calls.push(current.operationEpoch));
    coordinator.clearPendingInitialWindow();

    expect(coordinator.consumePendingInitialWindow(operation)).toBe(false);
    expect(calls).toEqual([operation.operationEpoch]);
    expect(coordinator.releaseOperation(operation)).toBe(true);
  });

  it("exposes the current write receipt until its promise resolves and then clears it", async () => {
    const { coordinator, frames } = createHarness();
    const operation = acquireOperation(coordinator, "receipt-owner");
    let receipt: ScrollWriteReceipt | undefined;

    expect(coordinator.scheduleExistingOperation(operation, () => {
      operation.requestScrollTop(360, "receipt-owner");
      receipt = coordinator.getWriteReceipt(operation);
      expect(receipt).toBeDefined();
      expect(coordinator.getWriteReceiptByEpoch(operation.operationEpoch)).toBe(receipt);
    })).toBe(true);

    expect(frames.deliverFrame()).toBe(true);

    expect(receipt).toBeDefined();
    expect(coordinator.getWriteReceipt(operation)).toBe(receipt);
    expect(await receipt!.result).toEqual({ status: "committed", value: 360 });
    await flushMicrotasks();

    expect(coordinator.getWriteReceipt(operation)).toBeUndefined();
    expect(coordinator.getWriteReceiptByEpoch(operation.operationEpoch)).toBeUndefined();
    expect(coordinator.releaseOperation(operation)).toBe(true);
  });
});

import { describe, expect, it } from "vitest";
import { createHeldOperationScrollPolicy } from "../src/heldOperationScrollPolicy";

describe("held operation scroll policy", () => {
  it("resolves the current active target ahead of retained navigation", () => {
    const policy = createHeldOperationScrollPolicy<string>();
    const restore = { documentEpoch: 3, operationEpoch: 11 };

    policy.register(restore, "restore", "restore-anchor");

    expect(policy.resolve(restore, "retained-anchor")).toEqual({
      kind: "active",
      registration: {
        ...restore,
        mode: "restore",
        target: "restore-anchor",
      },
    });
  });

  it("falls back to retained navigation and then generic for unregistered operations", () => {
    const policy = createHeldOperationScrollPolicy<string>();
    const restore = { documentEpoch: 3, operationEpoch: 11 };
    const navigation = { documentEpoch: 3, operationEpoch: 12 };
    policy.register(restore, "restore", "restore-anchor");

    expect(policy.resolve(navigation, "retained-anchor")).toEqual({
      kind: "retained-navigation",
      target: "retained-anchor",
    });
    expect(policy.resolve(navigation, null)).toEqual({ kind: "generic" });
  });

  it("clears only the matching document and operation identity", () => {
    const policy = createHeldOperationScrollPolicy<string>();
    const restore = { documentEpoch: 3, operationEpoch: 11 };
    const gesture = { documentEpoch: 3, operationEpoch: 12 };
    policy.register(restore, "restore", "restore-anchor");
    policy.register(gesture, "gesture", "gesture-target");

    expect(policy.clear(restore)).toBe(false);
    expect(policy.read(gesture)).toEqual({
      ...gesture,
      mode: "gesture",
      target: "gesture-target",
    });
    expect(policy.clear(gesture)).toBe(true);
    expect(policy.read(gesture)).toBeNull();
  });

  it("updates a target only while the same operation owns the slot", () => {
    const policy = createHeldOperationScrollPolicy<number>();
    const gesture = { documentEpoch: 5, operationEpoch: 17 };
    policy.register(gesture, "gesture", 100);

    expect(policy.update({ documentEpoch: 5, operationEpoch: 16 }, 200)).toBe(false);
    expect(policy.read(gesture)?.target).toBe(100);
    expect(policy.update(gesture, 300)).toBe(true);
    expect(policy.read(gesture)?.target).toBe(300);
  });
});

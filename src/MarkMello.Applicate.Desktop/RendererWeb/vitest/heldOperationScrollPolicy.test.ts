import { describe, expect, it } from "vitest";
import { createHeldOperationScrollPolicy } from "../src/heldOperationScrollPolicy";

describe("held operation scroll policy", () => {
  type TestState =
    | Readonly<{ mode: "gesture"; latestTarget: number; witnessSequence: number }>
    | Readonly<{ mode: "restore"; anchor: string }>
    | Readonly<{
        geometryRevision: number;
        mode: "navigation";
        modelAnchor: { blockIndex: number; targetLocalOffset: number; viewportOffset: number };
        phase: "settling" | "post-settle-hold" | "preserving-geometry";
        semanticTarget: string;
        witnessSequence: number;
      }>;

  it("owns the complete navigation state under one operation identity", () => {
    const policy = createHeldOperationScrollPolicy<TestState>();
    const navigation = { documentEpoch: 3, operationEpoch: 11 };

    policy.register(navigation, {
      geometryRevision: 7,
      mode: "navigation",
      modelAnchor: { blockIndex: 4, targetLocalOffset: 18, viewportOffset: 24 },
      phase: "settling",
      semanticTarget: "heading-anchor",
      witnessSequence: 5,
    });

    expect(policy.read(navigation)).toEqual({
      ...navigation,
      geometryRevision: 7,
      mode: "navigation",
      modelAnchor: { blockIndex: 4, targetLocalOffset: 18, viewportOffset: 24 },
      phase: "settling",
      semanticTarget: "heading-anchor",
      witnessSequence: 5,
    });
    expect(policy.resolve(navigation)).toEqual({
      kind: "active",
      registration: policy.read(navigation),
    });
  });

  it("returns generic for an operation that does not own the registration", () => {
    const policy = createHeldOperationScrollPolicy<TestState>();
    const restore = { documentEpoch: 3, operationEpoch: 11 };
    policy.register(restore, { anchor: "restore-anchor", mode: "restore" });

    expect(policy.resolve({ documentEpoch: 3, operationEpoch: 12 })).toEqual({ kind: "generic" });
  });

  it("clears only the matching document and operation identity", () => {
    const policy = createHeldOperationScrollPolicy<TestState>();
    const restore = { documentEpoch: 3, operationEpoch: 11 };
    const gesture = { documentEpoch: 3, operationEpoch: 12 };
    policy.register(restore, { anchor: "restore-anchor", mode: "restore" });
    policy.register(gesture, { latestTarget: 100, mode: "gesture", witnessSequence: 4 });

    expect(policy.clear(restore)).toBe(false);
    expect(policy.read(gesture)).toEqual({
      ...gesture,
      latestTarget: 100,
      mode: "gesture",
      witnessSequence: 4,
    });
    expect(policy.clear(gesture)).toBe(true);
    expect(policy.read(gesture)).toBeNull();
  });

  it("transitions navigation phase only while the same operation owns the slot", () => {
    const policy = createHeldOperationScrollPolicy<TestState>();
    const navigation = { documentEpoch: 5, operationEpoch: 17 };
    const settling = {
      geometryRevision: 2,
      mode: "navigation" as const,
      modelAnchor: { blockIndex: 8, targetLocalOffset: 9, viewportOffset: 12 },
      phase: "settling" as const,
      semanticTarget: "source-line",
      witnessSequence: 3,
    };
    policy.register(navigation, settling);

    expect(policy.update(
      { documentEpoch: 5, operationEpoch: 16 },
      { ...settling, phase: "post-settle-hold" }
    )).toBe(false);
    expect(policy.read(navigation)).toMatchObject({ phase: "settling" });
    expect(policy.update(navigation, { ...settling, phase: "post-settle-hold" })).toBe(true);
    expect(policy.read(navigation)).toMatchObject({ phase: "post-settle-hold" });
  });
});

import { afterEach, describe, expect, it, vi } from "vitest";
import {
  createUserInputWitness,
  installUserInputWitnessListeners,
} from "../src/userInputWitness";

describe("user input witness", () => {
  afterEach(() => {
    document.body.replaceChildren();
    vi.restoreAllMocks();
  });

  it("uses an inclusive monotonic recent-input window", () => {
    let now = 1000;
    const witness = createUserInputWitness({ now: () => now });

    expect(witness.hasRecentUserInput(250)).toBe(false);
    witness.recordUserInput("wheel");
    expect(witness.hasRecentUserInput(250)).toBe(true);

    now = 1250;
    expect(witness.hasRecentUserInput(250)).toBe(true);
    now = 1250.01;
    expect(witness.hasRecentUserInput(250)).toBe(false);
    expect(witness.hasRecentUserInput(-1)).toBe(false);
    expect(witness.hasRecentUserInput(Number.NaN)).toBe(false);
  });

  it("returns only typed evidence recorded after a captured sequence", () => {
    const witness = createUserInputWitness({ now: () => 0 });
    witness.recordUserInput("wheel");
    const acquisitionSequence = witness.captureSequence();

    expect(witness.readEvidenceAfter(acquisitionSequence)).toBeNull();
    witness.recordUserInput("scroll-key");
    expect(witness.readEvidenceAfter(acquisitionSequence)).toEqual({
      kind: "scroll-key",
      sequence: acquisitionSequence + 1,
    });
  });

  it("notifies evidence subscribers synchronously at ingress", () => {
    const witness = createUserInputWitness({ now: () => 0 });
    const observed: string[] = [];
    const dispose = witness.subscribeEvidence(evidence => {
      observed.push(`callback:${evidence.kind}:${evidence.sequence}`);
    });

    observed.push("before");
    const evidence = witness.recordUserInput("wheel");
    observed.push("after");

    expect(evidence).toEqual({ kind: "wheel", sequence: 1 });
    expect(observed).toEqual(["before", "callback:wheel:1", "after"]);

    dispose();
    witness.recordUserInput("scroll-key");
    expect(observed).toEqual(["before", "callback:wheel:1", "after"]);
  });

  it("suppresses compatibility touch evidence while an owned pointer is active", () => {
    const witness = createUserInputWitness({ now: () => 0 });
    const dispose = installUserInputWitnessListeners({ document, ownerWindow: window, witness });
    const acquisitionSequence = witness.captureSequence();

    witness.beginOwnedPointer(7);
    document.dispatchEvent(new TouchEvent("touchstart"));
    document.dispatchEvent(new TouchEvent("touchmove"));
    expect(witness.readEvidenceAfter(acquisitionSequence)).toBeNull();

    witness.endOwnedPointer(7);
    document.dispatchEvent(new TouchEvent("touchstart"));
    expect(witness.readEvidenceAfter(acquisitionSequence)).toEqual({
      kind: "touch",
      sequence: acquisitionSequence + 1,
    });
    dispose();
  });

  it("records wheel, touch, approved keys, and primary gutter pointer input", () => {
    let now = 0;
    const witness = createUserInputWitness({ now: () => now });
    const dispose = installUserInputWitnessListeners({
      document,
      ownerWindow: window,
      witness,
    });

    document.dispatchEvent(new WheelEvent("wheel"));
    now = 1;
    expect(witness.hasRecentUserInput(1)).toBe(true);

    now = 10;
    document.dispatchEvent(new TouchEvent("touchstart"));
    now = 11;
    expect(witness.hasRecentUserInput(1)).toBe(true);

    now = 20;
    document.dispatchEvent(new TouchEvent("touchmove"));
    now = 21;
    expect(witness.hasRecentUserInput(1)).toBe(true);

    for (const key of ["ArrowUp", "ArrowDown", "PageUp", "PageDown", "Home", "End", " ", "Spacebar"]) {
      now += 10;
      document.dispatchEvent(new KeyboardEvent("keydown", { key, shiftKey: key === " " }));
      now += 1;
      expect(witness.hasRecentUserInput(1), key).toBe(true);
    }

    Object.defineProperty(document.documentElement, "clientWidth", { configurable: true, value: 900 });
    Object.defineProperty(window, "innerWidth", { configurable: true, value: 920 });
    Object.defineProperty(window, "innerHeight", { configurable: true, value: 700 });
    now += 10;
    document.dispatchEvent(new PointerEvent("pointerdown", {
      button: 0,
      clientX: 900,
      clientY: 699,
    }));
    now += 1;
    expect(witness.hasRecentUserInput(1)).toBe(true);

    dispose();
  });

  it("excludes modified keys, editable targets, non-scroll keys, and non-gutter pointers", () => {
    let now = 0;
    const witness = createUserInputWitness({ now: () => now });
    const dispose = installUserInputWitnessListeners({ document, ownerWindow: window, witness });
    const input = document.createElement("input");
    const editable = document.createElement("div");
    editable.contentEditable = "true";
    document.body.append(input, editable);

    document.dispatchEvent(new KeyboardEvent("keydown", { ctrlKey: true, key: "ArrowDown" }));
    document.dispatchEvent(new KeyboardEvent("keydown", { metaKey: true, key: "PageDown" }));
    document.dispatchEvent(new KeyboardEvent("keydown", { altKey: true, key: "End" }));
    document.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter" }));
    input.dispatchEvent(new KeyboardEvent("keydown", { bubbles: true, key: "ArrowDown" }));
    editable.dispatchEvent(new KeyboardEvent("keydown", { bubbles: true, key: "PageDown" }));

    Object.defineProperty(document.documentElement, "clientWidth", { configurable: true, value: 900 });
    Object.defineProperty(window, "innerWidth", { configurable: true, value: 920 });
    Object.defineProperty(window, "innerHeight", { configurable: true, value: 700 });
    document.dispatchEvent(new PointerEvent("pointerdown", { button: 1, clientX: 910, clientY: 10 }));
    document.dispatchEvent(new PointerEvent("pointerdown", { button: 0, clientX: 899, clientY: 10 }));
    document.dispatchEvent(new PointerEvent("pointerdown", { button: 0, clientX: 920, clientY: 10 }));
    document.dispatchEvent(new PointerEvent("pointerdown", { button: 0, clientX: 910, clientY: -1 }));
    document.dispatchEvent(new PointerEvent("pointerdown", { button: 0, clientX: 910, clientY: 700 }));

    now = 1;
    expect(witness.hasRecentUserInput(1)).toBe(false);
    dispose();
  });

  it("registers passive capture listeners and disposes them idempotently", () => {
    const add = vi.spyOn(document, "addEventListener");
    const remove = vi.spyOn(document, "removeEventListener");
    const witness = createUserInputWitness({ now: () => 0 });

    const dispose = installUserInputWitnessListeners({ document, ownerWindow: window, witness });

    expect(add).toHaveBeenCalledTimes(5);
    for (const call of add.mock.calls) {
      expect(call[2]).toEqual({ capture: true, passive: true });
    }

    dispose();
    dispose();
    expect(remove).toHaveBeenCalledTimes(5);
    for (const call of remove.mock.calls) {
      expect(call[2]).toEqual({ capture: true });
    }
  });
});

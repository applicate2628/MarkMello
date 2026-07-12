export type UserInputEvidenceKind = "scroll-key" | "scrollbar-gutter" | "touch" | "wheel";

export type UserInputEvidence = Readonly<{
  kind: UserInputEvidenceKind;
  sequence: number;
}>;

export type UserInputEvidenceListener = (evidence: UserInputEvidence) => void;

export type UserInputWitness = {
  beginOwnedPointer: (pointerId: number) => void;
  captureSequence: () => number;
  endOwnedPointer: (pointerId: number) => void;
  hasRecentUserInput: (withinMs: number) => boolean;
  hasOwnedPointer: () => boolean;
  readEvidenceAfter: (sequence: number) => UserInputEvidence | null;
  recordUserInput: (kind: UserInputEvidenceKind) => UserInputEvidence;
  subscribeEvidence: (listener: UserInputEvidenceListener) => () => void;
};

export type UserInputWitnessListenerDeps = {
  document: Document;
  ownerWindow: Window;
  witness: UserInputWitness;
};

const SCROLL_KEYS = new Set([
  "ArrowUp",
  "ArrowDown",
  "PageUp",
  "PageDown",
  "Home",
  "End",
  " ",
  "Spacebar",
]);

export function createUserInputWitness(deps: { now: () => number }): UserInputWitness {
  let evidence: UserInputEvidence | null = null;
  let recordedAt: number | null = null;
  let sequence = 0;
  const ownedPointers = new Set<number>();
  const evidenceListeners = new Set<UserInputEvidenceListener>();

  return {
    beginOwnedPointer: pointerId => {
      if (Number.isFinite(pointerId)) {
        ownedPointers.add(pointerId);
      }
    },
    captureSequence: () => sequence,
    endOwnedPointer: pointerId => { ownedPointers.delete(pointerId); },
    hasRecentUserInput: withinMs => {
      if (!Number.isFinite(withinMs) || withinMs < 0 || recordedAt === null) {
        return false;
      }
      const elapsed = deps.now() - recordedAt;
      return Number.isFinite(recordedAt) && Number.isFinite(elapsed)
        && elapsed >= 0 && elapsed <= withinMs;
    },
    hasOwnedPointer: () => ownedPointers.size > 0,
    readEvidenceAfter: capturedSequence =>
      Number.isSafeInteger(capturedSequence)
      && capturedSequence >= 0
      && evidence !== null
      && evidence.sequence > capturedSequence
        ? evidence
        : null,
    recordUserInput: kind => {
      recordedAt = deps.now();
      evidence = Object.freeze({ kind, sequence: ++sequence });
      for (const listener of evidenceListeners) {
        listener(evidence);
      }
      return evidence;
    },
    subscribeEvidence: listener => {
      evidenceListeners.add(listener);
      return () => { evidenceListeners.delete(listener); };
    },
  };
}

function isEditableTarget(target: EventTarget | null): boolean {
  if (!(target instanceof Element)) {
    return false;
  }
  if (target.closest("input, textarea, select") !== null) {
    return true;
  }
  for (let element: Element | null = target; element !== null; element = element.parentElement) {
    if (element instanceof HTMLElement) {
      const attribute = element.getAttribute("contenteditable");
      if (
        element.isContentEditable
        || element.contentEditable === "true"
        || (attribute !== null && attribute.toLowerCase() !== "false")
      ) {
        return true;
      }
    }
  }
  return false;
}

export function installUserInputWitnessListeners(
  deps: UserInputWitnessListenerDeps
): () => void {
  const onWheel = (): void => { deps.witness.recordUserInput("wheel"); };
  const onTouch = (): void => {
    if (!deps.witness.hasOwnedPointer()) {
      deps.witness.recordUserInput("touch");
    }
  };
  const onKeyDown = (event: KeyboardEvent): void => {
    if (
      event.ctrlKey
      || event.metaKey
      || event.altKey
      || !SCROLL_KEYS.has(event.key)
      || isEditableTarget(event.target)
    ) {
      return;
    }
    deps.witness.recordUserInput("scroll-key");
  };
  const onPointerDown = (event: PointerEvent): void => {
    if (
      event.button === 0
      && event.clientX >= deps.document.documentElement.clientWidth
      && event.clientX < deps.ownerWindow.innerWidth
      && event.clientY >= 0
      && event.clientY < deps.ownerWindow.innerHeight
    ) {
      deps.witness.recordUserInput("scrollbar-gutter");
    }
  };
  const options = { capture: true, passive: true } as const;
  deps.document.addEventListener("wheel", onWheel, options);
  deps.document.addEventListener("touchstart", onTouch, options);
  deps.document.addEventListener("touchmove", onTouch, options);
  deps.document.addEventListener("keydown", onKeyDown, options);
  deps.document.addEventListener("pointerdown", onPointerDown, options);

  let disposed = false;
  return () => {
    if (disposed) {
      return;
    }
    disposed = true;
    const removeOptions = { capture: true } as const;
    deps.document.removeEventListener("wheel", onWheel, removeOptions);
    deps.document.removeEventListener("touchstart", onTouch, removeOptions);
    deps.document.removeEventListener("touchmove", onTouch, removeOptions);
    deps.document.removeEventListener("keydown", onKeyDown, removeOptions);
    deps.document.removeEventListener("pointerdown", onPointerDown, removeOptions);
  };
}

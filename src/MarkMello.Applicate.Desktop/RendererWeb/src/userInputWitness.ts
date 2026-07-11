export type UserInputWitness = {
  hasRecentUserInput: (withinMs: number) => boolean;
  recordUserInput: () => void;
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
  let recordedAt: number | null = null;

  return {
    hasRecentUserInput: withinMs => {
      if (!Number.isFinite(withinMs) || withinMs < 0 || recordedAt === null) {
        return false;
      }
      const elapsed = deps.now() - recordedAt;
      return Number.isFinite(recordedAt) && Number.isFinite(elapsed)
        && elapsed >= 0 && elapsed <= withinMs;
    },
    recordUserInput: () => {
      recordedAt = deps.now();
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
  const record = (): void => deps.witness.recordUserInput();
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
    record();
  };
  const onPointerDown = (event: PointerEvent): void => {
    if (
      event.button === 0
      && event.clientX >= deps.document.documentElement.clientWidth
      && event.clientX < deps.ownerWindow.innerWidth
      && event.clientY >= 0
      && event.clientY < deps.ownerWindow.innerHeight
    ) {
      record();
    }
  };
  const options = { capture: true, passive: true } as const;
  deps.document.addEventListener("wheel", record, options);
  deps.document.addEventListener("touchstart", record, options);
  deps.document.addEventListener("touchmove", record, options);
  deps.document.addEventListener("keydown", onKeyDown, options);
  deps.document.addEventListener("pointerdown", onPointerDown, options);

  let disposed = false;
  return () => {
    if (disposed) {
      return;
    }
    disposed = true;
    const removeOptions = { capture: true } as const;
    deps.document.removeEventListener("wheel", record, removeOptions);
    deps.document.removeEventListener("touchstart", record, removeOptions);
    deps.document.removeEventListener("touchmove", record, removeOptions);
    deps.document.removeEventListener("keydown", onKeyDown, removeOptions);
    deps.document.removeEventListener("pointerdown", onPointerDown, removeOptions);
  };
}

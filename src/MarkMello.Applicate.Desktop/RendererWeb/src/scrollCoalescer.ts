export type ScrollCoalescerDeps = {
  postScroll: () => void;
  schedule: (cb: () => void) => void;
};

export function createScrollCoalescer(deps: ScrollCoalescerDeps): () => void {
  let pending = false;
  return function queuePostScroll(): void {
    if (pending) return;
    pending = true;
    deps.schedule(() => {
      pending = false;
      deps.postScroll();
    });
  };
}

const FRAME_YIELD_FALLBACK_MS = 32;

export function yieldAnimationFrameOrTimeout(): Promise<void> {
  return new Promise(resolve => {
    let resolved = false;
    let animationFrame: number | undefined;
    let timeout: number | undefined;
    const finish = (source: "animation-frame" | "timeout") => {
      if (resolved) return;
      resolved = true;
      if (source === "animation-frame" && timeout !== undefined) {
        window.clearTimeout(timeout);
      }
      if (source === "timeout" && animationFrame !== undefined) {
        window.cancelAnimationFrame(animationFrame);
      }
      resolve();
    };

    if (typeof window.requestAnimationFrame === "function") {
      timeout = window.setTimeout(() => finish("timeout"), FRAME_YIELD_FALLBACK_MS);
      animationFrame = window.requestAnimationFrame(() => finish("animation-frame"));
      return;
    }

    timeout = window.setTimeout(() => finish("timeout"), 0);
  });
}

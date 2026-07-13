import { afterEach, describe, expect, it, vi } from "vitest";
import { applyLoadDocument, type LoadDocumentDeps } from "../src/loadDocument";

describe("load-document first paint", () => {
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it("reports a cached document first paint only after two animation frames", () => {
    document.documentElement.innerHTML = "<body><main class='mm-document'><p>old</p></main></body>";
    const cachedFragment = document.createDocumentFragment();
    const cachedContent = document.createElement("p");
    cachedContent.textContent = "cached";
    cachedFragment.append(cachedContent);

    const animationFrames: FrameRequestCallback[] = [];
    vi.spyOn(window, "requestAnimationFrame").mockImplementation(callback => {
      animationFrames.push(callback);
      return animationFrames.length;
    });
    const notifyDocumentFirstPaint = vi.fn();
    const deps = {
      runInitialRenderPipeline: async () => {},
      cancelCurrentMathController: () => {},
      resetModuleGlobals: () => {},
      scrollWindowToTop: () => {},
      emitMark: () => {},
      ensureChromeNodes: () => {},
      applyTheme: () => {},
      debugLog: () => {},
      getCachedDocumentFragment: () => cachedFragment,
      restoreCachedScrollPosition: () => {},
      completeCachedDocumentLoad: () => {},
      notifyDocumentFirstPaint,
    } satisfies LoadDocumentDeps & {
      notifyDocumentFirstPaint: (renderId?: number) => void;
    };

    applyLoadDocument(
      { cacheKey: "cached-key", renderId: 17 },
      deps,
    );

    expect(notifyDocumentFirstPaint).not.toHaveBeenCalled();
    expect(animationFrames).toHaveLength(1);

    animationFrames.shift()?.(0);

    expect(notifyDocumentFirstPaint).not.toHaveBeenCalled();
    expect(animationFrames).toHaveLength(1);

    animationFrames.shift()?.(16);

    expect(notifyDocumentFirstPaint).toHaveBeenCalledOnce();
    expect(notifyDocumentFirstPaint).toHaveBeenCalledWith(17);
  });
});

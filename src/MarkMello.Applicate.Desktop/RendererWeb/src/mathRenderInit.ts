import type { MathReadinessController } from "./initialRenderPipeline";

type KatexLike = {
  render(
    tex: string,
    node: HTMLElement,
    opts: { throwOnError?: boolean; displayMode?: boolean; strict?: string; trust?: boolean },
  ): void;
};

export type RenderMathDeps = {
  katex: KatexLike | undefined;
  documentRoot: Document;
};

// Stage 4 Task 14 replaces this stub body with full queue+IO impl.
// The exported seam (renderMath) is stable; only the implementation changes.
export function renderMath(deps: RenderMathDeps): MathReadinessController {
  const mathNodes = Array.from(deps.documentRoot.querySelectorAll<HTMLElement>("[data-tex]"));
  const katex = deps.katex;
  if (!katex) {
    return {
      initialVisibleReady: Promise.resolve(),
      allMathRendered: Promise.resolve(),
      cancel: () => {},
    };
  }

  // Sync-preserving stub — Task 14 replaces this loop with queue dispatch.
  mathNodes.forEach((node) => {
    const tex = node.dataset["tex"];
    if (!tex) return;
    try {
      katex.render(tex, node, {
        throwOnError: false,
        displayMode: node.classList.contains("math-display"),
        strict: "warn",
        trust: false,
      });
      node.dataset["mmMathRendered"] = "true";
    } catch {
      node.dataset["mmMathRendered"] = "failed";
    }
  });

  return {
    initialVisibleReady: Promise.resolve(),
    allMathRendered: Promise.resolve(),
    cancel: () => {},
  };
}

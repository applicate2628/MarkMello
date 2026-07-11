const FIND_VISIBLE_SKIP_TAGS = new Set(["SCRIPT", "STYLE", "NOSCRIPT", "ASIDE"]);

const FIND_VISIBLE_SKIP_CLASSES = new Set<string>([
  "mm-minimap",
  "mm-minimap-viewport",
  "mm-width-handle",
  "mm-drop-overlay",
  "katex-mathml",
  "mm-find-bar",
]);

const FIND_VISIBLE_SKIP_SELECTOR = "pre.mm-mermaid.is-rendered";

export type VisibleTextTraversalCheckpoint = "before-work" | "after-yield";

export type SlicedVisibleTextWalkOptions = {
  shouldCancel: (checkpoint: VisibleTextTraversalCheckpoint) => boolean;
  yieldControl: () => Promise<void>;
  now?: () => number;
  sliceBudgetMs: number;
};

export type SlicedVisibleTextWalkResult = "complete" | "cancelled";

export function shouldSkipVisibleTextSubtree(element: Element): boolean {
  if (FIND_VISIBLE_SKIP_TAGS.has(element.tagName)) {
    return true;
  }
  for (const className of FIND_VISIBLE_SKIP_CLASSES) {
    if (element.classList.contains(className)) {
      return true;
    }
  }
  return element.matches?.(FIND_VISIBLE_SKIP_SELECTOR) === true;
}

export function walkVisibleTextNodes(root: Node): Text[] {
  const out: Text[] = [];

  const visit = (node: Node): void => {
    if (node.nodeType === Node.ELEMENT_NODE) {
      const element = node as Element;
      if (shouldSkipVisibleTextSubtree(element)) {
        return;
      }
    } else if (node.nodeType === Node.TEXT_NODE) {
      out.push(node as Text);
      return;
    }

    for (const child of Array.from(node.childNodes)) {
      visit(child);
    }
  };

  visit(root);
  return out;
}

export async function walkVisibleTextNodesSliced(
  root: Node,
  options: SlicedVisibleTextWalkOptions,
  visitTextNode: (node: Text) => void
): Promise<SlicedVisibleTextWalkResult> {
  const now = options.now ?? (() => performance.now());
  const stack: Node[] = [root];
  let sliceActive = false;
  let sliceStart = 0;

  const beginOrContinueWork = async (): Promise<boolean> => {
    if (!sliceActive) {
      if (options.shouldCancel("before-work")) {
        return true;
      }
      sliceStart = now();
      sliceActive = true;
      return false;
    }

    if (now() - sliceStart < options.sliceBudgetMs) {
      return false;
    }

    await options.yieldControl();
    if (options.shouldCancel("after-yield")) {
      return true;
    }
    if (options.shouldCancel("before-work")) {
      return true;
    }
    sliceStart = now();
    return false;
  };

  while (stack.length > 0) {
    if (await beginOrContinueWork()) {
      return "cancelled";
    }

    const node = stack.pop()!;
    if (node.nodeType === Node.ELEMENT_NODE) {
      const element = node as Element;
      if (shouldSkipVisibleTextSubtree(element)) {
        continue;
      }
    } else if (node.nodeType === Node.TEXT_NODE) {
      visitTextNode(node as Text);
      continue;
    }

    for (let index = node.childNodes.length - 1; index >= 0; index--) {
      const child = node.childNodes.item(index);
      if (child !== null) {
        stack.push(child);
      }
    }
  }

  return "complete";
}

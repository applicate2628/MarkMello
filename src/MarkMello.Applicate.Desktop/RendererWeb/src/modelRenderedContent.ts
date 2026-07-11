import {
  DocumentWindowModel,
  type RenderedContentState,
  type SectionModelEntry,
} from "./documentWindow";
import {
  MathRenderQueue,
  isTerminalMathState,
  type MathRenderQueueDeps,
  type MathRenderTask,
} from "./mathRenderQueue";

export type ModelRenderedContentPreparationStatus =
  | RenderedContentState
  | "cancelled"
  | "unavailable";

export type ModelRenderedContentProgressEvent = {
  type: "progress";
  sectionIndex: number;
  committed: boolean;
  pendingMathCount: number;
  renderedMathCount: number;
  failedMathCount: number;
  status: RenderedContentState;
};

export type ModelRenderedContentTerminalEvent = {
  type: "complete" | "cancelled" | "skipped-no-katex";
  pendingMathCount: number;
  renderedMathCount: number;
  failedMathCount: number;
  status: ModelRenderedContentPreparationStatus;
};

export type ModelRenderedContentEvent =
  | ModelRenderedContentProgressEvent
  | ModelRenderedContentTerminalEvent;

export type ModelRenderedContentPreparationResult = {
  status: ModelRenderedContentPreparationStatus;
  completed: boolean;
  cancelled: boolean;
  skippedNoKatex: boolean;
  attemptedSectionCount: number;
  committedSectionCount: number;
  renderedMathCount: number;
  failedMathCount: number;
  pendingMathCount: number;
};

export type PrepareDocumentWindowModelRenderedContentDeps = {
  ownerDocument: Document;
  katex: MathRenderQueueDeps["katex"] | undefined;
  yield: () => Promise<void>;
  timeBudgetMs?: number;
  now?: () => number;
  shouldContinue?: () => boolean;
  onProgress?: (event: ModelRenderedContentEvent) => void;
};

const DEFAULT_MODEL_RENDERED_CONTENT_TIME_BUDGET_MS = 7;

export async function prepareDocumentWindowModelRenderedContent(
  model: DocumentWindowModel,
  deps: PrepareDocumentWindowModelRenderedContentDeps
): Promise<ModelRenderedContentPreparationResult> {
  const shouldContinue = deps.shouldContinue ?? (() => true);
  const pendingEntryIndexes = model.getPendingRenderedContentEntryIndexes();
  let renderedMathCount = 0;
  let failedMathCount = 0;
  let attemptedSectionCount = 0;
  let committedSectionCount = 0;

  if (pendingEntryIndexes.length === 0) {
    return finish({
      attemptedSectionCount,
      cancelled: false,
      committedSectionCount,
      deps,
      failedMathCount,
      model,
      renderedMathCount,
      skippedNoKatex: false,
      status: model.getRenderedContentState(),
      type: "complete",
    });
  }

  if (!deps.katex) {
    return finish({
      attemptedSectionCount,
      cancelled: false,
      committedSectionCount,
      deps,
      failedMathCount,
      model,
      renderedMathCount,
      skippedNoKatex: true,
      status: "unavailable",
      type: "skipped-no-katex",
    });
  }

  for (const sectionIndex of pendingEntryIndexes) {
    if (!shouldContinue()) {
      return finish({
        attemptedSectionCount,
        cancelled: true,
        committedSectionCount,
        deps,
        failedMathCount,
        model,
        renderedMathCount,
        skippedNoKatex: false,
        status: "cancelled",
        type: "cancelled",
      });
    }

    const entry = model.sections[sectionIndex];
    const section = entry === undefined
      ? null
      : readPendingRenderedSection(entry, deps.ownerDocument);
    if (section === null || section.pendingMathNodes.length === 0) {
      continue;
    }

    attemptedSectionCount++;
    let queue: MathRenderQueue | undefined;
    queue = new MathRenderQueue({
      katex: deps.katex,
      now: deps.now ?? readNow,
      timeBudgetMs: deps.timeBudgetMs ?? DEFAULT_MODEL_RENDERED_CONTENT_TIME_BUDGET_MS,
      yield: async () => {
        await deps.yield();
        if (!shouldContinue()) {
          queue?.cancel();
        }
      },
    });

    const unsubscribe = queue.onTaskComplete(node => {
      renderedMathCount++;
    });
    for (const node of section.pendingMathNodes) {
      queue.enqueue(readMathRenderTask(node), "low");
    }
    try {
      await queue.start();
    } finally {
      unsubscribe();
    }

    if (!shouldContinue() || section.allMathNodes.some(node => !isTerminalMathState(node.dataset["mmMathRendered"]))) {
      return finish({
        attemptedSectionCount,
        cancelled: true,
        committedSectionCount,
        deps,
        failedMathCount,
        model,
        renderedMathCount,
        skippedNoKatex: false,
        status: "cancelled",
        type: "cancelled",
      });
    }

    const sectionFailedCount = section.allMathNodes.filter(node => node.dataset["mmMathRendered"] === "failed").length;
    const renderedHtml = serializeRenderedSection(section.template);
    const status = sectionFailedCount > 0 ? "ready-with-failures" : "ready";
    const commit = model.commitRenderedFormulaFragment(sectionIndex, renderedHtml, { status });
    if (commit.changed) {
      committedSectionCount++;
      failedMathCount += sectionFailedCount;
    }
    deps.onProgress?.({
      committed: commit.changed,
      failedMathCount,
      pendingMathCount: commit.pendingMathCount,
      renderedMathCount,
      sectionIndex,
      status: model.getRenderedContentState(),
      type: "progress",
    });
  }

  return finish({
    attemptedSectionCount,
    cancelled: false,
    committedSectionCount,
    deps,
    failedMathCount,
    model,
    renderedMathCount,
    skippedNoKatex: false,
    status: model.getRenderedContentState(),
    type: "complete",
  });
}

type PendingRenderedSection = {
  template: HTMLTemplateElement;
  allMathNodes: HTMLElement[];
  pendingMathNodes: HTMLElement[];
};

function readPendingRenderedSection(
  entry: SectionModelEntry,
  ownerDocument: Document
): PendingRenderedSection | null {
  if (typeof entry.html !== "string" || !entry.html.includes("data-tex")) {
    return null;
  }

  const template = ownerDocument.createElement("template");
  template.innerHTML = entry.html;
  const allMathNodes = Array.from(template.content.querySelectorAll<HTMLElement>("[data-tex]"));
  const pendingMathNodes = allMathNodes.filter(node => !isTerminalMathState(node.dataset["mmMathRendered"]));
  return { allMathNodes, pendingMathNodes, template };
}

function readMathRenderTask(node: HTMLElement): MathRenderTask {
  return {
    displayMode: node.classList.contains("math-display"),
    node,
    tex: node.dataset["tex"] ?? "",
  };
}

function serializeRenderedSection(template: HTMLTemplateElement): string {
  const firstElement = template.content.firstElementChild;
  return firstElement instanceof HTMLElement ? firstElement.outerHTML : template.innerHTML;
}

function finish(args: {
  deps: PrepareDocumentWindowModelRenderedContentDeps;
  model: DocumentWindowModel;
  type: "complete" | "cancelled" | "skipped-no-katex";
  status: ModelRenderedContentPreparationStatus;
  cancelled: boolean;
  skippedNoKatex: boolean;
  attemptedSectionCount: number;
  committedSectionCount: number;
  renderedMathCount: number;
  failedMathCount: number;
}): ModelRenderedContentPreparationResult {
  const pendingMathCount = countPendingMath(args.model, args.deps.ownerDocument);
  const completed = args.status === "not-needed"
    || args.status === "ready"
    || args.status === "ready-with-failures";
  args.deps.onProgress?.({
    failedMathCount: args.failedMathCount,
    pendingMathCount,
    renderedMathCount: args.renderedMathCount,
    status: args.status,
    type: args.type,
  });
  return {
    attemptedSectionCount: args.attemptedSectionCount,
    cancelled: args.cancelled,
    committedSectionCount: args.committedSectionCount,
    completed,
    failedMathCount: args.failedMathCount,
    pendingMathCount,
    renderedMathCount: args.renderedMathCount,
    skippedNoKatex: args.skippedNoKatex,
    status: args.status,
  };
}

function countPendingMath(model: DocumentWindowModel, ownerDocument: Document): number {
  let pendingMathCount = 0;
  for (const entry of model.sections) {
    const section = readPendingRenderedSection(entry, ownerDocument);
    pendingMathCount += section?.pendingMathNodes.length ?? 0;
  }
  return pendingMathCount;
}

function readNow(): number {
  return typeof performance === "undefined" ? Date.now() : performance.now();
}

import {
  collectLiveDocumentSectionElements,
  type DocumentWindowModel,
  type SectionModelEntry,
  type WindowRange,
} from "./documentWindow";
import {
  captureReadingAnchor,
  scrollTopForReadingAnchor,
  type EnsureSectionRenderedOptions,
  type VirtualizedDocumentWindowController,
  type VirtualizedWindowOperation,
} from "./virtualizedDocumentWindow";

export type WindowTargetDescriptor =
  | { kind: "section"; sectionIndex: number }
  | { kind: "block"; blockIndex: number }
  | { kind: "heading-anchor"; anchor: string }
  | { kind: "source-line"; sourceLine: number }
  | { kind: "document-y"; documentY: number }
  | {
      kind: "find-match";
      matchId: string;
      blockIndex?: number;
      startBlockIndex?: number;
      endBlockIndex?: number;
    };

export type WindowTargetActionKind = "navigate" | "query";

export type WindowTargetContext = {
  entry: SectionModelEntry;
  sectionIndex: number;
  sectionTop: number;
  sectionHeight: number;
  element: HTMLElement | null;
  targetElement: HTMLElement | null;
  range: WindowRange | null;
};

export type WindowTargetController = Pick<
  VirtualizedDocumentWindowController,
  "ensureSectionRangeRendered" | "ensureSectionRendered" | "getCurrentRange" | "isSectionRendered"
>;

export type RenderWindowTargetThenActInput<T> = {
  virtualizationEnabled: boolean;
  model: DocumentWindowModel | null;
  operation?: WindowTargetOperation;
  controller: WindowTargetController | null;
  main: HTMLElement;
  root: Element & { scrollTop: number };
  ownerWindow: Window & typeof globalThis;
  descriptor: WindowTargetDescriptor;
  actionKind: WindowTargetActionKind;
  action: (context: WindowTargetContext) => T | Promise<T>;
  legacyAction: () => T | Promise<T>;
  layoutTicks?: 1 | 2;
};

export type WindowTargetOperation = VirtualizedWindowOperation & {
  documentEpoch: number;
  operationEpoch: number;
  isCurrent: () => boolean;
  scheduleFrameTransaction: (work: () => void) => boolean;
};

export type ReadWindowTargetContextInput = {
  controller: WindowTargetController | null;
  main: HTMLElement;
  model: DocumentWindowModel | null;
  ownerWindow: Window & typeof globalThis;
};

type WindowTargetResolution = {
  entry: SectionModelEntry;
  sectionIndex: number;
  range: WindowRange;
  descriptor: WindowTargetDescriptor;
};

export async function renderWindowTargetThenAct<T>(input: RenderWindowTargetThenActInput<T>): Promise<T | undefined> {
  const model = input.model;
  const controller = input.controller;
  if (!input.virtualizationEnabled) {
    return input.legacyAction();
  }
  const operation = input.operation;
  if (model === null || controller === null || operation === undefined) {
    return input.legacyAction();
  }

  const resolution = resolveWindowTarget(model, input.descriptor);
  if (resolution === null) {
    return input.legacyAction();
  }

  const originalAnchor = input.actionKind === "query"
    ? captureReadingAnchor(collectLiveDocumentSectionElements(input.main))
    : null;
  const originalRange = input.actionKind === "query" ? controller.getCurrentRange() : null;
  let didRender = false;
  let actionResult: T | Promise<T> | undefined;

  try {
    const delivered = await deliverOperationFrame(operation, () => {
      didRender = ensureResolutionRendered(controller, resolution, operation);
      if (!operation.isCurrent()) {
        return;
      }
      actionResult = input.action(readWindowTargetContext(input, resolution));
      if (input.actionKind === "query" && didRender) {
        operation.requestScrollTop(
          scrollTopForReadingAnchor(model, originalAnchor) ?? 0,
          "query-anchor-preserve"
        );
      }
    });
    if (!delivered || !operation.isCurrent()) {
      return undefined;
    }
    const result = await actionResult;
    if (!operation.isCurrent()) {
      return undefined;
    }
    return result;
  } finally {
    if (input.actionKind === "query" && didRender && operation.isCurrent()) {
      await restoreReadingAnchor({
        controller,
        model,
        operation,
        originalAnchor,
        originalRange,
      });
    }
  }
}

export function resolveWindowTarget(
  model: DocumentWindowModel,
  descriptor: WindowTargetDescriptor
): WindowTargetResolution | null {
  switch (descriptor.kind) {
    case "section":
      return resolveSectionIndex(model, descriptor.sectionIndex, descriptor);
    case "block": {
      const entry = model.getEntryContainingBlockIndex(descriptor.blockIndex);
      return entry === undefined ? null : resolutionForEntry(model, entry, descriptor);
    }
    case "heading-anchor": {
      const entry = model.getEntryByHeadingAnchor(descriptor.anchor);
      return entry === undefined ? null : resolutionForEntry(model, entry, descriptor);
    }
    case "source-line": {
      const entry = model.getEntryBySourceLine(descriptor.sourceLine);
      return entry === undefined ? null : resolutionForEntry(model, entry, descriptor);
    }
    case "document-y":
      return resolveSectionIndex(model, model.sectionIndexAtDocumentY(descriptor.documentY), descriptor);
    case "find-match":
      return resolveFindMatch(model, descriptor);
  }
}

function resolveFindMatch(
  model: DocumentWindowModel,
  descriptor: Extract<WindowTargetDescriptor, { kind: "find-match" }>
): WindowTargetResolution | null {
  if (descriptor.blockIndex !== undefined) {
    const entry = model.getEntryContainingBlockIndex(descriptor.blockIndex);
    return entry === undefined ? null : resolutionForEntry(model, entry, descriptor);
  }

  if (descriptor.startBlockIndex === undefined || descriptor.endBlockIndex === undefined) {
    return null;
  }

  const start = model.getEntryContainingBlockIndex(descriptor.startBlockIndex);
  const end = model.getEntryContainingBlockIndex(descriptor.endBlockIndex);
  if (start === undefined || end === undefined) {
    return null;
  }

  const startSection = findSectionArrayIndex(model, start);
  const endSection = findSectionArrayIndex(model, end);
  if (startSection < 0 || endSection < 0) {
    return null;
  }

  return {
    descriptor,
    entry: start,
    range: {
      end: Math.max(startSection, endSection),
      start: Math.min(startSection, endSection),
    },
    sectionIndex: startSection,
  };
}

function resolveSectionIndex(
  model: DocumentWindowModel,
  sectionIndex: number,
  descriptor: WindowTargetDescriptor
): WindowTargetResolution | null {
  if (!Number.isFinite(sectionIndex)) {
    return null;
  }

  const normalized = Math.floor(sectionIndex);
  const entry = model.sections[normalized];
  if (entry === undefined) {
    return null;
  }

  return {
    descriptor,
    entry,
    range: { end: normalized, start: normalized },
    sectionIndex: normalized,
  };
}

function resolutionForEntry(
  model: DocumentWindowModel,
  entry: SectionModelEntry,
  descriptor: WindowTargetDescriptor
): WindowTargetResolution | null {
  const sectionIndex = findSectionArrayIndex(model, entry);
  if (sectionIndex < 0) {
    return null;
  }

  return {
    descriptor,
    entry,
    range: { end: sectionIndex, start: sectionIndex },
    sectionIndex,
  };
}

function ensureResolutionRendered(
  controller: WindowTargetController,
  resolution: WindowTargetResolution,
  operation: WindowTargetOperation
): boolean {
  if (
    resolution.range.start === resolution.range.end
    && controller.isSectionRendered(resolution.range.start)
  ) {
    return false;
  }

  const options: EnsureSectionRenderedOptions = { operation, preserveAnchor: false };
  return resolution.range.start === resolution.range.end
    ? controller.ensureSectionRendered(resolution.range.start, options)
    : controller.ensureSectionRangeRendered(resolution.range.start, resolution.range.end, options);
}

export function readWindowTargetContext(
  input: ReadWindowTargetContextInput,
  resolution: WindowTargetResolution
): WindowTargetContext {
  const sectionElement = findSectionElement(input.main, resolution.entry);
  return {
    element: sectionElement,
    entry: resolution.entry,
    range: input.controller?.getCurrentRange() ?? null,
    sectionHeight: input.model?.sectionEffectiveHeight(resolution.sectionIndex) ?? 0,
    sectionIndex: resolution.sectionIndex,
    sectionTop: input.model?.sectionTop(resolution.sectionIndex) ?? 0,
    targetElement: findTargetElement(input.ownerWindow, sectionElement, resolution.descriptor),
  };
}

function findTargetElement(
  ownerWindow: Window & typeof globalThis,
  sectionElement: HTMLElement | null,
  descriptor: WindowTargetDescriptor
): HTMLElement | null {
  if (sectionElement === null) {
    return null;
  }

  switch (descriptor.kind) {
    case "block":
      return findBlockElement(sectionElement, descriptor.blockIndex);
    case "heading-anchor": {
      const anchor = descriptor.anchor.startsWith("#") ? descriptor.anchor.slice(1) : descriptor.anchor;
      return findElementByIdWithinSection(ownerWindow, sectionElement, anchor);
    }
    case "source-line":
      return findSourceLineElement(sectionElement, descriptor.sourceLine);
    case "find-match":
      return descriptor.blockIndex === undefined ? sectionElement : findBlockElement(sectionElement, descriptor.blockIndex);
    case "document-y":
    case "section":
      return sectionElement;
  }
}

function findElementByIdWithinSection(
  ownerWindow: Window & typeof globalThis,
  sectionElement: HTMLElement,
  id: string
): HTMLElement | null {
  if (sectionElement.id === id) {
    return sectionElement;
  }

  for (const element of Array.from(sectionElement.querySelectorAll<HTMLElement>("[id]"))) {
    if (element instanceof ownerWindow.HTMLElement && element.id === id) {
      return element;
    }
  }
  return null;
}

function findSectionElement(main: HTMLElement, entry: SectionModelEntry): HTMLElement | null {
  for (const child of Array.from(main.children)) {
    if (child instanceof main.ownerDocument.defaultView!.HTMLElement && readElementBlockIndex(child) === entry.blockIndex) {
      return child;
    }
  }
  return null;
}

function findBlockElement(sectionElement: HTMLElement, blockIndex: number): HTMLElement | null {
  if (readElementBlockIndex(sectionElement) === blockIndex) {
    return sectionElement;
  }

  for (const element of Array.from(sectionElement.querySelectorAll<HTMLElement>("[data-mm-block-index]"))) {
    if (readElementBlockIndex(element) === blockIndex) {
      return element;
    }
  }
  return null;
}

function findSourceLineElement(sectionElement: HTMLElement, sourceLine: number): HTMLElement | null {
  if (!Number.isFinite(sourceLine)) {
    return null;
  }

  const normalizedLine = Math.max(0, Math.floor(sourceLine));
  for (const element of Array.from(sectionElement.querySelectorAll<HTMLElement>("[data-mm-source-line]"))) {
    const start = parseNonNegativeInt(element.dataset["mmSourceLine"]);
    if (start === null) {
      continue;
    }

    const end = Math.max(start, parseNonNegativeInt(element.dataset["mmSourceEndLine"]) ?? start);
    if (normalizedLine >= start && normalizedLine <= end) {
      return element;
    }
  }
  return null;
}

async function restoreReadingAnchor(input: {
  controller: WindowTargetController;
  model: DocumentWindowModel;
  operation: WindowTargetOperation;
  originalAnchor: ReturnType<typeof captureReadingAnchor>;
  originalRange: WindowRange | null;
}): Promise<void> {
  await deliverOperationFrame(input.operation, () => {
    if (input.originalRange !== null) {
      input.controller.ensureSectionRangeRendered(input.originalRange.start, input.originalRange.end, {
        force: true,
        operation: input.operation,
        preserveAnchor: false,
      });
    } else if (input.originalAnchor !== null) {
      const entry = input.model.getEntryByBlockIndex(input.originalAnchor.blockIndex);
      if (entry !== undefined) {
        input.controller.ensureSectionRendered(entry.sectionIndex, {
          force: true,
          operation: input.operation,
          preserveAnchor: false,
        });
      }
    }
    input.operation.requestScrollTop(
      scrollTopForReadingAnchor(input.model, input.originalAnchor) ?? 0,
      "query-anchor-restore"
    );
  });
}

function deliverOperationFrame(
  operation: WindowTargetOperation,
  work: () => void
): Promise<boolean> {
  if (!operation.isCurrent()) {
    return Promise.resolve(false);
  }
  return new Promise<boolean>((resolve, reject) => {
    const scheduled = operation.scheduleFrameTransaction(() => {
      if (!operation.isCurrent()) {
        resolve(false);
        return;
      }
      try {
        work();
        resolve(true);
      } catch (error) {
        reject(error);
        throw error;
      }
    });
    if (!scheduled) {
      resolve(false);
    }
  });
}

function findSectionArrayIndex(model: DocumentWindowModel, entry: SectionModelEntry): number {
  return model.sections.findIndex(candidate => candidate.blockIndex === entry.blockIndex);
}

function readElementBlockIndex(element: Element): number | null {
  const raw = element instanceof HTMLElement ? element.dataset["mmBlockIndex"] : undefined;
  if (raw === undefined || raw.trim() === "") {
    return null;
  }

  const parsed = Number.parseInt(raw, 10);
  return Number.isFinite(parsed) ? parsed : null;
}

function parseNonNegativeInt(value: string | undefined): number | null {
  if (value === undefined || value.trim() === "") {
    return null;
  }

  const parsed = Number.parseInt(value, 10);
  return Number.isFinite(parsed) && parsed >= 0 ? parsed : null;
}

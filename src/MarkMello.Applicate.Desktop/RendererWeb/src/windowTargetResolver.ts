import {
  type DocumentWindowModel,
  type ScrollAnchor,
  type SectionModelEntry,
  type WindowRange,
} from "./documentWindow";
import type { EnsureSectionRenderedOptions, VirtualizedDocumentWindowController } from "./virtualizedDocumentWindow";

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

export async function renderWindowTargetThenAct<T>(input: RenderWindowTargetThenActInput<T>): Promise<T> {
  const model = input.model;
  const controller = input.controller;
  if (!input.virtualizationEnabled || model === null || controller === null) {
    return input.legacyAction();
  }

  const resolution = resolveWindowTarget(model, input.descriptor);
  if (resolution === null) {
    return input.legacyAction();
  }

  const originalAnchor = input.actionKind === "query" ? model.captureAnchor(input.root.scrollTop) : null;
  const originalRange = input.actionKind === "query" ? controller.getCurrentRange() : null;
  const didRender = ensureResolutionRendered(controller, resolution);
  if (didRender) {
    await waitForLayoutTicks(input.ownerWindow, input.layoutTicks ?? 1);
  }

  try {
    return await input.action(readWindowTargetContext(input, resolution));
  } finally {
    if (input.actionKind === "query" && didRender && originalAnchor !== null) {
      restoreReadingAnchor({
        controller,
        model,
        originalAnchor,
        originalRange,
        root: input.root,
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
  resolution: WindowTargetResolution
): boolean {
  if (
    resolution.range.start === resolution.range.end
    && controller.isSectionRendered(resolution.range.start)
  ) {
    return false;
  }

  const options: EnsureSectionRenderedOptions = { preserveAnchor: true };
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
      const element = sectionElement.ownerDocument.getElementById(anchor);
      return element instanceof ownerWindow.HTMLElement && sectionElement.contains(element) ? element : null;
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

function restoreReadingAnchor(input: {
  controller: WindowTargetController;
  model: DocumentWindowModel;
  originalAnchor: ScrollAnchor;
  originalRange: WindowRange | null;
  root: Element & { scrollTop: number };
}): void {
  if (input.originalRange !== null) {
    input.controller.ensureSectionRangeRendered(input.originalRange.start, input.originalRange.end, {
      force: true,
      preserveAnchor: false,
    });
  } else if (input.originalAnchor.sectionIndex >= 0) {
    input.controller.ensureSectionRendered(input.originalAnchor.sectionIndex, {
      force: true,
      preserveAnchor: false,
    });
  }

  input.root.scrollTop = input.model.scrollTopForAnchor(input.originalAnchor);
}

function waitForLayoutTicks(ownerWindow: Window & typeof globalThis, count: 1 | 2): Promise<void> {
  const tick = (): Promise<void> => new Promise(resolve => {
    if (typeof ownerWindow.requestAnimationFrame === "function") {
      ownerWindow.requestAnimationFrame(() => resolve());
      return;
    }

    ownerWindow.setTimeout(() => resolve(), 0);
  });

  return count === 1 ? tick() : tick().then(tick);
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

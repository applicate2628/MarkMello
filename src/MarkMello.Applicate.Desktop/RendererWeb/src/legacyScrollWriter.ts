export const LEGACY_SCROLL_WRITER_TRACE_ID = "mm-virt-legacy-scroll-writer-flag-on";

export type LegacyScrollWriterTraceEvent = {
  id: typeof LEGACY_SCROLL_WRITER_TRACE_ID;
  operation: keyof LegacyScrollWriter;
};

export type LegacyScrollWriterTrace = (event: LegacyScrollWriterTraceEvent) => void;

export type LegacyScrollToOptions = {
  behavior?: ScrollBehavior;
  left?: number;
};

export type LegacyScrollWriter = {
  legacyScrollTo: (top: number, options?: LegacyScrollToOptions) => void;
  legacyScrollBy: (delta: number) => void;
  legacyScrollIntoView: (element: Element, options?: ScrollIntoViewOptions) => void;
  legacySetScrollTop: (element: Element & { scrollTop: number }, value: number) => void;
};

export type LegacyScrollWriterDeps = {
  developmentDiagnosticsEnabled: boolean;
  ownerWindow: Window;
  trace?: LegacyScrollWriterTrace;
  virtualizationEnabled: boolean;
};

export function createLegacyScrollWriter(deps: LegacyScrollWriterDeps): LegacyScrollWriter {
  function trace(operation: keyof LegacyScrollWriter): void {
    if (!deps.virtualizationEnabled || !deps.developmentDiagnosticsEnabled) {
      return;
    }
    try {
      deps.trace?.({ id: LEGACY_SCROLL_WRITER_TRACE_ID, operation });
    } catch {
      // Diagnostics must never alter legacy scroll behavior.
    }
  }

  return {
    legacyScrollTo(top, options) {
      trace("legacyScrollTo");
      const init: ScrollToOptions = { top };
      if (options?.left !== undefined) {
        init.left = options.left;
      }
      if (options?.behavior !== undefined) {
        init.behavior = options.behavior;
      }
      deps.ownerWindow.scrollTo(init);
    },
    legacyScrollBy(delta) {
      trace("legacyScrollBy");
      deps.ownerWindow.scrollBy({ top: delta, behavior: "instant" as ScrollBehavior });
    },
    legacyScrollIntoView(element, options) {
      trace("legacyScrollIntoView");
      element.scrollIntoView(options);
    },
    legacySetScrollTop(element, value) {
      trace("legacySetScrollTop");
      element.scrollTop = value;
    },
  };
}

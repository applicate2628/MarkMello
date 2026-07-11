import { describe, expect, it } from "vitest";
import {
  LEGACY_SCROLL_WRITER_TRACE_ID,
  createLegacyScrollWriter,
  type LegacyScrollWriterTrace,
} from "../src/legacyScrollWriter";

function createHarness(options: {
  developmentDiagnosticsEnabled?: boolean;
  trace?: LegacyScrollWriterTrace;
  virtualizationEnabled?: boolean;
} = {}) {
  const scrollToCalls: unknown[][] = [];
  const scrollByCalls: unknown[][] = [];
  const ownerWindow = {
    scrollTo: (...args: unknown[]) => {
      scrollToCalls.push(args);
    },
    scrollBy: (...args: unknown[]) => {
      scrollByCalls.push(args);
    },
  } as unknown as Window;
  const writer = createLegacyScrollWriter({
    developmentDiagnosticsEnabled: options.developmentDiagnosticsEnabled ?? false,
    ownerWindow,
    trace: options.trace,
    virtualizationEnabled: options.virtualizationEnabled ?? false,
  });
  return { scrollByCalls, scrollToCalls, writer };
}

describe("legacy scroll writer", () => {
  it("delegates root scroll operations without changing arguments", () => {
    const { scrollByCalls, scrollToCalls, writer } = createHarness();

    writer.legacyScrollTo(25, { left: 0, behavior: "instant" as ScrollBehavior });
    writer.legacyScrollBy(40);

    expect(scrollToCalls).toEqual([[{ left: 0, top: 25, behavior: "instant" }]]);
    expect(scrollByCalls).toEqual([[{ top: 40, behavior: "instant" }]]);
  });

  it("preserves the distinction between omitted and supplied optional left values", () => {
    const { scrollToCalls, writer } = createHarness();

    writer.legacyScrollTo(10, { behavior: "instant" as ScrollBehavior });
    writer.legacyScrollTo(20, { left: 0, behavior: "instant" as ScrollBehavior });

    expect(scrollToCalls).toEqual([
      [{ top: 10, behavior: "instant" }],
      [{ left: 0, top: 20, behavior: "instant" }],
    ]);
  });

  it("delegates element scroll operations", () => {
    const { writer } = createHarness();
    const scrollIntoViewCalls: unknown[][] = [];
    const element = {
      scrollTop: 0,
      scrollIntoView: (...args: unknown[]) => {
        scrollIntoViewCalls.push(args);
      },
    } as unknown as HTMLElement;

    writer.legacyScrollIntoView(element, { block: "center" });
    writer.legacySetScrollTop(element, 44);

    expect(scrollIntoViewCalls).toEqual([[{ block: "center" }]]);
    expect(element.scrollTop).toBe(44);
  });

  it("traces facade use only when virtualization and development diagnostics are both enabled", () => {
    const emissions: Array<{ id: string; operation: string }> = [];
    const trace: LegacyScrollWriterTrace = event => {
      emissions.push(event);
    };

    createHarness({ virtualizationEnabled: false, developmentDiagnosticsEnabled: true, trace })
      .writer.legacyScrollTo(1);
    createHarness({ virtualizationEnabled: true, developmentDiagnosticsEnabled: false, trace })
      .writer.legacyScrollTo(2);
    createHarness({ virtualizationEnabled: true, developmentDiagnosticsEnabled: true, trace })
      .writer.legacyScrollTo(3);

    expect(emissions).toEqual([
      { id: LEGACY_SCROLL_WRITER_TRACE_ID, operation: "legacyScrollTo" },
    ]);
  });

  it("contains trace sink failures without suppressing delegation", () => {
    const { scrollToCalls, writer } = createHarness({
      virtualizationEnabled: true,
      developmentDiagnosticsEnabled: true,
      trace: () => {
        throw new Error("trace failed");
      },
    });

    expect(() => writer.legacyScrollTo(10)).not.toThrow();
    expect(scrollToCalls).toEqual([[{ top: 10 }]]);
  });
});

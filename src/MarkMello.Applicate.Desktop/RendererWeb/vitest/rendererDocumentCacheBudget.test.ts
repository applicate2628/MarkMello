import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { applyLoadDocument, type LoadDocumentDeps } from "../src/loadDocument";

type HostBridge = (message: unknown) => void;

type EvictionResult = {
  totalWeight: number;
  evicted: number;
  overBudget: boolean;
};

type RendererCacheInternals = {
  PROCESSED_DOCUMENT_CACHE_WEIGHT_BUDGET: number;
  PROCESSED_DOCUMENT_CACHE_MAX_ENTRIES: number;
  evictProcessedDocumentCacheEntries: (
    cache: Map<string, { weight: number }>,
    justStoredKey: string,
    pinnedIncomingKey: string | null | undefined,
  ) => EvictionResult;
};

// The eviction rule is the production owner of both caps, exported from
// renderer.ts alongside the other internals the bench drives directly. Fail
// loudly rather than silently degrading into a vacuous suite if it is gone.
function requireCacheInternals(rendererModule: object): RendererCacheInternals {
  const candidate = rendererModule as Partial<RendererCacheInternals>;
  if (typeof candidate.evictProcessedDocumentCacheEntries !== "function"
    || typeof candidate.PROCESSED_DOCUMENT_CACHE_WEIGHT_BUDGET !== "number"
    || typeof candidate.PROCESSED_DOCUMENT_CACHE_MAX_ENTRIES !== "number") {
    throw new Error("renderer.ts does not export the processed-document cache budget internals");
  }

  return candidate as RendererCacheInternals;
}

async function loadRendererWithMessages() {
  vi.resetModules();
  // Mirrors the SHIPPED shell (ApplicateHtmlDocumentTemplate BuildShell): these
  // loads carry a documentName and applyLoadDocument writes document.title,
  // which happy-dom's setter cannot do without a <head><title>.
  document.documentElement.innerHTML =
    `<head><title>MarkMello</title></head>`
    + `<body><main class="mm-document"></main></body>`;
  const messages: unknown[] = [];
  (window as unknown as { chrome: { webview: { postMessage: (m: unknown) => void } } }).chrome = {
    webview: { postMessage: (m: unknown) => messages.push(m) }
  };
  const rendererModule = await import("../src/renderer");
  const load = (window as unknown as { __mmRendererLoad: HostBridge }).__mmRendererLoad;
  return { load, messages, rendererModule };
}

async function letPipelineSettle(): Promise<void> {
  // Same budget the neighbouring cache suite uses: post-ready enhancements must
  // complete before preserveCurrentProcessedDocument will publish an entry.
  await new Promise(resolve => setTimeout(resolve, 700));
}

function perfMarkDetails(messages: unknown[], name: string): Array<Record<string, unknown>> {
  return messages
    .filter((message): message is { type: "perf-mark"; name: string; detail?: string } =>
      typeof message === "object"
      && message !== null
      && (message as { type?: unknown }).type === "perf-mark"
      && (message as { name?: unknown }).name === name)
    // `perf-mark.detail` is a JSON *string* by IPC contract, so its fields are
    // read back by parsing rather than by the contract itself.
    .map(mark => (typeof mark.detail === "string" ? JSON.parse(mark.detail) : {}) as Record<string, unknown>);
}

function requireDetail(details: Array<Record<string, unknown>>, index: number): Record<string, unknown> {
  const detail = details[index];
  if (detail === undefined) {
    throw new Error(`no perf-mark detail at index ${index} (got ${details.length})`);
  }

  return detail;
}

function weighedCache(weights: Array<[string, number]>): Map<string, { weight: number }> {
  return new Map(weights.map(([key, weight]) => [key, { weight }]));
}

function residentWeight(cache: Map<string, { weight: number }>): number {
  let sum = 0;
  for (const entry of cache.values()) {
    sum += entry.weight;
  }
  return sum;
}

beforeEach(() => {
  delete (window as unknown as { chrome?: unknown }).chrome;
});

afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

describe("processed-document cache weight budget", () => {
  it("keeps reported weight equal to a recomputed sum of the entries that survive", async () => {
    const { rendererModule } = await loadRendererWithMessages();
    const {
      PROCESSED_DOCUMENT_CACHE_WEIGHT_BUDGET: budget,
      PROCESSED_DOCUMENT_CACHE_MAX_ENTRIES: maxEntries,
      evictProcessedDocumentCacheEntries: evict,
    } = requireCacheInternals(rendererModule);

    // The sizing decision itself, pinned. 500 000 holds the operator's whole
    // measured 7-document session (489 548 on the minimap host) and stays under
    // the ceiling rule `<= 4 x heaviest document` (4 x 242 538 = 970 152).
    expect(budget).toBe(500_000);
    expect(maxEntries).toBe(12);

    // An arbitrary store/evict sequence: distinct weights, insertion order
    // meaningful, over budget on purpose so several entries have to go.
    const cache = weighedCache([
      ["a", 90_000],
      ["b", 130_000],
      ["c", 70_000],
      ["d", 185_160],
      ["e", 242_538],
    ]);
    expect(residentWeight(cache)).toBe(717_698);

    const result = evict(cache, "e", null);

    // Dropping "a" (90 000) then "b" (130 000) is the least that gets under
    // 500 000; "c" must therefore survive.
    expect([...cache.keys()]).toEqual(["c", "d", "e"]);
    expect(result.evicted).toBe(2);
    // The accounting is maintained incrementally across the evictions rather
    // than re-summed, so this is the assertion that catches a bad decrement.
    expect(result.totalWeight).toBe(497_698);
    expect(result.totalWeight).toBe(residentWeight(cache));
    expect(result.overBudget).toBe(false);
  });

  it("stores an entry heavier than the whole budget, discloses the overshoot, and terminates", async () => {
    const { rendererModule } = await loadRendererWithMessages();
    const {
      PROCESSED_DOCUMENT_CACHE_WEIGHT_BUDGET: budget,
      evictProcessedDocumentCacheEntries: evict,
    } = requireCacheInternals(rendererModule);

    // Refusing to store this would make a heavy document permanently
    // uncacheable — strictly worse than the count cap it replaces, which always
    // admitted at least one entry regardless of size.
    const cache = weighedCache([
      ["old", 40_000],
      ["huge", budget + 1],
    ]);

    const result = evict(cache, "huge", null);

    expect(cache.has("huge")).toBe(true);
    expect(cache.get("huge")!.weight).toBe(budget + 1);
    expect(cache.has("old")).toBe(false);
    expect(result.evicted).toBe(1);
    expect(result.totalWeight).toBe(budget + 1);
    expect(result.overBudget).toBe(true);
    expect(result.totalWeight).toBe(residentWeight(cache));
  });

  it("terminates when the just-stored and pinned entries alone exceed the budget", async () => {
    const { rendererModule } = await loadRendererWithMessages();
    const {
      PROCESSED_DOCUMENT_CACHE_WEIGHT_BUDGET: budget,
      evictProcessedDocumentCacheEntries: evict,
    } = requireCacheInternals(rendererModule);

    // Both survivors are non-evictable, so the loop has no admissible victim
    // and must exit on its termination guard instead of spinning.
    const cache = weighedCache([
      ["incoming", budget],
      ["filler", 10_000],
      ["stored", budget],
    ]);

    const result = evict(cache, "stored", "incoming");

    expect(cache.has("stored")).toBe(true);
    expect(cache.has("incoming")).toBe(true);
    expect(cache.has("filler")).toBe(false);
    expect(result.evicted).toBe(1);
    expect(result.overBudget).toBe(true);
    expect(result.totalWeight).toBe(budget * 2);
  });

  it("binds on the entry ceiling when many tiny documents stay well under budget", async () => {
    const { rendererModule } = await loadRendererWithMessages();
    const {
      PROCESSED_DOCUMENT_CACHE_WEIGHT_BUDGET: budget,
      PROCESSED_DOCUMENT_CACHE_MAX_ENTRIES: maxEntries,
      evictProcessedDocumentCacheEntries: evict,
    } = requireCacheInternals(rendererModule);

    // The cache key embeds the theme, so a theme switch orphans a whole
    // generation of cheap entries. Nothing about their weight would ever evict
    // them; only the count ceiling can.
    const overshoot = 8;
    const entries: Array<[string, number]> = [];
    for (let index = 0; index < maxEntries + overshoot; index++) {
      entries.push([`tiny-${index}`, 10]);
    }
    const cache = weighedCache(entries);
    const justStored = `tiny-${maxEntries + overshoot - 1}`;
    expect(residentWeight(cache)).toBeLessThan(budget);

    const result = evict(cache, justStored, null);

    expect(cache.size).toBe(maxEntries);
    expect(result.evicted).toBe(overshoot);
    expect(result.overBudget).toBe(false);
    expect(result.totalWeight).toBe(residentWeight(cache));
    // The survivors are the most recently stored, in insertion order.
    expect([...cache.keys()]).toEqual(
      entries.slice(overshoot).map(([key]) => key));
  });

  it("passes the incoming cache key to the cache owner as non-evictable", () => {
    document.documentElement.innerHTML =
      `<head><title>MarkMello</title></head>`
      + `<body><main class="mm-document"></main></body>`;
    const preserveCalls: Array<string | null | undefined> = [];
    const deps: LoadDocumentDeps = {
      runInitialRenderPipeline: async () => {},
      cancelCurrentMathController: () => {},
      resetModuleGlobals: () => {},
      scrollWindowToTop: () => {},
      emitMark: () => {},
      ensureChromeNodes: () => {},
      applyTheme: () => {},
      debugLog: () => {},
      preserveCurrentDocumentCache: pinnedKey => preserveCalls.push(pinnedKey),
      getCachedDocumentFragment: () => undefined,
    };

    applyLoadDocument(
      { html: "<h1>doc</h1>", documentName: "doc.md", theme: "light", cacheKey: "light|11|deadbeef" },
      deps);

    // The owner is TOLD what it may not evict. Leaving this implicit in the call
    // order of this module is what the design rejected: the get at the bottom of
    // applyLoadDocument runs after the preserve above it, so without the pin a
    // budget-driven pass can delete the very entry that get is about to ask for.
    expect(preserveCalls).toEqual(["light|11|deadbeef"]);
  });

  it("keeps entries and nodeCount meaning what they meant, alongside the new eviction fields", async () => {
    const { load, messages } = await loadRendererWithMessages();
    const firstHtml =
      "<h1 id='first'>First</h1>"
      + "<table><tbody><tr><td><span>cell</span></td><td><em>other</em></td></tr></tbody></table>";
    const secondHtml = "<h1 id='second'>Second</h1><p>other document</p>";

    load({ type: "load-document", html: firstHtml, documentName: "first.md", theme: "light", hasMermaid: false, renderId: 1 });
    await letPipelineSettle();

    const main = document.querySelector<HTMLElement>("main.mm-document")!;
    const expectedNodeCount = main.childNodes.length;
    const expectedElementCount = main.querySelectorAll("*").length;
    expect(expectedElementCount).toBeGreaterThan(expectedNodeCount);

    messages.length = 0;
    load({ type: "load-document", html: secondHtml, documentName: "second.md", theme: "light", hasMermaid: false, renderId: 2 });
    await letPipelineSettle();

    const detail = requireDetail(perfMarkDetails(messages, "mm-document-cache-store"), 0);
    // Unchanged meaning — the open tab-switch filing reads its measurements
    // against exactly these two fields.
    expect(detail.nodeCount).toBe(expectedNodeCount);
    expect(detail.entries).toBe(1);
    expect(detail.elementCount).toBe(expectedElementCount);
    expect(detail.weight).toBe(expectedElementCount);
    expect(detail.totalWeight).toBe(expectedElementCount);
    // Additive.
    expect(detail.evicted).toBe(0);
    expect(detail.overBudget).toBe(false);
  });

  it("serves the incoming document from cache on the load path that preserves before it looks up", async () => {
    const { load, messages, rendererModule } = await loadRendererWithMessages();
    const { PROCESSED_DOCUMENT_CACHE_MAX_ENTRIES: maxEntries } = requireCacheInternals(rendererModule);

    // One more document than the ceiling admits, cycling 1..N+1 and back to 1.
    // Returning to document 1 stores document N+1, which pushes the cache one
    // over the ceiling and makes document 1 — the least recently stored — the
    // eviction target of the very pass that runs immediately before the lookup
    // for document 1. Without the pin the lookup misses and the whole cache
    // delivers 0%, not (N/N+1)%.
    const documents = Array.from({ length: maxEntries + 1 }, (_unused, index) => ({
      html: `<h1 id='d${index}'>Doc ${index}</h1><p>body of document ${index}</p>`,
      name: `doc${index}.md`,
    }));
    const [firstDocument] = documents;
    if (firstDocument === undefined) {
      throw new Error("the entry ceiling must admit at least one document");
    }

    for (const [index, document_] of documents.entries()) {
      load({ type: "load-document", html: document_.html, documentName: document_.name, theme: "light", hasMermaid: false, renderId: index + 1 });
      await letPipelineSettle();
    }

    load({
      type: "load-document",
      html: firstDocument.html,
      documentName: firstDocument.name,
      theme: "light",
      hasMermaid: false,
      renderId: documents.length + 1,
    });
    await letPipelineSettle();

    const stores = perfMarkDetails(messages, "mm-document-cache-store");
    // Fixture sanity: a settle too short to publish entries would make every
    // assertion below vacuously true.
    expect(stores.length).toBe(documents.length);

    const lastStore = requireDetail(stores, stores.length - 1);
    expect(lastStore.entries).toBe(maxEntries);
    expect(lastStore.evicted).toBe(1);
    expect(lastStore.overBudget).toBe(false);

    expect(perfMarkDetails(messages, "mm-load-document-cache-hit").length).toBe(1);
    expect(perfMarkDetails(messages, "mm-load-document-cache-miss").length).toBe(0);
    expect(document.querySelector("main.mm-document")?.textContent).toContain("Doc 0");
  }, 60_000);
});

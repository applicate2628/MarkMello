import { beforeEach, describe, it, expect, vi } from "vitest";
import {
  isMermaidNodeNearViewport,
  makeCopiedSlotSelfContained,
  renderMermaidNode,
  type MermaidApiLike
} from "../src/mermaidRender";

function makeNode(source: string): HTMLElement {
  const pre = document.createElement("pre");
  pre.className = "mm-mermaid";
  const code = document.createElement("code");
  code.className = "language-mermaid";
  code.dataset.mmMermaid = "";
  code.textContent = source;
  pre.appendChild(code);
  document.body.appendChild(pre);
  return pre;
}

describe("renderMermaidNode", () => {
  it("on success adds is-rendered class and sibling .mm-mermaid-svg", async () => {
    const node = makeNode("graph TD");
    const onLayoutBoxChange = vi.fn();
    const api: MermaidApiLike = {
      render: async () => ({ svg: "<svg>OK</svg>" })
    };
    await renderMermaidNode(node, 1, () => 1, api, onLayoutBoxChange);

    expect(node.classList.contains("is-rendered")).toBe(true);
    expect(node.nextElementSibling?.className).toBe("mm-mermaid-svg");
    expect(node.nextElementSibling?.innerHTML).toBe("<svg>OK</svg>");
    expect(onLayoutBoxChange).toHaveBeenCalledTimes(1);
  });

  it("on syntax error leaves pre/code visible without svg sibling", async () => {
    const node = makeNode("bad syntax");
    const onLayoutBoxChange = vi.fn();
    const api: MermaidApiLike = {
      render: async () => { throw new Error("syntax"); }
    };
    await renderMermaidNode(node, 1, () => 1, api, onLayoutBoxChange);

    expect(node.classList.contains("is-rendered")).toBe(false);
    expect(node.nextElementSibling).toBeNull();
    expect(onLayoutBoxChange).not.toHaveBeenCalled();
  });

  it("notifies when a failed re-render restores the source layout box", async () => {
    const node = makeNode("bad syntax");
    node.classList.add("is-rendered");
    const svgHost = document.createElement("div");
    svgHost.className = "mm-mermaid-svg";
    node.after(svgHost);
    const onLayoutBoxChange = vi.fn();
    const api: MermaidApiLike = {
      render: async () => { throw new Error("syntax"); }
    };

    await renderMermaidNode(node, 1, () => 1, api, onLayoutBoxChange);

    expect(node.classList.contains("is-rendered")).toBe(false);
    expect(node.nextElementSibling).toBeNull();
    expect(onLayoutBoxChange).toHaveBeenCalledTimes(1);
  });

  it("waits for a slow render to settle instead of failing it on a deadline", async () => {
    vi.useFakeTimers();
    try {
      const node = makeNode("slow but valid");
      let finishRender!: (value: { svg: string }) => void;
      const api: MermaidApiLike = {
        render: () => new Promise(resolve => { finishRender = resolve; })
      };

      const pending = renderMermaidNode(node, 1, () => 1, api);

      // Far past every deadline this helper ever carried. No clock may decide the
      // outcome: the diagram is still rendering, so it is neither done nor failed.
      await vi.advanceTimersByTimeAsync(60_000);
      expect(node.classList.contains("is-rendered")).toBe(false);
      expect(node.nextElementSibling).toBeNull();

      finishRender({ svg: "<svg>SLOW</svg>" });
      await pending;

      expect(node.classList.contains("is-rendered")).toBe(true);
      expect(node.nextElementSibling?.className).toBe("mm-mermaid-svg");
      expect(node.nextElementSibling?.innerHTML).toBe("<svg>SLOW</svg>");
    } finally {
      vi.useRealTimers();
    }
  });

  it("arms no timer at all while a render is in flight", async () => {
    vi.useFakeTimers();
    try {
      const node = makeNode("slow but valid");
      let finishRender!: (value: { svg: string }) => void;
      const api: MermaidApiLike = {
        render: () => new Promise(resolve => { finishRender = resolve; })
      };

      const pending = renderMermaidNode(node, 1, () => 1, api);
      await Promise.resolve();

      expect(vi.getTimerCount()).toBe(0);

      finishRender({ svg: "<svg>SLOW</svg>" });
      await pending;
      expect(vi.getTimerCount()).toBe(0);
    } finally {
      vi.useRealTimers();
    }
  });

  it("stale generation does not mutate DOM after late resolve", async () => {
    const node = makeNode("graph TD");
    let resolveRender!: (v: { svg: string }) => void;
    const api: MermaidApiLike = {
      render: () => new Promise((resolve) => { resolveRender = resolve; })
    };
    let currentGen = 1;
    const promise = renderMermaidNode(node, 1, () => currentGen, api);

    currentGen = 2;
    resolveRender!({ svg: "<svg>STALE</svg>" });
    await promise;

    expect(node.classList.contains("is-rendered")).toBe(false);
    expect(node.nextElementSibling).toBeNull();
  });
});

describe("isMermaidNodeNearViewport", () => {
  function makeMeasuredNode(top: number, bottom: number): HTMLElement {
    const node = document.createElement("pre");
    node.getBoundingClientRect = () => ({
      x: 0,
      y: top,
      top,
      bottom,
      left: 0,
      right: 100,
      width: 100,
      height: bottom - top,
      toJSON: () => ({})
    } as DOMRect);
    return node;
  }

  it("treats visible and near-viewport diagrams as eager", () => {
    expect(isMermaidNodeNearViewport(makeMeasuredNode(100, 220), 800, 200)).toBe(true);
    expect(isMermaidNodeNearViewport(makeMeasuredNode(900, 1020), 800, 200)).toBe(true);
    expect(isMermaidNodeNearViewport(makeMeasuredNode(-180, -20), 800, 200)).toBe(true);
  });

  it("keeps distant offscreen diagrams lazy", () => {
    expect(isMermaidNodeNearViewport(makeMeasuredNode(1101, 1220), 800, 200)).toBe(false);
    expect(isMermaidNodeNearViewport(makeMeasuredNode(-421, -301), 800, 200)).toBe(false);
  });
});

// ---------------------------------------------------------------------------
// Result propagation: one render, N surfaces.
// ---------------------------------------------------------------------------

function makeMirrorFor(node: HTMLElement, blockIndex: string): HTMLElement {
  node.dataset.mmBlockIndex = blockIndex;
  const followerRoot = document.querySelector<HTMLElement>("div.mm-minimap-content")
    ?? (() => {
      const created = document.createElement("div");
      created.className = "mm-minimap-content";
      document.body.appendChild(created);
      return created;
    })();
  const mirror = document.createElement("pre");
  mirror.className = "mm-mermaid";
  mirror.dataset.mmBlockIndex = blockIndex;
  const code = document.createElement("code");
  code.className = "language-mermaid";
  code.dataset.mmMermaid = "";
  code.textContent = node.querySelector("code")?.textContent ?? "";
  mirror.appendChild(code);
  followerRoot.appendChild(mirror);
  return mirror;
}

function slotHostOf(node: HTMLElement): HTMLElement | null {
  const sibling = node.nextElementSibling;
  if (!(sibling instanceof HTMLElement)) return null;
  return sibling.classList.contains("mm-mermaid-svg") ? sibling : null;
}

// A rendered mermaid SVG in the shape the corpus measurement found: every id namespaced
// by the caller-supplied render id, and `marker-end="url(#…)"` references into a <defs>
// marker. The <style> half of the measured shape is added by buildRenderedSvgHost below
// rather than here, because happy-dom's HTML parser treats <style> as raw text inside
// foreign content and swallows the remainder of the SVG — a fixture artefact, not a
// product one, so the two mechanisms are exercised through the two construction paths.
function renderedSvgMarkup(renderId: string): string {
  return [
    `<svg id="${renderId}" viewBox="0 0 100 40">`,
    `<defs><marker id="${renderId}_flowchart-v2-pointEnd" refX="6"><path d="M0,0 L6,3 L0,6 z"/></marker></defs>`,
    `<g class="node"><rect width="40" height="20"/></g>`,
    `<g class="edgePath"><path d="M0,0 L40,0" marker-end="url(#${renderId}_flowchart-v2-pointEnd)"/></g>`,
    `<g class="edgePath"><path d="M0,20 L40,20" marker-end="url(#${renderId}_flowchart-v2-pointEnd)"/></g>`,
    `</svg>`
  ].join("");
}

const SVG_NAMESPACE = "http://www.w3.org/2000/svg";

// The full measured shape, built through the DOM so the <style> block survives: 70 of the
// corpus's 117 references were `#id` selectors inside one such block, and a <style>
// element inside the document is document-scoped CSS that matches EVERY duplicate.
// It deliberately includes the case a longest-first substring rewrite corrupts: one id
// (`R1`) is a STRICT PREFIX of another (`R1_flowchart-v2-pointEnd`), and the style block
// references both.
function buildRenderedSvgHost(renderId: string): HTMLElement {
  const host = document.createElement("div");
  host.className = "mm-mermaid-svg";
  host.innerHTML = renderedSvgMarkup(renderId);
  const svg = host.querySelector("svg")!;
  const style = document.createElementNS(SVG_NAMESPACE, "style");
  style.textContent = [
    `#${renderId} .node rect{fill:#eeeeee;stroke:#333333}`,
    `#${renderId} .edgePath path{stroke:#1f2937}`,
    `#${renderId}_flowchart-v2-pointEnd{fill:#1f2937}`
  ].join("");
  svg.insertBefore(style, svg.firstChild);
  return host;
}

type ReferenceCensus = {
  total: number;
  insideScope: number;
  outsideScope: number;
  dangling: number;
};

const URL_REFERENCE = /url\(\s*(['"]?)#([^)'"\s]+)\1\s*\)/g;
const CSS_ID_REFERENCE = /#([A-Za-z0-9_-]+)/g;
const HEX_LITERAL = /^[0-9a-fA-F]+$/;

function isHexColourLiteral(token: string): boolean {
  return HEX_LITERAL.test(token)
    && (token.length === 3 || token.length === 4 || token.length === 6 || token.length === 8);
}

function collectReferencedIds(scope: Element): string[] {
  const referenced: string[] = [];
  const elements: Element[] = [scope, ...Array.from(scope.querySelectorAll("*"))];
  for (const element of elements) {
    for (const attribute of Array.from(element.attributes)) {
      if (attribute.name === "id") continue;
      const value = attribute.value;
      if (!value.includes("#")) continue;
      URL_REFERENCE.lastIndex = 0;
      let match: RegExpExecArray | null;
      while ((match = URL_REFERENCE.exec(value)) !== null) referenced.push(match[2]!);
      if ((attribute.name === "href" || attribute.name === "xlink:href") && value.startsWith("#")) {
        referenced.push(value.slice(1));
      }
    }
    if (element.tagName.toLowerCase() !== "style") continue;
    const css = element.textContent ?? "";
    CSS_ID_REFERENCE.lastIndex = 0;
    let cssMatch: RegExpExecArray | null;
    while ((cssMatch = CSS_ID_REFERENCE.exec(css)) !== null) {
      const token = cssMatch[1]!;
      if (isHexColourLiteral(token)) continue;
      referenced.push(token);
    }
  }
  return referenced;
}

// THE ASSERTION IS ABOUT RESOLUTION, NOT ABOUT THE TOKEN.
//
// getComputedStyle cannot detect a wrong copy: it returns the specified `url(#…)` string
// whatever that reference actually resolves to, so a correctly-bound, a wrongly-bound and
// a dangling copy all report the same value. And a wrongly-bound copy RENDERS
// IDENTICALLY, and self-heals when its source is removed. So each reference is followed
// to the element the document would actually pick — the FIRST match in tree order — and
// checked for containment inside the copy.
function censusReferences(scope: Element): ReferenceCensus {
  const census: ReferenceCensus = { total: 0, insideScope: 0, outsideScope: 0, dangling: 0 };
  for (const id of collectReferencedIds(scope)) {
    census.total++;
    const resolved = document.querySelectorAll(`[id="${id}"]`)[0] ?? null;
    if (resolved === null) census.dangling++;
    else if (scope.contains(resolved)) census.insideScope++;
    else census.outsideScope++;
  }
  return census;
}

describe("renderMermaidNode result propagation", () => {
  beforeEach(() => {
    document.documentElement.innerHTML = "<body></body>";
  });

  it("G-M4 success: the mirror gets the same slot, and mermaid.render is called ONCE", async () => {
    const node = makeNode("graph TD; A --> B");
    const mirror = makeMirrorFor(node, "6");
    let renderCalls = 0;
    const api: MermaidApiLike = {
      render: async (id) => {
        renderCalls++;
        return { svg: renderedSvgMarkup(id) };
      }
    };

    await renderMermaidNode(node, 1, () => 1, api, undefined, () => [mirror]);

    expect(renderCalls).toBe(1);
    expect(node.classList.contains("is-rendered")).toBe(true);
    expect(mirror.classList.contains("is-rendered")).toBe(true);
    const mirrorHost = slotHostOf(mirror);
    expect(mirrorHost).not.toBeNull();
    expect(mirrorHost!.querySelectorAll("path").length)
      .toBe(slotHostOf(node)!.querySelectorAll("path").length);
  });

  it("G-M4 refusal: the mirror gets the oversize placeholder, not raw source", async () => {
    const oversize = "graph TD\n" + "A --> B\n".repeat(501);
    const node = makeNode(oversize);
    const mirror = makeMirrorFor(node, "6");
    let renderCalls = 0;
    const api: MermaidApiLike = {
      render: async () => {
        renderCalls++;
        return { svg: "<svg/>" };
      }
    };

    await renderMermaidNode(node, 1, () => 1, api, undefined, () => [mirror]);

    expect(renderCalls).toBe(0);
    expect(node.classList.contains("is-rendered")).toBe(true);
    expect(mirror.classList.contains("is-rendered")).toBe(true);
    expect(slotHostOf(mirror)?.classList.contains("mm-mermaid-oversize")).toBe(true);
    expect(slotHostOf(mirror)?.querySelector(".mm-mermaid-oversize-source")?.textContent)
      .toBe(oversize);
  });

  it("G-M4 failure: a mirror showing a stale diagram is cleared back to source", async () => {
    const node = makeNode("bad syntax");
    node.classList.add("is-rendered");
    const staleHost = document.createElement("div");
    staleHost.className = "mm-mermaid-svg";
    node.after(staleHost);

    const mirror = makeMirrorFor(node, "6");
    mirror.classList.add("is-rendered");
    const staleMirrorHost = document.createElement("div");
    staleMirrorHost.className = "mm-mermaid-svg";
    mirror.after(staleMirrorHost);

    const api: MermaidApiLike = {
      render: async () => { throw new Error("syntax"); }
    };

    await renderMermaidNode(node, 1, () => 1, api, undefined, () => [mirror]);

    expect(node.classList.contains("is-rendered")).toBe(false);
    expect(slotHostOf(node)).toBeNull();
    expect(mirror.classList.contains("is-rendered")).toBe(false);
    expect(slotHostOf(mirror)).toBeNull();
  });

  it("G-M3: the mirror is already installed when onLayoutBoxChange fires", async () => {
    const node = makeNode("graph TD; A --> B");
    const mirror = makeMirrorFor(node, "6");
    let mirrorPresentAtCallback: boolean | null = null;
    const onLayoutBoxChange = () => {
      mirrorPresentAtCallback = mirror.classList.contains("is-rendered")
        && slotHostOf(mirror) !== null;
    };
    const api: MermaidApiLike = {
      render: async (id) => ({ svg: renderedSvgMarkup(id) })
    };

    await renderMermaidNode(node, 1, () => 1, api, onLayoutBoxChange, () => [mirror]);

    // One geometry-change signal must cover BOTH boxes; a callback that fires before the
    // mirror lands leaves the clone's new height unaccounted for.
    expect(mirrorPresentAtCallback).toBe(true);
  });

  it("does not propagate a stale render to the mirror", async () => {
    const node = makeNode("graph TD; A --> B");
    const mirror = makeMirrorFor(node, "6");
    let resolveRender!: (value: { svg: string }) => void;
    const api: MermaidApiLike = {
      render: () => new Promise((resolve) => { resolveRender = resolve; })
    };
    let currentGeneration = 1;
    const pending = renderMermaidNode(node, 1, () => currentGeneration, api, undefined, () => [mirror]);

    currentGeneration = 2;
    resolveRender({ svg: renderedSvgMarkup("stale") });
    await pending;

    expect(node.classList.contains("is-rendered")).toBe(false);
    expect(mirror.classList.contains("is-rendered")).toBe(false);
    expect(slotHostOf(mirror)).toBeNull();
  });

  it("arms no timer while propagating", async () => {
    vi.useFakeTimers();
    try {
      const node = makeNode("graph TD; A --> B");
      const mirror = makeMirrorFor(node, "6");
      const api: MermaidApiLike = {
        render: async (id) => ({ svg: renderedSvgMarkup(id) })
      };

      await renderMermaidNode(node, 1, () => 1, api, undefined, () => [mirror]);

      expect(vi.getTimerCount()).toBe(0);
      expect(mirror.classList.contains("is-rendered")).toBe(true);
    } finally {
      vi.useRealTimers();
    }
  });
});

describe("G-M5 the propagated copy is self-contained", () => {
  beforeEach(() => {
    document.documentElement.innerHTML = "<body></body>";
  });

  it("RED CONTROL: the naive copy resolves its references into the ORIGINAL", () => {
    const original = buildRenderedSvgHost("R1");
    document.body.appendChild(original);

    // A plain cloneNode with no id rewrite — the implementation this design bars.
    const naiveCopy = original.cloneNode(true) as HTMLElement;
    document.body.appendChild(naiveCopy);

    const census = censusReferences(naiveCopy);
    expect(census.total).toBeGreaterThan(0);
    // Every reference in the copy is answered by the ORIGINAL's element, because the
    // duplicate ids resolve first-in-tree-order. Nothing about this is visible in the
    // markup, in getComputedStyle, or in how the copy paints.
    expect(census.outsideScope).toBe(census.total);
    expect(census.insideScope).toBe(0);
  });

  it("the rewritten copy resolves every reference inside itself", () => {
    const original = buildRenderedSvgHost("R1");
    document.body.appendChild(original);

    const copy = original.cloneNode(true) as HTMLElement;
    makeCopiedSlotSelfContained(copy, "-mm-mirror-0");
    document.body.appendChild(copy);

    const copyCensus = censusReferences(copy);
    expect(copyCensus.total).toBeGreaterThan(0);
    expect(copyCensus.insideScope).toBe(copyCensus.total);
    expect(copyCensus.outsideScope).toBe(0);
    expect(copyCensus.dangling).toBe(0);

    // …and the ORIGINAL is not damaged in the other direction either.
    const originalCensus = censusReferences(original);
    expect(originalCensus.insideScope).toBe(originalCensus.total);
    expect(originalCensus.outsideScope).toBe(0);
    expect(originalCensus.dangling).toBe(0);
  });

  it("holds when the copy is inserted BEFORE the original", () => {
    const original = buildRenderedSvgHost("R1");
    document.body.appendChild(original);

    const copy = original.cloneNode(true) as HTMLElement;
    makeCopiedSlotSelfContained(copy, "-mm-mirror-0");
    document.body.insertBefore(copy, original);

    expect(censusReferences(copy).outsideScope).toBe(0);
    // Direction is the discriminator the measurement lane used: whichever subtree is
    // second borrows from the first, so the BEFORE case is what proves the ORIGINAL is
    // not the one left borrowing.
    const originalCensus = censusReferences(original);
    expect(originalCensus.total).toBeGreaterThan(0);
    expect(originalCensus.outsideScope).toBe(0);
    expect(originalCensus.dangling).toBe(0);
  });

  it("RED CONTROL: the naive copy placed BEFORE the original steals the original's references", () => {
    const original = buildRenderedSvgHost("R1");
    document.body.appendChild(original);
    const naiveCopy = original.cloneNode(true) as HTMLElement;
    document.body.insertBefore(naiveCopy, original);

    // Measured in the probe lane and reproduced here: the direction reverses, it does not
    // disappear. The copy now looks fine and the ORIGINAL is the one borrowing.
    expect(censusReferences(naiveCopy).outsideScope).toBe(0);
    const originalCensus = censusReferences(original);
    expect(originalCensus.total).toBeGreaterThan(0);
    expect(originalCensus.outsideScope).toBe(originalCensus.total);
  });

  it("does not corrupt an id that is a strict prefix of another id", () => {
    const original = buildRenderedSvgHost("R1");
    document.body.appendChild(original);
    const copy = original.cloneNode(true) as HTMLElement;
    makeCopiedSlotSelfContained(copy, "-mm-mirror-0");
    document.body.appendChild(copy);

    // A longest-first substring rewrite produces "R1-mm-mirror-0_flowchart-v2-pointEnd-mm-mirror-0"
    // here, because the short id is still a prefix of the already-rewritten long one.
    expect(copy.querySelector("#R1-mm-mirror-0")).not.toBeNull();
    expect(copy.querySelector("#R1_flowchart-v2-pointEnd-mm-mirror-0")).not.toBeNull();
    const css = copy.querySelector("style")?.textContent ?? "";
    expect(css).toContain("#R1-mm-mirror-0 .node");
    expect(css).toContain("#R1_flowchart-v2-pointEnd-mm-mirror-0{");
    expect(css).not.toContain("#R1-mm-mirror-0_flowchart");
  });

  it("leaves colour literals and unmapped references alone", () => {
    const copy = document.createElement("div");
    const svg = document.createElementNS(SVG_NAMESPACE, "svg");
    svg.setAttribute("id", "Z");
    const style = document.createElementNS(SVG_NAMESPACE, "style");
    style.textContent = "#Z rect{fill:#1f2937;stroke:#fff}";
    const rect = document.createElementNS(SVG_NAMESPACE, "rect");
    rect.setAttribute("fill", "url(#not-in-this-copy)");
    svg.append(style, rect);
    copy.appendChild(svg);

    makeCopiedSlotSelfContained(copy, "-mm-mirror-0");

    const css = copy.querySelector("style")?.textContent ?? "";
    expect(css).toContain("fill:#1f2937");
    expect(css).toContain("stroke:#fff");
    expect(css).toContain("#Z-mm-mirror-0 rect");
    expect(copy.querySelector("rect")?.getAttribute("fill")).toBe("url(#not-in-this-copy)");
  });

  it("rewrites IDREF attributes, which carry bare ids rather than # references", () => {
    const copy = document.createElement("div");
    const svg = document.createElementNS(SVG_NAMESPACE, "svg");
    svg.setAttribute("id", "Z");
    svg.setAttribute("aria-labelledby", "T1");
    const title = document.createElementNS(SVG_NAMESPACE, "title");
    title.setAttribute("id", "T1");
    title.textContent = "t";
    svg.appendChild(title);
    copy.appendChild(svg);

    makeCopiedSlotSelfContained(copy, "-mm-mirror-0");

    expect(copy.querySelector("svg")?.getAttribute("aria-labelledby")).toBe("T1-mm-mirror-0");
    expect(copy.querySelector("title")?.getAttribute("id")).toBe("T1-mm-mirror-0");
  });

  it("gives two mirrors of one diagram distinct id namespaces", () => {
    const original = buildRenderedSvgHost("R1");
    document.body.appendChild(original);

    const first = original.cloneNode(true) as HTMLElement;
    makeCopiedSlotSelfContained(first, "-mm-mirror-0");
    const second = original.cloneNode(true) as HTMLElement;
    makeCopiedSlotSelfContained(second, "-mm-mirror-1");
    document.body.append(first, second);

    expect(censusReferences(first).outsideScope).toBe(0);
    expect(censusReferences(second).outsideScope).toBe(0);
    expect(document.querySelectorAll('[id="R1-mm-mirror-0"]').length).toBe(1);
    expect(document.querySelectorAll('[id="R1-mm-mirror-1"]').length).toBe(1);
  });

  it("end to end: a rendered diagram's mirror references only itself", async () => {
    const node = makeNode("graph TD; A --> B");
    const mirror = makeMirrorFor(node, "6");
    const api: MermaidApiLike = {
      render: async (id) => ({ svg: renderedSvgMarkup(id) })
    };

    await renderMermaidNode(node, 1, () => 1, api, undefined, () => [mirror]);

    const mirrorHost = slotHostOf(mirror)!;
    const liveHost = slotHostOf(node)!;
    const mirrorCensus = censusReferences(mirrorHost);
    expect(mirrorCensus.total).toBeGreaterThan(0);
    expect(mirrorCensus.outsideScope).toBe(0);
    expect(mirrorCensus.dangling).toBe(0);
    const liveCensus = censusReferences(liveHost);
    expect(liveCensus.outsideScope).toBe(0);
    expect(liveCensus.dangling).toBe(0);
  });
});

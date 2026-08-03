import { beforeEach, describe, expect, it } from "vitest";
import {
  classifyMermaidSurface,
  partitionMermaidNodesBySurface,
  resolveLiveDocumentRoot,
  resolveMermaidMirrors,
} from "../src/mermaidSurface";

// The classifier is what makes "one render decision per diagram" possible: the same
// diagram reaches the mermaid sweep twice — once as its live element, once as the
// minimap clone's copy — and only the live one may decide. These tests pin the three
// classes and the pairing key, WITHOUT a real mermaid and WITHOUT renderer.ts.

function mermaidPre(blockIndex: string | null): HTMLElement {
  const pre = document.createElement("pre");
  pre.className = "mm-mermaid";
  if (blockIndex !== null) pre.dataset.mmBlockIndex = blockIndex;
  const code = document.createElement("code");
  code.className = "language-mermaid";
  code.dataset.mmMermaid = "";
  code.textContent = "graph TD; A --> B";
  pre.appendChild(code);
  return pre;
}

function buildSurfaces(): { liveRoot: HTMLElement; followerRoot: HTMLElement } {
  document.documentElement.innerHTML = "<body></body>";
  const liveRoot = document.createElement("main");
  liveRoot.className = "mm-document";
  const minimap = document.createElement("aside");
  minimap.className = "mm-minimap";
  const followerRoot = document.createElement("div");
  followerRoot.className = "mm-minimap-content";
  minimap.appendChild(followerRoot);
  document.body.append(liveRoot, minimap);
  return { liveRoot, followerRoot };
}

describe("mermaid surface classification", () => {
  beforeEach(() => {
    document.documentElement.innerHTML = "<body></body>";
  });

  it("resolves the live document root through the one shared selector", () => {
    const { liveRoot } = buildSurfaces();
    expect(resolveLiveDocumentRoot(document)).toBe(liveRoot);
  });

  it("reports no live root when the document has none", () => {
    expect(resolveLiveDocumentRoot(document)).toBeNull();
  });

  it("classifies live, follower and unscoped nodes distinctly", () => {
    const { liveRoot, followerRoot } = buildSurfaces();
    const live = mermaidPre("4");
    liveRoot.appendChild(live);
    const follower = mermaidPre("4");
    followerRoot.appendChild(follower);
    const detached = mermaidPre("4");
    document.body.appendChild(detached);

    const roots = { liveRoot, followerRoot };
    expect(classifyMermaidSurface(live, roots)).toBe("live");
    expect(classifyMermaidSurface(follower, roots)).toBe("follower");
    expect(classifyMermaidSurface(detached, roots)).toBe("unscoped");
  });

  it("classifies a mermaid block nested inside a list item by the root that contains it", () => {
    const { liveRoot, followerRoot } = buildSurfaces();
    const list = document.createElement("ul");
    const item = document.createElement("li");
    const nested = mermaidPre("9");
    item.appendChild(nested);
    list.appendChild(item);
    liveRoot.appendChild(list);

    expect(classifyMermaidSurface(nested, { liveRoot, followerRoot })).toBe("live");
  });

  it("fails OPEN to today's behaviour when no root is known at all", () => {
    const orphan = mermaidPre("1");
    document.body.appendChild(orphan);
    // An unenumerated surface decides for itself rather than silently never rendering.
    expect(classifyMermaidSurface(orphan, { liveRoot: null, followerRoot: null })).toBe("unscoped");
  });

  it("partitions a mixed sweep into deciders only, preserving document order", () => {
    const { liveRoot, followerRoot } = buildSurfaces();
    const liveA = mermaidPre("2");
    const liveB = mermaidPre("7");
    liveRoot.append(liveA, liveB);
    const followerA = mermaidPre("2");
    const followerB = mermaidPre("7");
    followerRoot.append(followerA, followerB);
    const unscoped = mermaidPre("11");
    document.body.appendChild(unscoped);

    const swept = [liveA, liveB, followerA, followerB, unscoped];
    const partition = partitionMermaidNodesBySurface(swept, { liveRoot, followerRoot });

    expect(partition.deciding).toEqual([liveA, liveB, unscoped]);
    expect(partition.live).toBe(2);
    expect(partition.follower).toBe(2);
    expect(partition.unscoped).toBe(1);
  });
});

describe("mermaid mirror resolution", () => {
  beforeEach(() => {
    document.documentElement.innerHTML = "<body></body>";
  });

  it("pairs a live node with its clone twin by data-mm-block-index", () => {
    const { liveRoot, followerRoot } = buildSurfaces();
    const live = mermaidPre("13");
    liveRoot.appendChild(live);
    const decoy = mermaidPre("12");
    const twin = mermaidPre("13");
    followerRoot.append(decoy, twin);

    expect(resolveMermaidMirrors(live, followerRoot)).toEqual([twin]);
  });

  it("pairs a mermaid block nested inside a list item", () => {
    const { liveRoot, followerRoot } = buildSurfaces();
    const live = mermaidPre("21");
    const liveItem = document.createElement("li");
    liveItem.appendChild(live);
    liveRoot.appendChild(liveItem);
    const twin = mermaidPre("21");
    const twinItem = document.createElement("li");
    twinItem.appendChild(twin);
    followerRoot.appendChild(twinItem);

    expect(resolveMermaidMirrors(live, followerRoot)).toEqual([twin]);
  });

  it("returns no mirror when no clone is mounted", () => {
    const { liveRoot, followerRoot } = buildSurfaces();
    const live = mermaidPre("3");
    liveRoot.appendChild(live);

    expect(resolveMermaidMirrors(live, followerRoot)).toEqual([]);
    expect(resolveMermaidMirrors(live, null)).toEqual([]);
  });

  it("never resolves a node to itself", () => {
    const { followerRoot } = buildSurfaces();
    const follower = mermaidPre("5");
    followerRoot.appendChild(follower);

    expect(resolveMermaidMirrors(follower, followerRoot)).toEqual([]);
  });

  it("refuses a key that is not a decimal integer, so the composed selector stays well-formed", () => {
    const { liveRoot, followerRoot } = buildSurfaces();
    const live = mermaidPre('4"] , script');
    liveRoot.appendChild(live);
    followerRoot.appendChild(mermaidPre("4"));

    expect(() => resolveMermaidMirrors(live, followerRoot)).not.toThrow();
    expect(resolveMermaidMirrors(live, followerRoot)).toEqual([]);
    expect(resolveMermaidMirrors(mermaidPre(null), followerRoot)).toEqual([]);
  });

  it("only pairs mermaid sources, never the rendered SVG host beside them", () => {
    const { liveRoot, followerRoot } = buildSurfaces();
    const live = mermaidPre("8");
    liveRoot.appendChild(live);
    const twin = mermaidPre("8");
    followerRoot.appendChild(twin);
    // The rendered host is inserted unkeyed as a sibling; it must never be mistaken for a twin.
    const host = document.createElement("div");
    host.className = "mm-mermaid-svg";
    host.dataset.mmBlockIndex = "8";
    followerRoot.appendChild(host);

    expect(resolveMermaidMirrors(live, followerRoot)).toEqual([twin]);
  });
});

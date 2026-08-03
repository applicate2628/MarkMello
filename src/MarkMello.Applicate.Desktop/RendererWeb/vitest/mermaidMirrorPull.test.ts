import { afterEach, describe, expect, it, vi } from "vitest";

// THE PULL — propagation's second moment, and the one that closes the cache-restore hole.
//
// restoreMinimapSnapshot mounts a clone fragment captured at an earlier instant. A diagram
// that rendered in the live document AFTER that instant leaves a raw <pre> in the restored
// clone whose live twin is already `is-rendered` — and nothing repairs it, because every
// resuming sweep selects `pre.mm-mermaid:not(.is-rendered)` and that selector matches on
// the LIVE twin's state, so it structurally cannot reach the follower. The divergence is
// permanent for the session and has no trigger. The mount edge itself is the trigger.

type PullFixture = {
  pull: () => void;
  liveNode: HTMLElement;
  followerRoot: HTMLElement;
};

function mermaidPre(blockIndex: string): HTMLElement {
  const pre = document.createElement("pre");
  pre.className = "mm-mermaid";
  pre.dataset.mmBlockIndex = blockIndex;
  const code = document.createElement("code");
  code.className = "language-mermaid";
  code.dataset.mmMermaid = "";
  code.textContent = "graph TD; A --> B";
  pre.appendChild(code);
  return pre;
}

function renderedSlotFor(node: HTMLElement, renderId: string): HTMLElement {
  const host = document.createElement("div");
  host.className = "mm-mermaid-svg";
  host.innerHTML = `<svg id="${renderId}" viewBox="0 0 10 10">`
    + `<defs><marker id="${renderId}_pointEnd"><path d="M0,0"/></marker></defs>`
    + `<g class="edgePath"><path marker-end="url(#${renderId}_pointEnd)"/></g>`
    + `</svg>`;
  node.after(host);
  node.classList.add("is-rendered");
  return host;
}

async function loadPullFixture(): Promise<PullFixture> {
  vi.resetModules();
  document.documentElement.innerHTML = `<body><main class="mm-document"></main></body>`;

  vi.stubGlobal("requestAnimationFrame", (callback: FrameRequestCallback) => {
    void callback;
    return 0;
  });
  (window as unknown as {
    chrome: { webview: { postMessage: (message: unknown) => void } };
  }).chrome = { webview: { postMessage: () => undefined } };

  const rendererModule = await import("../src/renderer") as unknown as {
    __testMirrorRenderedMermaidIntoMinimapCloneForTesting: () => void;
    __testSetMinimapCloneBlockElementsForTesting: (
      clone: HTMLElement,
      elements: readonly HTMLElement[]
    ) => void;
  };

  const main = document.querySelector<HTMLElement>("main.mm-document")!;
  const liveNode = mermaidPre("5");
  main.appendChild(liveNode);
  renderedSlotFor(liveNode, "LIVE1");

  const minimap = document.createElement("aside");
  minimap.className = "mm-minimap";
  const followerRoot = document.createElement("div");
  followerRoot.className = "mm-minimap-content";
  minimap.appendChild(followerRoot);
  document.body.appendChild(minimap);
  rendererModule.__testSetMinimapCloneBlockElementsForTesting(followerRoot, []);

  return {
    pull: rendererModule.__testMirrorRenderedMermaidIntoMinimapCloneForTesting,
    liveNode,
    followerRoot
  };
}

function referencedIdsIn(scope: Element): string[] {
  const referenced: string[] = [];
  for (const element of [scope, ...Array.from(scope.querySelectorAll("*"))]) {
    for (const attribute of Array.from(element.attributes)) {
      if (attribute.name === "id") continue;
      const match = /url\(\s*#([^)]+)\s*\)/.exec(attribute.value);
      if (match) referenced.push(match[1]!);
    }
  }
  return referenced;
}

describe("the mount-edge pull", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("reconciles a stale restored clone whose live twin already rendered", async () => {
    const fixture = await loadPullFixture();
    // The snapshot was captured before the diagram rendered, so the restored clone holds
    // raw source. No sweep can reach it: `:not(.is-rendered)` matches on the LIVE twin.
    const staleTwin = mermaidPre("5");
    fixture.followerRoot.appendChild(staleTwin);
    expect(staleTwin.classList.contains("is-rendered")).toBe(false);

    fixture.pull();

    expect(staleTwin.classList.contains("is-rendered")).toBe(true);
    const mirrorHost = staleTwin.nextElementSibling as HTMLElement | null;
    expect(mirrorHost?.className).toBe("mm-mermaid-svg");
    expect(mirrorHost?.querySelector("marker")).not.toBeNull();
  });

  it("makes the reconciled copy self-contained rather than borrowing the live document's ids", async () => {
    const fixture = await loadPullFixture();
    const staleTwin = mermaidPre("5");
    fixture.followerRoot.appendChild(staleTwin);

    fixture.pull();

    const mirrorHost = staleTwin.nextElementSibling as HTMLElement;
    const referenced = referencedIdsIn(mirrorHost);
    expect(referenced.length).toBeGreaterThan(0);
    for (const id of referenced) {
      const resolved = document.querySelectorAll(`[id="${id}"]`)[0] ?? null;
      expect(resolved, `#${id} must resolve`).not.toBeNull();
      expect(mirrorHost.contains(resolved!), `#${id} must resolve INSIDE the mirror`).toBe(true);
    }
    // …and the live document's own references are untouched.
    const liveHost = fixture.liveNode.nextElementSibling as HTMLElement;
    for (const id of referencedIdsIn(liveHost)) {
      expect(liveHost.contains(document.querySelectorAll(`[id="${id}"]`)[0] ?? null)).toBe(true);
    }
  });

  it("is near-idempotent: a second pull neither duplicates nor drops the mirrored slot", async () => {
    const fixture = await loadPullFixture();
    const staleTwin = mermaidPre("5");
    fixture.followerRoot.appendChild(staleTwin);

    fixture.pull();
    fixture.pull();

    expect(fixture.followerRoot.querySelectorAll("div.mm-mermaid-svg")).toHaveLength(1);
    expect(staleTwin.classList.contains("is-rendered")).toBe(true);
  });

  it("does nothing when the clone has no twin for the rendered diagram", async () => {
    const fixture = await loadPullFixture();
    fixture.followerRoot.appendChild(mermaidPre("9"));

    fixture.pull();

    expect(fixture.followerRoot.querySelectorAll("div.mm-mermaid-svg")).toHaveLength(0);
    expect(fixture.followerRoot.querySelector('[data-mm-block-index="9"]')!
      .classList.contains("is-rendered")).toBe(false);
  });

  it("leaves an unrendered live diagram's twin as raw source, so both surfaces agree", async () => {
    const fixture = await loadPullFixture();
    const main = document.querySelector<HTMLElement>("main.mm-document")!;
    const unrenderedLive = mermaidPre("11");
    main.appendChild(unrenderedLive);
    const twin = mermaidPre("11");
    fixture.followerRoot.appendChild(twin);

    fixture.pull();

    expect(twin.classList.contains("is-rendered")).toBe(false);
    expect(twin.nextElementSibling).toBeNull();
  });
});

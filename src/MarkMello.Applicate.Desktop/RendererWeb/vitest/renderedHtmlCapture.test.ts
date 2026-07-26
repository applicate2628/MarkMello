import { afterEach, describe, expect, it, vi } from "vitest";
import { readFileSync } from "node:fs";
import * as rendererModule from "../src/renderer";

type CaptureRendererInternals = {
  captureRenderedHtmlSnapshot: (source: Document) => string;
  handleCaptureRenderedHtml: (message: { type: "capture-rendered-html"; requestId: string }) => void;
};

type CapturedTerminal = Record<string, unknown> & { type?: unknown };

const CAPTURE_REQUEST_ID = "capture-request-1";

function rendererInternals(): CaptureRendererInternals {
  const candidate = rendererModule as Partial<CaptureRendererInternals>;
  expect(
    candidate.captureRenderedHtmlSnapshot,
    "actual production export captureRenderedHtmlSnapshot",
  ).toBeTypeOf("function");
  expect(
    candidate.handleCaptureRenderedHtml,
    "actual production export handleCaptureRenderedHtml",
  ).toBeTypeOf("function");
  return candidate as CaptureRendererInternals;
}

function captureSnapshot(source: Document = document): string {
  return rendererInternals().captureRenderedHtmlSnapshot(source);
}

function setReadMode(enabled: boolean): void {
  const load = (window as typeof window & {
    __mmRendererLoad?: (message: unknown) => void;
  }).__mmRendererLoad;
  expect(load).toBeTypeOf("function");

  const requestAnimationFrame = vi.spyOn(window, "requestAnimationFrame")
    .mockImplementation(callback => {
      callback(0);
      return 1;
    });
  load?.({
    type: "reading-preferences",
    fontSize: 21,
    lineHeight: 1.8,
    maxWidth: 940,
    minimapMode: "on",
    fontFamily: "sans",
    viewerChromeEnabled: enabled,
  });
  requestAnimationFrame.mockRestore();
}

function captureTerminal(
  run: (capture: CaptureRendererInternals) => void,
): CapturedTerminal[] {
  const captured: CapturedTerminal[] = [];
  const rendererWindow = window as typeof window & {
    invokeCSharpAction?: (message: string) => void;
    chrome?: unknown;
  };
  const previousInvoke = rendererWindow.invokeCSharpAction;
  const previousChrome = rendererWindow.chrome;
  rendererWindow.chrome = undefined;
  rendererWindow.invokeCSharpAction = message => {
    captured.push(JSON.parse(message) as CapturedTerminal);
  };
  try {
    run(rendererInternals());
  } finally {
    // `invokeCSharpAction` is an optional property (present ⇒ typed function,
    // never explicitly `undefined`), so restoring "was absent" means deleting
    // it rather than assigning `undefined` back onto it.
    if (previousInvoke === undefined) {
      delete rendererWindow.invokeCSharpAction;
    } else {
      rendererWindow.invokeCSharpAction = previousInvoke;
    }
    rendererWindow.chrome = previousChrome;
  }
  return captured;
}

function installVisualFixture(): void {
  document.documentElement.setAttribute("data-theme", "dark");
  document.documentElement.setAttribute("data-mm-font-family", "sans");
  document.documentElement.setAttribute("data-mm-chrome", "on");
  document.documentElement.style.setProperty("--mm-document-font-size", "21px");
  document.documentElement.style.setProperty("--mm-document-line-height", "1.8");
  document.documentElement.style.setProperty("--mm-document-max-width", "940px");
  document.documentElement.innerHTML = `
    <head>
      <meta charset="utf-8">
      <style id="renderer-css">@font-face{font-family:Fixture;src:url(data:font/woff2;base64,AAAA)} .kept{background:url(data:image/png;base64,AAAA);color:red}</style>
      <style id="katex-css">.katex{font:normal 1em Fixture}</style>
      <script>setTimeout(() => {}, 1)</script>
      <meta http-equiv="refresh" content="0;url=https://example.test/">
    </head>
    <body class="mm-minimap-visible mm-width-resizer-always">
      <main class="mm-document">
        <div class="math-display" data-tex="x^2"><span class="katex">x²</span></div>
        <p>Inline <span class="math-inline" data-tex="y"><span class="katex">y</span></span></p>
        <pre class="mm-mermaid is-rendered"><svg><path d="M0 0h1"></path></svg></pre>
        <pre><code class="hljs"><span class="hljs-keyword">const</span> x = 1;</code></pre>
        <table><tbody><tr><td class="mm-editable-cell" contenteditable="true"
          data-mm-cell-line="7" data-mm-cell-index="0" onblur="evil()">value</td>
          <td class="mm-editable-cell" contenteditable="true" data-mm-cell-line="7"
            data-mm-cell-index="1" data-mm-cell-key="deadbeef" data-mm-cell-raw="$x^2$"
            ><span class="math-inline" data-tex="x^2"><span class="katex">x&sup2;</span></span></td></tr></tbody></table>
        <label><input class="mm-task-checkbox" type="checkbox" checked data-task-line="8"
          data-task-key="task" onchange="evil()">done</label>
        <img alt="fixture" src="data:image/png;base64,AAAA"
          srcset="data:image/png;base64,AAAA 1x, data:image/png;base64,BBBB 2x" onload="evil()">
        <svg><image href="data:image/png;base64,AAAA"></image></svg>
        <a id="unsafe-link" href="javascript:evil()">unsafe</a>
      </main>
      <aside class="mm-minimap"><div class="mm-minimap-content">chrome</div></aside>
      <div class="mm-width-handle">resize</div>
      <div id="mm-drop-overlay">drop</div>
      <div class="mm-find-bar">find</div>
      <div class="mm-mode-reveal-shield">cover</div>
      <div class="mm-document-reveal-shield">cover</div>
    </body>`;
}

afterEach(() => {
  setReadMode(false);
  document.documentElement.removeAttribute("data-theme");
  document.documentElement.removeAttribute("data-mm-font-family");
  document.documentElement.removeAttribute("data-mm-chrome");
  document.documentElement.removeAttribute("style");
  document.documentElement.innerHTML = "<head></head><body></body>";
  vi.restoreAllMocks();
});

describe("rendered HTML snapshot", () => {
  it("Snapshot_ReadModeFixture_IsVisualAndInert", () => {
    installVisualFixture();

    const html = captureSnapshot();
    const parsed = new DOMParser().parseFromString(html, "text/html");

    expect(html.startsWith("<!DOCTYPE html>\n")).toBe(true);
    expect(parsed.documentElement.dataset.theme).toBe("dark");
    expect(parsed.documentElement.style.getPropertyValue("--mm-document-font-family"))
      .toBe("var(--mm-document-font-family-sans)");
    expect(parsed.documentElement.style.getPropertyValue("--mm-document-font-size")).toBe("21px");
    expect(parsed.documentElement.style.getPropertyValue("--mm-document-line-height")).toBe("1.8");
    expect(parsed.documentElement.style.getPropertyValue("--mm-document-max-width")).toBe("940px");
    // 2 inlined asset stylesheets (renderer.css + katex.css) + 1 export-only
    // scrollbar-restore style injected by enableStandaloneScrollbar (the app hides the
    // native scrollbar because the Avalonia host draws its own overlay; a saved file has
    // no host, so native scrolling + a visible scrollbar are restored for the browser).
    expect(parsed.querySelectorAll("style")).toHaveLength(3);
    expect(html).toContain("::-webkit-scrollbar");
    expect(html).toContain("scrollbar-width:auto");
    expect(parsed.querySelectorAll(".katex")).toHaveLength(3);
    expect(parsed.querySelectorAll("[data-tex]")).toHaveLength(0);
    expect(parsed.querySelectorAll(".mm-mermaid svg")).toHaveLength(1);
    expect(parsed.querySelectorAll("code.hljs .hljs-keyword")).toHaveLength(1);
    expect(parsed.querySelectorAll("script, meta[http-equiv='refresh'], .mm-minimap, .mm-width-handle, #mm-drop-overlay, .mm-find-bar, .mm-mode-reveal-shield, .mm-document-reveal-shield")).toHaveLength(0);
    expect(parsed.querySelectorAll("[contenteditable], .mm-editable-cell, [data-task-line], [data-task-key]")).toHaveLength(0);
    // A RICH editable cell carries its markdown in data-mm-cell-raw. That must
    // never reach an exported document: the generic data-mm-* strip covers it,
    // and assertSnapshotCleanup throws if any survives.
    expect(parsed.querySelectorAll("[data-mm-cell-raw], [data-mm-cell-key]")).toHaveLength(0);
    expect(captureSnapshot()).not.toContain("data-mm-cell-raw");
    expect(parsed.querySelectorAll("[onclick], [onload], [onchange], [onblur]")).toHaveLength(0);
    expect(parsed.querySelector("#unsafe-link")?.hasAttribute("href")).toBe(false);
    expect(parsed.querySelector<HTMLInputElement>(".mm-task-checkbox")?.disabled).toBe(true);
    const leakedDataMm = Array.from(parsed.querySelectorAll("*")).flatMap(element =>
      Array.from(element.attributes).filter(attribute => attribute.name.startsWith("data-mm-"))
    );
    expect(leakedDataMm).toEqual([]);
  });

  it("Snapshot_RemovesDataTexMetadataAndRetainsRenderedKatex", () => {
    installVisualFixture();
    const before = document.documentElement.outerHTML;

    expect(document.querySelectorAll("[data-tex]")).toHaveLength(3);
    expect(document.querySelectorAll(".katex")).toHaveLength(3);

    const parsed = new DOMParser().parseFromString(captureSnapshot(), "text/html");

    expect(parsed.querySelectorAll("[data-tex]")).toHaveLength(0);
    expect(parsed.querySelectorAll(".katex")).toHaveLength(3);
    expect(parsed.querySelector(".math-display")?.textContent).toBe("x²");
    expect(parsed.querySelector(".math-inline")?.textContent).toBe("y");
    expect(document.documentElement.outerHTML).toBe(before);
    expect(document.querySelectorAll("[data-tex]")).toHaveLength(3);
  });

  it("Snapshot_DoesNotMutateLiveDom on success or resource failure", () => {
    installVisualFixture();
    const beforeSuccess = document.documentElement.outerHTML;
    captureSnapshot();
    expect(document.documentElement.outerHTML).toBe(beforeSuccess);

    document.querySelector("img")?.setAttribute("src", "https://example.test/remote.png");
    const beforeFailure = document.documentElement.outerHTML;
    expect(() => captureSnapshot()).toThrow(/data URI/i);
    expect(document.documentElement.outerHTML).toBe(beforeFailure);
  });

  it.each([
    ["missing image source", '<img alt="missing">'],
    ["empty image source", '<img alt="empty" src="">'],
    ["malformed image data URI", '<img alt="malformed" src="data:image/png;base64">'],
    ["relative image source", '<img alt="relative" src="image.png">'],
    ["HTTP image source", '<img alt="remote" src="https://example.test/image.png">'],
    ["blob image source", '<img alt="blob" src="blob:fixture">'],
    ["file image source", '<img alt="file" src="file:///fixture.png">'],
    ["relative image srcset candidate", '<img alt="srcset" src="data:image/png;base64,AAAA" srcset="data:image/png;base64,AAAA 1x, image-2x.png 2x">'],
    ["relative source srcset candidate", '<picture><source srcset="image.png 1x"><img alt="fallback" src="data:image/png;base64,AAAA"></picture>'],
    ["remote CSS image", '<style>.x{background:url(https://example.test/image.png)}</style>'],
    ["remote inline CSS image", '<div style="background:url(https://example.test/image.png)">x</div>'],
    ["remote font source", '<style>@font-face{font-family:X;src:url(https://example.test/font.woff2)}</style>'],
    ["local font source", '<style>@font-face{font-family:X;src:local(X)}</style>'],
    ["relative SVG image", '<svg><image href="image.png"></image></svg>'],
    ["remote xlink SVG image", '<svg><image xlink:href="https://example.test/image.png"></image></svg>'],
  ])("rejects %s instead of returning partial HTML", (_name, invalidMarkup) => {
    document.documentElement.innerHTML = `<head></head><body><main class="mm-document">${invalidMarkup}</main></body>`;
    const before = document.documentElement.outerHTML;
    expect(() => captureSnapshot()).toThrow(/data URI/i);
    expect(document.documentElement.outerHTML).toBe(before);
  });

  it("rejects a document switch during clone without serializing the stale clone", () => {
    installVisualFixture();
    const liveRoot = document.documentElement;
    const replacement = document.implementation.createHTMLDocument("replacement").documentElement;
    const cloneNode = liveRoot.cloneNode.bind(liveRoot);
    let currentRoot = liveRoot;
    vi.spyOn(liveRoot, "cloneNode").mockImplementation(deep => {
      const clone = cloneNode(deep);
      currentRoot = replacement;
      return clone;
    });
    const switchingDocument = {
      get documentElement() {
        return currentRoot;
      },
    } as Document;

    expect(() => captureSnapshot(switchingDocument)).toThrow(/HTMLX-DOCUMENT-CHANGED/);
  });

  it("capture-rendered-html posts the exact rendered-html-captured terminal", () => {
    installVisualFixture();
    setReadMode(true);

    const terminals = captureTerminal(capture => {
      capture.handleCaptureRenderedHtml({
        type: "capture-rendered-html",
        requestId: CAPTURE_REQUEST_ID,
      });
    });

    expect(terminals).toHaveLength(1);
    expect(terminals[0]).toEqual({
      type: "rendered-html-captured",
      requestId: CAPTURE_REQUEST_ID,
      html: captureSnapshot(),
    });
  });

  it("capture-rendered-html posts the exact reason-only rendered-html-failed terminal", () => {
    installVisualFixture();
    document.querySelector("img")?.setAttribute("src", "https://example.test/image.png");
    setReadMode(true);

    const terminals = captureTerminal(capture => {
      capture.handleCaptureRenderedHtml({
        type: "capture-rendered-html",
        requestId: CAPTURE_REQUEST_ID,
      });
    });

    expect(terminals).toHaveLength(1);
    expect(Object.keys(terminals[0]!).sort()).toEqual(["reason", "requestId", "type"]);
    expect(terminals[0]).toEqual({
      type: "rendered-html-failed",
      requestId: CAPTURE_REQUEST_ID,
      reason: expect.stringMatching(/HTMLX-RESOURCE-NOT-DATA-URI/),
    });
    expect(terminals[0]).not.toHaveProperty("code");
  });

  it("capture-rendered-html rejects non-read mode with one exact failure terminal", () => {
    installVisualFixture();
    setReadMode(false);

    const terminals = captureTerminal(capture => {
      capture.handleCaptureRenderedHtml({
        type: "capture-rendered-html",
        requestId: CAPTURE_REQUEST_ID,
      });
    });

    expect(terminals).toEqual([{
      type: "rendered-html-failed",
      requestId: CAPTURE_REQUEST_ID,
      reason: expect.stringMatching(/HTMLX-NOT-READ-MODE/),
    }]);
  });

  it("capture-rendered-html converts serialization exceptions into one failure terminal", () => {
    installVisualFixture();
    setReadMode(true);
    const root = document.documentElement;
    const cloneNode = root.cloneNode.bind(root);
    vi.spyOn(root, "cloneNode").mockImplementation(deep => {
      const clone = cloneNode(deep) as HTMLElement;
      Object.defineProperty(clone, "outerHTML", {
        configurable: true,
        get: () => { throw new Error("serialize boom"); },
      });
      return clone;
    });
    const before = root.outerHTML;

    const terminals = captureTerminal(capture => {
      capture.handleCaptureRenderedHtml({
        type: "capture-rendered-html",
        requestId: CAPTURE_REQUEST_ID,
      });
    });

    expect(terminals).toEqual([{
      type: "rendered-html-failed",
      requestId: CAPTURE_REQUEST_ID,
      reason: expect.stringMatching(/HTMLX-SERIALIZATION.*serialize boom/),
    }]);
    expect(root.outerHTML).toBe(before);
  });

  it("capture implementation is event-driven and adds no timer or delay", () => {
    const source = readFileSync("RendererWeb/src/renderer.ts", "utf8");
    const captureStart = source.indexOf("function captureRenderedHtmlSnapshot(");
    const handlerEnd = source.indexOf("function postPostReadyEnhancementsComplete(", captureStart);
    const capturePath = source.slice(captureStart, handlerEnd);

    expect(captureStart).toBeGreaterThanOrEqual(0);
    expect(handlerEnd).toBeGreaterThan(captureStart);
    expect(capturePath).not.toMatch(/setTimeout|setInterval|Task\.Delay|DispatcherTimer/);
  });
});

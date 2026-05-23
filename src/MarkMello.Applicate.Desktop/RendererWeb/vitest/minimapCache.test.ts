import { describe, expect, it } from "vitest";
import {
  captureMinimapSnapshot,
  restoreMinimapSnapshot,
  type CachedMinimapSnapshot
} from "../src/minimapCache";

describe("minimap cache", () => {
  it("moves rendered minimap content into a snapshot and restores it without reading the source document", () => {
    document.body.innerHTML = `
      <main class="mm-document"><h1>source after switch</h1></main>
      <aside class="mm-minimap">
        <div class="mm-minimap-content" style="width: 120px; transform: scale(0.2);"><section data-minimap-copy="cached">cached minimap tree</section></div>
        <div class="mm-minimap-viewport" style="height: 44px; transform: translateY(12px);"></div>
      </aside>
    `;

    const minimapContent = document.querySelector<HTMLElement>(".mm-minimap-content");
    const minimapViewport = document.querySelector<HTMLElement>(".mm-minimap-viewport");
    const snapshot = captureMinimapSnapshot({
      ownerDocument: document,
      minimapContent,
      minimapViewport,
      documentHeight: 1234,
      lastPostedState: { hasPosted: true, visible: true, reservedWidth: 168 },
    });

    expect(snapshot).not.toBeNull();
    expect(minimapContent?.childNodes.length).toBe(0);

    document.querySelector<HTMLElement>(".mm-document")!.innerHTML = "<h1>new source should not be cloned</h1>";

    const restoredContent = document.createElement("div");
    const restoredViewport = document.createElement("div");
    const restored = restoreMinimapSnapshot(snapshot as CachedMinimapSnapshot, {
      minimapContent: restoredContent,
      minimapViewport: restoredViewport,
    });

    expect(restored).toEqual({
      contentNodeCount: 1,
      documentHeight: 1234,
      lastPostedState: { hasPosted: true, visible: true, reservedWidth: 168 },
    });
    expect(restoredContent.querySelector("[data-minimap-copy='cached']")).not.toBeNull();
    expect(restoredContent.textContent).toContain("cached minimap tree");
    expect(restoredContent.textContent).not.toContain("new source should not be cloned");
    expect(restoredContent.style.width).toBe("120px");
    expect(restoredContent.style.transform).toBe("scale(0.2)");
    expect(restoredViewport.style.height).toBe("44px");
    expect(restoredViewport.style.transform).toBe("translateY(12px)");
  });
});

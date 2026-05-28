import { beforeEach, describe, expect, it, vi } from "vitest";

type HostMessage = { type?: string; combo?: string };

describe("host shortcut repeat handling", () => {
  let messages: unknown[];
  let rendererLoaded = false;

  beforeEach(async () => {
    messages = [];
    document.documentElement.innerHTML = `<body><main class="mm-document"></main></body>`;
    (window as unknown as { chrome: { webview: { postMessage: (m: unknown) => void } } }).chrome = {
      webview: { postMessage: (message: unknown) => messages.push(message) },
    };

    if (!rendererLoaded) {
      vi.resetModules();
      await import("../src/renderer");
      document.dispatchEvent(new Event("DOMContentLoaded"));
      rendererLoaded = true;
    }

    window.dispatchEvent(new Event("blur"));
  });

  it("does not forward held ctrl+e repeats until keyup releases the shortcut", () => {
    const first = new KeyboardEvent("keydown", {
      key: "e",
      ctrlKey: true,
      bubbles: true,
      cancelable: true,
    });
    const repeatWithoutBrowserFlag = new KeyboardEvent("keydown", {
      key: "e",
      ctrlKey: true,
      bubbles: true,
      cancelable: true,
    });
    const repeatWithBrowserFlag = new KeyboardEvent("keydown", {
      key: "e",
      ctrlKey: true,
      repeat: true,
      bubbles: true,
      cancelable: true,
    });

    window.dispatchEvent(first);
    window.dispatchEvent(repeatWithoutBrowserFlag);
    window.dispatchEvent(repeatWithBrowserFlag);

    const shortcuts = messages.filter(
      (message): message is HostMessage =>
        (message as HostMessage).type === "host-shortcut"
        && (message as HostMessage).combo === "ctrl+e");
    expect(shortcuts).toHaveLength(1);
    expect(first.defaultPrevented).toBe(true);
    expect(repeatWithoutBrowserFlag.defaultPrevented).toBe(true);
    expect(repeatWithBrowserFlag.defaultPrevented).toBe(true);

    const release = new KeyboardEvent("keyup", {
      key: "e",
      ctrlKey: true,
      bubbles: true,
      cancelable: true,
    });
    const second = new KeyboardEvent("keydown", {
      key: "e",
      ctrlKey: true,
      bubbles: true,
      cancelable: true,
    });

    window.dispatchEvent(release);
    window.dispatchEvent(second);

    const afterRelease = messages.filter(
      (message): message is HostMessage =>
        (message as HostMessage).type === "host-shortcut"
        && (message as HostMessage).combo === "ctrl+e");
    expect(afterRelease).toHaveLength(2);
  });

  it("resets ctrl+e latch when the host hides this WebView for a mode switch", () => {
    const first = new KeyboardEvent("keydown", {
      key: "e",
      ctrlKey: true,
      bubbles: true,
      cancelable: true,
    });
    const second = new KeyboardEvent("keydown", {
      key: "e",
      ctrlKey: true,
      bubbles: true,
      cancelable: true,
    });

    window.dispatchEvent(first);
    window.dispatchEvent(new MessageEvent("message", { data: { type: "host-shortcuts-reset" } }));
    window.dispatchEvent(second);

    const shortcuts = messages.filter(
      (message): message is HostMessage =>
        (message as HostMessage).type === "host-shortcut"
        && (message as HostMessage).combo === "ctrl+e");
    expect(shortcuts).toHaveLength(2);
  });

  it("forwards ctrl+1 through ctrl+9 when the renderer owns keyboard focus", () => {
    for (let ordinal = 1; ordinal <= 9; ordinal++) {
      const event = new KeyboardEvent("keydown", {
        key: String(ordinal),
        ctrlKey: true,
        bubbles: true,
        cancelable: true,
      });

      window.dispatchEvent(event);

      expect(event.defaultPrevented).toBe(true);
    }

    const shortcuts = messages.filter(
      (message): message is HostMessage =>
        (message as HostMessage).type === "host-shortcut"
        && typeof (message as HostMessage).combo === "string"
        && (message as HostMessage).combo!.startsWith("ctrl+"));

    for (let ordinal = 1; ordinal <= 9; ordinal++) {
      expect(shortcuts).toContainEqual({ type: "host-shortcut", combo: `ctrl+${ordinal}` });
    }
  });
});

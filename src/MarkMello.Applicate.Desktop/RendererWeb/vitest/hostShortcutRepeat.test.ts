import { beforeEach, describe, expect, it, vi } from "vitest";

type HostMessage = { type?: string; combo?: string };

describe("host shortcut repeat handling", () => {
  let messages: unknown[];

  beforeEach(async () => {
    vi.resetModules();
    messages = [];
    document.documentElement.innerHTML = `<body><main class="mm-document"></main></body>`;
    (window as unknown as { chrome: { webview: { postMessage: (m: unknown) => void } } }).chrome = {
      webview: { postMessage: (message: unknown) => messages.push(message) },
    };

    await import("../src/renderer");
    document.dispatchEvent(new Event("DOMContentLoaded"));
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
});

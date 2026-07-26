import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

type CapturedMessage = { type?: string; message?: string };

describe("shell initialization failure reporting", () => {
  let messages: CapturedMessage[];

  beforeEach(async () => {
    messages = [];
    document.documentElement.innerHTML = "<body><main class=\"mm-document\"></main></body>";
    (window as unknown as { chrome: { webview: { postMessage: (message: CapturedMessage) => void } } }).chrome = {
      webview: { postMessage: (message: CapturedMessage) => messages.push(message) },
    };

    vi.resetModules();
    await import("../src/renderer");
  });

  afterEach(() => {
    delete (window as unknown as { chrome?: unknown }).chrome;
    document.body.replaceChildren();
  });

  it("reports errors before shell-ready and suppresses errors after shell-ready", () => {
    const originalAddEventListener = window.addEventListener;
    let errorInjected = false;
    let rejectionInjected = false;
    // Production only ever calls addEventListener with a real listener (never
    // null — that's a removeEventListener-only affordance), so the intercept
    // matches the DOM lib's actual (non-nullable) addEventListener signature.
    window.addEventListener = ((type: string, listener: EventListenerOrEventListenerObject, options?: boolean | AddEventListenerOptions) => {
      originalAddEventListener.call(window, type, listener, options);
      if (type === "error" && !errorInjected) {
        errorInjected = true;
        window.dispatchEvent(new ErrorEvent("error", { message: "before-ready error" }));
      }
      if (type === "unhandledrejection" && !rejectionInjected) {
        rejectionInjected = true;
        const rejection = new Event("unhandledrejection");
        Object.defineProperty(rejection, "reason", { value: new Error("before-ready rejection") });
        window.dispatchEvent(rejection);
      }
    }) as typeof window.addEventListener;

    document.dispatchEvent(new Event("DOMContentLoaded"));
    window.addEventListener = originalAddEventListener;

    expect(errorInjected).toBe(true);
    expect(rejectionInjected).toBe(true);

    window.dispatchEvent(new ErrorEvent("error", { message: "after-ready error" }));

    expect(messages.filter((message) => message.type === "shell-init-failed")).toEqual([
      { type: "shell-init-failed", message: "before-ready error" },
      { type: "shell-init-failed", message: "before-ready rejection" },
    ]);
  });
});

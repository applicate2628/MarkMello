import { afterEach, describe, expect, it } from "vitest";
import {
  readVirtualizationFlag,
  readRendererBooleanFlag,
} from "../src/virtualizationFlags";
import { readVirtualizationShadowFlag } from "../src/virtualizationShadow";

let localStorageValues = new Map<string, string>();

Object.defineProperty(window, "localStorage", {
  configurable: true,
  value: {
    getItem: (key: string) => localStorageValues.get(key) ?? null,
    setItem: (key: string, value: string) => {
      localStorageValues.set(key, value);
    },
    clear: () => {
      localStorageValues = new Map<string, string>();
    },
  },
});

describe("renderer virtualization flags", () => {
  afterEach(() => {
    window.localStorage.clear();
    document.documentElement.removeAttribute("data-markmello-virtualization");
    document.documentElement.removeAttribute("data-markmello-virt-shadow");
    delete (window as Window & { MARKMELLO_VIRTUALIZATION?: unknown }).MARKMELLO_VIRTUALIZATION;
    delete (window as Window & { MARKMELLO_VIRT_SHADOW?: unknown }).MARKMELLO_VIRT_SHADOW;
  });

  it("keeps MARKMELLO_VIRTUALIZATION owned by host-injected sources, not localStorage", () => {
    expect(readVirtualizationFlag(window, document)).toBe(false);

    window.localStorage.setItem("MARKMELLO_VIRTUALIZATION", "1");
    expect(readVirtualizationFlag(window, document)).toBe(false);

    window.localStorage.setItem("MARKMELLO_VIRTUALIZATION", "off");
    document.documentElement.dataset.markmelloVirtualization = "true";
    expect(readVirtualizationFlag(window, document)).toBe(true);

    document.documentElement.dataset.markmelloVirtualization = "0";
    (window as Window & { MARKMELLO_VIRTUALIZATION?: unknown }).MARKMELLO_VIRTUALIZATION = true;
    expect(readVirtualizationFlag(window, document)).toBe(true);
  });

  it("keeps the windowing flag separate from the shadow validation flag", () => {
    window.localStorage.setItem("MARKMELLO_VIRT_SHADOW", "true");

    expect(readVirtualizationShadowFlag(window, document)).toBe(true);
    expect(readVirtualizationFlag(window, document)).toBe(false);

    window.localStorage.setItem("MARKMELLO_VIRTUALIZATION", "yes");
    window.localStorage.setItem("MARKMELLO_VIRT_SHADOW", "0");

    expect(readVirtualizationFlag(window, document)).toBe(false);
    expect(readVirtualizationShadowFlag(window, document)).toBe(false);
  });

  it("uses the shared true-value parser for window globals, data attributes, and localStorage", () => {
    expect(readRendererBooleanFlag({
      dataKey: "exampleFeature",
      globalName: "MARKMELLO_EXAMPLE",
      ownerDocument: document,
      ownerWindow: window,
      storageName: "MARKMELLO_EXAMPLE",
    })).toBe(false);

    document.documentElement.dataset.exampleFeature = "on";
    expect(readRendererBooleanFlag({
      dataKey: "exampleFeature",
      globalName: "MARKMELLO_EXAMPLE",
      ownerDocument: document,
      ownerWindow: window,
      storageName: "MARKMELLO_EXAMPLE",
    })).toBe(true);
  });

  it("keeps virtualization disabled by default so flag-off installs no controller machinery", () => {
    expect(readVirtualizationFlag(window, document)).toBe(false);
    expect(document.documentElement.dataset.markmelloVirtualization).toBeUndefined();
    expect((window as Window & { MARKMELLO_VIRTUALIZATION?: unknown }).MARKMELLO_VIRTUALIZATION)
      .toBeUndefined();
  });
});

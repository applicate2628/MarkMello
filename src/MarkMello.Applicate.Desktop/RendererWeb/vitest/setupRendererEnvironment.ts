import { afterEach, beforeEach } from "vitest";

const virtualizationStorageKeys = [
  "MARKMELLO_VIRTUALIZATION",
  "MARKMELLO_VIRT_SHADOW",
];

function clearStorageKey(storage: Storage | undefined, key: string): void {
  if (storage === undefined) {
    return;
  }

  try {
    if (typeof storage.removeItem === "function") {
      storage.removeItem(key);
      return;
    }
    if (typeof storage.clear === "function") {
      storage.clear();
    }
  } catch {
    // Some tests replace localStorage with narrow stubs; isolation is best-effort there.
  }
}

function resetRendererVirtualizationFlags(): void {
  delete (window as Window & { MARKMELLO_VIRTUALIZATION?: unknown }).MARKMELLO_VIRTUALIZATION;
  delete (window as Window & { MARKMELLO_VIRT_SHADOW?: unknown }).MARKMELLO_VIRT_SHADOW;
  document.documentElement.removeAttribute("data-markmello-virtualization");
  document.documentElement.removeAttribute("data-markmello-virt-shadow");
  for (const key of virtualizationStorageKeys) {
    clearStorageKey(window.localStorage, key);
  }
}

beforeEach(() => {
  resetRendererVirtualizationFlags();
});

afterEach(() => {
  resetRendererVirtualizationFlags();
});

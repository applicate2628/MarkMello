export type RendererBooleanFlagInput = {
  ownerWindow: Window;
  ownerDocument: Document;
  globalName: string;
  dataKey: string;
  storageName: string;
};

export function readRendererBooleanFlag(input: RendererBooleanFlagInput): boolean {
  return isTrueFlagValue(readWindowFlag(input.ownerWindow, input.globalName))
    || isTrueFlagValue(input.ownerDocument.documentElement.dataset[input.dataKey])
    || isTrueFlagValue(readLocalStorageFlag(input.ownerWindow, input.storageName));
}

export function readVirtualizationFlag(
  ownerWindow: Window = window,
  ownerDocument: Document = document
): boolean {
  return readRendererBooleanFlag({
    dataKey: "markmelloVirtualization",
    globalName: "MARKMELLO_VIRTUALIZATION",
    ownerDocument,
    ownerWindow,
    storageName: "MARKMELLO_VIRTUALIZATION",
  });
}

function readWindowFlag(ownerWindow: Window, name: string): unknown {
  return (ownerWindow as Window & Record<string, unknown>)[name];
}

function readLocalStorageFlag(ownerWindow: Window, name: string): string | null {
  try {
    return ownerWindow.localStorage.getItem(name);
  } catch {
    return null;
  }
}

function isTrueFlagValue(value: unknown): boolean {
  if (value === true) {
    return true;
  }
  if (typeof value !== "string") {
    return false;
  }

  switch (value.trim().toLowerCase()) {
    case "1":
    case "true":
    case "yes":
    case "on":
      return true;
    default:
      return false;
  }
}

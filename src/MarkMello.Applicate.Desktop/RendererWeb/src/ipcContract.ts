// Host <-> renderer IPC contract — single source of truth.
//
// The two discriminated unions below are the canonical, type-checked statement
// of every message that crosses the WebView2 boundary in either direction:
//   - `RendererMessage`: renderer -> host (posted via `postHostMessage`).
//   - `HostMessage`:      host -> renderer (received by `handleHostMessage`).
//
// The two `as const satisfies` RECURSIVE-descriptor registries make the
// contract enforceable from BOTH sides without a codegen toolchain (design H2,
// Option B+, terra revision 1 — flat key arrays proved which keys are LEGAL, not
// that nested shapes match):
//   - tsc guarantees every union member appears in its registry, every declared
//     field is described (mapped `-?`), and no stray field is (excess-property
//     check) — a missing/typo'd member or field is a compile error under
//     `npm run check:renderer`.
//   - each field carries a recursive descriptor (kind / optional / nullable /
//     nested `of` (object) / `element` (array) / `variants`) so nested payloads
//     (`minimap-policy.minimapPolicy`, `headings-updated.headings[].segments[]`)
//     are walked, not just top-level keys.
//   - `RendererWeb/contract/ipc-contract.json` is the serialized descriptor
//     (regenerate with `npm run gen:ipc-contract`; kept OUT of assets/ so the
//     build does not ship it as runtime content). A vitest deep-equal keeps it
//     in lockstep, and the C# `IpcContractTests` read the same JSON so the
//     producer (C#) and the declared contract (TS) cannot drift silently.
//
// WIRE RULE: never spread an object into a `postHostMessage` / anonymous-object
// literal — list fields explicitly. A spread leaks undeclared internal fields
// onto the wire (see DRIFT-2). Enforced by the vitest producer-capture test.
//
// Declared-but-unread fields are intentionally kept for contract honesty; do
// not prune them here without also pruning the producer.

import type { RendererTheme } from "./initialRenderPipeline";
import type { WidthResizerVisibility } from "./widthResizerVisibility";

export type MinimapMode = "auto" | "on" | "off";

export type MinimapPolicy = {
  minHostWidth: number;
  minScrollableViewportRatio: number;
  maxDetailedDocumentHeight: number;
};

export type FontFamilyMode = "serif" | "sans" | "mono";

export type HeadingSegmentPayload = {
  kind: "text" | "math";
  text: string;
};

export type HeadingPayload = {
  id: string;
  level: number;
  text: string;
  segments: HeadingSegmentPayload[];
};

export type RendererMessage =
  | { type: "document-ready"; mathCount: number }
  | { type: "shell-init-failed"; message?: string }
  | { type: "layout-ready"; scrollTop: number; scrollHeight: number; clientHeight: number; cached?: boolean; renderId?: number | null }
  | { type: "post-ready-enhancements-complete"; renderId?: number; hasMermaid: boolean; hasHljs: boolean }
  | { type: "theme-applied"; theme: RendererTheme; requestId: number }
  | { type: "link-clicked"; href: string; button: number; ctrlKey: boolean; shiftKey: boolean; altKey: boolean; metaKey: boolean }
  | { type: "task-toggle"; line: number; checked: boolean; key: string | null; renderId?: number | null }
  | { type: "table-cell-edit"; line: number; cellIndex: number; text: string; key: string | null; renderId: number | null }
  | { type: "minimap-state"; visible: boolean; reservedWidth: number }
  | { type: "minimap-settled"; transactionGeneration: number; visible: boolean; reservedWidth: number }
  | { type: "scroll"; scrollTop: number; scrollHeight: number; clientHeight: number; topBlockIndex: number | null }
  | { type: "viewer-interaction" }
  | { type: "wheel"; deltaY: number; deltaMode: number }
  | { type: "width-drag"; phase: "start" | "move" | "end"; deltaX: number }
  | { type: "drag-hover"; hovering: boolean }
  | { type: "drop-file"; name: string; text: string }
  | { type: "host-shortcut"; combo: string }
  | { type: "debug-log"; text: string }
  // Round-2 perf-engineer plan item C, [renderer-perf] group. The renderer
  // posts a perf-mark whenever a startup-relevant pipeline milestone fires;
  // the host stamps elapsed-ms against its own process-anchored Stopwatch
  // (avoids clock-skew between renderer performance.now() and host wall clock)
  // and re-emits as `[renderer-perf] <name> ms=<elapsed>` via ApplicateTrace.
  | { type: "perf-mark"; name: string; detail?: string }
  | { type: "headings-updated"; headings: ReadonlyArray<HeadingPayload> }
  | { type: "active-heading-changed"; id: string }
  | { type: "preview-source-line"; sourceLine: number }
  | { type: "csp-violation"; blockedURI: string; violatedDirective: string; sourceFile: string; lineNumber: number; columnNumber: number }
  | { type: "document-cache-miss"; renderId?: number; cacheKey?: string }
  | { type: "document-first-paint"; renderId: number }
  // Mode-toggle reveal gate (2026-05-20). Posted in response to a host-sent
  // `mode-settle-probe` message after the renderer has applied pending reading
  // preferences and let layout chrome such as the minimap paint at the new slot
  // bounds. The host uses this
  // to defer `SetNativeWebViewVisibility(true)` on the Commit fast-path
  // (Ctrl+E mode toggle within the same document), so the user never sees the
  // HWND repainted at the old document width before the renderer catches up.
  | { type: "mode-toggle-settled"; transactionGeneration?: number };

export type HostMessage =
  | { type: "theme"; theme: RendererTheme; requestId?: number }
  | { type: "minimap-policy"; minimapPolicy: MinimapPolicy }
  | {
      type: "reading-preferences";
      fontSize: number;
      lineHeight: number;
      maxWidth: number;
      minMaxWidth?: number;
      minimapMode: MinimapMode;
      fontFamily?: FontFamilyMode;
      viewerChromeEnabled?: boolean;
      documentScrollEnabled?: boolean;
      wheelProxyEnabled?: boolean;
      widthResizerVisibility?: WidthResizerVisibility;
      skipFrameWait?: boolean;
    }
  | { type: "scroll-by"; deltaY: number }
  | { type: "scroll-to-block"; blockIndex: number }
  | { type: "scroll-to"; anchor: string }
  | { type: "scroll-to-progress"; progressPercent: number }
  | { type: "load-document"; html: string; documentName?: string; theme?: RendererTheme; hasMermaid?: boolean; hasHljs?: boolean; renderId?: number; skipFrameWait?: boolean; cacheKey?: string | null }
  | { type: "append-document"; html: string; hasMermaid?: boolean; hasHljs?: boolean; renderId?: number; isFinal?: boolean; cacheKey?: string | null }
  | { type: "load-cached-document"; cacheKey: string; documentName?: string; theme?: RendererTheme; hasMermaid?: boolean; hasHljs?: boolean; renderId?: number; skipFrameWait?: boolean }
  | { type: "clear-document" }
  | { type: "invalidate-document-cache-key" }
  | { type: "set-task-checkbox"; line: number; checked: boolean; renderId?: number }
  | { type: "table-cell-updated"; line: number; cellIndex: number; ok: boolean; text?: string; key?: string; reason?: string; renderId?: number }
  | { type: "scroll-to-heading"; id: string }
  | { type: "scroll-to-source-line"; sourceLine: number }
  | { type: "open-find-bar" }
  | { type: "host-scrollbar"; active: boolean }
  // Host-sent probe (2026-05-20). The host sends this after Avalonia
  // UpdateLayout has settled the slot bounds but BEFORE making the WebView2
  // HWND visible on the Commit fast-path (Ctrl+E same-document reparent).
  // The renderer applies any pending reading preferences, schedules at least
  // two requestAnimationFrame ticks so CSS reflow has propagated and one paint
  // has happened, then posts `mode-toggle-settled` back after layout-dependent
  // chrome has been refreshed. If chrome visibility changes during that
  // refresh, the ack waits one more paint. This keeps the host reveal behind
  // the final minimap/width-handle geometry instead of exposing one frame at
  // the previous text width.
  | {
      type: "mode-settle-probe";
      fontSize?: number;
      lineHeight?: number;
      maxWidth?: number;
      minMaxWidth?: number;
      minimapMode?: MinimapMode;
      fontFamily?: FontFamilyMode;
      viewerChromeEnabled?: boolean;
      documentScrollEnabled?: boolean;
      wheelProxyEnabled?: boolean;
      widthResizerVisibility?: WidthResizerVisibility;
      viewportWidth?: number;
      viewportHeight?: number;
      transactionGeneration?: number;
      skipFrameWait?: boolean;
    }
  | { type: "minimap-settle-probe"; transactionGeneration: number }
  | { type: "host-shortcuts-reset" }
  | { type: "mode-reveal-prepare"; durationMs?: number }
  | { type: "mode-reveal-start"; durationMs?: number }
  | { type: "document-reveal-prepare"; durationMs?: number; theme?: RendererTheme }
  | { type: "document-reveal-start"; durationMs?: number };

// --- Recursive wire-shape descriptors (the enforced canon) --------------------
//
// Each message maps to a descriptor of ALL its declared fields. Per field:
//   kind      : "string" | "number" | "boolean" | "object" | "array"
//   optional? : the field may be absent (TS `?`)
//   nullable? : the value may be null (TS `| null`)
//   of?       : nested field descriptors for kind "object"
//   element?  : the element descriptor for kind "array"
//   variants? : the allowed literal string values
//
// The `satisfies` constraint below forces, per message: every declared field is
// described (mapped `-?`) and no undeclared field is (excess-property check) —
// the flat-array registry proved neither. Nested shapes are walked by
// collectWireShapeViolations (used by the vitest producer-capture and mirrored
// in the C# IpcContractTests) so a stray key INSIDE minimapPolicy / a heading /
// a segment is caught, not just top-level drift.

export type IpcFieldKind = "string" | "number" | "boolean" | "object" | "array";

export type IpcFieldDescriptor = {
  readonly kind: IpcFieldKind;
  readonly optional?: true;
  readonly nullable?: true;
  readonly of?: IpcShapeDescriptor;
  readonly element?: IpcFieldDescriptor;
  readonly variants?: readonly string[];
};

export type IpcShapeDescriptor = { readonly [field: string]: IpcFieldDescriptor };

const STR: IpcFieldDescriptor = { kind: "string" };
const NUM: IpcFieldDescriptor = { kind: "number" };
const BOOL: IpcFieldDescriptor = { kind: "boolean" };
const STR_OPT: IpcFieldDescriptor = { kind: "string", optional: true };
const NUM_OPT: IpcFieldDescriptor = { kind: "number", optional: true };
const BOOL_OPT: IpcFieldDescriptor = { kind: "boolean", optional: true };
// renderId on layout-ready / post-ready-enhancements-complete / document-cache-miss:
// optional in TS and the host DROPS the message when it is absent or non-numeric
// (renderId-mandatory-in-C#). Encoded optional+nullable here so the contract
// DOCUMENTS the mismatch (terra revision 5); the behavioral fix is the filed bug
// work-items/bugs/2026-07-17-ipc-latent-drift-findings.md, pinned by
// IpcContractTests.RenderIdOptionalInContractButDroppedOnMissingByHost.
const NUM_OPT_NULLABLE: IpcFieldDescriptor = { kind: "number", optional: true, nullable: true };
const STR_OPT_NULLABLE: IpcFieldDescriptor = { kind: "string", optional: true, nullable: true };

export const RENDERER_MESSAGE_SHAPES = {
  "document-ready": { type: STR, mathCount: NUM },
  "shell-init-failed": { type: STR, message: STR_OPT },
  "layout-ready": { type: STR, scrollTop: NUM, scrollHeight: NUM, clientHeight: NUM, cached: BOOL_OPT, renderId: NUM_OPT_NULLABLE },
  "post-ready-enhancements-complete": { type: STR, renderId: NUM_OPT_NULLABLE, hasMermaid: BOOL, hasHljs: BOOL },
  "theme-applied": { type: STR, theme: STR, requestId: NUM },
  "link-clicked": { type: STR, href: STR, button: NUM, ctrlKey: BOOL, shiftKey: BOOL, altKey: BOOL, metaKey: BOOL },
  "task-toggle": { type: STR, line: NUM, checked: BOOL, key: { kind: "string", nullable: true }, renderId: NUM_OPT_NULLABLE },
  // renderId here is the currency stamp (the render generation the renderer holds),
  // ALWAYS present but nullable: a null carries no currency info and the host falls
  // back to its disk/key/round-trip gates rather than dropping. This differs from the
  // optional renderId on layout-ready etc., which the host DROPS when absent.
  "table-cell-edit": { type: STR, line: NUM, cellIndex: NUM, text: STR, key: { kind: "string", nullable: true }, renderId: { kind: "number", nullable: true } },
  "minimap-state": { type: STR, visible: BOOL, reservedWidth: NUM },
  "minimap-settled": { type: STR, transactionGeneration: NUM, visible: BOOL, reservedWidth: NUM },
  "scroll": { type: STR, scrollTop: NUM, scrollHeight: NUM, clientHeight: NUM, topBlockIndex: { kind: "number", nullable: true } },
  "viewer-interaction": { type: STR },
  "wheel": { type: STR, deltaY: NUM, deltaMode: NUM },
  "width-drag": { type: STR, phase: { kind: "string", variants: ["start", "move", "end"] }, deltaX: NUM },
  "drag-hover": { type: STR, hovering: BOOL },
  "drop-file": { type: STR, name: STR, text: STR },
  "host-shortcut": { type: STR, combo: STR },
  "debug-log": { type: STR, text: STR },
  "perf-mark": { type: STR, name: STR, detail: STR_OPT },
  "headings-updated": {
    type: STR,
    headings: {
      kind: "array",
      element: {
        kind: "object",
        of: {
          id: STR,
          level: NUM,
          text: STR,
          segments: {
            kind: "array",
            element: { kind: "object", of: { kind: { kind: "string", variants: ["text", "math"] }, text: STR } },
          },
        },
      },
    },
  },
  "active-heading-changed": { type: STR, id: STR },
  "preview-source-line": { type: STR, sourceLine: NUM },
  "csp-violation": { type: STR, blockedURI: STR, violatedDirective: STR, sourceFile: STR, lineNumber: NUM, columnNumber: NUM },
  "document-cache-miss": { type: STR, renderId: NUM_OPT_NULLABLE, cacheKey: STR_OPT },
  "document-first-paint": { type: STR, renderId: NUM },
  "mode-toggle-settled": { type: STR, transactionGeneration: NUM_OPT },
} satisfies {
  [K in RendererMessage["type"]]: { readonly [F in keyof Extract<RendererMessage, { type: K }>]-?: IpcFieldDescriptor };
};

export const HOST_MESSAGE_SHAPES = {
  "theme": { type: STR, theme: STR, requestId: NUM_OPT },
  "minimap-policy": {
    type: STR,
    minimapPolicy: {
      kind: "object",
      of: { minHostWidth: NUM, minScrollableViewportRatio: NUM, maxDetailedDocumentHeight: NUM },
    },
  },
  "reading-preferences": {
    type: STR,
    fontSize: NUM,
    lineHeight: NUM,
    maxWidth: NUM,
    minMaxWidth: NUM_OPT,
    minimapMode: { kind: "string", variants: ["auto", "on", "off"] },
    fontFamily: { kind: "string", optional: true, variants: ["serif", "sans", "mono"] },
    viewerChromeEnabled: BOOL_OPT,
    documentScrollEnabled: BOOL_OPT,
    wheelProxyEnabled: BOOL_OPT,
    widthResizerVisibility: STR_OPT,
    skipFrameWait: BOOL_OPT,
  },
  "scroll-by": { type: STR, deltaY: NUM },
  "scroll-to-block": { type: STR, blockIndex: NUM },
  "scroll-to": { type: STR, anchor: STR },
  "scroll-to-progress": { type: STR, progressPercent: NUM },
  "load-document": {
    type: STR, html: STR, documentName: STR_OPT, theme: STR_OPT, hasMermaid: BOOL_OPT,
    hasHljs: BOOL_OPT, renderId: NUM_OPT, skipFrameWait: BOOL_OPT, cacheKey: STR_OPT_NULLABLE,
  },
  "append-document": {
    type: STR, html: STR, hasMermaid: BOOL_OPT, hasHljs: BOOL_OPT, renderId: NUM_OPT,
    isFinal: BOOL_OPT, cacheKey: STR_OPT_NULLABLE,
  },
  "load-cached-document": {
    type: STR, cacheKey: STR, documentName: STR_OPT, theme: STR_OPT, hasMermaid: BOOL_OPT,
    hasHljs: BOOL_OPT, renderId: NUM_OPT, skipFrameWait: BOOL_OPT,
  },
  "clear-document": { type: STR },
  "invalidate-document-cache-key": { type: STR },
  "set-task-checkbox": { type: STR, line: NUM, checked: BOOL, renderId: NUM_OPT },
  // reason is present ONLY on a BUSY failure (serializer mid-commit); success and
  // validation-failure both OMIT it (never null). It tells the renderer to KEEP the
  // user's typed text on a busy refusal instead of restoring the pre-edit stash.
  "table-cell-updated": { type: STR, line: NUM, cellIndex: NUM, ok: BOOL, text: STR_OPT, key: STR_OPT, reason: STR_OPT, renderId: NUM_OPT },
  "scroll-to-heading": { type: STR, id: STR },
  "scroll-to-source-line": { type: STR, sourceLine: NUM },
  "open-find-bar": { type: STR },
  "host-scrollbar": { type: STR, active: BOOL },
  "mode-settle-probe": {
    type: STR, fontSize: NUM_OPT, lineHeight: NUM_OPT, maxWidth: NUM_OPT, minMaxWidth: NUM_OPT,
    minimapMode: { kind: "string", optional: true, variants: ["auto", "on", "off"] },
    fontFamily: { kind: "string", optional: true, variants: ["serif", "sans", "mono"] },
    viewerChromeEnabled: BOOL_OPT, documentScrollEnabled: BOOL_OPT, wheelProxyEnabled: BOOL_OPT,
    widthResizerVisibility: STR_OPT, viewportWidth: NUM_OPT, viewportHeight: NUM_OPT,
    transactionGeneration: NUM_OPT, skipFrameWait: BOOL_OPT,
  },
  "minimap-settle-probe": { type: STR, transactionGeneration: NUM },
  "host-shortcuts-reset": { type: STR },
  "mode-reveal-prepare": { type: STR, durationMs: NUM_OPT },
  "mode-reveal-start": { type: STR, durationMs: NUM_OPT },
  "document-reveal-prepare": { type: STR, durationMs: NUM_OPT, theme: STR_OPT },
  "document-reveal-start": { type: STR, durationMs: NUM_OPT },
} satisfies {
  [K in HostMessage["type"]]: { readonly [F in keyof Extract<HostMessage, { type: K }>]-?: IpcFieldDescriptor };
};

// Recursive validator: walks a serialized wire value against a shape descriptor
// and returns a list of violations (empty = valid). Catches undeclared fields at
// ANY nesting level, missing required fields, wrong JSON kind, unexpected null,
// and out-of-variant literals. Mirrored in the C# IpcContractTests.
export function collectWireShapeViolations(
  value: unknown,
  shape: IpcShapeDescriptor,
  path = "$",
): string[] {
  const violations: string[] = [];
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    violations.push(`${path}: expected object, got ${describeWireKind(value)}`);
    return violations;
  }

  const record = value as Record<string, unknown>;
  const declared = new Set(Object.keys(shape));
  for (const key of Object.keys(record)) {
    if (!declared.has(key)) {
      violations.push(`${path}.${key}: undeclared field`);
    }
  }

  for (const [field, descriptor] of Object.entries(shape)) {
    if (!Object.prototype.hasOwnProperty.call(record, field)) {
      if (descriptor.optional !== true) {
        violations.push(`${path}.${field}: missing required field`);
      }
      continue;
    }
    collectFieldViolations(record[field], descriptor, `${path}.${field}`, violations);
  }

  return violations;
}

function collectFieldViolations(
  value: unknown,
  descriptor: IpcFieldDescriptor,
  path: string,
  out: string[],
): void {
  if (value === null) {
    if (descriptor.nullable !== true) {
      out.push(`${path}: unexpected null`);
    }
    return;
  }

  switch (descriptor.kind) {
    case "string":
      if (typeof value !== "string") {
        out.push(`${path}: expected string, got ${describeWireKind(value)}`);
        return;
      }
      if (descriptor.variants && !descriptor.variants.includes(value)) {
        out.push(`${path}: '${value}' not in variants [${descriptor.variants.join(", ")}]`);
      }
      return;
    case "number":
      if (typeof value !== "number") {
        out.push(`${path}: expected number, got ${describeWireKind(value)}`);
      }
      return;
    case "boolean":
      if (typeof value !== "boolean") {
        out.push(`${path}: expected boolean, got ${describeWireKind(value)}`);
      }
      return;
    case "object":
      out.push(...collectWireShapeViolations(value, descriptor.of ?? {}, path));
      return;
    case "array":
      if (!Array.isArray(value)) {
        out.push(`${path}: expected array, got ${describeWireKind(value)}`);
        return;
      }
      if (descriptor.element) {
        const element = descriptor.element;
        value.forEach((item, index) => collectFieldViolations(item, element, `${path}[${index}]`, out));
      }
      return;
  }
}

function describeWireKind(value: unknown): string {
  if (value === null) {
    return "null";
  }
  if (Array.isArray(value)) {
    return "array";
  }
  return typeof value;
}

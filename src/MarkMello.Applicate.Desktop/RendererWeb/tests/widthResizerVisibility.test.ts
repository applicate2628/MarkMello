import assert from "node:assert/strict";
import test from "node:test";
import {
  getWidthResizerVisibilityClasses,
  normalizeWidthResizerVisibility
} from "../src/widthResizerVisibility";

test("normalizes known width resizer visibility values", () => {
  assert.equal(normalizeWidthResizerVisibility("always"), "always");
  assert.equal(normalizeWidthResizerVisibility("on-hover"), "on-hover");
});

test("falls back to on-hover for missing or unknown width resizer visibility", () => {
  assert.equal(normalizeWidthResizerVisibility(undefined), "on-hover");
  assert.equal(normalizeWidthResizerVisibility("other"), "on-hover");
});

test("uses a body class only for always-visible width resizer", () => {
  assert.deepEqual(getWidthResizerVisibilityClasses("always"), {
    alwaysClass: true
  });
  assert.deepEqual(getWidthResizerVisibilityClasses("on-hover"), {
    alwaysClass: false
  });
});

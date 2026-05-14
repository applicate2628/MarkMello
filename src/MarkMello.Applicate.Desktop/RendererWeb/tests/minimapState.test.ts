import assert from "node:assert/strict";
import test from "node:test";
import { shouldPostMinimapState, type PostedMinimapState } from "../src/minimapState";

test("posts first minimap state even when hidden and width is zero", () => {
  const previous: PostedMinimapState = { hasPosted: false, visible: false, reservedWidth: 0 };

  assert.equal(
    shouldPostMinimapState(previous, { visible: false, reservedWidth: 0 }),
    true);
});

test("force posts minimap state even when visible width did not change", () => {
  const previous: PostedMinimapState = { hasPosted: true, visible: true, reservedWidth: 168 };

  assert.equal(
    shouldPostMinimapState(previous, { visible: true, reservedWidth: 168 }, true),
    true);
});

test("skips unchanged non-forced minimap state", () => {
  const previous: PostedMinimapState = { hasPosted: true, visible: true, reservedWidth: 168 };

  assert.equal(
    shouldPostMinimapState(previous, { visible: true, reservedWidth: 168 }),
    false);
});

test("posts minimap state when width changes past epsilon", () => {
  const previous: PostedMinimapState = { hasPosted: true, visible: true, reservedWidth: 168 };

  assert.equal(
    shouldPostMinimapState(previous, { visible: true, reservedWidth: 169 }),
    true);
});

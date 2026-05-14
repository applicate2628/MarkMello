import assert from "node:assert/strict";
import test from "node:test";
import { calculateMinimapViewportLayout } from "../src/minimapLayout";

test("uses uniform scale so minimap text keeps document proportions", () => {
  const layout = calculateMinimapViewportLayout({
    minimapWidth: 136,
    minimapHeight: 600,
    documentWidth: 820,
    documentHeight: 20000,
    viewportHeight: 900,
    scrollTop: 1000
  });

  assert.ok(layout);
  assert.equal(layout.contentWidth, 820);
  assert.equal(layout.scale, 136 / 820);
  assert.equal(layout.transform, `translateY(${layout.contentTranslateY}px) scale(${136 / 820})`);
  assert.ok(layout.contentTranslateY < 0);
});

test("maps viewport overlay through uniform scale and vertical translation", () => {
  const layout = calculateMinimapViewportLayout({
    minimapWidth: 136,
    minimapHeight: 600,
    documentWidth: 820,
    documentHeight: 20000,
    viewportHeight: 900,
    scrollTop: 1000
  });

  assert.ok(layout);
  assert.equal(layout.thumbHeight, 900 * (136 / 820));
  assert.ok(layout.thumbTop > 0);
  assert.ok(layout.thumbTop < 600 - layout.thumbHeight);
});

test("keeps minimum viewport overlay height for very long documents", () => {
  const layout = calculateMinimapViewportLayout({
    minimapWidth: 136,
    minimapHeight: 600,
    documentWidth: 820,
    documentHeight: 120000,
    viewportHeight: 20,
    scrollTop: 0
  });

  assert.ok(layout);
  assert.equal(layout.thumbHeight, 22);
});

test("does not translate content when scaled document fits minimap height", () => {
  const layout = calculateMinimapViewportLayout({
    minimapWidth: 136,
    minimapHeight: 600,
    documentWidth: 820,
    documentHeight: 1200,
    viewportHeight: 900,
    scrollTop: 100
  });

  assert.ok(layout);
  assert.equal(layout.contentTranslateY, 0);
  assert.equal(layout.thumbTop, 100 * (136 / 820));
});

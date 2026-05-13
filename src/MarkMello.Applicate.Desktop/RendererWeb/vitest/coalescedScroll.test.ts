import { describe, it, expect } from "vitest";
import { createScrollCoalescer } from "../src/scrollCoalescer";

describe("scrollCoalescer", () => {
  it("burst of 10 sync calls + 1 schedule fire produces exactly 1 postScroll", () => {
    let postScrollCount = 0;
    const scheduledCallbacks: Array<() => void> = [];
    const coalesce = createScrollCoalescer({
      postScroll: () => { postScrollCount++; },
      schedule: (cb) => { scheduledCallbacks.push(cb); },
    });
    for (let i = 0; i < 10; i++) coalesce();
    expect(scheduledCallbacks).toHaveLength(1);
    expect(postScrollCount).toBe(0);
    scheduledCallbacks[0]!();
    expect(postScrollCount).toBe(1);
  });

  it("sequential scrolls across separate schedules each get a postScroll", () => {
    let postScrollCount = 0;
    const scheduledCallbacks: Array<() => void> = [];
    const coalesce = createScrollCoalescer({
      postScroll: () => { postScrollCount++; },
      schedule: (cb) => { scheduledCallbacks.push(cb); },
    });
    coalesce();
    scheduledCallbacks[0]!();
    coalesce();
    scheduledCallbacks[1]!();
    expect(postScrollCount).toBe(2);
  });
});

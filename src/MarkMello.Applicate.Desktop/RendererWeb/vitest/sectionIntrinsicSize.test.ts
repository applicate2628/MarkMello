import { describe, expect, it } from "vitest";
import {
  createSectionIntrinsicCalibrator,
  estimateSectionIntrinsicHeight,
  estimateSectionIntrinsicHeightFromElement,
  readIntrinsicSizeMetrics,
  type SectionIntrinsicInputs,
  type IntrinsicSizeMetrics,
} from "../src/sectionIntrinsicSize";

const metrics: IntrinsicSizeMetrics = {
  charsPerLine: 20,
  fontSizePx: 18,
  lineHeightPx: 30,
};

const paragraphInput: SectionIntrinsicInputs = {
  headingLevel: 0,
  listItemCount: 0,
  newlineCount: 0,
  tableRowCount: 0,
  textLength: 41,
};

const headingLevelTwoInput: SectionIntrinsicInputs = {
  headingLevel: 2,
  listItemCount: 0,
  newlineCount: 0,
  tableRowCount: 0,
  textLength: 12,
};

describe("section intrinsic-size estimator", () => {
  it("uses structural table rows instead of formatted text newlines", () => {
    const element = document.createElement("table");
    element.dataset.mmBlockKind = "table";
    element.innerHTML = `
      <tbody>
        <tr>
          <td>row one with lots of formatting whitespace</td>
        </tr>
      </tbody>
    `;

    expect(estimateSectionIntrinsicHeightFromElement(element, metrics)).toBe(98);
  });

  it("uses list item count for list height instead of html formatting", () => {
    const element = document.createElement("ul");
    element.dataset.mmBlockKind = "list";
    element.innerHTML = `
      <li>alpha</li>
      <li>beta</li>
      <li>gamma</li>
    `;

    expect(estimateSectionIntrinsicHeightFromElement(element, metrics)).toBe(176);
  });

  it("applies calibrated gap-inclusive heading and paragraph formulas from normalized inputs", () => {
    expect(estimateSectionIntrinsicHeight("heading", {
      headingLevel: 2,
      listItemCount: 0,
      newlineCount: 0,
      tableRowCount: 0,
      textLength: 12,
    }, metrics)).toBeCloseTo(79, 0);

    expect(estimateSectionIntrinsicHeight("paragraph", {
      headingLevel: 0,
      listItemCount: 0,
      newlineCount: 0,
      tableRowCount: 0,
      textLength: 41,
    }, metrics)).toBe(146.6);
  });

  it("uses a conservative default text width so paragraph estimates wrap earlier", () => {
    const main = document.createElement("main");
    Object.defineProperty(main, "clientWidth", {
      configurable: true,
      get: () => 900,
    });
    main.style.fontSize = "18px";
    main.style.lineHeight = "30px";

    expect(readIntrinsicSizeMetrics(main).charsPerLine).toBe(81);
  });

  it("predicts display math at the measured gap-inclusive runtime height", () => {
    expect(estimateSectionIntrinsicHeight("math", {
      headingLevel: 0,
      listItemCount: 0,
      newlineCount: 0,
      tableRowCount: 0,
      textLength: 8,
    }, metrics)).toBe(164);

    expect(estimateSectionIntrinsicHeight("math", {
      headingLevel: 0,
      listItemCount: 0,
      newlineCount: 0,
      tableRowCount: 0,
      textLength: 12,
    }, metrics, "a \\\\ b")).toBeGreaterThan(164);
  });

  it("falls back to defaults until a bucket has enough distinct measured samples", () => {
    const calibrator = createSectionIntrinsicCalibrator({ minSamplesPerBucket: 3 });
    const defaultHeight = estimateSectionIntrinsicHeight("paragraph", paragraphInput, metrics);

    expect(calibrator.estimateHeight("paragraph", paragraphInput, metrics)).toBe(defaultHeight);

    calibrator.recordSample({
      blockIndex: 1,
      defaultHeight,
      input: paragraphInput,
      kind: "paragraph",
      measuredHeight: 180,
    });
    calibrator.recordSample({
      blockIndex: 2,
      defaultHeight,
      input: paragraphInput,
      kind: "paragraph",
      measuredHeight: 186,
    });

    expect(calibrator.estimateHeight("paragraph", paragraphInput, metrics)).toBe(defaultHeight);

    calibrator.recordSample({
      blockIndex: 3,
      defaultHeight,
      input: paragraphInput,
      kind: "paragraph",
      measuredHeight: 192,
    });

    expect(calibrator.estimateHeight("paragraph", paragraphInput, metrics)).toBeCloseTo(186, 1);
  });

  it("keeps calibration bucketed by kind and coarse size signal", () => {
    const calibrator = createSectionIntrinsicCalibrator({ minSamplesPerBucket: 3 });
    const headingDefault = estimateSectionIntrinsicHeight("heading", headingLevelTwoInput, metrics);
    const paragraphDefault = estimateSectionIntrinsicHeight("paragraph", paragraphInput, metrics);
    for (let index = 0; index < 3; index++) {
      calibrator.recordSample({
        blockIndex: 10 + index,
        defaultHeight: headingDefault,
        input: headingLevelTwoInput,
        kind: "heading",
        measuredHeight: 84 + index,
      });
    }

    expect(calibrator.estimateHeight("heading", headingLevelTwoInput, metrics)).toBeCloseTo(85, 1);
    expect(calibrator.estimateHeight("paragraph", paragraphInput, metrics)).toBe(paragraphDefault);

    const headingLevelOneInput = { ...headingLevelTwoInput, headingLevel: 1 };
    expect(calibrator.estimateHeight("heading", headingLevelOneInput, metrics)).toBe(
      estimateSectionIntrinsicHeight("heading", headingLevelOneInput, metrics));
  });

  it("ignores placeholder measurements and duplicate block samples", () => {
    const calibrator = createSectionIntrinsicCalibrator({ minSamplesPerBucket: 2 });
    const defaultHeight = estimateSectionIntrinsicHeight("paragraph", paragraphInput, metrics);

    calibrator.recordSample({
      blockIndex: 20,
      defaultHeight,
      input: paragraphInput,
      kind: "paragraph",
      measuredHeight: 1000,
      measuredHeightPlaceholder: true,
    });
    calibrator.recordSample({
      blockIndex: 21,
      defaultHeight,
      input: paragraphInput,
      kind: "paragraph",
      measuredHeight: 180,
    });
    calibrator.recordSample({
      blockIndex: 21,
      defaultHeight,
      input: paragraphInput,
      kind: "paragraph",
      measuredHeight: 240,
    });

    expect(calibrator.estimateHeight("paragraph", paragraphInput, metrics)).toBe(defaultHeight);
  });
});

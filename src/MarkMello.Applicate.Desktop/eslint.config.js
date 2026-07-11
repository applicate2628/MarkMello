import tsParser from "@typescript-eslint/parser";

const restrictedScrollWriterSyntax = [
  {
    selector: "CallExpression[callee.type='MemberExpression'][callee.object.name='window'][callee.property.name=/^scroll(To|By)$/]",
    message: "Use the legacy scroll writer facade or the scroll ownership control plane for root scroll writes.",
  },
  {
    selector: "CallExpression[callee.type='MemberExpression'][callee.object.name='window'][callee.property.value=/^scroll(To|By)$/]",
    message: "Use the legacy scroll writer facade or the scroll ownership control plane for root scroll writes.",
  },
  {
    selector: "AssignmentExpression[left.type='MemberExpression'][left.property.name='scrollTop']",
    message: "Use the legacy scroll writer facade or the scroll ownership control plane for scrollTop writes.",
  },
  {
    selector: "AssignmentExpression[left.type='MemberExpression'][left.property.value='scrollTop']",
    message: "Use the legacy scroll writer facade or the scroll ownership control plane for scrollTop writes.",
  },
  {
    selector: "UpdateExpression[argument.type='MemberExpression'][argument.property.name='scrollTop']",
    message: "Use the legacy scroll writer facade or the scroll ownership control plane for scrollTop writes.",
  },
  {
    selector: "UpdateExpression[argument.type='MemberExpression'][argument.property.value='scrollTop']",
    message: "Use the legacy scroll writer facade or the scroll ownership control plane for scrollTop writes.",
  },
  {
    selector: "CallExpression[callee.type='MemberExpression'][callee.property.name='scrollIntoView']",
    message: "Use the legacy scroll writer facade or the scroll ownership control plane for scrollIntoView writes.",
  },
  {
    selector: "CallExpression[callee.type='MemberExpression'][callee.property.value='scrollIntoView']",
    message: "Use the legacy scroll writer facade or the scroll ownership control plane for scrollIntoView writes.",
  },
];

export default [
  {
    files: ["RendererWeb/src/**/*.ts"],
    languageOptions: {
      ecmaVersion: 2020,
      parser: tsParser,
      sourceType: "module",
    },
    rules: {
      "no-restricted-syntax": ["error", ...restrictedScrollWriterSyntax],
    },
  },
  {
    files: [
      "RendererWeb/src/scrollOwnershipControlPlane.ts",
      "RendererWeb/src/legacyScrollWriter.ts",
    ],
    rules: {
      "no-restricted-syntax": "off",
    },
  },
];

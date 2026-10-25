import "@testing-library/jest-dom/vitest";

// jsdom lacks what Radix Select calls when it opens (real browsers have them).
Object.assign(Element.prototype, { scrollIntoView: () => undefined, hasPointerCapture: () => false, releasePointerCapture: () => undefined });

import "@testing-library/jest-dom/vitest";
import { configure } from "@testing-library/react";

// The first test of a file also loads its page's lazy route; on a busy CI runner that alone can pass the default
// 1 s of findBy*/waitFor (seen: sites.test.tsx, 1.25 s, main 2026-11-19). A wait still ends as soon as the element is
// there, so a longer limit costs nothing when the page is fast.
configure({ asyncUtilTimeout: 5000 });

// jsdom lacks what Radix Select calls when it opens (real browsers have them).
Object.assign(Element.prototype, { scrollIntoView: () => undefined, hasPointerCapture: () => false, releasePointerCapture: () => undefined });

import { lazy, Suspense } from "react";
import { render, screen } from "@testing-library/react";
import { PartBoundary } from "./PartBoundary";

describe("a form part that cannot load (a lazy chunk missing offline)", () => {
  it("shows the explanation instead of an empty page", async () => {
    const Missing = lazy(() => Promise.reject(new TypeError("Failed to fetch dynamically imported module")));
    vi.spyOn(console, "error").mockImplementation(() => undefined);
    render(<PartBoundary fallback={<p role="alert">absent</p>}><Suspense fallback={<p>…</p>}><Missing /></Suspense></PartBoundary>);

    expect(await screen.findByRole("alert")).toHaveTextContent("absent");
  });
});
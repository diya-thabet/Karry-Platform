import { render, screen } from "@testing-library/react";
import { App } from "./App";

describe("Karry Field shell (TC-P1-PLT-015, US-P1-PLT-01)", () => {
  it("renders the product name as the page heading inside the main landmark", () => {
    render(<App />);

    const main = screen.getByRole("main");
    expect(main).toContainElement(screen.getByRole("heading", { level: 1, name: "Karry Field" }));
  });
});

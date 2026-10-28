import { act, fireEvent, render, screen } from "@testing-library/react";
import { createI18n, I18nProvider } from "@karry/i18n";
import type { StoredAsset } from "@karry/offline";
import { MachineFinder } from "./MachineFinder";
import type { MachineLookup } from "./machines";

const machine = (code: string, name: string): StoredAsset => ({
  id: code, code, name, type: "excavator", siteId: "s1", status: "active", make: null, model: null, serialNumber: null, plate: null, labelCode: null, words: [],
});

describe("the machine finder's search", () => {
  it("shows the answer to the latest text even when an earlier lookup answers last", async () => {
    const pending = new Map<string, (lookup: MachineLookup) => void>();
    const find = vi.fn((text: string) => new Promise<MachineLookup>((resolve) => { pending.set(text, resolve); }));
    render(<I18nProvider i18n={await createI18n({ language: "fr" })}><MachineFinder find={find} scanner={null} onBack={vi.fn()} /></I18nProvider>);
    const field = screen.getByLabelText(/Code, nom ou étiquette/);

    fireEvent.change(field, { target: { value: "p" } });
    fireEvent.change(field, { target: { value: "pe-02" } });
    await act(async () => { pending.get("pe-02")?.({ kind: "search", machines: [machine("PE-02", "Pelle 2")] }); await Promise.resolve(); });
    await act(async () => { pending.get("p")?.({ kind: "search", machines: [machine("PE-01", "Pelle 1"), machine("PE-02", "Pelle 2")] }); await Promise.resolve(); });

    expect(screen.getAllByRole("button", { name: /PE-0/ }).map((b) => b.textContent)).toEqual(["PE-02Pelle 2"]);
  });
});

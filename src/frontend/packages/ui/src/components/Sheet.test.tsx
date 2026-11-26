import { fireEvent, render, screen } from "@testing-library/react";
import axe from "axe-core";
import { useState } from "react";
import { Sheet } from "./Sheet";
import { StatusChip } from "./StatusChip";

function Opened({ onClosed }: { onClosed: () => void }) {
  const [open, setOpen] = useState(true);
  return (
    <Sheet open={open} onOpenChange={(next) => { setOpen(next); if (!next) onClosed(); }} title="Review an entry" description="PEL-01 · night shift"
      closeLabel="Close" aside={<StatusChip status="warning">Waits</StatusChip>} footer={<button type="button">Approve</button>}>
      <p>Photos</p>
    </Sheet>
  );
}

describe("Sheet: a detail panel from the end edge (the review panel, owner's UI review 2026-11-26)", () => {
  it("is a dialog named by its visible title, described by its subtitle, with its actions in view and a close button", async () => {
    const closed = vi.fn();
    render(<Opened onClosed={closed} />);

    const sheet = screen.getByRole("dialog", { name: "Review an entry" });
    expect(sheet).toHaveAccessibleDescription("PEL-01 · night shift");
    expect(screen.getByRole("heading", { name: "Review an entry" })).toBeVisible();
    expect(screen.getByText("Waits")).toBeInTheDocument();
    expect(screen.getByText("Photos")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Approve" })).toBeInTheDocument();
    // Wide and at the end edge: not the phone navigation's narrow start drawer.
    expect(sheet.className).toContain("end-0");
    expect(sheet.className).toContain("w-[min(56rem,100vw)]");
    const result = await axe.run(sheet, { rules: { "color-contrast": { enabled: false } } });
    expect(result.violations.filter((v) => v.impact === "serious" || v.impact === "critical").map((v) => v.id)).toEqual([]);

    fireEvent.click(screen.getByRole("button", { name: "Close" }));
    expect(closed).toHaveBeenCalledOnce();
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });

  it("lets a long status wrap instead of widening its table column", () => {
    render(<><StatusChip status="warning" wrap>The start differs from the previous end on this meter</StatusChip><StatusChip status="ok">Done</StatusChip></>);
    const [long, short] = [screen.getByText(/The start differs/), screen.getByText("Done")];
    expect(long.className).not.toContain("whitespace-nowrap");
    expect(short.className).toContain("whitespace-nowrap");
  });
});

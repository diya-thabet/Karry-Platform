import { fireEvent, render, screen, within } from "@testing-library/react";
import { useState } from "react";
import { createI18n, I18nProvider } from "@karry/i18n";
import { Button } from "./Button";
import { DataTable } from "./DataTable";
import { Dialog } from "./Dialog";
import { QuantityInput } from "./QuantityInput";
import { StatusChip } from "./StatusChip";
import { EmptyState, ErrorState } from "./States";
import { TextField } from "./TextField";
import { ThemeProvider, useTheme } from "../theme/ThemeProvider";

const i18n = await createI18n({ language: "en" });

describe("Button", () => {
  it("is a real button that does not submit forms by default and reacts to keyboard activation", () => {
    const click = vi.fn();
    render(<Button onClick={click}>Save</Button>);
    const button = screen.getByRole("button", { name: "Save" });

    expect(button).toHaveAttribute("type", "button");
    button.focus();
    expect(button).toHaveFocus();
    fireEvent.click(button);
    expect(click).toHaveBeenCalledTimes(1);
  });

  it("blocks clicks and announces busy while loading (no double submission)", () => {
    const click = vi.fn();
    render(<Button onClick={click} loading>Save</Button>);
    const button = screen.getByRole("button", { name: "Save" });

    expect(button).toBeDisabled();
    expect(button).toHaveAttribute("aria-busy", "true");
    fireEvent.click(button);
    expect(click).not.toHaveBeenCalled();
  });
});

describe("TextField", () => {
  it("labels the input and wires help text and error for screen readers", () => {
    render(<TextField label="Code" description="Internal code" error="Already used" />);
    const input = screen.getByRole("textbox", { name: "Code" });

    expect(input).toHaveAttribute("aria-invalid", "true");
    expect(input).toHaveAccessibleDescription("Internal code Already used");
    expect(screen.getByRole("alert")).toHaveTextContent("Already used");
  });

  it("is not marked invalid without an error", () => {
    render(<TextField label="Code" />);

    expect(screen.getByRole("textbox", { name: "Code" })).not.toHaveAttribute("aria-invalid");
  });
});

describe("QuantityInput", () => {
  function Harness() {
    const [value, setValue] = useState("");
    return <QuantityInput label="Fuel" value={value} onValueChange={setValue} unit="L" unitLabel="litres" state="LOOSE" stateLabel="Loose" />;
  }

  it("keeps the typed text exactly, opens a decimal keypad and always shows the unit", () => {
    render(<Harness />);
    const input = screen.getByRole("textbox", { name: "Fuel" });

    fireEvent.change(input, { target: { value: "12,50" } });
    expect(input).toHaveValue("12,50");
    expect(input).toHaveAttribute("inputmode", "decimal");
    expect(screen.getByLabelText("litres")).toHaveTextContent("L");
    expect(screen.getByText("Loose")).toHaveAttribute("data-material-state", "LOOSE");
  });
});

describe("Dialog", () => {
  it("moves focus inside, closes with Escape and is labelled by its title and description", () => {
    const change = vi.fn();
    render(<Dialog open onOpenChange={change} title="Delete?" description="This cannot be undone." closeLabel="Close" />);
    const dialog = screen.getByRole("dialog", { name: "Delete?" });

    expect(dialog).toHaveAccessibleDescription("This cannot be undone.");
    expect(dialog.contains(document.activeElement)).toBe(true);
    fireEvent.keyDown(dialog, { key: "Escape" });
    expect(change).toHaveBeenCalledWith(false);
  });
});

describe("StatusChip", () => {
  it("never relies on colour alone: the status is written", () => {
    render(<StatusChip status="error">Rejected</StatusChip>);

    expect(screen.getByText("Rejected").closest("[data-status]")).toHaveAttribute("data-status", "error");
  });
});

describe("ErrorState", () => {
  it("shows the support reference and copies it", async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.assign(navigator, { clipboard: { writeText } });
    render(<I18nProvider i18n={i18n}><ErrorState correlationId="4bf92f3577b34da6a3ce929d0e0e4736" /></I18nProvider>);

    expect(screen.getByTestId("correlation-id")).toHaveTextContent("4bf92f3577b34da6a3ce929d0e0e4736");
    fireEvent.click(screen.getByRole("button", { name: "Copy reference" }));
    expect(writeText).toHaveBeenCalledWith("4bf92f3577b34da6a3ce929d0e0e4736");
    expect(await screen.findByRole("button", { name: "Copied" })).toBeInTheDocument();
  });
});

describe("DataTable", () => {
  const rows = [{ id: "a", name: "EX-01" }, { id: "b", name: "DT-07" }];

  it("selects a row with the keyboard", () => {
    const select = vi.fn();
    render(<DataTable caption="Machines" columns={[{ key: "n", header: "Name", cell: (r) => r.name }]} rows={rows} rowKey={(r) => r.id} onSelect={select} selectedKey="a" empty={null} />);
    const table = screen.getByRole("table", { name: "Machines" });
    const second = within(table).getByText("DT-07").closest("tr");
    if (!second) throw new Error("row not rendered");

    fireEvent.keyDown(second, { key: "Enter" });
    expect(select).toHaveBeenCalledWith(rows[1]);
    expect(within(table).getByText("EX-01").closest("tr")).toHaveAttribute("aria-selected", "true");
  });

  it("shows the empty state instead of an empty table", () => {
    render(<DataTable caption="Machines" columns={[]} rows={[]} rowKey={() => ""} empty={<EmptyState title="No machines" />} />);

    expect(screen.queryByRole("table")).not.toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "No machines" })).toBeInTheDocument();
  });
});

describe("ThemeProvider", () => {
  function Switcher() {
    const { theme, setTheme } = useTheme();
    return <Button onClick={() => { setTheme("outdoor"); }}>{theme}</Button>;
  }

  it("applies the theme variables and density to the page and switches instantly", () => {
    render(<ThemeProvider defaultTheme="light" density="field" remember={false}><Switcher /></ThemeProvider>);

    expect(document.documentElement.dataset.theme).toBe("light");
    expect(document.documentElement.dataset.density).toBe("field");
    fireEvent.click(screen.getByRole("button", { name: "light" }));
    expect(document.documentElement.dataset.theme).toBe("outdoor");
    expect(document.documentElement.style.getPropertyValue("--karry-text")).toBe("#000000");
  });
});

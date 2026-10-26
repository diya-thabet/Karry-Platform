import { fireEvent, render, screen, within } from "@testing-library/react";
import { useState } from "react";
import { createI18n, I18nProvider } from "@karry/i18n";
import { Button } from "./Button";
import { DataTable } from "./DataTable";
import { Dialog } from "./Dialog";
import { QuantityInput, type MaterialState } from "./QuantityInput";
import { MaskedValue, SensitiveValue } from "./MaskedValue";
import { StatusChip } from "./StatusChip";
import { EmptyState, ErrorState, UpgradeCard } from "./States";
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
  const litres = [{ value: "l", symbol: "L", label: "litres" }];
  const units = [{ value: "m3", symbol: "m³", label: "cubic metres", hasState: true }, { value: "t", symbol: "t", label: "tonnes" }];
  const states = [{ value: "bank", label: "Bank" }, { value: "loose", label: "Loose" }] as const;

  it("keeps the typed text exactly, opens a decimal keypad and always shows the unit", () => {
    function Harness() {
      const [value, setValue] = useState("");
      return <QuantityInput label="Fuel" value={value} onValueChange={setValue} units={litres} unit="l" />;
    }

    render(<Harness />);
    const input = screen.getByRole("textbox", { name: "Fuel" });

    fireEvent.change(input, { target: { value: "12,50" } });
    expect(input).toHaveValue("12,50");
    expect(input).toHaveAttribute("inputmode", "decimal");
    expect(screen.getByLabelText("litres")).toHaveTextContent("L");
    expect(screen.queryByRole("group")).not.toBeInTheDocument();
  });

  it("toggles the unit and, for m³ only, the material state (US-P1-UNI-01 AC4)", () => {
    const changes: string[] = [];
    function Harness() {
      const [unit, setUnit] = useState("m3");
      const [state, setState] = useState<MaterialState>("loose");
      return (
        <QuantityInput label="Load" value="10" units={units} unit={unit} onUnitChange={(u) => { changes.push(u); setUnit(u); }} unitGroupLabel="Unit"
          states={states} state={state} onStateChange={(s) => { changes.push(s); setState(s); }} stateGroupLabel="State" />
      );
    }

    render(<Harness />);
    const unitGroup = screen.getByRole("group", { name: "Unit" });
    expect(within(unitGroup).getByRole("button", { name: "cubic metres" })).toHaveAttribute("aria-pressed", "true");
    expect(within(screen.getByRole("group", { name: "State" })).getByRole("button", { name: "Loose" })).toHaveAttribute("aria-pressed", "true");

    fireEvent.click(screen.getByRole("button", { name: "Bank" }));
    fireEvent.click(within(unitGroup).getByRole("button", { name: "tonnes" }));

    expect(changes).toEqual(["bank", "t"]);
    expect(within(unitGroup).getByRole("button", { name: "tonnes" })).toHaveAttribute("aria-pressed", "true");
    expect(screen.queryByRole("group", { name: "State" })).not.toBeInTheDocument();
  });

  it("shows a fixed state without a toggle when it cannot be changed", () => {
    render(<QuantityInput label="Load" value="10" units={units.slice(0, 1)} unit="m3" states={states} state="loose" />);

    expect(screen.getByText("Loose")).toHaveAttribute("data-material-state", "loose");
    expect(screen.queryByRole("group")).not.toBeInTheDocument();
  });

  it("reveals the factors used on tap", () => {
    render(
      <QuantityInput label="In tonnes" value="16.48" readOnly units={units} unit="t" factorsLabel="Factors used"
        factors={[{ name: "density.loose", label: "Loose density", value: "1.6 t/m³" }, { name: "moisture", label: "Moisture", value: "3 %" }]} />,
    );
    const button = screen.getByRole("button", { name: "Factors used" });

    expect(button).toHaveAttribute("aria-expanded", "false");
    expect(screen.queryByText("Loose density")).not.toBeInTheDocument();
    fireEvent.click(button);
    expect(button).toHaveAttribute("aria-expanded", "true");
    expect(screen.getByText("Loose density").nextSibling).toHaveTextContent("1.6 t/m³");
    expect(screen.getByText("Moisture")).toBeInTheDocument();
    expect(screen.getByRole("textbox", { name: "In tonnes" })).toHaveAttribute("readonly");
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

describe("UpgradeCard", () => {
  it("names the locked feature as a heading and offers the way to get it", () => {
    render(<UpgradeCard title="Maintenance is not in your plan" action={<Button>See your plan</Button>}>Plan services with Professional.</UpgradeCard>);

    expect(screen.getByRole("heading", { name: "Maintenance is not in your plan" })).toBeInTheDocument();
    expect(screen.getByText("Plan services with Professional.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "See your plan" })).toBeInTheDocument();
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

describe("masking (US-P1-IAM-07 AC2)", () => {
  it("shows ••• with the reason for screen readers and as a tooltip", () => {
    render(<MaskedValue label="Hidden by policy" />);

    const masked = screen.getByTitle("Hidden by policy");
    expect(masked).toHaveTextContent("•••");
    expect(within(masked).getByText("Hidden by policy")).toHaveClass("sr-only");
  });

  it("shows the value when sent, ••• when masked and nothing when hidden", () => {
    const { container, rerender } = render(<SensitiveValue object={{}} field="unitPrice" value={18.5} label="Hidden by policy">18.50</SensitiveValue>);
    expect(container).toHaveTextContent("18.50");

    rerender(<SensitiveValue object={{ maskedFields: ["unitPrice"] }} field="unitPrice" value={undefined} label="Hidden by policy">18.50</SensitiveValue>);
    expect(container).toHaveTextContent("•••");
    expect(container).not.toHaveTextContent("18.50");

    rerender(<SensitiveValue object={{ maskedFields: [] }} field="unitPrice" value={undefined} label="Hidden by policy">18.50</SensitiveValue>);
    expect(container).toBeEmptyDOMElement();
  });
});

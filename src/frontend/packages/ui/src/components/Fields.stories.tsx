import type { Meta, StoryObj } from "@storybook/react-vite";
import { useState } from "react";
import { QuantityInput } from "./QuantityInput";
import { SelectField } from "./SelectField";
import { TextField } from "./TextField";

const meta = { title: "Components/Fields" } satisfies Meta;
export default meta;
type Story = StoryObj<typeof meta>;

export const Text: Story = { render: () => <TextField label="Immatriculation" placeholder="AB-123-CD" /> };
export const TextWithHelpAndError: Story = {
  render: () => <TextField label="Code engin" description="Code interne, 10 caractères au plus." error="Ce code est déjà utilisé." defaultValue="EX-01" required />,
};
export const TextDisabled: Story = { render: () => <TextField label="Site" defaultValue="Carrière Nord" disabled /> };

function Quantity(props: { unit: string; unitLabel: string; state?: "LOOSE"; error?: string }) {
  const [value, setValue] = useState("12,5");
  return (
    <QuantityInput label="Quantité chargée" value={value} onValueChange={setValue} unit={props.unit} unitLabel={props.unitLabel}
      {...(props.state ? { state: props.state, stateLabel: "Foisonné (LOOSE)" } : {})} {...(props.error ? { error: props.error } : {})} />
  );
}

export const QuantityTonnes: Story = { render: () => <Quantity unit="t" unitLabel="tonnes" /> };
export const QuantityVolumeLoose: Story = { render: () => <Quantity unit="m³" unitLabel="mètres cubes" state="LOOSE" /> };
export const QuantityWithError: Story = { render: () => <Quantity unit="t" unitLabel="tonnes" error="Doit être un nombre positif." /> };

export const Select: Story = {
  render: () => {
    const [value, setValue] = useState<string | undefined>(undefined);
    return (
      <SelectField label="Matériau" placeholder="Choisir…" value={value} onValueChange={setValue}
        options={[{ value: "gravel", label: "Gravier 4/6" }, { value: "sand", label: "Sable 0/4" }]} />
    );
  },
};

/** Field density: every control at least 48 px high, text at least 16 px (US-P1-OFF-05 AC2; checked in a11y tests). */
export const FieldControls: Story = {
  parameters: { density: "field" },
  render: () => (
    <div className="flex flex-col gap-4">
      <TextField label="Compteur horaire" inputMode="decimal" />
      <Quantity unit="L" unitLabel="litres" />
    </div>
  ),
};

import type { Meta, StoryObj } from "@storybook/react-vite";
import { useState } from "react";
import { QuantityInput, type MaterialState, type QuantityInputProps } from "./QuantityInput";
import { SelectField } from "./SelectField";
import { TextAreaField, TextField } from "./TextField";

const meta = { title: "Components/Fields" } satisfies Meta;
export default meta;
type Story = StoryObj<typeof meta>;

export const Text: Story = { render: () => <TextField label="Immatriculation" placeholder="AB-123-CD" /> };
export const TextWithHelpAndError: Story = {
  render: () => <TextField label="Code engin" description="Code interne, 10 caractères au plus." error="Ce code est déjà utilisé." defaultValue="EX-01" required />,
};
export const TextArea: Story = {
  render: () => <TextAreaField label="Motif" description="30 caractères au moins." defaultValue="INC-42 : les totaux du pont-bascule sont faux la nuit." />,
};
export const TextDisabled: Story = { render: () => <TextField label="Site" defaultValue="Carrière Nord" disabled /> };

const tonnes = [{ value: "t", symbol: "t", label: "tonnes" }];
const volumeOrMass = [{ value: "m3", symbol: "m³", label: "mètres cubes", hasState: true }, { value: "t", symbol: "t", label: "tonnes" }];
const states = [{ value: "bank", label: "En place" }, { value: "loose", label: "Foisonné" }, { value: "compacted", label: "Compacté" }] as const;

function Quantity(props: { units?: QuantityInputProps["units"]; unit?: string; error?: string }) {
  const [value, setValue] = useState("12,5");
  return (
    <QuantityInput label="Quantité chargée" value={value} onValueChange={setValue} units={props.units ?? tonnes} unit={props.unit ?? "t"}
      {...(props.error ? { error: props.error } : {})} />
  );
}

/** Unit and state toggle, and the factors behind the converted value on tap (US-P1-UNI-01 AC4). */
function Toggled() {
  const [unit, setUnit] = useState("m3");
  const [state, setState] = useState<MaterialState>("loose");
  return (
    <QuantityInput label="Chargement" value={unit === "t" ? "16,48" : "10"} readOnly units={volumeOrMass} unit={unit} onUnitChange={setUnit}
      unitGroupLabel="Unité" states={states} state={state} onStateChange={setState} stateGroupLabel="État" factorsLabel="Facteurs utilisés"
      factors={unit === "t" ? [{ name: "density.loose", label: "Densité foisonnée", value: "1,6 t/m³" }, { name: "moisture", label: "Humidité", value: "3 %" }] : undefined} />
  );
}

export const QuantityTonnes: Story = { render: () => <Quantity /> };
export const QuantityVolumeToggle: Story = { render: () => <Toggled /> };
export const QuantityWithError: Story = { render: () => <Quantity error="Doit être un nombre positif." /> };

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
      <Quantity units={[{ value: "l", symbol: "L", label: "litres" }]} unit="l" />
    </div>
  ),
};

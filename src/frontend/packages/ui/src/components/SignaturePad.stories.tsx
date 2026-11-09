import type { Meta, StoryObj } from "@storybook/react-vite";
import { SignaturePad } from "./SignaturePad";

const meta = { title: "Components/SignaturePad", parameters: { density: "field" } } satisfies Meta;
export default meta;
type Story = StoryObj<typeof meta>;

export const ShiftEnd: Story = {
  render: () => (
    <SignaturePad
      signer={<><strong>Mamadou Diallo</strong> · Opérateur · 09/11/2026 18:04</>}
      summary="Fin de poste EX-01 : compteur 12 482,5 h, gasoil 3/4."
      onConfirm={() => undefined}
    />
  ),
};
export const Disabled: Story = {
  render: () => <SignaturePad signer={<><strong>Mamadou Diallo</strong> · Opérateur</>} onConfirm={() => undefined} disabled />,
};

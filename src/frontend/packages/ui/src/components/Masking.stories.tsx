import type { Meta, StoryObj } from "@storybook/react-vite";
import { MaskedValue, SensitiveValue } from "./MaskedValue";

const meta = { title: "Patterns/Masking" } satisfies Meta;
export default meta;
type Story = StoryObj<typeof meta>;

export const Masked: Story = {
  render: () => <MaskedValue label="Masqué par la politique de l’entreprise" />,
};
export const InATable: Story = {
  render: () => (
    <table>
      <caption>Devis</caption>
      <thead>
        <tr>
          <th scope="col">Client</th>
          <th scope="col">Prix unitaire</th>
        </tr>
      </thead>
      <tbody>
        <tr>
          <td>Béton Sahel</td>
          <td>
            <SensitiveValue object={{ maskedFields: ["unitPrice"] }} field="unitPrice" value={undefined} label="Masqué par la politique de l’entreprise">
              —
            </SensitiveValue>
          </td>
        </tr>
        <tr>
          <td>Routes du Nord</td>
          <td>
            <SensitiveValue object={{}} field="unitPrice" value={18.5} label="Masqué par la politique de l’entreprise">
              18,50 TND/t
            </SensitiveValue>
          </td>
        </tr>
      </tbody>
    </table>
  ),
};

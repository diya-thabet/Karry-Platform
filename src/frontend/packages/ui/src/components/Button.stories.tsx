import type { Meta, StoryObj } from "@storybook/react-vite";
import { Button } from "./Button";

const meta = { title: "Components/Button", component: Button, args: { children: "Enregistrer" } } satisfies Meta<typeof Button>;
export default meta;
type Story = StoryObj<typeof meta>;

export const Primary: Story = {};
export const Secondary: Story = { args: { variant: "secondary", children: "Annuler" } };
export const Danger: Story = { args: { variant: "danger", children: "Supprimer" } };
export const Ghost: Story = { args: { variant: "ghost", children: "Voir le détail" } };
export const Loading: Story = { args: { loading: true } };
export const Disabled: Story = { args: { disabled: true } };

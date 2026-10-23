import type { Meta, StoryObj } from "@storybook/react-vite";
import { LanguageSwitcher, ThemeSwitcher } from "./Switchers";

const meta = { title: "Components/Switchers" } satisfies Meta;
export default meta;
type Story = StoryObj<typeof meta>;

export const Language: Story = { render: () => <LanguageSwitcher /> };
export const Theme: Story = { render: () => <ThemeSwitcher /> };

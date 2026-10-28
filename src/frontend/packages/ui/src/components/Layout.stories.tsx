import type { Meta, StoryObj } from "@storybook/react-vite";
import { Globe, LogOut, Truck } from "lucide-react";
import { Button } from "./Button";
import { Card, PageHeader, StatCard } from "./Layout";
import { Menu } from "./Menu";
import { StatusChip } from "./StatusChip";

const meta = { title: "Components/Layout" } satisfies Meta;
export default meta;
type Story = StoryObj<typeof meta>;

export const Header: Story = {
  render: () => <PageHeader title="Engins" description="Le parc de l’entreprise, par site." actions={<Button>Nouvel engin</Button>} />,
};

export const ContentCard: Story = {
  render: () => (
    <Card title="Carrière Nord" description="CAR-NORD · Mornag" actions={<StatusChip status="ok">Actif</StatusChip>}>
      <p className="text-sm">Deux équipes de 12 h.</p>
    </Card>
  ),
};

export const Stat: Story = { render: () => <StatCard icon={Truck} label="Engins" value="68" hint="2 sites" /> };

export const AccountMenu: Story = {
  render: () => (
    <Menu
      label="Connecté : Karim Trabelsi"
      trigger={<Button variant="secondary">Karim Trabelsi</Button>}
      groups={[
        { label: "Langue", items: [{ key: "fr", label: "Français", icon: <Globe aria-hidden className="size-4" />, checked: true, onSelect: () => undefined }] },
        { items: [{ key: "out", label: "Se déconnecter", icon: <LogOut aria-hidden className="size-4" />, onSelect: () => undefined }] },
      ]}
    />
  ),
};

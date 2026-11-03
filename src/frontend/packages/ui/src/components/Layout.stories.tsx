import type { Meta, StoryObj } from "@storybook/react-vite";
import { Globe, LogOut, Moon, Palette, Sun, Truck } from "lucide-react";
import { useState } from "react";
import { Breadcrumbs } from "./Breadcrumbs";
import { Button } from "./Button";
import { Drawer } from "./Drawer";
import { Card, PageHeader, StatCard } from "./Layout";
import { Menu } from "./Menu";
import { PageSkeleton } from "./Skeleton";
import { StatusChip } from "./StatusChip";

const meta = { title: "Components/Layout" } satisfies Meta;
export default meta;
type Story = StoryObj<typeof meta>;

export const Header: Story = {
  render: () => <PageHeader title="Engins" description="Le parc de l’entreprise, par site." actions={<Button>Nouvel engin</Button>} />,
};

export const HeaderWithBreadcrumbs: Story = {
  render: () => (
    <PageHeader
      breadcrumbs={<Breadcrumbs label="Fil d’Ariane"><a href="#parametres">Paramètres</a><a href="#materiaux">Matériaux</a></Breadcrumbs>}
      title="Calcaire 0/31,5"
      status={<StatusChip status="ok">Actif</StatusChip>}
      description="Densités en vigueur depuis le 1er janvier."
    />
  ),
};

export const Loading: Story = { render: () => <PageSkeleton /> };

export const ContentCard: Story = {
  render: () => (
    <Card title="Carrière Nord" description="CAR-NORD · Mornag" actions={<StatusChip status="ok">Actif</StatusChip>}>
      <p className="text-sm">Deux équipes de 12 h.</p>
    </Card>
  ),
};

export const Stat: Story = { render: () => <StatCard icon={Truck} label="Engins" value="68" hint="2 sites" /> };
export const StatLoading: Story = { render: () => <StatCard icon={Truck} label="Engins" value={undefined} /> };

export const AccountMenu: Story = {
  render: () => (
    <Menu
      label="Connecté : Karim Trabelsi"
      trigger={<Button variant="secondary">Karim Trabelsi</Button>}
      header={<strong>Karim Trabelsi</strong>}
      groups={[
        {
          items: [
            {
              kind: "choice", key: "language", label: "Langue", icon: <Globe aria-hidden className="size-4" />, value: "fr", onValueChange: () => undefined,
              options: [{ value: "fr", label: "Français", lang: "fr" }, { value: "en", label: "English", lang: "en" }, { value: "ar", label: "العربية", lang: "ar" }],
            },
            {
              kind: "choice", key: "theme", label: "Thème", icon: <Palette aria-hidden className="size-4" />, value: "light", onValueChange: () => undefined,
              options: [{ value: "light", label: "Clair", icon: <Sun aria-hidden className="size-4" /> }, { value: "dark", label: "Sombre", icon: <Moon aria-hidden className="size-4" /> }],
            },
          ],
        },
        { items: [{ key: "out", label: "Se déconnecter", icon: <LogOut aria-hidden className="size-4" />, onSelect: () => undefined }] },
      ]}
    />
  ),
};

function DrawerDemo() {
  const [open, setOpen] = useState(true);
  return (
    <>
      <Button variant="secondary" onClick={() => { setOpen(true); }}>Ouvrir le menu</Button>
      <Drawer open={open} onOpenChange={setOpen} title="Navigation principale" closeLabel="Fermer" header={<strong>Carrière Démo A</strong>}>
        <nav aria-label="Navigation principale" className="p-3"><a href="#sites" className="block rounded-lg px-3 py-2 text-sm">Sites</a></nav>
      </Drawer>
    </>
  );
}

export const MobileDrawer: Story = { render: () => <DrawerDemo /> };

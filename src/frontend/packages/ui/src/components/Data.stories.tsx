import type { Meta, StoryObj } from "@storybook/react-vite";
import { useState } from "react";
import { Button } from "./Button";
import { DataTable, type Column } from "./DataTable";
import { ListDetailPage } from "./ListDetailPage";
import { StatusChip } from "./StatusChip";
import { EmptyState } from "./States";
import { TextField } from "./TextField";

interface Machine { id: string; code: string; name: string; hours: string }
const machines: Machine[] = [
  { id: "1", code: "EX-01", name: "Pelle Liebherr R 956", hours: "12 480 h" },
  { id: "2", code: "DT-07", name: "Tombereau CAT 775G", hours: "8 112 h" },
];
const columns: Column<Machine>[] = [
  { key: "code", header: "Code", cell: (m) => m.code },
  { key: "name", header: "Engin", cell: (m) => m.name },
  { key: "hours", header: "Compteur", cell: (m) => m.hours, numeric: true },
];

const meta = { title: "Patterns/Data" } satisfies Meta;
export default meta;
type Story = StoryObj<typeof meta>;

export const Table: Story = {
  render: () => <DataTable caption="Engins" columns={columns} rows={machines} rowKey={(m) => m.id} empty={<EmptyState title="Aucun engin" />} />,
};
export const TableEmpty: Story = {
  render: () => <DataTable caption="Engins" columns={columns} rows={[]} rowKey={(m) => m.id} empty={<EmptyState title="Aucun engin" />} />,
};
export const ListDetail: Story = {
  render: () => {
    const [selected, setSelected] = useState<Machine | undefined>(machines[0]);
    return (
      <ListDetailPage
        title="Engins"
        actions={<Button>Ajouter</Button>}
        search={<TextField label="Rechercher" type="search" />}
        detailLabel="Détail de l’engin"
        list={<DataTable caption="Engins" columns={columns} rows={machines} rowKey={(m) => m.id} selectedKey={selected?.id} onSelect={setSelected} empty={<EmptyState title="Aucun engin" />} />}
        detail={selected && <div className="flex flex-col gap-2"><h2 className="text-xl font-bold">{selected.code}</h2><p>{selected.name}</p><StatusChip status="ok">En service</StatusChip></div>}
      />
    );
  },
};

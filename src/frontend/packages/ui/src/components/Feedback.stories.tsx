import type { Meta, StoryObj } from "@storybook/react-vite";
import { Button } from "./Button";
import { Dialog } from "./Dialog";
import { StatusChip } from "./StatusChip";
import { EmptyState, ErrorState } from "./States";

const meta = { title: "Components/Feedback" } satisfies Meta;
export default meta;
type Story = StoryObj<typeof meta>;

export const Statuses: Story = {
  render: () => (
    <div className="flex flex-wrap gap-2">
      <StatusChip status="ok">Synchronisé</StatusChip>
      <StatusChip status="warning">À vérifier</StatusChip>
      <StatusChip status="error">Rejeté</StatusChip>
      <StatusChip status="pending">En attente</StatusChip>
    </div>
  ),
};
export const Empty: Story = { render: () => <EmptyState title="Aucun engin" action={<Button>Ajouter un engin</Button>}>Ajoutez votre premier engin.</EmptyState> };
export const ErrorWithReference: Story = { render: () => <ErrorState correlationId="4bf92f3577b34da6a3ce929d0e0e4736" onRetry={() => undefined} /> };
export const DialogOpen: Story = {
  render: () => (
    <Dialog open onOpenChange={() => undefined} title="Supprimer l’engin ?" description="L’engin EX-01 sera retiré de la liste." closeLabel="Fermer"
      footer={<><Button variant="secondary">Annuler</Button><Button variant="danger">Supprimer</Button></>} />
  ),
};

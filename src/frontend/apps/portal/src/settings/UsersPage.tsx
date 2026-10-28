import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import type { components } from "@karry/api-client";
import { useTranslation } from "@karry/i18n";
import { Button, DataTable, Dialog, EmptyState, ListDetailPage, SelectField, StatusChip, TextField, useToast } from "@karry/ui";
import { useAccess, useApi, useWithStepUp } from "../api";
import { useSites } from "../sites/api";
import { roleName } from "./labels";

type User = components["schemas"]["UserResponse"];
type Assignment = components["schemas"]["RoleAssignmentDto"];

const usersKey = ["identity", "users"] as const;

/**
 * People of the company and their roles (US-P1-IAM-06 AC1): the list shows those the person may see (their sites);
 * the detail changes a user's roles, each across the company or on one site (P1.11). Giving or taking a privileged
 * role asks the person to confirm their identity.
 */
export function UsersPage() {
  const { t } = useTranslation();
  const api = useApi();
  const users = useQuery({ queryKey: usersKey, queryFn: async () => (await api.GET("/api/v1/identity/users", { params: { query: { limit: 200 } } })).data?.items ?? [] });
  const [selected, setSelected] = useState<string | undefined>();
  const user = users.data?.find((u) => u.id === selected);

  return (
    <ListDetailPage
      title={t("portal.users.title")}
      detailLabel={t("portal.users.detail")}
      list={
        <DataTable<User>
          caption={t("portal.users.title")}
          rows={users.data ?? []}
          rowKey={(u) => u.id}
          selectedKey={selected}
          onSelect={(u) => { setSelected(u.id); }}
          empty={<EmptyState title={users.isPending ? t("common.loading") : t("common.empty.title")} />}
          columns={[
            { key: "name", header: t("portal.users.name"), cell: (u) => `${u.firstName} ${u.lastName}` },
            { key: "status", header: t("portal.users.status"), cell: (u) => <UserStatus status={u.status} /> },
            { key: "roles", header: t("portal.users.roles"), cell: (u) => <RoleNames roles={u.roles} /> },
          ]}
        />
      }
      detail={user ? <UserRoles key={user.id} user={user} /> : <EmptyState title={t("portal.users.pick")} />}
    />
  );
}

function UserStatus({ status }: { status: string }) {
  const { t } = useTranslation();
  const chip = status === "active" ? "ok" : status === "invited" ? "pending" : "error";
  return <StatusChip status={chip}>{t(`portal.users.status.${status}`, { defaultValue: status })}</StatusChip>;
}

function RoleNames({ roles }: { roles: Assignment[] }) {
  const { t } = useTranslation();
  const known = useQuery<components["schemas"]["RoleResponse"][]>({ queryKey: ["identity", "roles"], enabled: false });
  const name = (code: string) => roleName(t, known.data?.find((r) => r.code === code) ?? { code });
  return <>{roles.map((r) => name(r.role)).join(", ")}</>;
}

function UserRoles({ user }: { user: User }) {
  const { t } = useTranslation();
  const api = useApi();
  const withStepUp = useWithStepUp();
  const queryClient = useQueryClient();
  const toast = useToast();
  const { can } = useAccess();
  const roles = useQuery({ queryKey: ["identity", "roles"], queryFn: async () => (await api.GET("/api/v1/identity/roles")).data?.items ?? [], enabled: can("identity.roles.read") });
  const [assignments, setAssignments] = useState<Assignment[]>(user.roles);
  const manage = can("identity.users.manage");
  const sites = useSites(true);
  const companyWide = "company";
  const siteLabel = (id: string) => {
    const site = sites.data?.find((s) => s.id === id);
    return site ? `${site.code} · ${site.name}` : t("portal.users.scope.site", { site: id.slice(-6) });
  };
  // A site that is archived or out of the person's sight stays selectable for the assignment that already has it.
  const scopeOptions = (current: string | null) => [
    { value: companyWide, label: t("portal.users.scope.company") },
    ...(sites.data ?? []).filter((s) => s.status === "active" || s.id === current).map((s) => ({ value: s.id, label: siteLabel(s.id) })),
    ...(current && !sites.data?.some((s) => s.id === current) ? [{ value: current, label: siteLabel(current) }] : []),
  ];
  // Without identity.roles.read the choice is the default roles (their names are translated in the app).
  const choices = (roles.data ?? []).filter((r) => r.office || user.kind === "field");

  const save = useMutation({
    mutationFn: () =>
      withStepUp(async () => (await api.PUT("/api/v1/identity/users/{id}/roles", { params: { path: { id: user.id } }, body: { roles: assignments } })).data),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: usersKey });
      toast({ status: "ok", title: t("portal.users.saved") });
    },
  });

  const resend = useMutation({
    mutationFn: async () => (await api.POST("/api/v1/identity/users/{id}/invitation", { params: { path: { id: user.id } } })).data,
    onSuccess: () => { toast({ status: "ok", title: t("portal.users.resent") }); },
  });

  return (
    <div className="flex flex-col gap-4">
      <h2 className="text-xl font-bold">{`${user.firstName} ${user.lastName}`}</h2>
      <p className="text-text-muted">{user.email ?? user.phone ?? user.employeeId}</p>
      {user.requiresMfa && <StatusChip status="warning">{t("portal.users.mfa")}</StatusChip>}
      {user.kind === "field" && user.status === "active" && !user.pinSet && <StatusChip status="warning">{t("portal.users.noPin")}</StatusChip>}

      <ul className="flex flex-col gap-3" aria-label={t("portal.users.roles")}>
        {assignments.map((assignment, index) => (
          <li key={`${assignment.role}-${assignment.siteId ?? "all"}-${String(index)}`} className="flex flex-wrap items-end gap-3">
            <SelectField
              label={t("portal.users.role")}
              options={choices.map((r) => ({ value: r.code, label: roleName(t, r) }))}
              value={assignment.role}
              disabled={!manage}
              onValueChange={(role) => { setAssignments(assignments.map((a, i) => (i === index ? { ...a, role } : a))); }}
            />
            <SelectField
              label={t("portal.users.where")}
              options={scopeOptions(assignment.siteId ?? null)}
              value={assignment.siteId ?? companyWide}
              disabled={!manage}
              onValueChange={(where) => {
                setAssignments(assignments.map((a, i) => (i === index ? { ...a, siteId: where === companyWide ? null : where } : a)));
              }}
            />
            {manage && assignments.length > 1 && (
              <Button type="button" variant="secondary" onClick={() => { setAssignments(assignments.filter((_, i) => i !== index)); }}>
                {t("portal.users.removeRole")}
              </Button>
            )}
          </li>
        ))}
      </ul>

      {manage && (
        <div className="flex flex-wrap gap-3">
          <Button type="button" variant="secondary" disabled={choices.length === 0}
            onClick={() => { setAssignments([...assignments, { role: choices[0]?.code ?? "", siteId: null }]); }}>
            {t("portal.users.addRole")}
          </Button>
          <Button type="button" loading={save.isPending} onClick={() => { save.mutate(); }}>{t("portal.users.save")}</Button>
          {user.kind === "field" && user.status === "active" && <SetPinButton user={user} />}
          {user.status === "invited" && (
            <Button type="button" variant="ghost" loading={resend.isPending} onClick={() => { resend.mutate(); }}>{t("portal.users.resend")}</Button>
          )}
        </div>
      )}
    </div>
  );
}

const pinPattern = /^\d{6}$/;

/**
 * Sets a field user's PIN (an imported one has none until then; also a forgotten PIN). Typed by the administrator and
 * sent once: it is never shown again, and the API keeps only its hash. Simple PINs (111111, 123456) are refused there.
 */
function SetPinButton({ user }: { user: User }) {
  const { t } = useTranslation();
  const api = useApi();
  const queryClient = useQueryClient();
  const toast = useToast();
  const [open, setOpen] = useState(false);
  const [pin, setPin] = useState("");
  const valid = pinPattern.test(pin);

  const save = useMutation({
    mutationFn: async () => (await api.PUT("/api/v1/identity/users/{id}/pin", { params: { path: { id: user.id } }, body: { pin } })).data,
    onSuccess: async () => {
      close(false);
      await queryClient.invalidateQueries({ queryKey: usersKey });
      toast({ status: "ok", title: t("portal.users.pinSaved") });
    },
  });

  const close = (next: boolean) => {
    setOpen(next);
    if (!next) {
      setPin("");
      save.reset();
    }
  };

  return (
    <>
      <Button type="button" variant="secondary" onClick={() => { close(true); }}>{user.pinSet ? t("portal.users.changePin") : t("portal.users.setPin")}</Button>
      <Dialog
        open={open}
        onOpenChange={close}
        title={user.pinSet ? t("portal.users.changePin") : t("portal.users.setPin")}
        description={t("portal.users.pinHint", { name: `${user.firstName} ${user.lastName}` })}
        closeLabel={t("common.close")}
        footer={<Button type="button" disabled={!valid} loading={save.isPending} onClick={() => { save.mutate(); }}>{t("portal.users.savePin")}</Button>}
      >
        <TextField
          label={t("portal.users.pin")}
          type="password"
          inputMode="numeric"
          autoComplete="off"
          maxLength={6}
          value={pin}
          error={pin !== "" && !valid ? t("portal.users.pinInvalid") : undefined}
          onChange={(e) => { setPin(e.target.value.trim()); }}
        />
      </Dialog>
    </>
  );
}

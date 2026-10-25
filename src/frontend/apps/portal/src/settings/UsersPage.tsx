import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import type { components } from "@karry/api-client";
import { useTranslation } from "@karry/i18n";
import { Button, DataTable, EmptyState, ListDetailPage, SelectField, StatusChip, useToast } from "@karry/ui";
import { useAccess, useApi, useWithStepUp } from "../api";
import { roleName } from "./labels";

type User = components["schemas"]["UserResponse"];
type Assignment = components["schemas"]["RoleAssignmentDto"];

const usersKey = ["identity", "users"] as const;

/**
 * People of the company and their roles (US-P1-IAM-06 AC1): the list shows those the person may see (their sites);
 * the detail changes a user's roles. Site scopes are kept as they are; choosing sites arrives with the sites screen
 * (P1.11). Giving or taking a privileged role asks the person to confirm their identity.
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
            <span className="pb-2 text-text-muted">
              {assignment.siteId ? t("portal.users.scope.site", { site: assignment.siteId.slice(-6) }) : t("portal.users.scope.company")}
            </span>
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
          {user.status === "invited" && (
            <Button type="button" variant="ghost" loading={resend.isPending} onClick={() => { resend.mutate(); }}>{t("portal.users.resend")}</Button>
          )}
        </div>
      )}
    </div>
  );
}

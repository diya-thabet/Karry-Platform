import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link, useNavigate } from "@tanstack/react-router";
import { useState } from "react";
import type { components } from "@karry/api-client";
import { useTranslation } from "@karry/i18n";
import { Button, DataTable, EmptyState, SelectField, StatusChip, TextField } from "@karry/ui";
import { useAccess, useApi, useWithStepUp } from "../api";
import { byModule, fieldLabel, fieldLevels, moduleLabel, permissionLabel, roleName, type FieldLevel } from "./labels";

type Role = components["schemas"]["RoleResponse"];

const rolesKey = ["identity", "roles"] as const;

function useRoles() {
  const api = useApi();
  return useQuery({ queryKey: rolesKey, queryFn: async () => (await api.GET("/api/v1/identity/roles")).data?.items ?? [] });
}

function useCatalog() {
  const api = useApi();
  return useQuery({
    queryKey: ["identity", "permissions"],
    queryFn: async () => (await api.GET("/api/v1/identity/permissions")).data,
    staleTime: Infinity,
  });
}

/** Roles of the company (US-P1-IAM-05): the 12 default roles and the company's own. */
export function RolesPage() {
  const { t } = useTranslation();
  const roles = useRoles();
  const { can } = useAccess();
  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center justify-between gap-4">
        <h1 className="text-2xl font-semibold tracking-tight">{t("portal.roles.title")}</h1>
        {can("identity.roles.manage") && (
          <Button asChild>
            <Link to="/settings/roles/new">{t("portal.roles.new")}</Link>
          </Button>
        )}
      </div>
      <DataTable<Role>
        caption={t("portal.roles.title")}
        rows={roles.data ?? []}
        rowKey={(role) => role.code}
        empty={<EmptyState title={roles.isPending ? t("common.loading") : t("common.empty.title")} />}
        columns={[
          {
            key: "name",
            header: t("portal.roles.name"),
            cell: (role) => (
              <Link to="/settings/roles/$code" params={{ code: role.code }} className="font-semibold text-primary underline-offset-4 hover:underline">
                {roleName(t, role)}
              </Link>
            ),
          },
          { key: "kind", header: t("portal.roles.kind"), cell: (role) => t(role.isDefault ? "portal.roles.kind.default" : "portal.roles.kind.custom") },
          { key: "seat", header: t("portal.roles.seat"), cell: (role) => t(role.office ? "portal.roles.seat.office" : "portal.roles.seat.field") },
          {
            key: "privileged",
            header: t("portal.roles.privileged"),
            cell: (role) => (role.privileged ? <StatusChip status="warning">{t("portal.roles.privileged.yes")}</StatusChip> : t("portal.roles.privileged.no")),
          },
          { key: "permissions", header: t("portal.roles.permissionCount"), cell: (role) => role.permissions.length, numeric: true },
        ]}
      />
    </div>
  );
}

interface Draft {
  name: string;
  description: string;
  office: boolean;
  basedOn: string | null;
  permissions: Set<string>;
  fieldLevels: Record<string, string>;
}

function draftOf(role: Role | undefined, basedOn: string | null): Draft {
  return {
    name: role && !role.isDefault && basedOn === null ? (role.name ?? "") : "",
    description: role && basedOn === null ? (role.description ?? "") : "",
    office: role?.office ?? true,
    basedOn,
    permissions: new Set(role?.permissions ?? []),
    fieldLevels: { ...(role?.fieldLevels ?? {}) },
  };
}

/**
 * One role: what it may do, grouped by module, and what it sees of each sensitive field (US-P1-IAM-05 AC1/AC2,
 * US-P1-IAM-07 AC1). A default role's permissions are fixed (copy it to change them); its field levels are company
 * policy. A change that touches privileged rights asks the person to confirm their identity (AC3).
 */
export function RoleEditorPage({ code }: { code?: string }) {
  const { t } = useTranslation();
  const roles = useRoles();
  const catalog = useCatalog();
  const role = code === undefined ? undefined : roles.data?.find((r) => r.code === code);
  if (roles.isPending || catalog.isPending) {
    return <EmptyState title={t("common.loading")} />;
  }

  if (code !== undefined && !role) {
    return <EmptyState title={t("portal.roles.notFound")} />;
  }

  return <RoleForm key={code ?? "new"} role={role} roles={roles.data ?? []} catalog={catalog.data} />;
}

function RoleForm({ role, roles, catalog }: { role: Role | undefined; roles: Role[]; catalog: components["schemas"]["PermissionCatalogResponse"] | undefined }) {
  const { t } = useTranslation();
  const api = useApi();
  const withStepUp = useWithStepUp();
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const { can } = useAccess();
  const [draft, setDraft] = useState<Draft>(() => draftOf(role, null));
  const creating = role === undefined;
  const manage = can("identity.roles.manage");
  const permissionsEditable = manage && (creating || !role.isDefault);

  const save = useMutation({
    mutationFn: () =>
      withStepUp(async () => {
        const fieldLevels = draft.fieldLevels;
        if (creating) {
          return (await api.POST("/api/v1/identity/roles", {
            body: { name: draft.name, description: draft.description || null, basedOn: draft.basedOn, office: draft.office, permissions: [...draft.permissions], fieldLevels },
          })).data;
        }

        if (role.isDefault) {
          return (await api.PUT("/api/v1/identity/roles/{code}/field-levels", { params: { path: { code: role.code } }, body: { fieldLevels } })).data;
        }

        return (await api.PUT("/api/v1/identity/roles/{code}", {
          params: { path: { code: role.code } },
          body: { name: draft.name, description: draft.description || null, permissions: [...draft.permissions], fieldLevels },
        })).data;
      }),
    onSuccess: async (saved) => {
      await queryClient.invalidateQueries({ queryKey: rolesKey });
      if (creating && saved) {
        await navigate({ to: "/settings/roles/$code", params: { code: saved.code } });
      }
    },
  });

  const remove = useMutation({
    mutationFn: () => withStepUp(async () => api.DELETE("/api/v1/identity/roles/{code}", { params: { path: { code: role?.code ?? "" } } })),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: rolesKey });
      await navigate({ to: "/settings/roles" });
    },
  });

  const copyFrom = (basedOn: string) => {
    const basis = roles.find((r) => r.code === basedOn);
    setDraft({ ...draftOf(basis, basedOn), name: draft.name });
  };

  const title = creating ? t("portal.roles.new") : roleName(t, role);
  return (
    <form className="flex max-w-4xl flex-col gap-6" onSubmit={(event) => { event.preventDefault(); save.mutate(); }}>
      <div className="flex flex-col gap-1">
        <Link to="/settings/roles" className="text-sm font-medium text-text-muted hover:text-primary">{t("portal.roles.back")}</Link>
        <h1 className="text-2xl font-semibold tracking-tight">{title}</h1>
        {role?.isDefault && <p className="text-text-muted">{t("portal.roles.defaultHint")}</p>}
      </div>

      {creating && (
        <SelectField
          label={t("portal.roles.copyFrom")}
          description={t("portal.roles.copyFromHint")}
          options={roles.map((r) => ({ value: r.code, label: roleName(t, r) }))}
          value={draft.basedOn ?? undefined}
          onValueChange={copyFrom}
          placeholder={t("portal.roles.copyFromNone")}
        />
      )}

      {(creating || !role.isDefault) && (
        <div className="grid gap-4 md:grid-cols-2">
          <TextField label={t("portal.roles.name")} required maxLength={60} value={draft.name} disabled={!manage}
            onChange={(e) => { setDraft({ ...draft, name: e.target.value }); }} />
          <TextField label={t("portal.roles.description")} maxLength={200} value={draft.description} disabled={!manage}
            onChange={(e) => { setDraft({ ...draft, description: e.target.value }); }} />
        </div>
      )}

      {creating && (
        <SelectField
          label={t("portal.roles.seat")}
          options={[{ value: "office", label: t("portal.roles.seat.office") }, { value: "field", label: t("portal.roles.seat.field") }]}
          value={draft.office ? "office" : "field"}
          onValueChange={(value) => { setDraft({ ...draft, office: value === "office" }); }}
        />
      )}

      <section aria-labelledby="permissions-heading" className="flex flex-col gap-3 rounded-xl border-[length:var(--karry-border-width)] border-divider bg-surface p-5 shadow-[var(--karry-card-shadow)]">
        <h2 id="permissions-heading" className="text-base font-semibold">{t("portal.roles.permissions")}</h2>
        {byModule(catalog?.permissions ?? []).map(([module, permissions]) => (
          <fieldset key={module} className="rounded-lg border-[length:var(--karry-border-width)] border-divider p-4">
            <legend className="px-1 font-semibold">{moduleLabel(t, module)}</legend>
            <ul className="flex flex-col gap-2">
              {permissions.map((permission) => (
                <li key={permission.code}>
                  <label className="flex min-h-[var(--karry-control-height)] items-center gap-3">
                    <input
                      type="checkbox"
                      className="size-5"
                      checked={draft.permissions.has(permission.code)}
                      disabled={!permissionsEditable}
                      onChange={(e) => {
                        const next = new Set(draft.permissions);
                        if (e.target.checked) next.add(permission.code);
                        else next.delete(permission.code);
                        setDraft({ ...draft, permissions: next });
                      }}
                    />
                    <span>{permissionLabel(t, permission.code)}</span>
                    {permission.privileged && <StatusChip status="warning">{t("portal.roles.privileged.yes")}</StatusChip>}
                  </label>
                </li>
              ))}
            </ul>
          </fieldset>
        ))}
      </section>

      <section aria-labelledby="fields-heading" className="flex flex-col gap-3 rounded-xl border-[length:var(--karry-border-width)] border-divider bg-surface p-5 shadow-[var(--karry-card-shadow)]">
        <h2 id="fields-heading" className="text-base font-semibold">{t("portal.roles.fields")}</h2>
        {(catalog?.sensitiveFields.length ?? 0) === 0 ? (
          <p className="text-text-muted">{t("portal.roles.noFields")}</p>
        ) : (
          <div className="grid gap-4 md:grid-cols-2">
            {catalog?.sensitiveFields.map((field) => (
              <SelectField
                key={field.code}
                label={fieldLabel(t, field.code)}
                options={fieldLevels.map((level: FieldLevel) => ({ value: level, label: t(`portal.roles.level.${level}`) }))}
                value={draft.fieldLevels[field.code] ?? field.fallback}
                disabled={!manage}
                onValueChange={(value) => { setDraft({ ...draft, fieldLevels: { ...draft.fieldLevels, [field.code]: value } }); }}
              />
            ))}
          </div>
        )}
      </section>

      {manage && (
        <div className="flex flex-wrap gap-3">
          <Button type="submit" loading={save.isPending}>{t("portal.roles.save")}</Button>
          {!creating && !role.isDefault && (
            <Button type="button" variant="danger" loading={remove.isPending} onClick={() => { remove.mutate(); }}>
              {t("portal.roles.delete")}
            </Button>
          )}
          {save.isSuccess && <span role="status" className="self-center text-success">{t("portal.roles.saved")}</span>}
        </div>
      )}
    </form>
  );
}

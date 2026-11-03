import { useMutation, useQueryClient } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { useState, type SyntheticEvent } from "react";
import { Building2, Coins } from "lucide-react";
import { intlLocale, useTranslation } from "@karry/i18n";
import { Button, DataTable, Dialog, EmptyState, PageHeader, StatCard, StatusChip, TextField, useToast } from "@karry/ui";
import { useApi } from "../app/api";
import { operatorTenantsKey, slugFrom, slugValid, statusLook, useOperatorTenants, type OperatorTenant } from "./api";

/**
 * The operator console's company list (US-P1-OPS-03 AC1): every company with its plan, subscription state, sites,
 * machines, office users, MRR and last activity, the MRR totals, and a new company with its first administrator.
 */
export function TenantsPage() {
  const { t, i18n } = useTranslation();
  const tenants = useOperatorTenants();
  const [creating, setCreating] = useState(false);
  const rows = tenants.data?.tenants ?? [];
  const money = (amount: number | string, currency: string) =>
    new Intl.NumberFormat(intlLocale(i18n.language), { style: "currency", currency }).format(Number(amount));
  const count = (value: number | string | null) => (value === null ? "—" : new Intl.NumberFormat(intlLocale(i18n.language)).format(Number(value)));
  const day = (value: string | null) => (value ? new Intl.DateTimeFormat(intlLocale(i18n.language), { dateStyle: "medium" }).format(new Date(value)) : "—");

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title={t("portal.operator.companies")}
        description={t("portal.operator.companiesDescription")}
        actions={<Button onClick={() => { setCreating(true); }}>{t("portal.operator.newCompany")}</Button>}
      />
      <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
        <StatCard icon={Building2} label={t("portal.operator.companyCount")} value={tenants.isPending ? undefined : count(rows.length)} />
        {(tenants.data?.mrr ?? []).map((total) => (
          <StatCard key={total.currency} icon={Coins} label={t("portal.operator.mrrTotal")} value={money(total.amount, total.currency)} hint={t("portal.operator.mrrHint")} />
        ))}
      </div>
      <DataTable<OperatorTenant>
        caption={t("portal.operator.companies")}
        rows={rows}
        rowKey={(tenant) => tenant.id}
        loading={tenants.isPending}
        empty={<EmptyState title={t("portal.operator.none")} />}
        columns={[
          {
            key: "name",
            header: t("portal.operator.company"),
            cell: (tenant) => (
              <div className="flex flex-col">
                <Link to="/operator/companies/$tenantId" params={{ tenantId: tenant.id }} className="font-semibold text-primary underline-offset-4 hover:underline" translate="no">{tenant.name}</Link>
                <span className="text-xs text-text-muted" translate="no">{tenant.slug}</span>
              </div>
            ),
          },
          {
            key: "plan",
            header: t("portal.operator.plan"),
            cell: (tenant) => tenant.planCode ? <span translate="no">{tenant.planCode} v{String(tenant.planVersion)}</span> : "—",
          },
          {
            key: "status",
            header: t("portal.operator.state"),
            cell: (tenant) => (
              <div className="flex flex-wrap gap-1">
                {tenant.status && <StatusChip status={statusLook[tenant.status] ?? "pending"}>{t(`portal.plan.status.${tenant.status}`, { defaultValue: tenant.status })}</StatusChip>}
                {!tenant.complete && <StatusChip status="warning">{t("portal.operator.incomplete")}</StatusChip>}
              </div>
            ),
          },
          { key: "sites", header: t("portal.operator.sites"), cell: (tenant) => count(tenant.sites) },
          { key: "assets", header: t("portal.operator.machines"), cell: (tenant) => count(tenant.assets) },
          { key: "users", header: t("portal.operator.officeUsers"), cell: (tenant) => count(tenant.officeUsers) },
          {
            key: "mrr",
            header: t("portal.operator.mrr"),
            cell: (tenant) => tenant.mrr !== null && tenant.mrrCurrency ? money(tenant.mrr, tenant.mrrCurrency) : t("portal.operator.notPriced"),
          },
          { key: "activity", header: t("portal.operator.lastActivity"), cell: (tenant) => <time dateTime={tenant.lastActivity ?? undefined}>{day(tenant.lastActivity)}</time> },
        ]}
      />
      <NewCompanyDialog open={creating} onOpenChange={setCreating} />
    </div>
  );
}

interface CompanyForm {
  name: string;
  slug: string;
  firstName: string;
  lastName: string;
  email: string;
}

const empty: CompanyForm = { name: "", slug: "", firstName: "", lastName: "", email: "" };

/** A new company (US-P1-TEN-01 AC1): its name and address, and optionally its first administrator, invited by e-mail. */
function NewCompanyDialog({ open, onOpenChange }: { open: boolean; onOpenChange: (open: boolean) => void }) {
  const { t } = useTranslation();
  const api = useApi();
  const toast = useToast();
  const queryClient = useQueryClient();
  const [form, setForm] = useState<CompanyForm>(empty);
  const [slugTouched, setSlugTouched] = useState(false);
  const [checked, setChecked] = useState(false);
  const slug = slugTouched ? form.slug : slugFrom(form.name);
  const admin = [form.firstName, form.lastName, form.email].map((value) => value.trim());
  const someAdmin = admin.some((value) => value !== "");
  const nameError = form.name.trim() === "" ? t("portal.operator.nameRequired") : undefined;
  const slugError = slugValid(slug) ? undefined : t("portal.operator.slugInvalid");
  const adminError = someAdmin && (admin.some((value) => value === "") || !/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(admin[2] ?? ""))
    ? t("portal.operator.adminIncomplete")
    : undefined;

  const close = (next: boolean) => {
    if (!next) {
      setForm(empty);
      setSlugTouched(false);
      setChecked(false);
    }

    onOpenChange(next);
  };

  const create = useMutation({
    mutationFn: async () => (await api.POST("/api/v1/tenancy/tenants", {
      body: {
        slug,
        name: form.name.trim(),
        admin: someAdmin ? { firstName: admin[0] ?? "", lastName: admin[1] ?? "", email: admin[2] ?? "" } : null,
      },
    })).data,
    onSuccess: async (created) => {
      const invitation = created?.adminInvitation;
      toast({
        status: invitation && !invitation.delivered ? "warning" : "ok",
        title: t("portal.operator.created", { name: created?.name ?? "" }),
        ...(invitation ? { description: t(invitation.delivered ? "portal.operator.invited" : "portal.operator.notInvited", { email: admin[2] ?? "" }) } : {}),
      });
      await queryClient.invalidateQueries({ queryKey: operatorTenantsKey });
      close(false);
    },
  });

  const submit = (event: SyntheticEvent) => {
    event.preventDefault();
    setChecked(true);
    if (!nameError && !slugError && !adminError) {
      create.mutate();
    }
  };

  return (
    <Dialog
      open={open}
      onOpenChange={close}
      title={t("portal.operator.newCompany")}
      description={t("portal.operator.newCompanyDescription")}
      closeLabel={t("common.close")}
    >
      <form className="flex flex-col gap-4" onSubmit={submit} noValidate>
        <TextField label={t("portal.operator.companyName")} value={form.name} error={checked ? nameError : undefined} onChange={(e) => { setForm({ ...form, name: e.target.value }); }} />
        <TextField
          label={t("portal.operator.address")}
          description={t("portal.operator.addressHint", { slug: slug || "carriere-sfax" })}
          value={slug}
          error={checked || slugTouched ? slugError : undefined}
          onChange={(e) => { setSlugTouched(true); setForm({ ...form, slug: e.target.value.trim().toLowerCase() }); }}
        />
        <fieldset className="flex flex-col gap-3">
          <legend className="mb-1 text-sm font-medium">{t("portal.operator.firstAdmin")}</legend>
          <p className="text-sm text-text-muted">{t("portal.operator.firstAdminHint")}</p>
          <div className="grid gap-3 sm:grid-cols-2">
            <TextField label={t("portal.operator.firstName")} value={form.firstName} onChange={(e) => { setForm({ ...form, firstName: e.target.value }); }} />
            <TextField label={t("portal.operator.lastName")} value={form.lastName} onChange={(e) => { setForm({ ...form, lastName: e.target.value }); }} />
          </div>
          <TextField label={t("portal.operator.email")} type="email" value={form.email} error={checked ? adminError : undefined} onChange={(e) => { setForm({ ...form, email: e.target.value }); }} />
        </fieldset>
        <div className="flex justify-end gap-2">
          <Button type="button" variant="secondary" onClick={() => { close(false); }}>{t("common.cancel")}</Button>
          <Button type="submit" loading={create.isPending}>{t("portal.operator.create")}</Button>
        </div>
      </form>
    </Dialog>
  );
}

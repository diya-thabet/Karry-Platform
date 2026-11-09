import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useState, type SyntheticEvent } from "react";
import { intlLocale, useTranslation } from "@karry/i18n";
import { Button, Card, DataTable, Dialog, EmptyState, PageHeader, SelectField, StatusChip, TextField, useToast } from "@karry/ui";
import { useApi } from "../app/api";
import { VersionConflict } from "../app/VersionConflict";
import { handlesVersionConflict, isVersionConflict } from "../app/conflicts";
import { currencies } from "../app/currencies";
import { useRecordDraft } from "../app/recordDraft";
import { Trail } from "../shell/Trail";
import {
  actionsFor, invoicesKey, operatorTenantsKey, statusLook, usePlans, useOperatorTenants, useTenantInvoices, useTenantSubscription,
  type Invoice, type Subscription,
} from "./api";
import { SupportCard } from "./SupportCard";

/** Today as `yyyy-mm-dd` where the operator is (invoice dates are calendar days). */
function today(): string {
  const now = new Date();
  return `${String(now.getFullYear())}-${String(now.getMonth() + 1).padStart(2, "0")}-${String(now.getDate()).padStart(2, "0")}`;
}

/**
 * One company in the operator console (US-P1-OPS-03 AC1, US-P1-BIL-02): its subscription (terms and state) and its
 * invoices, managed by Karry staff until the billing provider. Every change is in the company's own ledger (API).
 */
export function TenantPage({ tenantId }: { tenantId: string }) {
  const { t } = useTranslation();
  const tenants = useOperatorTenants();
  const tenant = tenants.data?.tenants.find((candidate) => candidate.id === tenantId);
  const subscription = useTenantSubscription(tenantId);

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        breadcrumbs={<Trail steps={[{ to: "/operator", label: t("portal.operator.companies") }]} />}
        title={tenant?.name ?? "…"}
        titleTranslate="no"
        description={tenant ? <span translate="no">{tenant.slug}</span> : undefined}
      />
      {subscription.data
        ? (
          <>
            <SubscriptionCard tenantId={tenantId} subscription={subscription.data} />
            <TermsCard key={tenantId} tenantId={tenantId} subscription={subscription.data} />
          </>
        )
        : <EmptyState title={subscription.isPending ? t("common.loading") : t("portal.operator.noSubscription")} />}
      <InvoicesCard tenantId={tenantId} />
      {tenant && <SupportCard tenantId={tenantId} tenantName={tenant.name} />}
    </div>
  );
}

/** The state, its dates and the actions allowed from it (cancel asks for confirmation). */
function SubscriptionCard({ tenantId, subscription }: { tenantId: string; subscription: Subscription }) {
  const { t, i18n } = useTranslation();
  const api = useApi();
  const toast = useToast();
  const queryClient = useQueryClient();
  const [cancelling, setCancelling] = useState(false);
  const day = (value: string | null) => (value ? new Intl.DateTimeFormat(intlLocale(i18n.language), { dateStyle: "medium" }).format(new Date(value)) : "—");
  const act = useMutation({
    mutationFn: async (action: string) => (await api.POST("/api/v1/billing/tenants/{tenantId}/subscription/actions", {
      params: { path: { tenantId } }, body: { action },
    })).data,
    onSuccess: async (changed) => {
      setCancelling(false);
      toast({ status: "ok", title: t("portal.operator.stateChanged", { state: t(`portal.plan.status.${changed?.status ?? ""}`, { defaultValue: changed?.status ?? "" }) }) });
      await queryClient.invalidateQueries({ queryKey: operatorTenantsKey }); // the list and this company's queries
    },
  });
  const actions = actionsFor[subscription.status] ?? [];

  return (
    <Card title={t("portal.operator.subscription")} description={t("portal.operator.subscriptionHint")}>
      <dl className="grid gap-3 text-sm sm:grid-cols-2 lg:grid-cols-4">
        <div>
          <dt className="text-text-muted">{t("portal.operator.status")}</dt>
          <dd><StatusChip status={statusLook[subscription.status] ?? "pending"}>{t(`portal.plan.status.${subscription.status}`, { defaultValue: subscription.status })}</StatusChip></dd>
        </div>
        <div>
          <dt className="text-text-muted">{t("portal.operator.since")}</dt>
          <dd><time dateTime={subscription.statusSince}>{day(subscription.statusSince)}</time></dd>
        </div>
        {subscription.trialEndsAt && subscription.status === "trial" && (
          <div>
            <dt className="text-text-muted">{t("portal.operator.trialEnds")}</dt>
            <dd><time dateTime={subscription.trialEndsAt}>{day(subscription.trialEndsAt)}</time></dd>
          </div>
        )}
        {subscription.unpaidSince && (
          <div>
            <dt className="text-text-muted">{t("portal.operator.unpaidSince")}</dt>
            <dd><time dateTime={subscription.unpaidSince}>{day(subscription.unpaidSince)}</time></dd>
          </div>
        )}
      </dl>
      {actions.length > 0 && (
        <div className="mt-4 flex flex-wrap gap-2">
          {actions.map((action) => action === "cancel"
            ? <Button key={action} variant="danger" onClick={() => { setCancelling(true); }}>{t("portal.operator.action.cancel")}</Button>
            : <Button key={action} variant="secondary" loading={act.isPending && act.variables === action} onClick={() => { act.mutate(action); }}>{t(`portal.operator.action.${action}`)}</Button>)}
        </div>
      )}
      <Dialog
        open={cancelling}
        onOpenChange={setCancelling}
        title={t("portal.operator.cancelTitle")}
        description={t("portal.operator.cancelDescription")}
        closeLabel={t("common.close")}
        footer={(
          <>
            <Button variant="secondary" onClick={() => { setCancelling(false); }}>{t("common.cancel")}</Button>
            <Button variant="danger" loading={act.isPending} onClick={() => { act.mutate("cancel"); }}>{t("portal.operator.action.cancel")}</Button>
          </>
        )}
      />
    </Card>
  );
}

/** What the terms form edits, as text while typed. */
function termsOf(subscription: Subscription | null) {
  return {
    planCode: subscription?.planCode ?? "", period: subscription?.period ?? "monthly",
    sites: String(subscription?.sites ?? 1), extraAssets: String(subscription?.extraAssets ?? 0),
  };
}

/** Plan, period, sites and extra machines; add-ons, extra features and limit overrides are kept as they are. */
function TermsCard({ tenantId, subscription }: { tenantId: string; subscription: Subscription }) {
  const { t } = useTranslation();
  const api = useApi();
  const toast = useToast();
  const queryClient = useQueryClient();
  const plans = usePlans();
  // The terms and the version they were filled from: a save is applied to it only (If-Match, H-C1). A state action
  // (activate, payment…) makes a new version without touching what the operator typed.
  const { draft, setDraft, version, accept } = useRecordDraft(subscription, termsOf);
  const { planCode, period, sites, extraAssets } = draft;
  const setPlanCode = (value: string) => { setDraft((current) => ({ ...current, planCode: value })); };
  const setPeriod = (value: string) => { setDraft((current) => ({ ...current, period: value })); };
  const setSites = (value: string) => { setDraft((current) => ({ ...current, sites: value })); };
  const setExtraAssets = (value: string) => { setDraft((current) => ({ ...current, extraAssets: value })); };
  const [checked, setChecked] = useState(false);
  const [conflict, setConflict] = useState(false);
  const closed = ["cancelled", "expired", "deleted"].includes(subscription.status);
  const whole = (text: string, min: number) => (/^\d{1,5}$/.test(text.trim()) && Number(text) >= min ? Number(text) : null);
  const siteCount = whole(sites, 1);
  const extraCount = whole(extraAssets, 0);

  const save = useMutation({
    meta: handlesVersionConflict,
    mutationFn: async (expected?: string) => (await api.PUT("/api/v1/billing/tenants/{tenantId}/subscription", {
      params: { path: { tenantId }, header: (expected ?? version) ? { "If-Match": expected ?? version ?? "" } : {} },
      body: {
        planCode, planVersion: planCode === subscription.planCode ? subscription.planVersion : null, period, sites: siteCount ?? 1, extraAssets: extraCount ?? 0,
        addOns: subscription.addOns, extraFeatures: subscription.extraFeatures, limitOverrides: subscription.limitOverrides,
      },
    })).data,
    onError: (error) => { setConflict(isVersionConflict(error)); },
    onSuccess: async (saved) => {
      if (saved) {
        accept(saved);
      }

      setConflict(false);
      toast({ status: "ok", title: t("portal.operator.termsSaved") });
      await queryClient.invalidateQueries({ queryKey: operatorTenantsKey }); // the list and this company's queries
    },
  });

  const submit = (event: SyntheticEvent) => {
    event.preventDefault();
    setChecked(true);
    if (siteCount !== null && extraCount !== null) {
      save.mutate(undefined);
    }
  };

  const latest = async () => (await api.GET("/api/v1/billing/tenants/{tenantId}/subscription", { params: { path: { tenantId } } })).data;
  // The operator chose to save their terms over the other change: from the current version, deliberately.
  const keepMine = async () => {
    const current = await latest();
    if (current) {
      save.mutate(current.version);
    }
  };
  // The operator chose the other change: the form shows it and their entries are dropped.
  const showTheirs = async () => {
    const current = await latest();
    if (current) {
      accept(current);
      setConflict(false);
      await queryClient.invalidateQueries({ queryKey: operatorTenantsKey });
    }
  };

  const codes = [...new Set([subscription.planCode, ...(plans.data ?? []).map((plan) => plan.code)])];
  return (
    <Card title={t("portal.operator.terms")} description={closed ? t("portal.operator.termsClosed") : t("portal.operator.termsHint")}>
      <form className="flex flex-col gap-4" onSubmit={submit} noValidate>
        <div className="grid gap-4 md:grid-cols-4">
          <SelectField
            label={t("portal.operator.plan")}
            value={planCode}
            onValueChange={setPlanCode}
            options={codes.map((code) => ({
              value: code,
              label: code === subscription.planCode ? `${code} v${String(subscription.planVersion)}` : code,
            }))}
          />
          <SelectField
            label={t("portal.operator.period")}
            value={period}
            onValueChange={setPeriod}
            options={["monthly", "annual"].map((value) => ({ value, label: t(`portal.operator.period.${value}`) }))}
          />
          <TextField label={t("portal.operator.sites")} inputMode="numeric" value={sites} error={checked && siteCount === null ? t("portal.operator.sitesInvalid") : undefined} onChange={(e) => { setSites(e.target.value); }} />
          <TextField label={t("portal.operator.extraMachines")} inputMode="numeric" value={extraAssets} error={checked && extraCount === null ? t("portal.operator.extraInvalid") : undefined} onChange={(e) => { setExtraAssets(e.target.value); }} />
        </div>
        {planCode !== subscription.planCode && <p className="text-sm text-text-muted">{t("portal.operator.latestVersion")}</p>}
        {conflict && <VersionConflict busy={save.isPending} onKeepMine={() => void keepMine()} onShowTheirs={() => void showTheirs()} />}
        <div>
          <Button type="submit" disabled={closed} loading={save.isPending}>{t("portal.operator.saveTerms")}</Button>
        </div>
      </form>
    </Card>
  );
}

/** The company's invoices, newest first, with "paid" and a new invoice. */
function InvoicesCard({ tenantId }: { tenantId: string }) {
  const { t, i18n } = useTranslation();
  const api = useApi();
  const toast = useToast();
  const queryClient = useQueryClient();
  const invoices = useTenantInvoices(tenantId);
  const [recording, setRecording] = useState(false);
  const day = (value: string) => new Intl.DateTimeFormat(intlLocale(i18n.language), { dateStyle: "medium", timeZone: "UTC" }).format(new Date(`${value}T00:00:00Z`));
  const money = (invoice: Invoice) => new Intl.NumberFormat(intlLocale(i18n.language), { style: "currency", currency: invoice.currency }).format(Number(invoice.amount));
  const pay = useMutation({
    mutationFn: async (invoiceId: string) => (await api.POST("/api/v1/billing/tenants/{tenantId}/invoices/{invoiceId}/payment", {
      params: { path: { tenantId, invoiceId } }, body: { paidOn: today() },
    })).data,
    onSuccess: async () => {
      toast({ status: "ok", title: t("portal.operator.invoicePaid") });
      await queryClient.invalidateQueries({ queryKey: operatorTenantsKey }); // the list and this company's queries
    },
  });

  return (
    <Card
      title={t("portal.operator.invoices")}
      description={t("portal.operator.invoicesHint")}
      actions={<Button variant="secondary" onClick={() => { setRecording(true); }}>{t("portal.operator.recordInvoice")}</Button>}
    >
      <DataTable<Invoice>
        caption={t("portal.operator.invoices")}
        rows={invoices.data ?? []}
        rowKey={(invoice) => invoice.id}
        loading={invoices.isPending}
        empty={<EmptyState title={t("portal.operator.noInvoices")} />}
        columns={[
          { key: "number", header: t("portal.operator.invoiceNumber"), cell: (invoice) => <span translate="no">{invoice.number}</span> },
          { key: "amount", header: t("portal.operator.amount"), cell: (invoice) => <span translate="no">{money(invoice)}</span> },
          { key: "issued", header: t("portal.operator.issuedOn"), cell: (invoice) => <time dateTime={invoice.issuedOn}>{day(invoice.issuedOn)}</time> },
          { key: "due", header: t("portal.operator.dueOn"), cell: (invoice) => <time dateTime={invoice.dueOn}>{day(invoice.dueOn)}</time> },
          {
            key: "paid",
            header: t("portal.operator.paid"),
            cell: (invoice) => invoice.paidOn
              ? <StatusChip status="ok">{t("portal.operator.paidOn", { day: day(invoice.paidOn) })}</StatusChip>
              : (
                <Button variant="ghost" loading={pay.isPending && pay.variables === invoice.id} aria-label={t("portal.operator.markPaidOf", { number: invoice.number })} onClick={() => { pay.mutate(invoice.id); }}>
                  {t("portal.operator.markPaid")}
                </Button>
              ),
          },
        ]}
      />
      <InvoiceDialog tenantId={tenantId} open={recording} onOpenChange={setRecording} />
    </Card>
  );
}

/** A new invoice: number, amount, currency, issue and due dates (the API refuses a number used twice). */
function InvoiceDialog({ tenantId, open, onOpenChange }: { tenantId: string; open: boolean; onOpenChange: (open: boolean) => void }) {
  const { t } = useTranslation();
  const api = useApi();
  const toast = useToast();
  const queryClient = useQueryClient();
  const blank = { number: "", amount: "", currency: "EUR", issuedOn: today(), dueOn: today() };
  const [form, setForm] = useState(blank);
  const [checked, setChecked] = useState(false);
  const amount = /^\d{1,9}([.,]\d{1,3})?$/.test(form.amount.trim()) && Number(form.amount.replace(",", ".")) > 0 ? form.amount.trim().replace(",", ".") : null;
  const numberError = form.number.trim() === "" ? t("portal.operator.invoiceNumberRequired") : undefined;
  const datesError = form.issuedOn === "" || form.dueOn === "" || form.dueOn < form.issuedOn ? t("portal.operator.invoiceDatesInvalid") : undefined;

  const close = (next: boolean) => {
    if (!next) {
      setForm(blank);
      setChecked(false);
    }

    onOpenChange(next);
  };

  const record = useMutation({
    mutationFn: async () => (await api.POST("/api/v1/billing/tenants/{tenantId}/invoices", {
      params: { path: { tenantId } },
      body: { number: form.number.trim(), amount: amount ?? "0", currency: form.currency, issuedOn: form.issuedOn, dueOn: form.dueOn },
    })).data,
    onSuccess: async () => {
      toast({ status: "ok", title: t("portal.operator.invoiceRecorded") });
      await queryClient.invalidateQueries({ queryKey: invoicesKey(tenantId) });
      close(false);
    },
  });

  const submit = (event: SyntheticEvent) => {
    event.preventDefault();
    setChecked(true);
    if (!numberError && amount !== null && !datesError) {
      record.mutate();
    }
  };

  return (
    <Dialog open={open} onOpenChange={close} title={t("portal.operator.recordInvoice")} description={t("portal.operator.recordInvoiceHint")} closeLabel={t("common.close")}>
      <form className="flex flex-col gap-4" onSubmit={submit} noValidate>
        <TextField label={t("portal.operator.invoiceNumber")} value={form.number} error={checked ? numberError : undefined} onChange={(e) => { setForm({ ...form, number: e.target.value }); }} />
        <div className="grid gap-3 sm:grid-cols-2">
          <TextField label={t("portal.operator.amount")} inputMode="decimal" value={form.amount} error={checked && amount === null ? t("portal.operator.amountInvalid") : undefined} onChange={(e) => { setForm({ ...form, amount: e.target.value }); }} />
          <SelectField label={t("portal.operator.currency")} value={form.currency} onValueChange={(currency) => { setForm({ ...form, currency }); }} options={currencies.map((code) => ({ value: code, label: code }))} />
          <TextField label={t("portal.operator.issuedOn")} type="date" value={form.issuedOn} onChange={(e) => { setForm({ ...form, issuedOn: e.target.value }); }} />
          <TextField label={t("portal.operator.dueOn")} type="date" value={form.dueOn} error={checked ? datesError : undefined} onChange={(e) => { setForm({ ...form, dueOn: e.target.value }); }} />
        </div>
        <div className="flex justify-end gap-2">
          <Button type="button" variant="secondary" onClick={() => { close(false); }}>{t("common.cancel")}</Button>
          <Button type="submit" loading={record.isPending}>{t("portal.operator.record")}</Button>
        </div>
      </form>
    </Dialog>
  );
}

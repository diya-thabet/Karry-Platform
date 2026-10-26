import { useMutation, useQueryClient } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { useState, type ChangeEvent } from "react";
import type { components } from "@karry/api-client";
import { supportedLanguages, useTranslation } from "@karry/i18n";
import { Button, EmptyState, SelectField, TextField, useToast } from "@karry/ui";
import { currentTenantKey, SettingsManagePermission, useAccess, useApi, useCurrentTenant } from "../api";

type Tenant = components["schemas"]["TenantResponse"];
type Settings = components["schemas"]["UpdateTenantSettings"];

/** Currencies the API accepts (Karry.SharedKernel.Monetary.Currency). */
const currencies = ["AED", "CAD", "CHF", "DZD", "EGP", "EUR", "GBP", "LYD", "MAD", "QAR", "SAR", "TND", "USD", "XAF", "XOF"];

const months = Array.from({ length: 12 }, (_, index) => index + 1);
const logoTypes = "image/png,image/jpeg,image/webp";

function zones(): string[] {
  try {
    return Intl.supportedValuesOf("timeZone");
  } catch {
    return [];
  }
}

function draftOf(tenant: Tenant): Settings {
  return {
    legalName: tenant.legalName, primaryColour: tenant.primaryColour ?? null, language: tenant.language, currency: tenant.currency,
    unitSystem: tenant.unitSystem, timeZone: tenant.timeZone, fiscalYearStartMonth: Number(tenant.fiscalYearStartMonth),
  };
}

/** Company settings (US-P1-TEN-04): everyone sees them; holders of `tenancy.settings.manage` change them. */
export function CompanyPage() {
  const { t } = useTranslation();
  const { loaded, tenant } = useCurrentTenant();
  if (!loaded) {
    return null;
  }

  return tenant ? <CompanyForm key={tenant.settingsChangedAt ?? "new"} tenant={tenant} /> : <EmptyState title={t("portal.notFound")} />;
}

function CompanyForm({ tenant }: { tenant: Tenant }) {
  const { t, i18n } = useTranslation();
  const api = useApi();
  const queryClient = useQueryClient();
  const toast = useToast();
  const { can } = useAccess();
  const editable = can(SettingsManagePermission);
  const [draft, setDraft] = useState<Settings>(() => draftOf(tenant));
  const set = <K extends keyof Settings>(key: K, value: Settings[K]) => { setDraft((current) => ({ ...current, [key]: value })); };

  const saved = async (title: string) => {
    await queryClient.invalidateQueries({ queryKey: currentTenantKey });
    toast({ status: "ok", title });
  };

  const save = useMutation({
    mutationFn: async () => (await api.PUT("/api/v1/tenancy/settings", { body: { ...draft, primaryColour: draft.primaryColour?.trim() ? draft.primaryColour : null } })).data,
    onSuccess: () => saved(t("portal.company.saved")),
  });

  const upload = useMutation({
    mutationFn: async (file: File) =>
      (await api.PUT("/api/v1/tenancy/settings/logo", {
        body: file as unknown as components["schemas"]["IFormFile"],
        bodySerializer: (body: unknown) => body as BodyInit,
        headers: { "Content-Type": file.type || "application/octet-stream" },
      })).data,
    onSuccess: () => saved(t("portal.company.logoSaved")),
  });

  const removeLogo = useMutation({
    mutationFn: async () => (await api.DELETE("/api/v1/tenancy/settings/logo")).data,
    onSuccess: () => saved(t("portal.company.logoRemoved")),
  });

  const onFile = (event: ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0];
    if (file) {
      upload.mutate(file);
    }

    event.target.value = "";
  };

  const monthName = (month: number) => new Intl.DateTimeFormat(i18n.language, { month: "long", timeZone: "UTC" }).format(Date.UTC(2026, month - 1, 1));
  const zoneOptions = zones();
  return (
    <div className="flex max-w-3xl flex-col gap-6">
      <div className="flex flex-col gap-1">
        <Link to="/settings" className="text-primary underline-offset-4 hover:underline">{t("portal.company.back")}</Link>
        <h1 className="text-2xl font-bold">{t("portal.company.title")}</h1>
        {!editable && <p className="text-text-muted">{t("portal.company.readOnly")}</p>}
      </div>

      <section aria-labelledby="company-logo" className="flex flex-col gap-3">
        <h2 id="company-logo" className="text-lg font-semibold">{t("portal.company.logo")}</h2>
        <div className="flex flex-wrap items-center gap-4">
          {tenant.logoUrl
            ? <img src={tenant.logoUrl} alt={t("portal.company.logoAlt", { company: tenant.name })} className="h-16 max-w-48 object-contain" />
            : <p className="text-text-muted">{t("portal.company.noLogo")}</p>}
          {editable && (
            <>
              <label className="inline-flex min-h-[var(--karry-control-height)] cursor-pointer items-center rounded-md border-[length:var(--karry-border-width)] border-border px-4 font-semibold focus-within:outline focus-within:outline-2">
                {t("portal.company.chooseLogo")}
                <input type="file" accept={logoTypes} className="sr-only" onChange={onFile} />
              </label>
              {tenant.logoUrl && <Button variant="secondary" loading={removeLogo.isPending} onClick={() => { removeLogo.mutate(); }}>{t("portal.company.removeLogo")}</Button>}
            </>
          )}
        </div>
        <p className="text-sm text-text-muted">{t("portal.company.logoHint")}</p>
      </section>

      <form className="flex flex-col gap-4" onSubmit={(event) => { event.preventDefault(); save.mutate(); }}>
        <fieldset disabled={!editable} className="grid gap-4 md:grid-cols-2">
          <TextField label={t("portal.company.legalName")} value={draft.legalName ?? ""} required onChange={(e) => { set("legalName", e.target.value); }} />
          <TextField
            label={t("portal.company.colour")}
            description={t("portal.company.colourHint")}
            value={draft.primaryColour ?? ""}
            placeholder="#1F4E79"
            onChange={(e) => { set("primaryColour", e.target.value); }}
          />
          <SelectField
            label={t("portal.company.language")}
            value={draft.language ?? undefined}
            onValueChange={(value) => { set("language", value); }}
            options={supportedLanguages.map((language) => ({ value: language, label: t(`common.language.${language}`) }))}
            disabled={!editable}
          />
          <SelectField
            label={t("portal.company.currency")}
            value={draft.currency ?? undefined}
            onValueChange={(value) => { set("currency", value); }}
            options={currencies.map((code) => ({ value: code, label: code }))}
            disabled={!editable}
          />
          <SelectField
            label={t("portal.company.units")}
            value={draft.unitSystem ?? undefined}
            onValueChange={(value) => { set("unitSystem", value); }}
            options={[{ value: "metric", label: t("portal.company.metric") }, { value: "imperial", label: t("portal.company.imperial") }]}
            disabled={!editable}
          />
          <TextField
            label={t("portal.company.timeZone")}
            description={t("portal.company.timeZoneHint")}
            value={draft.timeZone ?? ""}
            list="company-time-zones"
            required
            onChange={(e) => { set("timeZone", e.target.value); }}
          />
          <datalist id="company-time-zones">
            {zoneOptions.map((zone) => <option key={zone} value={zone} />)}
          </datalist>
          <SelectField
            label={t("portal.company.fiscalYear")}
            value={String(draft.fiscalYearStartMonth)}
            onValueChange={(value) => { set("fiscalYearStartMonth", Number(value)); }}
            options={months.map((month) => ({ value: String(month), label: monthName(month) }))}
            disabled={!editable}
          />
        </fieldset>
        {editable && (
          <div>
            <Button type="submit" loading={save.isPending}>{t("portal.company.save")}</Button>
          </div>
        )}
      </form>
    </div>
  );
}

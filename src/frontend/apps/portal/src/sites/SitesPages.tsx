import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link, useNavigate } from "@tanstack/react-router";
import { useState } from "react";
import { useTranslation } from "@karry/i18n";
import { Button, DataTable, EmptyState, SelectField, StatusChip, TextField, useToast } from "@karry/ui";
import { useAccess, useApi, useCurrentTenant } from "../api";
import { currencies } from "../currencies";
import { countries, siteKey, sitesKey, SitesManagePermission, siteTypes, timeZones, useSites, type Site, type SiteInput } from "./api";
import { MapPicker, type Ring } from "./MapPicker";
import { ShiftsSection } from "./ShiftsSection";

/** No currency override: the site uses the company's currency. */
const companyCurrency = "company";

/** Sites of the company (US-P1-ORG-01): those the person may see; archived ones on request. */
export function SitesPage() {
  const { t } = useTranslation();
  const { can } = useAccess();
  const [includeArchived, setIncludeArchived] = useState(false);
  const sites = useSites(includeArchived);

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center justify-between gap-4">
        <h1 className="text-2xl font-bold">{t("portal.nav.sites")}</h1>
        {can(SitesManagePermission) && (
          <Button asChild>
            <Link to="/sites/new">{t("portal.sites.new")}</Link>
          </Button>
        )}
      </div>
      <label className="flex min-h-[var(--karry-control-height)] items-center gap-3">
        <input type="checkbox" className="size-5" checked={includeArchived} onChange={(e) => { setIncludeArchived(e.target.checked); }} />
        <span>{t("portal.sites.includeArchived")}</span>
      </label>
      <DataTable<Site>
        caption={t("portal.nav.sites")}
        rows={sites.data ?? []}
        rowKey={(site) => site.id}
        empty={<EmptyState title={sites.isPending && sites.fetchStatus !== "idle" ? t("common.loading") : t("portal.sites.none")} />}
        columns={[
          { key: "code", header: t("portal.sites.code"), cell: (site) => <span translate="no">{site.code}</span> },
          {
            key: "name",
            header: t("portal.sites.name"),
            cell: (site) => (
              <Link to="/sites/$siteId" params={{ siteId: site.id }} className="font-semibold text-primary underline-offset-4 hover:underline" translate="no">
                {site.name}
              </Link>
            ),
          },
          { key: "type", header: t("portal.sites.type"), cell: (site) => t(`portal.sites.type.${site.type}`) },
          { key: "city", header: t("portal.sites.city"), cell: (site) => <span translate="no">{site.address.city}</span> },
          { key: "status", header: t("portal.sites.status"), cell: (site) => <SiteStatus site={site} /> },
        ]}
      />
    </div>
  );
}

function SiteStatus({ site }: { site: Site }) {
  const { t } = useTranslation();
  return site.status === "active"
    ? <StatusChip status="ok">{t("portal.sites.status.active")}</StatusChip>
    : <StatusChip status="pending">{t("portal.sites.status.archived")}</StatusChip>;
}

/** One site: its details, pin and boundary, then its shifts and non-working days; or a new site. */
export function SitePage({ siteId }: { siteId?: string }) {
  const { t } = useTranslation();
  const api = useApi();
  const site = useQuery({
    queryKey: siteKey(siteId ?? "new"),
    queryFn: async () => (await api.GET("/api/v1/organization/sites/{siteId}", { params: { path: { siteId: siteId ?? "" } } })).data ?? null,
    enabled: siteId !== undefined,
  });
  const { loaded, tenant } = useCurrentTenant();

  if (siteId === undefined) {
    return loaded ? <SiteForm site={null} defaultTimeZone={tenant?.timeZone ?? "UTC"} /> : null;
  }

  if (site.isPending) {
    return null;
  }

  if (!site.data) {
    return <EmptyState title={t("portal.notFound")} />;
  }

  return (
    <div className="flex flex-col gap-8">
      <SiteForm key={site.data.changedAt} site={site.data} defaultTimeZone={site.data.timeZone} />
      <ShiftsSection site={site.data} />
    </div>
  );
}

interface Draft {
  code: string;
  name: string;
  type: string;
  line: string;
  city: string;
  region: string;
  postalCode: string;
  country: string;
  latitude: string;
  longitude: string;
  boundary: Ring;
  timeZone: string;
  currency: string;
}

function draftOf(site: Site | null, timeZone: string): Draft {
  if (!site) {
    return {
      code: "", name: "", type: "quarry", line: "", city: "", region: "", postalCode: "", country: "TN", latitude: "", longitude: "",
      boundary: [], timeZone, currency: companyCurrency,
    };
  }

  // The API answers a closed ring; the form works on the open one.
  const ring = (site.boundary?.coordinates[0] ?? []).map((point) => [Number(point[0]), Number(point[1])] as [number, number]).slice(0, -1);
  return {
    code: site.code, name: site.name, type: site.type, line: site.address.line, city: site.address.city, region: site.address.region ?? "",
    postalCode: site.address.postalCode ?? "", country: site.address.country, latitude: String(site.location.latitude),
    longitude: String(site.location.longitude), boundary: ring, timeZone: site.timeZone, currency: site.currency ?? companyCurrency,
  };
}

const coordinate = (text: string, limit: number) => {
  const value = Number(text.replace(",", "."));
  return text.trim() !== "" && Number.isFinite(value) && Math.abs(value) <= limit ? value : null;
};

function SiteForm({ site, defaultTimeZone }: { site: Site | null; defaultTimeZone: string }) {
  const { t, i18n } = useTranslation();
  const api = useApi();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const toast = useToast();
  const { can } = useAccess();
  const editable = can(SitesManagePermission) && site?.status !== "archived";
  const [draft, setDraft] = useState<Draft>(() => draftOf(site, defaultTimeZone));
  const [checked, setChecked] = useState(false);
  const set = <K extends keyof Draft>(key: K, value: Draft[K]) => { setDraft((current) => ({ ...current, [key]: value })); };

  const latitude = coordinate(draft.latitude, 90);
  const longitude = coordinate(draft.longitude, 180);
  const boundaryIncomplete = draft.boundary.length > 0 && draft.boundary.length < 3;

  const body = (): SiteInput => ({
    code: draft.code, name: draft.name, type: draft.type,
    address: { line: draft.line, city: draft.city, region: draft.region || null, postalCode: draft.postalCode || null, country: draft.country },
    location: { latitude: latitude ?? 0, longitude: longitude ?? 0 },
    boundary: draft.boundary.length >= 3 ? [[...draft.boundary, ...draft.boundary.slice(0, 1)]] : null,
    timeZone: draft.timeZone,
    currency: draft.currency === companyCurrency ? null : draft.currency,
  });

  const refresh = async (saved: Site) => {
    await queryClient.invalidateQueries({ queryKey: sitesKey });
    queryClient.setQueryData(siteKey(saved.id), saved);
  };

  const save = useMutation({
    mutationFn: async () => site
      ? (await api.PUT("/api/v1/organization/sites/{siteId}", { params: { path: { siteId: site.id } }, body: body() })).data
      : (await api.POST("/api/v1/organization/sites", { body: body() })).data,
    onSuccess: async (saved) => {
      if (!saved) {
        return;
      }

      await refresh(saved);
      toast({ status: "ok", title: t("portal.sites.saved") });
      if (!site) {
        await navigate({ to: "/sites/$siteId", params: { siteId: saved.id } });
      }
    },
  });

  const archive = useMutation({
    mutationFn: async (action: "archive" | "restore") => action === "archive"
      ? (await api.POST("/api/v1/organization/sites/{siteId}/archive", { params: { path: { siteId: site?.id ?? "" } } })).data
      : (await api.POST("/api/v1/organization/sites/{siteId}/restore", { params: { path: { siteId: site?.id ?? "" } } })).data,
    onSuccess: async (saved, action) => {
      if (saved) {
        await refresh(saved);
        toast({ status: "ok", title: t(action === "archive" ? "portal.sites.archived" : "portal.sites.restored") });
      }
    },
  });

  const submit = () => {
    setChecked(true);
    if (latitude !== null && longitude !== null && !boundaryIncomplete) {
      save.mutate();
    }
  };

  const zoneOptions = timeZones();
  return (
    <div className="flex max-w-4xl flex-col gap-6">
      <div className="flex flex-col gap-1">
        <Link to="/sites" className="text-primary underline-offset-4 hover:underline">{t("portal.sites.back")}</Link>
        <div className="flex flex-wrap items-center gap-3">
          <h1 className="text-2xl font-bold" translate={site ? "no" : undefined}>{site ? site.name : t("portal.sites.new")}</h1>
          {site && <SiteStatus site={site} />}
        </div>
        {site?.status === "archived" && <p className="text-text-muted">{t("portal.sites.archivedHint")}</p>}
        {!can(SitesManagePermission) && <p className="text-text-muted">{t("portal.sites.readOnly")}</p>}
      </div>

      <form className="flex flex-col gap-6" noValidate onSubmit={(event) => { event.preventDefault(); submit(); }}>
        <fieldset disabled={!editable} className="grid gap-4 md:grid-cols-2">
          <TextField
            label={t("portal.sites.code")}
            description={site ? t("portal.sites.codeFixed") : t("portal.sites.codeHint")}
            value={draft.code}
            required
            disabled={site !== null || !editable}
            onChange={(e) => { set("code", e.target.value.toUpperCase()); }}
          />
          <TextField label={t("portal.sites.name")} value={draft.name} required onChange={(e) => { set("name", e.target.value); }} />
          <SelectField
            label={t("portal.sites.type")}
            value={draft.type}
            onValueChange={(value) => { set("type", value); }}
            options={siteTypes.map((type) => ({ value: type, label: t(`portal.sites.type.${type}`) }))}
            disabled={!editable}
          />
          <SelectField
            label={t("portal.sites.currency")}
            value={draft.currency}
            onValueChange={(value) => { set("currency", value); }}
            options={[{ value: companyCurrency, label: t("portal.sites.companyCurrency") }, ...currencies.map((code) => ({ value: code, label: code }))]}
            disabled={!editable}
          />
        </fieldset>

        <fieldset disabled={!editable} className="grid gap-4 md:grid-cols-2">
          <legend className="mb-2 text-lg font-semibold">{t("portal.sites.address")}</legend>
          <TextField label={t("portal.sites.addressLine")} value={draft.line} required onChange={(e) => { set("line", e.target.value); }} />
          <TextField label={t("portal.sites.city")} value={draft.city} required onChange={(e) => { set("city", e.target.value); }} />
          <TextField label={t("portal.sites.region")} value={draft.region} onChange={(e) => { set("region", e.target.value); }} />
          <TextField label={t("portal.sites.postalCode")} value={draft.postalCode} onChange={(e) => { set("postalCode", e.target.value); }} />
          <SelectField
            label={t("portal.sites.country")}
            value={draft.country}
            onValueChange={(value) => { set("country", value); }}
            options={countries(i18n.language)}
            disabled={!editable}
          />
          <TextField
            label={t("portal.sites.timeZone")}
            description={t("portal.sites.timeZoneHint")}
            value={draft.timeZone}
            list="site-time-zones"
            required
            onChange={(e) => { set("timeZone", e.target.value); }}
          />
          <datalist id="site-time-zones">
            {zoneOptions.map((zone) => <option key={zone} value={zone} />)}
          </datalist>
        </fieldset>

        <fieldset disabled={!editable} className="flex flex-col gap-4">
          <legend className="mb-2 text-lg font-semibold">{t("portal.sites.location")}</legend>
          <div className="grid gap-4 md:grid-cols-2">
            <TextField
              label={t("portal.sites.latitude")}
              inputMode="decimal"
              value={draft.latitude}
              required
              error={checked && latitude === null ? t("portal.sites.latitudeInvalid") : undefined}
              onChange={(e) => { set("latitude", e.target.value); }}
            />
            <TextField
              label={t("portal.sites.longitude")}
              inputMode="decimal"
              value={draft.longitude}
              required
              error={checked && longitude === null ? t("portal.sites.longitudeInvalid") : undefined}
              onChange={(e) => { set("longitude", e.target.value); }}
            />
          </div>
          <MapPicker
            location={latitude !== null && longitude !== null ? { latitude, longitude } : null}
            boundary={draft.boundary}
            editable={editable}
            onLocation={(lat, lon) => { setDraft((current) => ({ ...current, latitude: String(lat), longitude: String(lon) })); }}
            onBoundary={(ring) => { set("boundary", ring); }}
          />
          {checked && boundaryIncomplete && <p role="alert" className="font-semibold text-danger">{t("portal.sites.boundaryIncomplete")}</p>}
        </fieldset>

        {editable && (
          <div>
            <Button type="submit" loading={save.isPending}>{site ? t("portal.sites.save") : t("portal.sites.create")}</Button>
          </div>
        )}
      </form>

      {site && can(SitesManagePermission) && (
        <div>
          {site.status === "active"
            ? <Button variant="secondary" loading={archive.isPending} onClick={() => { archive.mutate("archive"); }}>{t("portal.sites.archive")}</Button>
            : <Button loading={archive.isPending} onClick={() => { archive.mutate("restore"); }}>{t("portal.sites.restore")}</Button>}
        </div>
      )}
    </div>
  );
}

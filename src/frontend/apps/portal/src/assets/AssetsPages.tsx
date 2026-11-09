import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link, useNavigate } from "@tanstack/react-router";
import { SearchX } from "lucide-react";
import { useState, type SyntheticEvent } from "react";
import { useTranslation } from "@karry/i18n";
import { Button, DataTable, EmptyState, PageHeader, PageSkeleton, SelectField, StatusChip, TextField, useToast, type Status } from "@karry/ui";
import { useAccess, useApi, useCurrentTenant } from "../app/api";
import { VersionConflict } from "../app/VersionConflict";
import { handlesVersionConflict, isVersionConflict } from "../app/conflicts";
import { useRecordDraft } from "../app/recordDraft";
import { Trail } from "../shell/Trail";
import { todayIn } from "../sites/api";
import { LabelPrintDialog, LabelPanel, MetersPanel, PhotoPanel, PlacePanel } from "./AssetPanels";
import {
  assetKey, assetsKey, AssetsManagePermission, assetStatuses, maxLabels, useAssetTypes, useSiteNames, type Asset, type AssetChangeInput, type AssetInput,
} from "./api";

/** "Any" in a filter: the select needs a value of its own. */
const any = "any";
const pageSize = 50;

/** How each status looks: in use, waiting, or out of the fleet. */
const statusLook: Record<string, Status> = { active: "ok", "in-repair": "warning", idle: "pending", disposed: "error" };

export function AssetStatusChip({ status }: { status: string }) {
  const { t } = useTranslation();
  return <StatusChip status={statusLook[status] ?? "pending"}>{t(`portal.assets.status.${status}`)}</StatusChip>;
}

interface Filters {
  q: string;
  siteId: string;
  type: string;
  status: string;
}

/** The machine register (US-P1-AST-02 AC4): search by code, name, make, model, serial or plate, filters, labels. */
export function AssetsPage() {
  const { t } = useTranslation();
  const api = useApi();
  const { can } = useAccess();
  const types = useAssetTypes();
  const { sites, name } = useSiteNames();
  const [typed, setTyped] = useState("");
  const [filters, setFilters] = useState<Filters>({ q: "", siteId: any, type: any, status: any });
  const [printing, setPrinting] = useState(false);
  const set = (key: keyof Filters, value: string) => { setFilters((current) => ({ ...current, [key]: value })); };

  const assets = useInfiniteQuery({
    queryKey: [...assetsKey, filters],
    queryFn: async ({ pageParam }) => (await api.GET("/api/v1/assets", {
      params: {
        query: {
          limit: pageSize,
          ...(filters.q ? { q: filters.q } : {}),
          ...(filters.siteId === any ? {} : { siteId: filters.siteId }),
          ...(filters.type === any ? {} : { type: filters.type }),
          ...(filters.status === any ? {} : { status: filters.status }),
          ...(pageParam ? { cursor: pageParam } : {}),
        },
      },
    })).data ?? { items: [], nextCursor: null },
    initialPageParam: "",
    getNextPageParam: (page) => page.nextCursor ?? undefined,
  });
  const rows = assets.data?.pages.flatMap((page) => page.items) ?? [];

  const search = (event: SyntheticEvent) => {
    event.preventDefault();
    set("q", typed.trim());
  };

  return (
    <div className="flex flex-col gap-4">
      <PageHeader
        title={t("portal.nav.assets")}
        actions={can(AssetsManagePermission) && (
          <>
            <Button variant="secondary" disabled={rows.length === 0} onClick={() => { setPrinting(true); }}>{t("portal.assets.printLabels")}</Button>
            <Button asChild>
              <Link to="/assets/new">{t("portal.assets.new")}</Link>
            </Button>
          </>
        )}
      />

      <form role="search" className="flex flex-wrap items-end gap-2" onSubmit={search}>
        <TextField className="min-w-64" label={t("portal.assets.search")} description={t("portal.assets.searchHint")} type="search" value={typed} onChange={(e) => { setTyped(e.target.value); }} />
        <Button type="submit" variant="secondary">{t("portal.assets.searchButton")}</Button>
      </form>
      <div className="grid gap-4 md:grid-cols-3">
        <SelectField
          label={t("portal.assets.site")}
          value={filters.siteId}
          onValueChange={(value) => { set("siteId", value); }}
          options={[{ value: any, label: t("portal.assets.anySite") }, ...sites.map((site) => ({ value: site.id, label: site.name }))]}
        />
        <SelectField
          label={t("portal.assets.type")}
          value={filters.type}
          onValueChange={(value) => { set("type", value); }}
          options={[{ value: any, label: t("portal.assets.anyType") }, ...(types.data ?? []).map((type) => ({ value: type.code, label: t(`portal.assets.type.${type.code}`) }))]}
        />
        <SelectField
          label={t("portal.assets.status")}
          value={filters.status}
          onValueChange={(value) => { set("status", value); }}
          options={[{ value: any, label: t("portal.assets.inFleet") }, ...assetStatuses.map((status) => ({ value: status, label: t(`portal.assets.status.${status}`) }))]}
        />
      </div>

      <DataTable<Asset>
        caption={t("portal.nav.assets")}
        rows={rows}
        rowKey={(asset) => asset.id}
        loading={assets.isPending}
        empty={<EmptyState title={t("portal.assets.none")} />}
        columns={[
          { key: "code", header: t("portal.assets.code"), cell: (asset) => <span translate="no">{asset.code}</span> },
          {
            key: "name",
            header: t("portal.assets.name"),
            cell: (asset) => (
              <Link to="/assets/$assetId" params={{ assetId: asset.id }} className="font-semibold text-primary underline-offset-4 hover:underline" translate="no">
                {asset.name}
              </Link>
            ),
          },
          { key: "type", header: t("portal.assets.type"), cell: (asset) => t(`portal.assets.type.${asset.type}`) },
          { key: "plate", header: t("portal.assets.plateOrSerial"), cell: (asset) => <span translate="no">{asset.plate ?? asset.serialNumber ?? "—"}</span> },
          { key: "site", header: t("portal.assets.site"), cell: (asset) => <span translate="no">{name(asset.siteId)}</span> },
          { key: "status", header: t("portal.assets.status"), cell: (asset) => <AssetStatusChip status={asset.status} /> },
        ]}
      />
      {assets.hasNextPage && (
        <div>
          <Button variant="secondary" loading={assets.isFetchingNextPage} onClick={() => void assets.fetchNextPage()}>{t("portal.assets.more")}</Button>
        </div>
      )}
      <LabelPrintDialog open={printing} onOpenChange={setPrinting} assets={rows.slice(0, maxLabels)} />
    </div>
  );
}

/** One machine: details, where it is, photo, label and meters; or a new machine. */
export function AssetPage({ assetId }: { assetId?: string }) {
  const { t } = useTranslation();
  const api = useApi();
  const asset = useQuery({
    queryKey: assetKey(assetId ?? "new"),
    queryFn: async () => (await api.GET("/api/v1/assets/{assetId}", { params: { path: { assetId: assetId ?? "" } } })).data ?? null,
    enabled: assetId !== undefined,
  });

  if (assetId === undefined) {
    return <AssetForm asset={null} />;
  }

  if (asset.isPending) {
    return <PageSkeleton />;
  }

  if (!asset.data) {
    return <EmptyState icon={SearchX} title={t("portal.notFound")} />;
  }

  return (
    <div className="flex max-w-4xl flex-col gap-8">
      <AssetForm key={asset.data.id} asset={asset.data} />
      <PlacePanel asset={asset.data} />
      <div className="grid gap-8 lg:grid-cols-2">
        <PhotoPanel asset={asset.data} />
        <LabelPanel asset={asset.data} />
      </div>
      <MetersPanel asset={asset.data} />
    </div>
  );
}

interface Draft {
  code: string;
  name: string;
  type: string;
  make: string;
  model: string;
  serialNumber: string;
  plate: string;
  year: string;
  acquiredOn: string;
  siteId: string;
}

function draftOf(asset: Asset | null): Draft {
  return {
    code: asset?.code ?? "", name: asset?.name ?? "", type: asset?.type ?? "excavator", make: asset?.make ?? "", model: asset?.model ?? "",
    serialNumber: asset?.serialNumber ?? "", plate: asset?.plate ?? "", year: asset?.year == null ? "" : String(asset.year),
    // The site is chosen here only for a new asset; a move (Place panel) is not something this form edits.
    acquiredOn: asset?.acquiredOn ?? "", siteId: "",
  };
}

function AssetForm({ asset }: { asset: Asset | null }) {
  const { t } = useTranslation();
  const api = useApi();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const toast = useToast();
  const { can } = useAccess();
  const { tenant } = useCurrentTenant();
  const types = useAssetTypes();
  const { sites } = useSiteNames();
  const editable = can(AssetsManagePermission) && asset?.status !== "disposed";
  // The details and the version they were filled from: a save is applied to it only (If-Match, H-C1). A move, a status,
  // a photo or a label on the same page makes a new version without touching what the person typed.
  const { draft, setDraft, version, accept } = useRecordDraft(asset, draftOf);
  const [checked, setChecked] = useState(false);
  const [conflict, setConflict] = useState(false);
  const set = <K extends keyof Draft>(key: K, value: Draft[K]) => { setDraft((current) => ({ ...current, [key]: value })); };

  const today = todayIn(tenant?.timeZone ?? "UTC");
  const year = draft.year.trim() === "" ? null : /^\d{4}$/.test(draft.year.trim()) ? Number(draft.year) : undefined;
  const siteMissing = asset === null && draft.siteId === "";
  const optional = (text: string) => text.trim() || null;
  const details = (): AssetChangeInput => ({
    name: draft.name, type: draft.type, make: optional(draft.make), model: optional(draft.model), serialNumber: optional(draft.serialNumber),
    plate: optional(draft.plate), year: year ?? null, acquiredOn: draft.acquiredOn || null,
  });

  const save = useMutation({
    meta: handlesVersionConflict,
    mutationFn: async (expected?: string) => {
      if (!asset) {
        return (await api.POST("/api/v1/assets", { body: { ...details(), code: draft.code, siteId: draft.siteId } satisfies AssetInput })).data;
      }

      const tag = expected ?? version;
      return (await api.PUT("/api/v1/assets/{assetId}", {
        params: { path: { assetId: asset.id }, header: tag ? { "If-Match": tag } : {} },
        body: details(),
      })).data;
    },
    onError: (error) => { setConflict(isVersionConflict(error)); },
    onSuccess: async (saved) => {
      if (!saved) {
        return;
      }

      accept(saved);
      setConflict(false);
      await queryClient.invalidateQueries({ queryKey: assetsKey });
      queryClient.setQueryData(assetKey(saved.id), saved);
      toast({ status: "ok", title: t("portal.assets.saved") });
      if (!asset) {
        await navigate({ to: "/assets/$assetId", params: { assetId: saved.id } });
      }
    },
  });

  const submit = () => {
    setChecked(true);
    if (year !== undefined && !siteMissing) {
      save.mutate(undefined);
    }
  };

  const latest = async () => asset ? (await api.GET("/api/v1/assets/{assetId}", { params: { path: { assetId: asset.id } } })).data : undefined;
  // The person chose to save their entries over the other change: from the current version, deliberately.
  const keepMine = async () => {
    const current = await latest();
    if (current) {
      save.mutate(current.version);
    }
  };
  // The person chose the other change: the form shows it and their entries are dropped.
  const showTheirs = async () => {
    const current = await latest();
    if (current) {
      accept(current);
      setConflict(false);
      queryClient.setQueryData(assetKey(current.id), current);
      await queryClient.invalidateQueries({ queryKey: assetsKey });
    }
  };

  const manageable = sites.filter((site) => site.status === "active");
  return (
    <div className="flex max-w-4xl flex-col gap-6">
      <PageHeader
        breadcrumbs={<Trail steps={[{ to: "/assets", label: t("portal.nav.assets") }]} />}
        title={asset ? `${asset.code} · ${asset.name}` : t("portal.assets.new")}
        titleTranslate={asset ? "no" : undefined}
        status={asset && <AssetStatusChip status={asset.status} />}
      >
        {asset?.status === "disposed" && <p className="text-text-muted">{t("portal.assets.disposedHint")}</p>}
        {!can(AssetsManagePermission) && <p className="text-text-muted">{t("portal.assets.readOnly")}</p>}
      </PageHeader>

      <form className="flex flex-col gap-6 rounded-xl border-[length:var(--karry-border-width)] border-divider bg-surface p-5 shadow-[var(--karry-card-shadow)] md:p-6" noValidate onSubmit={(event) => { event.preventDefault(); submit(); }}>
        <fieldset disabled={!editable} className="grid gap-4 md:grid-cols-2">
          <TextField
            label={t("portal.assets.code")}
            description={asset ? t("portal.assets.codeFixed") : t("portal.assets.codeHint")}
            value={draft.code}
            required
            disabled={asset !== null || !editable}
            onChange={(e) => { set("code", e.target.value.toUpperCase()); }}
          />
          <TextField label={t("portal.assets.name")} value={draft.name} required onChange={(e) => { set("name", e.target.value); }} />
          <SelectField
            label={t("portal.assets.type")}
            description={t("portal.assets.typeHint")}
            value={draft.type}
            onValueChange={(value) => { set("type", value); }}
            options={(types.data ?? []).map((type) => ({ value: type.code, label: t(`portal.assets.type.${type.code}`) }))}
            disabled={!editable}
          />
          {asset === null && (
            <SelectField
              label={t("portal.assets.site")}
              value={draft.siteId || undefined}
              placeholder={t("portal.assets.chooseSite")}
              error={checked && siteMissing ? t("portal.assets.siteRequired") : undefined}
              onValueChange={(value) => { set("siteId", value); }}
              options={manageable.map((site) => ({ value: site.id, label: site.name }))}
              disabled={!editable}
            />
          )}
          <TextField label={t("portal.assets.make")} value={draft.make} onChange={(e) => { set("make", e.target.value); }} />
          <TextField label={t("portal.assets.model")} value={draft.model} onChange={(e) => { set("model", e.target.value); }} />
          <TextField label={t("portal.assets.serialNumber")} value={draft.serialNumber} onChange={(e) => { set("serialNumber", e.target.value.toUpperCase()); }} />
          <TextField label={t("portal.assets.plate")} value={draft.plate} onChange={(e) => { set("plate", e.target.value.toUpperCase()); }} />
          <TextField
            label={t("portal.assets.year")}
            inputMode="numeric"
            value={draft.year}
            error={checked && year === undefined ? t("portal.assets.yearInvalid") : undefined}
            onChange={(e) => { set("year", e.target.value); }}
          />
          <TextField label={t("portal.assets.acquiredOn")} type="date" max={today} value={draft.acquiredOn} onChange={(e) => { set("acquiredOn", e.target.value); }} />
        </fieldset>

        {conflict && <VersionConflict busy={save.isPending} onKeepMine={() => void keepMine()} onShowTheirs={() => void showTheirs()} />}

        {editable && (
          <div>
            <Button type="submit" loading={save.isPending}>{asset ? t("portal.assets.save") : t("portal.assets.create")}</Button>
          </div>
        )}
      </form>
    </div>
  );
}

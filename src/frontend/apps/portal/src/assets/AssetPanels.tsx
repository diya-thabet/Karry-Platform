import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState, type ChangeEvent } from "react";
import type { components } from "@karry/api-client";
import { useTranslation } from "@karry/i18n";
import { Button, DataTable, Dialog, EmptyState, SelectField, StatusChip, TextField, useToast } from "@karry/ui";
import { useAccess, useApi } from "../api";
import {
  assetKey, assetsKey, AssetsManagePermission, assetStatuses, blobUrl, labelsPerSheet, meterKinds, metersKey, readingsKey, readingText,
  useSiteNames, type Asset, type Meter, type Reading,
} from "./api";

const photoTypes = "image/jpeg,image/png,image/webp";

/** Dates and times as the person reads them. */
function useFormat() {
  const { i18n } = useTranslation();
  return {
    number: (value: number | string) => new Intl.NumberFormat(i18n.language, { maximumFractionDigits: 1 }).format(Number(value)),
    date: (value: string) => new Intl.DateTimeFormat(i18n.language, { dateStyle: "medium", timeZone: "UTC" }).format(new Date(`${value.slice(0, 10)}T00:00:00Z`)),
    dateTime: (value: string) => new Intl.DateTimeFormat(i18n.language, { dateStyle: "medium", timeStyle: "short" }).format(new Date(value)),
  };
}

/** Saves an asset the API answered, so the page and the list show it at once. */
function useSaved() {
  const queryClient = useQueryClient();
  return async (saved: Asset | undefined) => {
    if (saved) {
      queryClient.setQueryData(assetKey(saved.id), saved);
      // Only the register's pages: the machine itself, its photo and meters are already up to date.
      await queryClient.invalidateQueries({ queryKey: assetsKey, predicate: (query) => typeof query.queryKey[1] === "object" });
    }
  };
}

/** Status, current site and the sites the machine has been on (the history cannot be changed, ADR-0026). */
export function PlacePanel({ asset }: { asset: Asset }) {
  const { t } = useTranslation();
  const api = useApi();
  const toast = useToast();
  const { can } = useAccess();
  const { sites, name } = useSiteNames();
  const format = useFormat();
  const saved = useSaved();
  const manage = can(AssetsManagePermission);
  const [siteId, setSiteId] = useState(asset.siteId);

  const status = useMutation({
    mutationFn: async (next: string) => (await api.POST("/api/v1/assets/{assetId}/status", { params: { path: { assetId: asset.id } }, body: { status: next } })).data,
    onSuccess: async (result) => {
      await saved(result);
      toast({ status: "ok", title: t("portal.assets.statusSaved") });
    },
  });

  const move = useMutation({
    mutationFn: async () => (await api.POST("/api/v1/assets/{assetId}/move", { params: { path: { assetId: asset.id } }, body: { siteId } })).data,
    onSuccess: async (result) => {
      await saved(result);
      toast({ status: "ok", title: t("portal.assets.moved") });
    },
  });

  const history = [...asset.sites].reverse();
  return (
    <section aria-labelledby="asset-place" className="flex max-w-4xl flex-col gap-4">
      <h2 id="asset-place" className="text-lg font-semibold">{t("portal.assets.place")}</h2>
      {manage && (
        <div className="grid items-end gap-4 md:grid-cols-2">
          <SelectField
            label={t("portal.assets.status")}
            value={asset.status}
            onValueChange={(value) => { status.mutate(value); }}
            options={assetStatuses.map((code) => ({ value: code, label: t(`portal.assets.status.${code}`) }))}
            disabled={status.isPending}
          />
          {asset.status !== "disposed" && (
            <div className="flex items-end gap-2">
              <SelectField
                label={t("portal.assets.moveTo")}
                value={siteId}
                onValueChange={setSiteId}
                options={sites.filter((site) => site.status === "active" || site.id === asset.siteId).map((site) => ({ value: site.id, label: site.name }))}
              />
              <Button variant="secondary" disabled={siteId === asset.siteId} loading={move.isPending} onClick={() => { move.mutate(); }}>{t("portal.assets.move")}</Button>
            </div>
          )}
        </div>
      )}
      <DataTable<components["schemas"]["SiteAssignmentResponse"]>
        caption={t("portal.assets.siteHistory")}
        rows={history}
        rowKey={(stay) => `${stay.siteId}-${stay.from}`}
        empty={null}
        columns={[
          { key: "site", header: t("portal.assets.site"), cell: (stay) => <span translate="no">{name(stay.siteId)}</span> },
          { key: "from", header: t("portal.assets.since"), cell: (stay) => format.dateTime(stay.from) },
        ]}
      />
    </section>
  );
}

/** The machine's photo (US-P1-AST-01 AC3): fetched with the person's token, so it is never a public link. */
export function PhotoPanel({ asset }: { asset: Asset }) {
  const { t } = useTranslation();
  const api = useApi();
  const toast = useToast();
  const { can } = useAccess();
  const saved = useSaved();
  const editable = can(AssetsManagePermission) && asset.status !== "disposed";
  const sha = asset.photo?.sha256;

  const photo = useQuery({
    queryKey: [...assetKey(asset.id), "photo", sha],
    queryFn: async () => {
      const blob = (await api.GET("/api/v1/assets/{assetId}/photo", { params: { path: { assetId: asset.id } }, parseAs: "blob" })).data;
      return blob ? blobUrl(blob) ?? null : null;
    },
    enabled: sha !== undefined,
    staleTime: Infinity,
  });
  const url = sha === undefined ? null : photo.data ?? null;
  useEffect(() => () => { if (url) URL.revokeObjectURL(url); }, [url]);

  const upload = useMutation({
    mutationFn: async (file: File) => (await api.PUT("/api/v1/assets/{assetId}/photo", {
      params: { path: { assetId: asset.id } },
      body: file as unknown as string,
      bodySerializer: (body: unknown) => body as BodyInit,
      headers: { "Content-Type": file.type || "application/octet-stream" },
    })).data,
    onSuccess: async (result) => {
      await saved(result);
      toast({ status: "ok", title: t("portal.assets.photoSaved") });
    },
  });

  const remove = useMutation({
    mutationFn: async () => (await api.DELETE("/api/v1/assets/{assetId}/photo", { params: { path: { assetId: asset.id } } })).data,
    onSuccess: async (result) => {
      await saved(result);
      toast({ status: "ok", title: t("portal.assets.photoRemoved") });
    },
  });

  const onFile = (event: ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0];
    if (file) {
      upload.mutate(file);
    }

    event.target.value = "";
  };

  return (
    <section aria-labelledby="asset-photo" className="flex flex-col gap-3">
      <h2 id="asset-photo" className="text-lg font-semibold">{t("portal.assets.photo")}</h2>
      {url
        ? <img src={url} alt={t("portal.assets.photoAlt", { asset: asset.name })} className="max-h-64 w-full max-w-md rounded-md object-contain" />
        : <p className="text-text-muted">{sha ? t("common.loading") : t("portal.assets.noPhoto")}</p>}
      {editable && (
        <div className="flex flex-wrap gap-2">
          <label className="inline-flex min-h-[var(--karry-control-height)] cursor-pointer items-center rounded-md border-[length:var(--karry-border-width)] border-border px-4 font-semibold focus-within:outline focus-within:outline-2">
            {t("portal.assets.choosePhoto")}
            <input type="file" accept={photoTypes} className="sr-only" onChange={onFile} />
          </label>
          {sha && <Button variant="secondary" loading={remove.isPending} onClick={() => { remove.mutate(); }}>{t("portal.assets.removePhoto")}</Button>}
        </div>
      )}
      <p className="text-sm text-text-muted">{t("portal.assets.photoHint")}</p>
    </section>
  );
}

/** The QR label (US-P1-AST-02 AC2): what it encodes, printing it, and reissuing it when one is lost. */
export function LabelPanel({ asset }: { asset: Asset }) {
  const { t } = useTranslation();
  const api = useApi();
  const toast = useToast();
  const { can } = useAccess();
  const saved = useSaved();
  const [printing, setPrinting] = useState(false);
  const [confirming, setConfirming] = useState(false);
  const editable = can(AssetsManagePermission) && asset.status !== "disposed";

  const reissue = useMutation({
    mutationFn: async () => (await api.POST("/api/v1/assets/{assetId}/label", { params: { path: { assetId: asset.id } } })).data,
    onSuccess: async (result) => {
      setConfirming(false);
      await saved(result);
      toast({ status: "ok", title: t("portal.assets.labelReissued") });
    },
  });

  return (
    <section aria-labelledby="asset-label" className="flex flex-col gap-3">
      <h2 id="asset-label" className="text-lg font-semibold">{t("portal.assets.label")}</h2>
      <p className="break-all font-mono" translate="no">{asset.label}</p>
      <p className="text-sm text-text-muted">{t("portal.assets.labelHint")}</p>
      {can(AssetsManagePermission) && (
        <div className="flex flex-wrap gap-2">
          <Button variant="secondary" onClick={() => { setPrinting(true); }}>{t("portal.assets.printLabel")}</Button>
          {editable && <Button variant="secondary" onClick={() => { setConfirming(true); }}>{t("portal.assets.reissueLabel")}</Button>}
        </div>
      )}
      <LabelPrintDialog open={printing} onOpenChange={setPrinting} assets={[asset]} />
      <Dialog
        open={confirming}
        onOpenChange={setConfirming}
        title={t("portal.assets.reissueLabel")}
        description={t("portal.assets.reissueHint")}
        closeLabel={t("common.close")}
        footer={(
          <>
            <Button variant="secondary" onClick={() => { setConfirming(false); }}>{t("portal.assets.cancel")}</Button>
            <Button loading={reissue.isPending} onClick={() => { reissue.mutate(); }}>{t("portal.assets.reissueConfirm")}</Button>
          </>
        )}
      />
    </section>
  );
}

/**
 * Prints labels as the API's A4 PDF (3 × 8). A sheet already partly used is reused by skipping its first labels; the
 * PDF opens as a download, since the browser's own viewer prints it at its true size.
 */
export function LabelPrintDialog({ open, onOpenChange, assets }: { open: boolean; onOpenChange: (open: boolean) => void; assets: Asset[] }) {
  const { t } = useTranslation();
  const api = useApi();
  const [skip, setSkip] = useState("0");
  const [file, setFile] = useState<string | null>(null);
  const skipValue = /^\d{1,2}$/.test(skip.trim()) && Number(skip) < labelsPerSheet ? Number(skip) : null;

  const print = useMutation({
    mutationFn: async () => (await api.POST("/api/v1/assets/labels/sheet", {
      body: { assetIds: assets.map((asset) => asset.id), skip: skipValue ?? 0 },
      parseAs: "blob",
    })).data,
    onSuccess: (pdf) => {
      setFile(pdf ? blobUrl(pdf) ?? null : null);
    },
  });

  const close = (next: boolean) => {
    if (!next) {
      if (file) URL.revokeObjectURL(file);
      setFile(null);
      print.reset();
    }

    onOpenChange(next);
  };

  return (
    <Dialog
      open={open}
      onOpenChange={close}
      title={t("portal.assets.printLabels")}
      description={t("portal.assets.printHint", { count: assets.length })}
      closeLabel={t("common.close")}
      footer={print.isSuccess
        ? file && <Button asChild><a href={file} download="karry-labels.pdf">{t("portal.assets.downloadLabels")}</a></Button>
        : <Button disabled={skipValue === null || assets.length === 0} loading={print.isPending} onClick={() => { print.mutate(); }}>{t("portal.assets.makeLabels")}</Button>}
    >
      {print.isSuccess
        ? <p role="status">{t("portal.assets.labelsReady")}</p>
        : (
          <TextField
            label={t("portal.assets.skip")}
            description={t("portal.assets.skipHint")}
            inputMode="numeric"
            value={skip}
            error={skipValue === null ? t("portal.assets.skipInvalid") : undefined}
            onChange={(e) => { setSkip(e.target.value); }}
          />
        )}
    </Dialog>
  );
}

/** Hour and km meters (US-P1-AST-03): the last reading, a new reading, and the history nobody can change. */
export function MetersPanel({ asset }: { asset: Asset }) {
  const { t } = useTranslation();
  const api = useApi();
  const { can } = useAccess();
  const format = useFormat();
  const meters = useQuery({
    queryKey: metersKey(asset.id),
    queryFn: async () => (await api.GET("/api/v1/assets/{assetId}/meters", { params: { path: { assetId: asset.id } } })).data?.items ?? [],
  });
  const [selected, setSelected] = useState<string | undefined>(undefined);
  const meter = meters.data?.find((m) => m.id === selected) ?? meters.data?.[0];
  const editable = can(AssetsManagePermission) && asset.status !== "disposed";

  return (
    <section aria-labelledby="asset-meters" className="flex max-w-4xl flex-col gap-4">
      <h2 id="asset-meters" className="text-lg font-semibold">{t("portal.assets.meters")}</h2>
      <DataTable<Meter>
        caption={t("portal.assets.meters")}
        rows={meters.data ?? []}
        rowKey={(m) => m.id}
        selectedKey={meter?.id}
        onSelect={(m) => { setSelected(m.id); }}
        empty={<EmptyState title={meters.isPending ? t("common.loading") : t("portal.assets.noMeters")} />}
        columns={[
          { key: "kind", header: t("portal.assets.meterKind"), cell: (m) => t(`portal.assets.meterKind.${m.kind}`) },
          { key: "last", header: t("portal.assets.lastReading"), cell: (m) => `${format.number(m.lastValue)} ${t(`portal.assets.unit.${m.kind}`)}` },
          { key: "at", header: t("portal.assets.readAt"), cell: (m) => format.dateTime(m.lastReadAt) },
          { key: "installed", header: t("portal.assets.installedOn"), cell: (m) => format.date(m.installedOn) },
          {
            key: "status",
            header: t("portal.assets.meterStatus"),
            cell: (m) => <StatusChip status={m.status === "ok" ? "ok" : "error"}>{t(`portal.assets.meterStatus.${m.status}`)}</StatusChip>,
          },
        ]}
      />
      {editable && <AddMeter asset={asset} />}
      {meter && <MeterDetail key={meter.id} asset={asset} meter={meter} editable={editable} />}
    </section>
  );
}

function AddMeter({ asset }: { asset: Asset }) {
  const { t } = useTranslation();
  const api = useApi();
  const queryClient = useQueryClient();
  const toast = useToast();
  const [kind, setKind] = useState<string>("hours");
  const [initial, setInitial] = useState("0");
  const value = readingText(initial);

  const add = useMutation({
    mutationFn: async () => (await api.POST("/api/v1/assets/{assetId}/meters", {
      params: { path: { assetId: asset.id } },
      body: { kind, initialReading: value ?? "0", installedOn: null },
    })).data,
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: metersKey(asset.id) });
      toast({ status: "ok", title: t("portal.assets.meterAdded") });
    },
  });

  return (
    <details className="rounded-md border-[length:var(--karry-border-width)] border-border p-4">
      <summary className="cursor-pointer font-semibold">{t("portal.assets.addMeter")}</summary>
      <form className="mt-4 grid items-end gap-4 md:grid-cols-3" noValidate onSubmit={(event) => { event.preventDefault(); if (value !== null) add.mutate(); }}>
        <SelectField
          label={t("portal.assets.meterKind")}
          value={kind}
          onValueChange={setKind}
          options={meterKinds.map((code) => ({ value: code, label: t(`portal.assets.meterKind.${code}`) }))}
        />
        <TextField
          label={t("portal.assets.initialReading")}
          inputMode="decimal"
          value={initial}
          error={value === null ? t("portal.assets.readingInvalid") : undefined}
          onChange={(e) => { setInitial(e.target.value); }}
        />
        <div>
          <Button type="submit" variant="secondary" loading={add.isPending}>{t("portal.assets.addMeterButton")}</Button>
        </div>
      </form>
    </details>
  );
}

function MeterDetail({ asset, meter, editable }: { asset: Asset; meter: Meter; editable: boolean }) {
  const { t } = useTranslation();
  const api = useApi();
  const queryClient = useQueryClient();
  const toast = useToast();
  const format = useFormat();
  const [typed, setTyped] = useState("");
  const [checked, setChecked] = useState(false);
  const value = readingText(typed);
  const unit = t(`portal.assets.unit.${meter.kind}`);
  const path = { assetId: asset.id, meterId: meter.id };

  const readings = useQuery({
    queryKey: readingsKey(asset.id, meter.id),
    queryFn: async () => (await api.GET("/api/v1/assets/{assetId}/meters/{meterId}/readings", { params: { path, query: { limit: 50 } } })).data?.items ?? [],
  });
  const refresh = async () => {
    await queryClient.invalidateQueries({ queryKey: metersKey(asset.id) });
  };

  const record = useMutation({
    mutationFn: async () => (await api.POST("/api/v1/assets/{assetId}/meters/{meterId}/readings", { params: { path }, body: { value: value ?? "0", readAt: null } })).data,
    onSuccess: async () => {
      setTyped("");
      setChecked(false);
      await refresh();
      toast({ status: "ok", title: t("portal.assets.readingSaved") });
    },
  });

  const status = useMutation({
    mutationFn: async (next: string) => (await api.POST("/api/v1/assets/{assetId}/meters/{meterId}/status", { params: { path }, body: { status: next } })).data,
    onSuccess: refresh,
  });

  // Newest first, as the API lists them.
  const rows = readings.data ?? [];
  return (
    <div className="flex flex-col gap-4">
      <h3 className="font-semibold">{t("portal.assets.meterHistory", { meter: t(`portal.assets.meterKind.${meter.kind}`) })}</h3>
      {editable && meter.status === "ok" && (
        <form className="flex flex-wrap items-end gap-2" noValidate onSubmit={(event) => { event.preventDefault(); setChecked(true); if (value !== null) record.mutate(); }}>
          <TextField
            label={t("portal.assets.newReading", { unit })}
            description={t("portal.assets.newReadingHint", { last: format.number(meter.lastValue), unit })}
            inputMode="decimal"
            value={typed}
            error={checked && value === null ? t("portal.assets.readingInvalid") : undefined}
            onChange={(e) => { setTyped(e.target.value); }}
          />
          <Button type="submit" loading={record.isPending}>{t("portal.assets.record")}</Button>
        </form>
      )}
      {editable && (
        <div>
          {meter.status === "ok"
            ? <Button variant="secondary" loading={status.isPending} onClick={() => { status.mutate("failed"); }}>{t("portal.assets.markFailed")}</Button>
            : <Button variant="secondary" loading={status.isPending} onClick={() => { status.mutate("ok"); }}>{t("portal.assets.markOk")}</Button>}
        </div>
      )}
      <DataTable<Reading>
        caption={t("portal.assets.meterHistory", { meter: t(`portal.assets.meterKind.${meter.kind}`) })}
        rows={rows}
        rowKey={(reading) => reading.id}
        empty={<EmptyState title={readings.isPending ? t("common.loading") : t("portal.assets.noReadings")} />}
        columns={[
          { key: "value", header: t("portal.assets.reading"), cell: (reading) => `${format.number(reading.value)} ${unit}` },
          { key: "readAt", header: t("portal.assets.readAt"), cell: (reading) => format.dateTime(reading.readAt) },
          { key: "recordedAt", header: t("portal.assets.recordedAt"), cell: (reading) => format.dateTime(reading.recordedAt) },
        ]}
      />
      <p className="text-sm text-text-muted">{t("portal.assets.historyHint")}</p>
    </div>
  );
}

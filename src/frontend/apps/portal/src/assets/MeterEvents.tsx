import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState, type ChangeEvent } from "react";
import { intlLocale, useTranslation } from "@karry/i18n";
import { Button, buttonVariants, DataTable, EmptyState, SelectField, TextAreaField, TextField, useToast } from "@karry/ui";
import { useApi } from "../app/api";
import { localInstant, meterEventsKey, metersKey, readingsKey, readingText, type Asset, type Meter, type MeterEvent } from "./api";

const photoTypes = "image/jpeg,image/png,image/webp";
const reasonMin = 10;
const reasonMax = 500;
const maxPhotos = 5;

/** What a meter event needs (ADR-0036 §5): replaced and reset both values, repaired the reading at the repair. */
const kindsFor = (status: string) => (status === "failed" ? ["repaired", "replaced"] : ["replaced", "reset", "failed"]);
const needsBefore = (kind: string) => kind === "replaced" || kind === "reset";
const allowsBefore = (kind: string) => needsBefore(kind) || kind === "failed";
const needsAfter = (kind: string) => kind !== "failed";

/**
 * The events of a meter (US-P2-AST-01, 02): replaced, reset, failed, repaired, each with when, the values, why and the
 * photos that show it. A person who manages the machine records one here; it cannot be changed afterwards.
 */
export function MeterEvents({ asset, meter, editable }: { asset: Asset; meter: Meter; editable: boolean }) {
  const { t, i18n } = useTranslation();
  const api = useApi();
  const path = { assetId: asset.id, meterId: meter.id };
  const unit = t(`portal.assets.unit.${meter.kind}`);
  const number = (value: number | string | null | undefined) =>
    value === null || value === undefined ? "—" : `${new Intl.NumberFormat(intlLocale(i18n.language), { maximumFractionDigits: 1 }).format(Number(value))} ${unit}`;
  const dateTime = (value: string) => new Intl.DateTimeFormat(intlLocale(i18n.language), { dateStyle: "medium", timeStyle: "short" }).format(new Date(value));

  const events = useQuery({
    queryKey: meterEventsKey(asset.id, meter.id),
    queryFn: async () => (await api.GET("/api/v1/assets/{assetId}/meters/{meterId}/events", { params: { path } })).data?.items ?? [],
  });
  // Newest first on screen; the API lists them as they happened.
  const rows = [...(events.data ?? [])].reverse();

  return (
    <div className="flex flex-col gap-3">
      <h3 className="font-semibold">{t("portal.meterEvents.title")}</h3>
      {editable && <RecordMeterEvent asset={asset} meter={meter} />}
      <DataTable<MeterEvent>
        caption={t("portal.meterEvents.title")}
        rows={rows}
        rowKey={(e) => e.id}
        loading={events.isPending}
        empty={<EmptyState title={t("portal.meterEvents.none")} />}
        columns={[
          { key: "at", header: t("portal.meterEvents.occurredAt"), cell: (e) => dateTime(e.occurredAt) },
          { key: "kind", header: t("portal.meterEvents.kind"), cell: (e) => t(`portal.meterEvents.kind.${e.kind}`) },
          { key: "before", header: t("portal.meterEvents.valueBefore"), cell: (e) => number(e.valueBefore) },
          { key: "after", header: t("portal.meterEvents.valueAfter"), cell: (e) => number(e.valueAfter) },
          { key: "reason", header: t("portal.meterEvents.reason"), cell: (e) => e.reason },
          { key: "photos", header: t("portal.meterEvents.photos"), cell: (e) => String(e.photos.length) },
          { key: "by", header: t("portal.meterEvents.recordedBy"), cell: (e) => e.recordedBy ?? "—" },
        ]}
      />
      <p className="text-sm text-text-muted">{t("portal.meterEvents.hint")}</p>
    </div>
  );
}

function RecordMeterEvent({ asset, meter }: { asset: Asset; meter: Meter }) {
  const { t } = useTranslation();
  const api = useApi();
  const queryClient = useQueryClient();
  const toast = useToast();
  const path = { assetId: asset.id, meterId: meter.id };
  const unit = t(`portal.assets.unit.${meter.kind}`);
  const kinds = kindsFor(meter.status);
  const [kind, setKind] = useState(kinds[0] ?? "replaced");
  const [at, setAt] = useState("");
  const [before, setBefore] = useState("");
  const [after, setAfter] = useState("");
  const [reason, setReason] = useState("");
  const [photos, setPhotos] = useState<string[]>([]);
  const [checked, setChecked] = useState(false);

  const occurredAt = localInstant(at);
  const beforeValue = before.trim() === "" ? null : readingText(before);
  const afterValue = after.trim() === "" ? null : readingText(after);
  const problems = {
    at: occurredAt === null ? t("portal.meterEvents.occurredAtInvalid") : undefined,
    before: (needsBefore(kind) && beforeValue === null) || (before.trim() !== "" && beforeValue === null) ? t("portal.assets.readingInvalid") : undefined,
    after: needsAfter(kind) && afterValue === null ? t("portal.assets.readingInvalid") : undefined,
    reason: reason.trim().length < reasonMin ? t("portal.meterEvents.reasonInvalid", { min: reasonMin }) : undefined,
    photos: photos.length === 0 ? t("portal.meterEvents.photosMissing") : undefined,
  };
  const valid = Object.values(problems).every((p) => p === undefined);

  const upload = useMutation({
    mutationFn: async (file: File) => (await api.POST("/api/v1/assets/{assetId}/meters/{meterId}/evidence", {
      params: { path },
      body: file as unknown as string,
      bodySerializer: (body: unknown) => body as BodyInit,
      headers: { "Content-Type": file.type || "application/octet-stream" },
    })).data,
    onSuccess: (result) => {
      if (result) {
        setPhotos((current) => (current.includes(result.photo) ? current : [...current, result.photo].slice(0, maxPhotos)));
      }
    },
  });

  const record = useMutation({
    mutationFn: async () => (await api.POST("/api/v1/assets/{assetId}/meters/{meterId}/events", {
      params: { path },
      body: {
        kind,
        occurredAt,
        valueBefore: allowsBefore(kind) ? beforeValue : null,
        valueAfter: needsAfter(kind) ? afterValue : null,
        reason: reason.trim(),
        photos,
      },
    })).data,
    onSuccess: async (saved) => {
      if (!saved) {
        return;
      }

      setAt("");
      setBefore("");
      setAfter("");
      setReason("");
      setPhotos([]);
      setChecked(false);
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: meterEventsKey(asset.id, meter.id) }),
        queryClient.invalidateQueries({ queryKey: readingsKey(asset.id, meter.id) }),
        queryClient.invalidateQueries({ queryKey: metersKey(asset.id) }),
      ]);
      toast({ status: "ok", title: t("portal.meterEvents.saved") });
    },
  });

  const onFiles = (event: ChangeEvent<HTMLInputElement>) => {
    for (const file of Array.from(event.target.files ?? []).slice(0, maxPhotos - photos.length)) {
      upload.mutate(file);
    }

    event.target.value = "";
  };

  return (
    <details className="rounded-lg border-[length:var(--karry-border-width)] border-divider p-4">
      <summary className="cursor-pointer font-semibold">{t("portal.meterEvents.record")}</summary>
      <form
        className="mt-4 grid gap-4 md:grid-cols-2"
        noValidate
        onSubmit={(event) => { event.preventDefault(); setChecked(true); if (valid) record.mutate(); }}
      >
        <SelectField
          label={t("portal.meterEvents.kind")}
          value={kind}
          onValueChange={setKind}
          options={kinds.map((code) => ({ value: code, label: t(`portal.meterEvents.kind.${code}`) }))}
        />
        <TextField
          label={t("portal.meterEvents.occurredAt")}
          type="datetime-local"
          value={at}
          error={checked ? problems.at : undefined}
          onChange={(e) => { setAt(e.target.value); }}
        />
        {allowsBefore(kind) && (
          <TextField
            label={kind === "failed" ? t("portal.meterEvents.lastShown", { unit }) : t("portal.meterEvents.valueBeforeOf", { unit })}
            description={kind === "failed" ? t("portal.meterEvents.optional") : t("portal.meterEvents.valueBeforeHint", { last: meter.lastValue, unit })}
            inputMode="decimal"
            value={before}
            error={checked ? problems.before : undefined}
            onChange={(e) => { setBefore(e.target.value); }}
          />
        )}
        {needsAfter(kind) && (
          <TextField
            label={kind === "repaired" ? t("portal.meterEvents.repairReading", { unit }) : t("portal.meterEvents.valueAfterOf", { unit })}
            inputMode="decimal"
            value={after}
            error={checked ? problems.after : undefined}
            onChange={(e) => { setAfter(e.target.value); }}
          />
        )}
        <div className="md:col-span-2">
          <TextAreaField
            label={t("portal.meterEvents.reason")}
            description={t("portal.meterEvents.reasonHint", { min: reasonMin, count: reason.trim().length })}
            value={reason}
            maxLength={reasonMax}
            error={checked ? problems.reason : undefined}
            onChange={(e) => { setReason(e.target.value); }}
            required
          />
        </div>
        <div className="flex flex-col gap-2 md:col-span-2">
          <span className="font-semibold">{t("portal.meterEvents.photos")}</span>
          <p className="text-sm text-text-muted">{t("portal.meterEvents.photosHint", { max: maxPhotos })}</p>
          {photos.length > 0 && (
            <ul className="flex flex-wrap gap-2" aria-label={t("portal.meterEvents.photos")}>
              {photos.map((photo, index) => (
                <li key={photo} className="flex items-center gap-2 rounded-md border-[length:var(--karry-border-width)] border-divider px-2 py-1 text-sm">
                  {t("portal.meterEvents.photoN", { n: index + 1 })}
                  <Button variant="ghost" onClick={() => { setPhotos((current) => current.filter((p) => p !== photo)); }}>
                    {t("portal.meterEvents.removePhoto", { n: index + 1 })}
                  </Button>
                </li>
              ))}
            </ul>
          )}
          {photos.length < maxPhotos && (
            <label className={`${buttonVariants({ variant: "secondary" })} w-fit cursor-pointer focus-within:outline focus-within:outline-2`}>
              {upload.isPending ? t("common.loading") : t("portal.meterEvents.addPhotos")}
              <input type="file" accept={photoTypes} multiple className="sr-only" onChange={onFiles} />
            </label>
          )}
          {checked && problems.photos && <p role="alert" className="text-sm text-danger">{problems.photos}</p>}
        </div>
        <div className="md:col-span-2">
          <Button type="submit" loading={record.isPending}>{t("portal.meterEvents.save")}</Button>
        </div>
      </form>
    </details>
  );
}

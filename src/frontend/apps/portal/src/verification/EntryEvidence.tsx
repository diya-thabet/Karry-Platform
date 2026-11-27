import { intlLocale, useTranslation } from "@karry/i18n";
import { StatusChip } from "@karry/ui";
import type { ReactNode } from "react";
import { SignatureVerification } from "../audit/SignatureVerification";
import { EntryPhoto } from "./EntryPhoto";
import { flagKey, type ShiftEntry } from "./queue";

/**
 * What a shift entry shows, wherever it is opened (the review panel, the entry history): its flags, the previous end on
 * the meter beside this start and end (a gap shows at a glance, ADR-0038 §4), Δ and fuel, the signature's checks and
 * every version (a correction keeps the signed one beside it).
 */
export function EntryEvidence({ entry, previous, versions, flags }: {
  entry: ShiftEntry | undefined;
  previous: ShiftEntry | null;
  versions: ShiftEntry[];
  flags: string[];
}) {
  const { t, i18n } = useTranslation();
  const locale = intlLocale(i18n.language);
  const number = (value: number | string | null | undefined) =>
    (value === null || value === undefined ? "—" : new Intl.NumberFormat(locale, { maximumFractionDigits: 1 }).format(Number(value)));

  return (
    <div className="flex flex-col gap-6">
      {flags.length > 0 && (
        <section aria-label={t("portal.review.flags")} className="flex flex-wrap gap-2">
          {flags.map((flag) => { const key = flagKey(flag); return <StatusChip key={flag} status="warning">{key ? t(key) : flag}</StatusChip>; })}
        </section>
      )}

      <section aria-label={t("portal.review.photos")} className="grid grid-cols-1 gap-4 md:grid-cols-3">
        <Figure label={t("portal.review.detail.previousEnd")} value={previous ? `${number(previous.endValue)} ${previous.unit}` : null}>
          {previous?.endPhoto
            ? <EntryPhoto entryId={previous.id} sha256={previous.endPhoto} label={t("portal.review.detail.previousEnd")} size={220} />
            : <NoPhoto text={t("portal.review.detail.noPrevious")} />}
        </Figure>
        <Figure label={t("portal.review.startPhoto")} value={entry ? `${number(entry.startValue)} ${entry.unit}` : null}>
          {entry && <EntryPhoto entryId={entry.id} sha256={entry.startPhoto} label={t("portal.review.startPhoto")} size={220} />}
        </Figure>
        <Figure label={t("portal.review.endPhoto")} value={entry ? `${number(entry.endValue)} ${entry.unit}` : null}>
          {entry?.endPhoto ? <EntryPhoto entryId={entry.id} sha256={entry.endPhoto} label={t("portal.review.endPhoto")} size={220} /> : <NoPhoto text="—" />}
        </Figure>
      </section>

      {entry && (
        <dl className="grid grid-cols-2 gap-3">
          <Fact label={t("portal.review.detail.delta")} value={entry.usage === null ? "—" : `${number(entry.usage)} ${entry.unit}`} />
          <Fact label={t("portal.review.detail.fuel")} value={t("portal.review.detail.fuelValues", { start: entry.fuelStartPercent, end: entry.fuelEndPercent ?? "—" })} />
        </dl>
      )}

      {entry && <SignatureVerification envelopeId={entry.envelopeId} />}

      {versions.length > 1 && (
        <section aria-label={t("portal.review.detail.versions")} className="flex flex-col gap-2">
          <h3 className="font-semibold">{t("portal.review.detail.versions")}</h3>
          <ol className="flex flex-col divide-y divide-divider rounded-lg border border-divider text-sm">
            {versions.map((version) => (
              <li key={version.version} className="px-3 py-2">
                {t("portal.review.detail.version", { version: version.version, start: number(version.startValue), end: number(version.endValue), unit: version.unit })}
              </li>
            ))}
          </ol>
        </section>
      )}
    </div>
  );
}

/** A meter photo with what it shows, the value large enough to compare across the three. */
function Figure({ label, value, children }: { label: string; value: string | null; children: ReactNode }) {
  return (
    <figure className="flex flex-col gap-2 rounded-xl border border-divider bg-surface-muted p-3">
      <figcaption className="text-sm font-medium text-text-muted">{label}</figcaption>
      {children}
      {value !== null && <span className="text-lg font-semibold tabular-nums">{value}</span>}
    </figure>
  );
}

function NoPhoto({ text }: { text: string }) {
  return <span className="flex aspect-square w-full max-w-[220px] items-center justify-center rounded-md border border-dashed border-border p-3 text-center text-sm text-text-muted">{text}</span>;
}

function Fact({ label, value }: { label: string; value: string }) {
  return (
    <div className="rounded-xl border border-divider px-4 py-3">
      <dt className="text-sm text-text-muted">{label}</dt>
      <dd className="text-lg font-semibold tabular-nums">{value}</dd>
    </div>
  );
}

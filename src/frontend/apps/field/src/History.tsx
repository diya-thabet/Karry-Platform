import { useEffect, useState } from "react";
import { ArrowLeft, FilePen } from "lucide-react";
import { useTranslation } from "@karry/i18n";
import type { KarryOfflineDb } from "@karry/offline";
import { Button, StatusChip } from "@karry/ui";
import { readDraft, saveDraft } from "./drafts";
import { historyDays, historyRows, type HistoryRow } from "./historyRows";
import { refusalReasons } from "./refusals";
import { meterTenths, meterText } from "./shiftRules";
import type { Session } from "./unlock";

const chip = { queued: "pending", submitted: "ok", rejected: "error", approved: "ok", refused: "error" } as const;
const flagKeys: Record<string, string> = {
  "RULE-FO-01": "field.history.flag.continuity",
  "RULE-FO-02": "field.history.flag.delta",
  END_BEFORE_START: "field.history.flag.endBeforeStart",
  SHIFT_OUTSIDE_PATTERN: "field.history.flag.outsideShift",
  ASSET_NOT_ON_SITE: "field.history.flag.notOnSite",
  OVERLAP: "field.history.flag.overlap",
  ASSET_UNSAFE: "field.history.flag.unsafe",
};

interface Shown extends HistoryRow {
  machine: string;
  thumbnail: string | null;
}

function base64(bytes: Uint8Array): string {
  let text = "";
  for (const byte of bytes) {
    text += String.fromCharCode(byte);
  }

  return btoa(text);
}

/**
 * "My history" (US-P2-FO-06 AC1): the operator's entries of the last 14 days as this tablet signed them, with where
 * each stands (to send, received with what the controller will check, refused with the reason) and the meter photo's
 * thumbnail, plus a start left unsigned. A start the server refused can be signed again from its values (AC2 for the
 * refusals the tablet knows; the controller's come with P2.8).
 */
export function History({ db, session, now, onBack, onStart }: {
  db: KarryOfflineDb; session: Session; now: () => Date; onBack: () => void; onStart: () => void;
}) {
  const { t } = useTranslation();
  const [rows, setRows] = useState<Shown[] | null>(null);
  const [draft, setDraft] = useState(false);

  useEffect(() => {
    const screen = { open: true };
    void (async () => {
      const found = historyRows(await db.outbox.toArray(), session.userId, now(), await db.reviewOutcomes.toArray());
      const shown: Shown[] = [];
      for (const row of found) {
        const machine = row.assetId ? (await db.assets.get(row.assetId))?.code ?? "—" : "—";
        const thumbnail = row.photo ? (await db.blobs.get(row.photo))?.thumbnail ?? null : null;
        shown.push({ ...row, machine, thumbnail: thumbnail ? `data:image/webp;base64,${base64(thumbnail)}` : null });
      }

      const left = await readDraft(db, session.userId, "start", now());
      if (screen.open) {
        setRows(shown);
        setDraft(left !== null);
      }
    })();
    return () => { screen.open = false; };
    // Read once when the screen opens.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [db, session.userId]);

  const again = async (row: HistoryRow) => {
    if (row.start) {
      await saveDraft(db, session.userId, "start", {
        machineId: row.start.assetId, value: row.start.startValue, fuel: row.start.fuelStartPercent, remarks: row.start.remarks, photo: null,
      }, now());
      onStart();
    }
  };

  return (
    <div className="mx-auto flex min-h-screen max-w-3xl flex-col gap-5 p-4">
      <header className="flex flex-wrap items-center gap-3">
        <Button variant="secondary" onClick={onBack} aria-label={t("field.machines.back")}>
          <ArrowLeft aria-hidden className="size-6 rtl:-scale-x-100" />
        </Button>
        <h1 className="flex-1 text-3xl font-bold">{t("field.history.title")}</h1>
        <p className="font-semibold">{t("field.unlock.user", { name: session.name, employeeId: session.employeeId })}</p>
      </header>
      <p className="text-text-muted">{t("field.history.scope", { days: historyDays })}</p>

      {draft && (
        <section aria-label={t("field.history.draft")} className="flex flex-wrap items-center gap-3 rounded-xl border-[length:var(--karry-border-width)] border-warning bg-warning-soft p-4">
          <FilePen aria-hidden className="size-6" />
          <p className="flex-1 font-medium">{t("field.history.draft")}</p>
          <Button onClick={onStart}>{t("field.history.continue")}</Button>
        </section>
      )}

      {rows !== null && rows.length === 0 && !draft && <p>{t("field.history.empty")}</p>}
      <ul className="flex flex-col gap-3">
        {(rows ?? []).map((row) => {
          const start = meterTenths(row.startValue);
          const end = meterTenths(row.endValue);
          return (
            <li key={row.entryId} className="flex gap-3 rounded-xl border-[length:var(--karry-border-width)] border-divider bg-surface p-4">
              {row.thumbnail && <img src={row.thumbnail} alt={t("field.history.photo")} className="size-20 rounded-lg object-cover" />}
              <div className="flex flex-1 flex-col gap-1">
                <div className="flex flex-wrap items-center gap-2">
                  <span className="font-mono text-lg font-bold">{row.machine}</span>
                  <StatusChip status={chip[row.status]}>{t(`field.history.status.${row.status}`)}</StatusChip>
                </div>
                {row.startedAt && <p>{t("field.history.started", { at: new Date(row.startedAt), value: row.startValue ?? "" })}</p>}
                {row.endedAt && <p>{t("field.history.ended", { at: new Date(row.endedAt), value: row.endValue ?? "" })}</p>}
                {start !== null && end !== null && <p className="font-semibold">{t("field.history.delta", { delta: `${end < start ? "-" : ""}${meterText(Math.abs(end - start))}` })}</p>}
                {row.flags.map((flag) => <p key={flag} className="text-warning">{flagKeys[flag] ? t(flagKeys[flag]) : flag}</p>)}
                {row.status === "rejected" && (
                  <p className="font-medium text-danger">
                    {row.code && row.code in refusalReasons ? t(refusalReasons[row.code] ?? "") : t("field.outbox.reason.other", { code: row.code ?? "?" })}
                  </p>
                )}
                {row.status === "refused" && row.review && (
                  <div className="flex flex-col gap-1 font-medium text-danger">
                    <p>{t(`field.history.reason.${row.review.reasonCode ?? "other"}`, { defaultValue: t("field.history.reason.other") })}</p>
                    {row.review.comment && <p className="font-normal">{t("field.history.comment", { comment: row.review.comment })}</p>}
                  </div>
                )}
                {row.status === "rejected" && row.start && !row.endedAt && (
                  <Button variant="secondary" onClick={() => { void again(row); }}>{t("field.history.again")}</Button>
                )}
              </div>
            </li>
          );
        })}
      </ul>
    </div>
  );
}

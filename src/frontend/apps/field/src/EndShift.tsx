import { Suspense, useEffect, useState } from "react";
import { ArrowLeft, Camera as CameraIcon, CircleCheck, Plus, RotateCcw, Trash2 } from "lucide-react";
import { useTranslation } from "@karry/i18n";
import { Button, TextField } from "@karry/ui";
import { PhotoCapture, preloadParts, SignStep } from "./lazyParts";
import { PartBoundary } from "./PartBoundary";
import type { Photo } from "./camera";
import { assessEnd, type ActiveShift } from "./endRules";
import { meterText, stoppageReasons, type Stoppage, type StoppageReason } from "./shiftRules";
import { signEnd } from "./signShiftEntry";
import type { FormEnvironment } from "./StartShift";

interface StoppageDraft {
  reason: StoppageReason | null;
  minutes: string;
}

const validStoppage = (s: StoppageDraft): s is { reason: StoppageReason; minutes: string } =>
  s.reason !== null && /^[1-9][0-9]{0,3}$/.test(s.minutes) && Number(s.minutes) <= 24 * 60;

/**
 * End a shift (US-P2-FO-04, P2.5): from "My active shift", photograph the meter, type the end value and see Δ at once,
 * set the fuel level, list the stoppages (reason and minutes), check the summary (start, end, Δ) and sign. Δ negative,
 * longer than the real time plus 30 minutes on an hour meter, or an end before the start are refused here with the
 * reason (AC2); the server applies the same rules to what arrives anyway.
 */
export function EndShift({ environment, active, onDone, onCancel }: {
  environment: FormEnvironment; active: ActiveShift; onDone: () => void; onCancel: () => void;
}) {
  const { t } = useTranslation();
  const { db, session } = environment;
  const now = environment.now ?? (() => new Date());
  const [photo, setPhoto] = useState<Photo | null>(null);
  const [capturing, setCapturing] = useState(false);
  const [value, setValue] = useState("");
  const [fuel, setFuel] = useState(50);
  const [stoppages, setStoppages] = useState<StoppageDraft[]>([]);
  const [remarks, setRemarks] = useState("");
  const [reviewing, setReviewing] = useState(false);
  const [signing, setSigning] = useState(false);
  const [failed, setFailed] = useState(false);
  useEffect(() => { preloadParts(); }, []);

  const { machine, open, unit } = active;
  const assessment = assessEnd({ value: value.trim(), meterKind: active.meterKind, startedAt: open.startedAt, startValue: open.startValue, endedAt: now() });
  const stoppagesValid = stoppages.every(validStoppage);
  const ready = photo !== null && assessment.block === null && stoppagesValid;
  const delta = assessment.deltaTenths === null ? null : `${assessment.deltaTenths < 0 ? "-" : ""}${meterText(Math.abs(assessment.deltaTenths))}`;

  const sign = async (signature: { strokesJson: string; png: Uint8Array }) => {
    if (!photo) {
      return;
    }

    setSigning(true);
    setFailed(false);
    try {
      await signEnd(db, { userId: session.userId, appVersion: environment.appVersion, now, position: environment.position }, {
        rootId: open.entryId, version: open.version + 1, endedAt: now(), endValue: value.trim(), fuelEndPercent: fuel, photo, signature,
        stoppages: stoppages.filter(validStoppage).map((s): Stoppage => ({ reason: s.reason, minutes: Number(s.minutes) })),
        ...(remarks.trim() === "" ? {} : { remarks: remarks.trim() }),
      });
      onDone();
    } catch {
      setFailed(true);
      setSigning(false);
    }
  };

  const change = (index: number, next: Partial<StoppageDraft>) => {
    setStoppages((list) => list.map((s, i) => (i === index ? { ...s, ...next } : s)));
  };

  return (
    <div className="mx-auto flex min-h-screen max-w-3xl flex-col gap-5 p-4">
      <header className="flex flex-wrap items-center gap-3">
        <Button variant="secondary" onClick={onCancel} aria-label={t("field.end.cancel")}>
          <ArrowLeft aria-hidden className="size-6 rtl:-scale-x-100" />
        </Button>
        <h1 className="flex-1 text-3xl font-bold">{t("field.end.title")}</h1>
        <p className="font-semibold">{t("field.unlock.user", { name: session.name, employeeId: session.employeeId })}</p>
      </header>

      <section aria-label={t("field.start.machine")} className="flex flex-col gap-1 rounded-xl border-[length:var(--karry-border-width)] border-divider bg-surface p-4">
        <p className="font-mono text-2xl font-bold">{machine.code}</p>
        <p className="text-lg">{machine.name}</p>
        <p>{t("field.end.started", { value: open.startValue, unit, at: new Date(open.startedAt) })}</p>
      </section>

      {!reviewing && (
        <>
          {capturing
            ? (
                <PartBoundary fallback={<p role="alert" className="font-medium text-danger">{t("field.start.partMissing")}</p>}>
                <Suspense fallback={<p>{t("field.start.loading")}</p>}>
                  <PhotoCapture camera={environment.camera} take={environment.take} label={t("field.start.photo")}
                    onPhoto={(taken) => { setPhoto(taken); setCapturing(false); }} onCancel={() => { setCapturing(false); }} />
                </Suspense>
                </PartBoundary>
              )
            : (
                <Button block variant={photo ? "secondary" : "primary"} onClick={() => { setCapturing(true); }} className="min-h-16 text-xl">
                  {photo ? <RotateCcw aria-hidden className="size-6" /> : <CameraIcon aria-hidden className="size-6" />}
                  {t(photo ? "field.start.retakePhoto" : "field.start.takePhoto")}
                </Button>
              )}
          {photo && !capturing && <p className="flex items-center gap-2 font-medium text-success"><CircleCheck aria-hidden className="size-5" />{t("field.start.photoTaken")}</p>}

          <TextField label={t("field.end.value", { unit })} description={t("field.start.valueHelp")} inputMode="decimal" autoComplete="off"
            value={value} onChange={(e) => { setValue(e.target.value); }} className="text-2xl" />
          {delta !== null && <p className="text-xl font-semibold">{t("field.end.delta", { delta, unit })}</p>}
          {value.trim() !== "" && assessment.block !== null && (
            <p role="alert" className="font-medium text-danger">{t(`field.end.block.${assessment.block}`, { start: open.startValue, unit })}</p>
          )}

          <label className="flex flex-col gap-2 font-medium">
            {t("field.start.fuel", { percent: fuel })}
            <input type="range" min={0} max={100} step={1} value={fuel} list="fuel-quarters" onChange={(e) => { setFuel(Number(e.target.value)); }} className="h-12" />
            <datalist id="fuel-quarters">{[0, 25, 50, 75, 100].map((q) => <option key={q} value={q} />)}</datalist>
          </label>

          <section aria-label={t("field.end.stoppages")} className="flex flex-col gap-3">
            <h2 className="text-xl font-semibold">{t("field.end.stoppages")}</h2>
            {stoppages.map((stoppage, index) => (
              <div key={index} role="group" aria-label={t("field.end.stoppage", { n: index + 1 })} className="flex flex-col gap-2 rounded-xl border-[length:var(--karry-border-width)] border-divider p-3">
                <div className="flex flex-wrap gap-2">
                  {stoppageReasons.map((reason) => (
                    <Button key={reason} variant={stoppage.reason === reason ? "primary" : "secondary"} aria-pressed={stoppage.reason === reason}
                      onClick={() => { change(index, { reason }); }}>{t(`field.end.reason.${reason}`)}</Button>
                  ))}
                </div>
                <TextField label={t("field.end.minutes")} inputMode="numeric" value={stoppage.minutes} onChange={(e) => { change(index, { minutes: e.target.value }); }} />
                {!validStoppage(stoppage) && <p className="text-text-muted">{t("field.end.stoppageHelp")}</p>}
                <Button variant="secondary" onClick={() => { setStoppages((list) => list.filter((_, i) => i !== index)); }}>
                  <Trash2 aria-hidden className="size-5" />{t("field.end.removeStoppage")}
                </Button>
              </div>
            ))}
            {stoppages.length < 20 && (
              <Button variant="secondary" onClick={() => { setStoppages((list) => [...list, { reason: null, minutes: "" }]); }}>
                <Plus aria-hidden className="size-5" />{t("field.end.addStoppage")}
              </Button>
            )}
          </section>

          <label className="flex flex-col gap-2 font-medium">
            {t("field.start.remarks")}
            <textarea value={remarks} maxLength={1000} rows={3} onChange={(e) => { setRemarks(e.target.value); }}
              className="rounded-lg border-[length:var(--karry-border-width)] border-divider bg-surface p-3" />
          </label>

          <Button block disabled={!ready} onClick={() => { setReviewing(true); }} className="min-h-16 text-xl">{t("field.start.review")}</Button>
        </>
      )}

      {reviewing && (
        <PartBoundary fallback={<p role="alert" className="font-medium text-danger">{t("field.start.partMissing")}</p>}>
        <Suspense fallback={<p>{t("field.start.loading")}</p>}>
          <SignStep disabled={signing} onConfirm={sign}
            signer={t("field.start.signer", { name: session.name, at: now() })}
            summary={(
              <dl className="grid grid-cols-2 gap-1">
                <dt>{t("field.start.machine")}</dt><dd className="font-semibold">{machine.code}</dd>
                <dt>{t("field.start.summaryValue")}</dt><dd className="font-semibold">{open.startValue} {unit}</dd>
                <dt>{t("field.end.summaryValue")}</dt><dd className="font-semibold">{value.trim()} {unit}</dd>
                <dt>{t("field.end.summaryDelta")}</dt><dd className="font-semibold">{delta} {unit}</dd>
                <dt>{t("field.start.summaryFuel")}</dt><dd className="font-semibold">{fuel} %</dd>
              </dl>
            )} />
          {failed && <p role="alert" className="font-medium text-danger">{t("field.end.failed")}</p>}
          <Button variant="secondary" disabled={signing} onClick={() => { setReviewing(false); }}>{t("field.start.back")}</Button>
        </Suspense>
        </PartBoundary>
      )}
    </div>
  );
}

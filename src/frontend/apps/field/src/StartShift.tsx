import { Suspense, useCallback, useEffect, useRef, useState } from "react";
import { ArrowLeft, Camera as CameraIcon, CircleCheck, RotateCcw } from "lucide-react";
import { useTranslation } from "@karry/i18n";
import type { DeviceRecord, KarryOfflineDb, MeterRow, StoredAsset } from "@karry/offline";
import { Button, TextField } from "@karry/ui";
import { PhotoCapture, preloadParts, SignStep } from "./lazyParts";
import { PartBoundary } from "./PartBoundary";
import type { Camera, Photo } from "./camera";
import { discardDraft, readDraft, saveDraft } from "./drafts";
import { MachineFinder } from "./MachineFinder";
import { findMachine, type QrScanner } from "./machines";
import { readMeterState, type MeterState } from "./meterState";
import { shiftAt, type ResolvedShift } from "./shifts";
import { signStart, type Position } from "./signShiftEntry";
import { assessStart, entryMeter, hasRunningSegment, unusualJumpHours } from "./startRules";
import type { Session } from "./unlock";

/** What a form needs from the app around it: the tablet, who unlocked it, and the browser's camera and position. */
export interface FormEnvironment {
  db: KarryOfflineDb;
  device: DeviceRecord;
  session: Session;
  scanner: QrScanner | null;
  camera: Camera | null;
  /** Takes the photo of the camera's current frame, with the tablet's capture record. */
  take: (video: HTMLVideoElement) => Promise<Photo>;
  appVersion: string;
  position: () => Promise<Position | null>;
  now?: () => Date;
}

/** What the start form keeps as a draft: everything but the signature. */
interface StartDraft extends Record<string, unknown> {
  machineId: string | null;
  value: string;
  fuel: number;
  remarks: string;
  photo: Photo | null;
}

const form = "start";

interface Context {
  machine: StoredAsset;
  meter: MeterRow | null;
  state: MeterState;
  shift: ResolvedShift | null;
  runningElsewhere: boolean;
  /** The name of the operator whose segment runs on this machine, if one does. */
  inUseBy: string | null;
}

/**
 * Start a shift on a machine (US-P2-FO-01, P2.5): pick the machine (QR or the site's list), see the shift of now and
 * the meter's previous end, photograph the meter with the app's camera, type the value, set the fuel level, add
 * remarks, then check the summary and sign. The tablet refuses to sign what it knows to be wrong (below the previous
 * end, a machine in use, a shift already running); the server still checks and flags everything that arrives.
 */
export function StartShift({ environment, onDone, onCancel }: { environment: FormEnvironment; onDone: () => void; onCancel: () => void }) {
  const { t } = useTranslation();
  const { db, device, session } = environment;
  const now = environment.now ?? (() => new Date());
  const [context, setContext] = useState<Context | null>(null);
  const [photo, setPhoto] = useState<Photo | null>(null);
  const [capturing, setCapturing] = useState(false);
  const [value, setValue] = useState("");
  const [fuel, setFuel] = useState(50);
  const [remarks, setRemarks] = useState("");
  const [reviewing, setReviewing] = useState(false);
  const [signing, setSigning] = useState(false);
  const [failed, setFailed] = useState(false);
  // The draft is read once before anything is written back, so an empty form never overwrites it.
  const [restored, setRestored] = useState(false);
  const [fromDraft, setFromDraft] = useState(false);
  // Draft writes run one after the other, and none once the form is signed or left: a save still on its way must
  // never bring back a draft that was just dropped.
  const writes = useRef<Promise<void>>(Promise.resolve());
  const closed = useRef(false);
  const write = (step: () => Promise<void>) => {
    writes.current = writes.current.then(step).catch(() => undefined);
    return writes.current;
  };
  const find = useCallback((text: string) => findMachine(db, text), [db]);

  useEffect(() => { preloadParts(); }, []);

  const choose = async (machine: StoredAsset) => {
    const meter = entryMeter(await db.meters.where("assetId").equals(machine.id).toArray());
    const state = meter ? await readMeterState(db, meter.id) : { lastEnd: null, open: null };
    const site = await db.sites.get(device.siteId);
    const patterns = await db.shiftPatterns.where("siteId").equals(device.siteId).toArray();
    const days = new Set((await db.nonWorkingDays.where("siteId").equals(device.siteId).toArray()).map((d) => d.date));
    const shift = site ? shiftAt(site.timeZone, patterns, days, now().getTime()) : null;
    const inUseBy = state.open && state.open.operatorId !== session.userId
      ? ((await db.users.get(state.open.operatorId))?.name ?? t("field.start.someone"))
      : null;
    setContext({ machine, meter, state, shift, runningElsewhere: await hasRunningSegment(db, session.userId), inUseBy });
  };

  // An operator who was locked out mid-form (auto-lock, closed app) finds it as they left it (US-P2-FO-01 AC6, T-01).
  useEffect(() => {
    let active = true;
    void readDraft<StartDraft>(db, session.userId, form, now()).then(async (draft) => {
      const machine = draft?.machineId ? await db.assets.get(draft.machineId) : undefined;
      if (!active) {
        return;
      }

      if (draft) {
        if (machine) {
          await choose(machine);
        }

        setValue(draft.value);
        setFuel(draft.fuel);
        setRemarks(draft.remarks);
        setPhoto(draft.photo);
        setFromDraft(true);
      }

      setRestored(true);
    });
    return () => { active = false; };
    // Read once, when the form opens for this operator.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [db, session.userId]);

  const machineId = context?.machine.id ?? null;
  useEffect(() => {
    // Nothing chosen or typed yet: nothing to keep (an opened form is not a draft).
    if (restored && (machineId !== null || value !== "" || remarks !== "" || photo !== null)) {
      const values: StartDraft = { machineId, value, fuel, remarks, photo };
      const at = now();
      void write(() => (closed.current ? Promise.resolve() : saveDraft(db, session.userId, form, values, at)));
    }
    // Saved at every change of the form; the clock is not a reason to save.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [restored, db, session.userId, machineId, value, fuel, remarks, photo]);

  /** Leaving on purpose drops the draft; a lock does not (the form is simply unmounted). */
  const close = () => {
    closed.current = true;
    return write(() => discardDraft(db, session.userId, form));
  };

  const leave = () => {
    void close().then(onCancel);
  };

  if (context === null) {
    return <MachineFinder find={find} scanner={environment.scanner} onBack={leave} onChoose={(m) => { void choose(m); }} title={t("field.start.title")}
      who={t("field.unlock.user", { name: session.name, employeeId: session.employeeId })} />;
  }

  const { machine, meter, state, shift } = context;
  const unit = meter?.unit ?? "";
  const assessment = meter
    ? assessStart({ value: value.trim(), meterKind: meter.kind, state, operatorId: session.userId, runningElsewhere: context.runningElsewhere })
    : null;
  const blockedBefore = assessment?.block === "machineInUse" || assessment?.block === "alreadyRunning";
  const reasonMissing = assessment?.needsReason === true && remarks.trim() === "";
  const ready = meter !== null && photo !== null && assessment?.block === null && !reasonMissing;
  // A segment ended during this shift is handed over (US-P2-FO-05 AC1): the start names it, and the server checks
  // continuity against that end.
  const handoverFrom = shift && state.lastEnd && Date.parse(state.lastEnd.at) >= shift.startsAt ? state.lastEnd.entryId : null;

  const sign = async (signature: { strokesJson: string; png: Uint8Array }) => {
    if (!meter || !photo) {
      return;
    }

    setSigning(true);
    setFailed(false);
    try {
      await signStart(db, { userId: session.userId, appVersion: environment.appVersion, now, position: environment.position }, {
        assetId: machine.id, meterId: meter.id, startedAt: now(), startValue: value.trim(), fuelStartPercent: fuel, photo, signature,
        ...(remarks.trim() === "" ? {} : { remarks: remarks.trim() }),
        ...(handoverFrom === null ? {} : { previousRootId: handoverFrom }),
      });
      await close();
      onDone();
    } catch {
      // A record refused by the shared rules cannot reach here (the button waits for them); anything else (storage
      // full, a lost key) leaves the form as it is, to try again.
      setFailed(true);
      setSigning(false);
    }
  };

  return (
    <div className="mx-auto flex min-h-screen max-w-3xl flex-col gap-5 p-4">
      <header className="flex flex-wrap items-center gap-3">
        <Button variant="secondary" onClick={leave} aria-label={t("field.start.cancel")}>
          <ArrowLeft aria-hidden className="size-6 rtl:-scale-x-100" />
        </Button>
        <h1 className="flex-1 text-3xl font-bold">{t("field.start.title")}</h1>
        <p className="font-semibold">{t("field.unlock.user", { name: session.name, employeeId: session.employeeId })}</p>
      </header>

      {fromDraft && <p role="status" className="rounded-xl bg-surface-muted p-3 font-medium">{t("field.start.draftRestored")}</p>}

      <section aria-label={t("field.start.machine")} className="flex flex-col gap-1 rounded-xl border-[length:var(--karry-border-width)] border-divider bg-surface p-4">
        <p className="font-mono text-2xl font-bold">{machine.code}</p>
        <p className="text-lg">{machine.name}</p>
        <p>{shift
          ? t("field.start.shift", { name: shift.name, from: new Date(shift.startsAt), to: new Date(shift.endsAt) })
          : t("field.start.noShift")}</p>
        {meter && <p>{state.lastEnd
          ? t("field.start.previous", { value: state.lastEnd.value, unit, at: new Date(state.lastEnd.at) })
          : t("field.start.noPrevious")}</p>}
      </section>

      {meter === null && <p role="alert" className="font-medium text-danger">{t("field.start.noMeter")}</p>}
      {assessment?.block === "machineInUse" && (
        <p role="alert" className="font-medium text-danger">{t("field.start.block.machineInUse", { name: context.inUseBy ?? "", at: new Date(state.open?.startedAt ?? 0) })}</p>
      )}
      {assessment?.block === "alreadyRunning" && <p role="alert" className="font-medium text-danger">{t("field.start.block.alreadyRunning")}</p>}

      {meter !== null && !blockedBefore && !reviewing && (
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

          <TextField label={t("field.start.value", { unit })} description={t("field.start.valueHelp")} inputMode="decimal" autoComplete="off"
            value={value} onChange={(e) => { setValue(e.target.value); }} className="text-2xl" />
          {value.trim() !== "" && assessment?.block === "value" && <p role="alert" className="font-medium text-danger">{t("field.start.block.value")}</p>}
          {assessment?.block === "belowPrevious" && (
            <p role="alert" className="font-medium text-danger">{t("field.start.block.belowPrevious", { value: state.lastEnd?.value ?? "", unit })}</p>
          )}

          <label className="flex flex-col gap-2 font-medium">
            {t("field.start.fuel", { percent: fuel })}
            <input type="range" min={0} max={100} step={1} value={fuel} list="fuel-quarters" onChange={(e) => { setFuel(Number(e.target.value)); }} className="h-12" />
            <datalist id="fuel-quarters">{[0, 25, 50, 75, 100].map((q) => <option key={q} value={q} />)}</datalist>
          </label>

          <label className="flex flex-col gap-2 font-medium">
            {t(assessment?.needsReason === true ? "field.start.reason" : "field.start.remarks")}
            <textarea value={remarks} maxLength={1000} rows={3} onChange={(e) => { setRemarks(e.target.value); }}
              className="rounded-lg border-[length:var(--karry-border-width)] border-divider bg-surface p-3" />
          </label>
          {assessment?.needsReason === true && <p className="font-medium text-warning">{t("field.start.jump", { hours: unusualJumpHours })}</p>}

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
                <dt>{t("field.start.summaryValue")}</dt><dd className="font-semibold">{value.trim()} {unit}</dd>
                <dt>{t("field.start.summaryFuel")}</dt><dd className="font-semibold">{fuel} %</dd>
              </dl>
            )} />
          {failed && <p role="alert" className="font-medium text-danger">{t("field.start.failed")}</p>}
          <Button variant="secondary" disabled={signing} onClick={() => { setReviewing(false); }}>{t("field.start.back")}</Button>
        </Suspense>
        </PartBoundary>
      )}
    </div>
  );
}

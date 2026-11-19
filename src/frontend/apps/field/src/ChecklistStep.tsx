import { Suspense, useState } from "react";
import { Camera as CameraIcon, CircleCheck, RotateCcw } from "lucide-react";
import { useTranslation } from "@karry/i18n";
import type { ChecklistTemplateRow, StoredAsset } from "@karry/offline";
import { Button } from "@karry/ui";
import type { Photo } from "./camera";
import { answers, itemText, type Answer } from "./checklistRules";
import { PhotoCapture } from "./lazyParts";
import { PartBoundary } from "./PartBoundary";
import { signChecklist } from "./signShiftEntry";
import type { FormEnvironment } from "./StartShift";

const answerKeys: Record<Answer, string> = { ok: "field.checklist.ok", not_ok: "field.checklist.notOk", na: "field.checklist.na" };

/**
 * The pre-start checklist of the machine's type (US-P2-FO-02, ADR-0035): OK, Not OK or N/A for each item, a photo for a
 * Not OK that asks for one, then signed by the unlocked operator. Required: it comes before the start; optional: the
 * operator may skip it. `onSigned` gets the critical items answered Not OK (the machine is then held).
 */
export function ChecklistStep({ environment, template, machine, onSigned, onSkip }: {
  environment: FormEnvironment;
  template: ChecklistTemplateRow;
  machine: StoredAsset;
  onSigned: (critical: string[]) => void;
  onSkip: (() => void) | null;
}) {
  const { t, i18n } = useTranslation();
  const now = environment.now ?? (() => new Date());
  const [given, setGiven] = useState<Record<string, Answer>>({});
  const [photos, setPhotos] = useState<Record<string, Photo>>({});
  const [capturing, setCapturing] = useState<string | null>(null);
  const [signing, setSigning] = useState(false);
  const [failed, setFailed] = useState(false);
  const needsPhoto = (code: string) => given[code] === "not_ok" && template.items.some((i) => i.code === code && i.photoOnNotOk);
  const complete = template.items.every((item) => given[item.code] !== undefined && (!needsPhoto(item.code) || photos[item.code] !== undefined));

  const answer = (code: string, value: Answer) => {
    setGiven((current) => ({ ...current, [code]: value }));
    if (value !== "not_ok") {
      setPhotos((current) => Object.fromEntries(Object.entries(current).filter(([key]) => key !== code)));
    }
  };

  const sign = async () => {
    setSigning(true);
    setFailed(false);
    try {
      const { critical } = await signChecklist(environment.db, { userId: environment.session.userId, appVersion: environment.appVersion, now, position: environment.position }, {
        template, assetId: machine.id, submittedAt: now(),
        answers: template.items.map((item) => {
          const value = given[item.code] ?? "na";
          const photo = value === "not_ok" ? photos[item.code] : undefined;
          return photo ? { item: item.code, answer: value, photo } : { item: item.code, answer: value };
        }),
      });
      onSigned(critical);
    } catch {
      setFailed(true);
      setSigning(false);
    }
  };

  return (
    <section aria-label={t("field.checklist.title")} className="flex flex-col gap-4">
      <div className="flex flex-col gap-1">
        <h2 className="text-2xl font-bold">{t("field.checklist.title")}</h2>
        <p>{t(onSkip ? "field.checklist.optional" : "field.checklist.required", { code: machine.code })}</p>
      </div>
      <ol className="flex flex-col gap-3">
        {template.items.map((item) => {
          const text = itemText(item, i18n.language, null);
          return (
            <li key={item.code} className="flex flex-col gap-3 rounded-xl border-[length:var(--karry-border-width)] border-divider bg-surface p-4">
              <p className="text-lg font-semibold">
                {text}
                {item.critical && <span className="ms-2 rounded bg-danger px-2 py-0.5 text-sm text-danger-text">{t("field.checklist.critical")}</span>}
              </p>
              <div role="group" aria-label={text} className="grid grid-cols-3 gap-2">
                {answers.map((value) => (
                  <Button key={value} variant={given[item.code] === value ? (value === "not_ok" ? "danger" : "primary") : "secondary"} aria-pressed={given[item.code] === value}
                    disabled={signing} onClick={() => { answer(item.code, value); }} className="min-h-14 text-lg">
                    {t(answerKeys[value])}
                  </Button>
                ))}
              </div>
              {needsPhoto(item.code) && (capturing === item.code
                ? (
                    <PartBoundary fallback={<p role="alert" className="font-medium text-danger">{t("field.start.partMissing")}</p>}>
                    <Suspense fallback={<p>{t("field.start.loading")}</p>}>
                      <PhotoCapture camera={environment.camera} take={environment.take} label={t("field.checklist.photo", { item: text })}
                        onPhoto={(taken) => { setPhotos((current) => ({ ...current, [item.code]: taken })); setCapturing(null); }} onCancel={() => { setCapturing(null); }} />
                    </Suspense>
                    </PartBoundary>
                  )
                : (
                    <>
                      <Button variant={photos[item.code] ? "secondary" : "primary"} disabled={signing} onClick={() => { setCapturing(item.code); }} className="min-h-14">
                        {photos[item.code] ? <RotateCcw aria-hidden className="size-5" /> : <CameraIcon aria-hidden className="size-5" />}
                        {t(photos[item.code] ? "field.checklist.retakePhoto" : "field.checklist.takePhoto", { item: text })}
                      </Button>
                      {photos[item.code] && <p className="flex items-center gap-2 font-medium text-success"><CircleCheck aria-hidden className="size-5" />{t("field.checklist.photoTaken")}</p>}
                    </>
                  ))}
            </li>
          );
        })}
      </ol>
      {!complete && <p className="text-text-muted">{t("field.checklist.answerAll")}</p>}
      {failed && <p role="alert" className="font-medium text-danger">{t("field.checklist.failed")}</p>}
      <Button block disabled={!complete || signing || capturing !== null} onClick={() => { void sign(); }} className="min-h-16 text-xl">{t("field.checklist.sign")}</Button>
      {onSkip && <Button block variant="secondary" disabled={signing} onClick={onSkip}>{t("field.checklist.skip")}</Button>}
    </section>
  );
}

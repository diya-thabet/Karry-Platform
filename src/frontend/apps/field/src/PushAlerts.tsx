import { useEffect, useState } from "react";
import { BellRing } from "lucide-react";
import { useTranslation } from "@karry/i18n";
import { Button } from "@karry/ui";
import type { PushStatus, TabletPush } from "./push";

/**
 * Push alerts on the home screen (ADR-0039 §5, US-P2-OFF-04 AC2): offered once push is on for the company and the
 * browser can, turned on by a tap (the browser asks its permission); then nothing more is shown. A tablet already
 * allowed is subscribed without asking. An alert only says that something needs a look: the notice is read here.
 */
export function PushAlerts({ push }: { push: TabletPush | null }) {
  const { t } = useTranslation();
  const [status, setStatus] = useState<PushStatus>("off");
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    let current = true;
    void push?.status().then((found) => {
      if (current) {
        setStatus(found);
      }
    });
    return () => { current = false; };
  }, [push]);

  if (!push || status !== "offer") {
    return null;
  }

  const enable = async () => {
    setBusy(true);
    setFailed(false);
    const on = await push.enable().catch(() => false);
    setBusy(false);
    if (on) {
      setStatus("on");
    } else {
      setFailed(true);
    }
  };

  return (
    <section aria-label={t("field.push.title")} className="flex flex-wrap items-center gap-3 rounded-xl border-[length:var(--karry-border-width)] border-divider bg-surface p-4">
      <BellRing aria-hidden className="size-6 text-primary" />
      <p className="min-w-0 flex-1">{t("field.push.description")}</p>
      <Button variant="secondary" loading={busy} onClick={() => { void enable(); }}>{t("field.push.enable")}</Button>
      {failed && <p role="alert" className="w-full text-sm text-danger">{t("field.push.failed")}</p>}
    </section>
  );
}

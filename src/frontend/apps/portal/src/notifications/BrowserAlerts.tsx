import { use, useEffect, useMemo, useState } from "react";
import { BellRing } from "lucide-react";
import { useTranslation } from "@karry/i18n";
import { Button, Card } from "@karry/ui";
import { useApi } from "../app/api";
import { BrowserPushContext, browserPushStatus, disableBrowserPush, enableBrowserPush, realBrowserPush, type BrowserPushStatus } from "./push";

/**
 * Alerts in this browser (ADR-0039 §5): shown once push is on for the company and the browser can. The person turns
 * them on (the browser asks its permission) or off; an alert only says that something needs a look and opens the
 * notification centre. Which kinds alert is chosen above ("How you are told").
 */
export function BrowserAlerts() {
  const { t } = useTranslation();
  const api = useApi();
  const injected = use(BrowserPushContext);
  const browser = useMemo(() => injected ?? realBrowserPush(), [injected]);
  const [status, setStatus] = useState<BrowserPushStatus>("off");
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    let current = true;
    browserPushStatus(api, browser).then((found) => { if (current) setStatus(found); }, () => { if (current) setStatus("off"); });
    return () => { current = false; };
  }, [api, browser]);

  if (status === "off") {
    return null;
  }

  const change = async (on: boolean) => {
    setBusy(true);
    setFailed(false);
    try {
      if (on) {
        const enabled = await enableBrowserPush(api, browser);
        setStatus(enabled ? "on" : browser.permission() === "denied" ? "blocked" : "available");
        setFailed(!enabled);
      } else {
        await disableBrowserPush(api, browser);
        setStatus("available");
      }
    } catch {
      setFailed(true);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Card>
      <section className="flex flex-wrap items-center gap-3" aria-labelledby="browser-alerts">
        <BellRing aria-hidden className="size-6 text-primary" />
        <div className="flex min-w-0 flex-1 flex-col gap-1">
          <h2 id="browser-alerts" className="text-lg font-semibold">{t("portal.push.title")}</h2>
          <p className="text-sm text-text-muted">
            {status === "blocked" ? t("portal.push.blocked") : status === "on" ? t("portal.push.on") : t("portal.push.description")}
          </p>
        </div>
        {status === "available" && <Button variant="secondary" loading={busy} onClick={() => { void change(true); }}>{t("portal.push.enable")}</Button>}
        {status === "on" && <Button variant="secondary" loading={busy} onClick={() => { void change(false); }}>{t("portal.push.disable")}</Button>}
        {failed && <p role="alert" className="w-full text-sm text-danger">{t("portal.push.failed")}</p>}
      </section>
    </Card>
  );
}

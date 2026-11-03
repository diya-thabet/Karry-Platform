import { useEffect, useState } from "react";
import { RefreshCw } from "lucide-react";
import { useTranslation } from "@karry/i18n";
import { lastRefresh, type DeviceRecord, type KarryOfflineDb } from "@karry/offline";
import { Button, StatusChip, type Status } from "@karry/ui";
import { nextRefreshMs, type PullOutcome } from "./sync";


interface Snapshot {
  site: string | null;
  last: string | null;
  machines: number;
  users: number;
}

async function read(db: KarryOfflineDb, device: DeviceRecord): Promise<Snapshot> {
  const site = await db.sites.get(device.siteId);
  return {
    site: site ? `${site.code} · ${site.name}` : null,
    last: await lastRefresh(db),
    machines: await db.assets.count(),
    users: await db.users.count(),
  };
}

const chips: Record<PullOutcome | "syncing", { status: Status; key: string }> = {
  syncing: { status: "pending", key: "field.sync.state.syncing" },
  ok: { status: "ok", key: "field.sync.state.ok" },
  offline: { status: "warning", key: "field.sync.state.offline" },
  failed: { status: "warning", key: "field.sync.state.failed" },
  busy: { status: "warning", key: "field.sync.state.busy" },
  revoked: { status: "error", key: "field.sync.state.revoked" },
};

/**
 * The tablet's reference data on the home screen (US-P1-OFF-03 AC1/AC2): its site, when the data was last refreshed
 * and how much is kept offline. It refreshes on opening, about every 5 minutes (see {@link nextRefreshMs}), when the
 * network returns and on request.
 */
export function ReferenceStatus({ db, device, pull }: { db: KarryOfflineDb; device: DeviceRecord; pull: () => Promise<PullOutcome> }) {
  const { t } = useTranslation();
  const [state, setState] = useState<PullOutcome | "syncing">("syncing");
  const [data, setData] = useState<Snapshot | null>(null);

  // A manual refresh bumps the counter: the effect downloads again (and restarts the 5-minute timer).
  const [requested, setRequested] = useState(0);
  const refresh = () => {
    setState("syncing");
    setRequested((n) => n + 1);
  };

  useEffect(() => {
    let active = true;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const download = async () => {
      const outcome = await pull();
      const snapshot = await read(db, device);
      // Together, so "up to date" never shows next to the data from before the refresh.
      if (active) {
        setData(snapshot);
        setState(outcome);
        clearTimeout(timer);
        timer = setTimeout(again, nextRefreshMs(outcome));
      }
    };
    const again = () => {
      setState("syncing");
      void download();
    };
    void read(db, device).then((snapshot) => { if (active) { setData(snapshot); } });
    void download();
    window.addEventListener("online", again);
    return () => {
      active = false;
      clearTimeout(timer);
      window.removeEventListener("online", again);
    };
  }, [db, device, pull, requested]);

  const chip = chips[state];
  return (
    <section aria-label={t("field.sync.label")} className="flex flex-col gap-3 rounded-xl border-[length:var(--karry-border-width)] border-divider bg-surface p-4">
      <div className="flex flex-wrap items-center gap-3">
        <StatusChip status={chip.status}>{t(chip.key)}</StatusChip>
        {data?.site && <p className="font-semibold">{t("field.sync.site", { site: data.site })}</p>}
      </div>
      {state === "revoked"
        ? <p role="alert" className="font-medium text-danger">{t("field.sync.revoked")}</p>
        : (
            <>
              <p>{data?.last ? t("field.sync.last", { at: new Date(data.last) }) : t("field.sync.never")}</p>
              {data?.last && <p className="text-text-muted">{t("field.sync.counts", { machines: data.machines, users: data.users })}</p>}
              <Button variant="secondary" disabled={state === "syncing"} onClick={refresh} className="self-start">
                <RefreshCw aria-hidden className="size-5" />
                {t("field.sync.refresh")}
              </Button>
            </>
          )}
    </section>
  );
}

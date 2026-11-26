import { useQuery } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { AlertTriangle, CheckCircle2, ClipboardCheck, Flag, Play, Truck } from "lucide-react";
import { useState } from "react";
import { intlLocale, useTranslation } from "@karry/i18n";
import { Button, Card, EmptyState, ErrorState, PageHeader, SelectField, StatCard, StatusChip, TextField, type Status } from "@karry/ui";
import { useAccess, useApi } from "../app/api";
import { AssetsReadPermission, useSiteNames } from "../assets/api";
import { Trail } from "../shell/Trail";
import { ItemsReadPermission } from "./api";
import { boardOf, type MachineState, type ReviewState } from "./board";
import { EntriesReadPermission } from "./queue";
import { ShiftReportButton } from "./ShiftReportButton";

const stateStatus: Record<MachineState, Status> = { not_started: "pending", running: "ok", missing_end: "error", ended: "ok" };
const reviewStatus: Record<ReviewState, Status> = { none: "pending", pending: "warning", decided: "ok" };

/** Today as the browser's calendar writes it (YYYY-MM-DD). */
function today(): string {
  const now = new Date();
  return `${String(now.getFullYear())}-${String(now.getMonth() + 1).padStart(2, "0")}-${String(now.getDate()).padStart(2, "0")}`;
}

/**
 * The shift board (US-P2-VER-08, ADR-0038 §5): for one site and date, each shift with its machines planned, started,
 * ended and with a missing end, the reviews still pending and the machines flagged; each machine's place in the shift.
 * It reads what the other screens read and refreshes every 30 s.
 */
export function BoardPage() {
  const { t, i18n } = useTranslation();
  const api = useApi();
  const { can } = useAccess();
  const { sites } = useSiteNames();
  const [siteId, setSiteId] = useState("");
  const [date, setDate] = useState(today());
  const site = siteId !== "" ? siteId : (sites[0]?.id ?? "");
  const ready = site !== "" && /^\d{4}-\d{2}-\d{2}$/.test(date);
  const refresh = 30_000;

  const shifts = useQuery({
    queryKey: ["board", "shifts", site, date],
    queryFn: async () => (await api.GET("/api/v1/organization/sites/{siteId}/shift-instances", { params: { path: { siteId: site }, query: { from: date, to: date } } })).data?.items ?? [],
    enabled: ready,
  });
  const machines = useQuery({
    queryKey: ["board", "machines", site],
    queryFn: async () => (await api.GET("/api/v1/assets", { params: { query: { siteId: site, limit: 200 } } })).data?.items ?? [],
    enabled: ready && can(AssetsReadPermission),
  });
  const entries = useQuery({
    queryKey: ["board", "entries", site, date],
    queryFn: async () => (await api.GET("/api/v1/fieldops/entries", { params: { query: { siteId: site, shiftDate: date, limit: 200 } } })).data?.items ?? [],
    enabled: ready && can(EntriesReadPermission),
    refetchInterval: refresh,
  });
  const items = useQuery({
    queryKey: ["board", "items", site, date],
    queryFn: async () => {
      // The shifts of a date may start the evening before or end the next morning: a day on each side.
      const day = new Date(`${date}T00:00:00Z`).getTime();
      const query = { siteId: site, status: "all", limit: 200, coveredFrom: new Date(day - 86_400_000).toISOString(), coveredBefore: new Date(day + 2 * 86_400_000).toISOString() };
      return (await api.GET("/api/v1/verification/items", { params: { query } })).data?.items ?? [];
    },
    enabled: ready && can(ItemsReadPermission),
    refetchInterval: refresh,
  });

  const board = boardOf(shifts.data ?? [], machines.data ?? [], entries.data ?? [], items.data ?? [], new Date());
  const time = (value: string) => new Intl.DateTimeFormat(intlLocale(i18n.language), { timeStyle: "short" }).format(new Date(value));
  const failed = shifts.isError || machines.isError || entries.isError || items.isError;

  return (
    <div className="flex flex-col gap-4">
      <PageHeader
        breadcrumbs={<Trail steps={[{ to: "/review", label: t("portal.nav.review") }]} />}
        title={t("portal.board.title")}
        description={t("portal.board.description")}
        actions={<Button variant="secondary" asChild><Link to="/review">{t("portal.review.title")}</Link></Button>}
      />
      <div className="flex flex-wrap items-end gap-3">
        <SelectField label={t("portal.board.site")} value={site} onValueChange={setSiteId}
          options={sites.map((option) => ({ value: option.id, label: option.name }))} />
        <TextField label={t("portal.board.date")} type="date" value={date} onChange={(event) => { setDate(event.target.value); }} />
      </div>
      {failed && <ErrorState onRetry={() => { void shifts.refetch(); void machines.refetch(); void entries.refetch(); void items.refetch(); }} />}
      {!failed && ready && shifts.isSuccess && board.length === 0 && <EmptyState title={t("portal.board.noShift")} />}
      {board.map(({ shift, machines: rows, counts }) => (
        <Card key={shift.code}>
          <section aria-label={t("portal.board.shift", { name: shift.name, from: time(shift.startsAt), to: time(shift.endsAt) })} className="flex flex-col gap-4">
            <div className="flex flex-wrap items-center justify-between gap-3">
              <h2 className="text-lg font-semibold">{t("portal.board.shift", { name: shift.name, from: time(shift.startsAt), to: time(shift.endsAt) })}</h2>
              <ShiftReportButton siteId={site} date={date} shift={shift.code} />
            </div>
            <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-6">
              <StatCard icon={Truck} label={t("portal.board.count.planned")} value={counts.planned} />
              <StatCard icon={Play} label={t("portal.board.count.started")} value={counts.started} />
              <StatCard icon={CheckCircle2} label={t("portal.board.count.ended")} value={counts.ended} />
              <StatCard icon={AlertTriangle} label={t("portal.board.count.missingEnd")} value={counts.missingEnd} />
              <StatCard icon={ClipboardCheck} label={t("portal.board.count.pendingReviews")} value={counts.pendingReviews} />
              <StatCard icon={Flag} label={t("portal.board.count.flagged")} value={counts.flagged} />
            </div>
            <ul className="flex flex-col divide-y divide-divider" aria-label={t("portal.board.machines")}>
              {rows.map((row) => (
                <li key={row.machine.id} className="flex flex-wrap items-center gap-3 py-2">
                  <span className="min-w-24 font-semibold">{row.machine.code}</span>
                  <StatusChip status={stateStatus[row.state]}>{t(`portal.board.state.${row.state}`)}</StatusChip>
                  {row.review !== "none" && <StatusChip status={reviewStatus[row.review]}>{t(`portal.board.review.${row.review}`)}</StatusChip>}
                  {row.flags > 0 && <span className="text-sm text-text-muted">{t("portal.board.flags", { count: row.flags })}</span>}
                </li>
              ))}
            </ul>
          </section>
        </Card>
      ))}
    </div>
  );
}

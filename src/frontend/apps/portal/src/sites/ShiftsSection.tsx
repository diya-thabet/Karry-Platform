import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "@karry/i18n";
import { Button, DataTable, EmptyState, SelectField, TextField, useToast } from "@karry/ui";
import { useAccess, useApi } from "../api";
import {
  addDays, dayCodes, dayKinds, ShiftsManagePermission, siteKey, todayIn,
  type NonWorkingDay, type ShiftInput, type ShiftInstance, type ShiftPattern, type Site,
} from "./api";

interface Row { key: number; code: string; name: string; start: string; end: string; days: string[] }

/** Rows have no identity until saved (their codes are being typed): a counter keys them. */
let nextRow = 0;
const row = (shift: Omit<Row, "key">): Row => ({ ...shift, key: (nextRow += 1) });

const everyDay = [...dayCodes];

/** Common patterns (open question 7 is still open: these are examples, edited freely before saving). */
const presets: Record<"twoTwelves" | "threeEights", Omit<Row, "key">[]> = {
  twoTwelves: [
    { code: "DAY", name: "", start: "06:00", end: "18:00", days: everyDay },
    { code: "NIGHT", name: "", start: "18:00", end: "06:00", days: everyDay },
  ],
  threeEights: [
    { code: "M", name: "", start: "06:00", end: "14:00", days: everyDay },
    { code: "A", name: "", start: "14:00", end: "22:00", days: everyDay },
    { code: "N", name: "", start: "22:00", end: "06:00", days: everyDay },
  ],
};

/** Monday 26 October 2026: weekday names come from the browser's calendar in the person's language. */
const dayName = (language: string, index: number, style: "long" | "short") =>
  new Intl.DateTimeFormat(language, { weekday: style, timeZone: "UTC" }).format(Date.UTC(2026, 9, 26 + index));

/** A site's shifts (US-P1-ORG-02): pattern versions, non-working days and the shifts of the coming days. */
export function ShiftsSection({ site }: { site: Site }) {
  const { can } = useAccess();
  const editable = can(ShiftsManagePermission) && site.status === "active";
  return (
    <div className="flex max-w-4xl flex-col gap-8">
      <Patterns site={site} editable={editable} />
      <NonWorkingDays site={site} editable={editable} />
      <Upcoming site={site} />
    </div>
  );
}

function usePatterns(site: Site) {
  const api = useApi();
  return useQuery({
    queryKey: [...siteKey(site.id), "patterns"],
    queryFn: async () => (await api.GET("/api/v1/organization/sites/{siteId}/shift-patterns", { params: { path: { siteId: site.id } } })).data?.items ?? [],
  });
}

function Patterns({ site, editable }: { site: Site; editable: boolean }) {
  const { t, i18n } = useTranslation();
  const api = useApi();
  const queryClient = useQueryClient();
  const toast = useToast();
  const patterns = usePatterns(site);
  const today = todayIn(site.timeZone);
  const first = (patterns.data ?? []).length === 0;
  const [rows, setRows] = useState<Row[] | null>(null);
  const [effectiveFrom, setEffectiveFrom] = useState("");

  const changed = async (title: string) => {
    await queryClient.invalidateQueries({ queryKey: siteKey(site.id) });
    toast({ status: "ok", title });
  };

  const save = useMutation({
    mutationFn: async () => (await api.PUT("/api/v1/organization/sites/{siteId}/shift-patterns/{effectiveFrom}", {
      params: { path: { siteId: site.id, effectiveFrom } },
      body: { shifts: (rows ?? []).map((row): ShiftInput => ({ code: row.code.trim(), name: row.name.trim() || row.code.trim(), start: row.start, end: row.end, days: row.days })) },
    })).data,
    onSuccess: async () => {
      setRows(null);
      await changed(t("portal.shifts.saved"));
    },
  });

  const remove = useMutation({
    mutationFn: async (date: string) => {
      await api.DELETE("/api/v1/organization/sites/{siteId}/shift-patterns/{effectiveFrom}", { params: { path: { siteId: site.id, effectiveFrom: date } } });
    },
    onSuccess: () => changed(t("portal.shifts.removed")),
  });

  const start = (preset: Omit<Row, "key">[]) => {
    setEffectiveFrom(first ? today : addDays(today, 1));
    setRows(preset.map((shift) => row({ ...shift, name: t(`portal.shifts.preset.${shift.code}`, { defaultValue: shift.code }) })));
  };

  const setRow = (index: number, next: Partial<Row>) => { setRows((current) => (current ?? []).map((shift, i) => (i === index ? { ...shift, ...next } : shift))); };

  const summary = (pattern: ShiftPattern) =>
    pattern.shifts.map((shift) => {
      const days = shift.days.length === 7 ? "" : ` (${shift.days.map((d) => dayName(i18n.language, dayCodes.indexOf(d as typeof dayCodes[number]), "short")).join(" ")})`;
      return `${shift.name} ${shift.start}–${shift.end}${days}`;
    }).join(" · ");

  return (
    <section aria-labelledby="shift-patterns" className="flex flex-col gap-3">
      <h2 id="shift-patterns" className="text-xl font-bold">{t("portal.shifts.title")}</h2>
      <p className="text-text-muted">{t("portal.shifts.hint", { zone: site.timeZone })}</p>
      <DataTable<ShiftPattern>
        caption={t("portal.shifts.title")}
        rows={patterns.data ?? []}
        rowKey={(pattern) => pattern.id}
        empty={<EmptyState title={patterns.isPending ? t("common.loading") : t("portal.shifts.none")} />}
        columns={[
          { key: "from", header: t("portal.shifts.effectiveFrom"), cell: (pattern) => pattern.effectiveFrom },
          { key: "shifts", header: t("portal.shifts.shifts"), cell: (pattern) => <span translate="no">{summary(pattern)}</span> },
          {
            key: "actions",
            header: t("portal.shifts.actions"),
            cell: (pattern) => editable && pattern.effectiveFrom > today
              ? <Button variant="ghost" loading={remove.isPending && remove.variables === pattern.effectiveFrom} onClick={() => { remove.mutate(pattern.effectiveFrom); }}>
                  {t("portal.shifts.remove", { date: pattern.effectiveFrom })}
                </Button>
              : null,
          },
        ]}
      />

      {editable && rows === null && (
        <div className="flex flex-wrap gap-3">
          <Button variant="secondary" onClick={() => { start(presets.twoTwelves); }}>{t("portal.shifts.newTwoTwelves")}</Button>
          <Button variant="secondary" onClick={() => { start(presets.threeEights); }}>{t("portal.shifts.newThreeEights")}</Button>
          <Button variant="secondary" onClick={() => { start([{ code: "", name: "", start: "07:00", end: "16:00", days: dayCodes.slice(0, 5) }]); }}>
            {t("portal.shifts.newEmpty")}
          </Button>
        </div>
      )}

      {editable && rows !== null && (
        <form
          aria-label={t("portal.shifts.newVersion")}
          className="flex flex-col gap-4 rounded-md border-[length:var(--karry-border-width)] border-border p-4"
          onSubmit={(event) => { event.preventDefault(); save.mutate(); }}
        >
          <h3 className="text-lg font-semibold">{t("portal.shifts.newVersion")}</h3>
          <TextField
            label={t("portal.shifts.effectiveFrom")}
            description={first ? t("portal.shifts.firstHint") : t("portal.shifts.futureHint")}
            type="date"
            value={effectiveFrom}
            min={first ? undefined : addDays(today, 1)}
            required
            onChange={(e) => { setEffectiveFrom(e.target.value); }}
          />
          {rows.map((shift, index) => (
            <fieldset key={shift.key} className="flex flex-col gap-3 rounded-md border-[length:var(--karry-border-width)] border-border p-3">
              <legend className="px-1 font-semibold">{t("portal.shifts.shiftNumber", { number: index + 1 })}</legend>
              <div className="grid gap-3 md:grid-cols-4">
                <TextField label={t("portal.shifts.code")} value={shift.code} required onChange={(e) => { setRow(index, { code: e.target.value.toUpperCase() }); }} />
                <TextField label={t("portal.shifts.name")} value={shift.name} required onChange={(e) => { setRow(index, { name: e.target.value }); }} />
                <TextField label={t("portal.shifts.start")} type="time" value={shift.start} required onChange={(e) => { setRow(index, { start: e.target.value }); }} />
                <TextField label={t("portal.shifts.end")} type="time" value={shift.end} required onChange={(e) => { setRow(index, { end: e.target.value }); }} />
              </div>
              {shift.end <= shift.start && <p className="text-sm text-text-muted">{t("portal.shifts.nextDay")}</p>}
              <div role="group" aria-label={t("portal.shifts.days")} className="flex flex-wrap gap-3">
                {dayCodes.map((day, dayIndex) => (
                  <label key={day} className="flex min-h-[var(--karry-control-height)] items-center gap-2">
                    <input
                      type="checkbox"
                      className="size-5"
                      checked={shift.days.includes(day)}
                      onChange={(e) => { setRow(index, { days: e.target.checked ? dayCodes.filter((d) => d === day || shift.days.includes(d)) : shift.days.filter((d) => d !== day) }); }}
                    />
                    <span>{dayName(i18n.language, dayIndex, "long")}</span>
                  </label>
                ))}
              </div>
              {rows.length > 1 && (
                <div>
                  <Button type="button" variant="ghost" onClick={() => { setRows(rows.filter((_, i) => i !== index)); }}>{t("portal.shifts.removeShift")}</Button>
                </div>
              )}
            </fieldset>
          ))}
          <div className="flex flex-wrap gap-3">
            <Button type="button" variant="secondary" onClick={() => { setRows([...rows, row({ code: "", name: "", start: "06:00", end: "14:00", days: everyDay })]); }}>
              {t("portal.shifts.addShift")}
            </Button>
            <Button type="submit" loading={save.isPending}>{t("portal.shifts.save")}</Button>
            <Button type="button" variant="ghost" onClick={() => { setRows(null); }}>{t("portal.shifts.cancel")}</Button>
          </div>
        </form>
      )}
    </section>
  );
}

function NonWorkingDays({ site, editable }: { site: Site; editable: boolean }) {
  const { t } = useTranslation();
  const api = useApi();
  const queryClient = useQueryClient();
  const toast = useToast();
  const today = todayIn(site.timeZone);
  const days = useQuery({
    queryKey: [...siteKey(site.id), "non-working-days"],
    queryFn: async () => (await api.GET("/api/v1/organization/sites/{siteId}/non-working-days", { params: { path: { siteId: site.id } } })).data?.items ?? [],
  });
  const [date, setDate] = useState("");
  const [kind, setKind] = useState<string>("public-holiday");
  const [name, setName] = useState("");

  const changed = async (title: string) => {
    await queryClient.invalidateQueries({ queryKey: siteKey(site.id) });
    toast({ status: "ok", title });
  };

  const add = useMutation({
    mutationFn: async () => (await api.PUT("/api/v1/organization/sites/{siteId}/non-working-days/{date}", {
      params: { path: { siteId: site.id, date } }, body: { kind, name },
    })).data,
    onSuccess: async () => {
      setDate("");
      setName("");
      await changed(t("portal.days.saved"));
    },
  });

  const remove = useMutation({
    mutationFn: async (day: string) => {
      await api.DELETE("/api/v1/organization/sites/{siteId}/non-working-days/{date}", { params: { path: { siteId: site.id, date: day } } });
    },
    onSuccess: () => changed(t("portal.days.removed")),
  });

  return (
    <section aria-labelledby="non-working-days" className="flex flex-col gap-3">
      <h2 id="non-working-days" className="text-xl font-bold">{t("portal.days.title")}</h2>
      <p className="text-text-muted">{t("portal.days.hint")}</p>
      <DataTable<NonWorkingDay>
        caption={t("portal.days.title")}
        rows={days.data ?? []}
        rowKey={(day) => day.date}
        empty={<EmptyState title={days.isPending ? t("common.loading") : t("portal.days.none")} />}
        columns={[
          { key: "date", header: t("portal.days.date"), cell: (day) => day.date },
          { key: "kind", header: t("portal.days.kind"), cell: (day) => t(`portal.days.kind.${day.kind}`) },
          { key: "name", header: t("portal.days.name"), cell: (day) => <span translate="no">{day.name}</span> },
          {
            key: "actions",
            header: t("portal.shifts.actions"),
            cell: (day) => editable && day.date >= today
              ? <Button variant="ghost" loading={remove.isPending && remove.variables === day.date} onClick={() => { remove.mutate(day.date); }}>
                  {t("portal.days.remove", { date: day.date })}
                </Button>
              : null,
          },
        ]}
      />
      {editable && (
        <form
          aria-label={t("portal.days.add")}
          className="grid items-end gap-3 md:grid-cols-4"
          onSubmit={(event) => { event.preventDefault(); add.mutate(); }}
        >
          <TextField label={t("portal.days.date")} type="date" value={date} min={today} required onChange={(e) => { setDate(e.target.value); }} />
          <SelectField
            label={t("portal.days.kind")}
            value={kind}
            onValueChange={setKind}
            options={dayKinds.map((value) => ({ value, label: t(`portal.days.kind.${value}`) }))}
          />
          <TextField label={t("portal.days.name")} value={name} required onChange={(e) => { setName(e.target.value); }} />
          <div>
            <Button type="submit" loading={add.isPending} disabled={date === "" || name.trim() === ""}>{t("portal.days.add")}</Button>
          </div>
        </form>
      )}
    </section>
  );
}

function Upcoming({ site }: { site: Site }) {
  const { t, i18n } = useTranslation();
  const api = useApi();
  const instances = useQuery({
    queryKey: [...siteKey(site.id), "instances"],
    queryFn: async () => (await api.GET("/api/v1/organization/sites/{siteId}/shift-instances", { params: { path: { siteId: site.id } } })).data?.items ?? [],
  });
  const time = new Intl.DateTimeFormat(i18n.language, { timeZone: site.timeZone, hour: "2-digit", minute: "2-digit", hourCycle: "h23" });
  const date = new Intl.DateTimeFormat(i18n.language, { timeZone: "UTC", weekday: "short", day: "numeric", month: "short" });
  const length = (minutes: number) => (minutes % 60 === 0 ? t("portal.upcoming.hours", { hours: minutes / 60 }) : t("portal.upcoming.hoursMinutes", { hours: Math.floor(minutes / 60), minutes: minutes % 60 }));

  return (
    <section aria-labelledby="upcoming-shifts" className="flex flex-col gap-3">
      <h2 id="upcoming-shifts" className="text-xl font-bold">{t("portal.upcoming.title")}</h2>
      <p className="text-text-muted">{t("portal.upcoming.hint", { zone: site.timeZone })}</p>
      <DataTable<ShiftInstance>
        caption={t("portal.upcoming.title")}
        rows={(instances.data ?? []).slice(0, 14)}
        rowKey={(shift) => `${shift.date}-${shift.code}`}
        empty={<EmptyState title={instances.isPending ? t("common.loading") : t("portal.upcoming.none")} />}
        columns={[
          { key: "date", header: t("portal.upcoming.date"), cell: (shift) => date.format(new Date(`${shift.date}T00:00:00Z`)) },
          { key: "shift", header: t("portal.upcoming.shift"), cell: (shift) => <span translate="no">{shift.name}</span> },
          { key: "time", header: t("portal.upcoming.time"), cell: (shift) => `${time.format(new Date(shift.startsAt))}–${time.format(new Date(shift.endsAt))}` },
          { key: "length", header: t("portal.upcoming.length"), cell: (shift) => length(Number(shift.lengthMinutes)), numeric: true },
        ]}
      />
    </section>
  );
}

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link, useNavigate } from "@tanstack/react-router";
import { SearchX } from "lucide-react";
import { useState } from "react";
import { intlLocale, useTranslation } from "@karry/i18n";
import { Button, Checkbox, DataTable, EmptyState, PageHeader, PageSkeleton, SelectField, StatusChip, TextField, useToast } from "@karry/ui";
import { useAccess, useApi, useWithStepUp } from "../app/api";
import { VersionConflict } from "../app/VersionConflict";
import { handlesVersionConflict, isVersionConflict } from "../app/conflicts";
import { useRecordDraft } from "../app/recordDraft";
import { Trail } from "../shell/Trail";
import {
  haulersKey, noHauler, tareMethods, truckKey, trucksKey, TrucksManagePermission, truckInput, truckStatuses, TrucksVerifyPermission, useHaulers, useTrucks,
  type Hauler, type HaulerInput, type Truck, type TruckDraft, type TruckStatus,
} from "./api";

const card = "flex flex-col gap-6 rounded-xl border-[length:var(--karry-border-width)] border-divider bg-surface p-5 shadow-[var(--karry-card-shadow)] md:p-6";
const link = "font-semibold text-primary underline-offset-4 hover:underline";
const anyStatus = "all";

function useKg() {
  const { i18n } = useTranslation();
  const format = new Intl.NumberFormat(intlLocale(i18n.language), { maximumFractionDigits: 0 });
  return (kg: number | string | null | undefined) => (kg === null || kg === undefined ? "—" : `${format.format(Number(kg))} kg`);
}

function TruckStatusChip({ status }: { status: string }) {
  const { t } = useTranslation();
  if (status === "active") return <StatusChip status="ok">{t("portal.trucks.status.active")}</StatusChip>;
  if (status === "unverified") return <StatusChip status="warning">{t("portal.trucks.status.unverified")}</StatusChip>;
  return <StatusChip status="pending">{t("portal.trucks.status.archived")}</StatusChip>;
}

// ── Trucks ───────────────────────────────────────────────────────────────────────────────────────────────────────

/** The company's trucks (US-P3-SAL-04): found by any part of the plate however it is typed; unverified ones stand out. */
export function TrucksPage() {
  const { t } = useTranslation();
  const { can } = useAccess();
  const [plate, setPlate] = useState("");
  const [status, setStatus] = useState<"" | TruckStatus>("");
  const trucks = useTrucks(plate, status);
  const haulers = useHaulers(true);
  const kg = useKg();
  const haulerName = (id: string | null | undefined) => (id ? haulers.data?.find((h) => h.id === id)?.name : undefined) ?? "—";

  return (
    <div className="flex flex-col gap-4">
      <PageHeader
        breadcrumbs={<Trail steps={[{ to: "/settings", label: t("portal.nav.settings") }]} />}
        title={t("portal.trucks.title")}
        actions={can(TrucksManagePermission) && (
          <div className="flex flex-wrap gap-2">
            <Button asChild variant="secondary"><Link to="/settings/haulers">{t("portal.haulers.title")}</Link></Button>
            <Button asChild><Link to="/settings/trucks/new">{t("portal.trucks.new")}</Link></Button>
          </div>
        )}
      />
      <div className="grid gap-4 md:grid-cols-2">
        <TextField label={t("portal.trucks.search")} description={t("portal.trucks.searchHint")} value={plate} type="search" autoComplete="off"
          onChange={(e) => { setPlate(e.target.value); }} />
        <SelectField
          label={t("portal.trucks.statusFilter")}
          value={status || anyStatus}
          onValueChange={(value) => { setStatus(value === anyStatus ? "" : (value as TruckStatus)); }}
          options={[{ value: anyStatus, label: t("portal.trucks.status.all") }, ...truckStatuses.map((value) => ({ value, label: t(`portal.trucks.status.${value}`) }))]}
        />
      </div>
      <DataTable<Truck>
        caption={t("portal.trucks.title")}
        rows={trucks.data ?? []}
        rowKey={(truck) => truck.id}
        loading={trucks.isPending && trucks.fetchStatus !== "idle"}
        empty={<EmptyState title={plate.trim() ? t("portal.trucks.noMatch") : t("portal.trucks.none")} />}
        columns={[
          {
            key: "plate",
            header: t("portal.trucks.plate"),
            cell: (truck) => <Link to="/settings/trucks/$truckId" params={{ truckId: truck.id }} className={link} translate="no">{truck.plate}</Link>,
          },
          { key: "hauler", header: t("portal.trucks.hauler"), cell: (truck) => <span translate="no">{haulerName(truck.haulerId)}</span> },
          { key: "tare", header: t("portal.trucks.tare"), numeric: true, cell: (truck) => kg(truck.tare?.kg) },
          { key: "payload", header: t("portal.trucks.payload"), numeric: true, cell: (truck) => kg(truck.maxPayloadKg) },
          { key: "axles", header: t("portal.trucks.axles"), cell: (truck) => truck.axles ?? "—" },
          { key: "status", header: t("portal.trucks.statusFilter"), cell: (truck) => <TruckStatusChip status={truck.status} /> },
        ]}
      />
    </div>
  );
}

/** One truck, or a new one. */
export function TruckPage({ truckId }: { truckId?: string }) {
  const { t } = useTranslation();
  const api = useApi();
  const truck = useQuery({
    queryKey: truckKey(truckId ?? "new"),
    queryFn: async () => (await api.GET("/api/v1/weighbridge/trucks/{truckId}", { params: { path: { truckId: truckId ?? "" } } })).data ?? null,
    enabled: truckId !== undefined,
  });

  if (truckId !== undefined && truck.isPending) {
    return <PageSkeleton />;
  }

  if (truckId !== undefined && !truck.data) {
    return <EmptyState icon={SearchX} title={t("portal.notFound")} />;
  }

  return <TruckForm key={truck.data?.id ?? "new"} truck={truck.data ?? null} />;
}

function truckDraft(truck: Truck | null): TruckDraft {
  return {
    haulerId: truck?.haulerId ?? noHauler,
    tareKg: truck?.tare ? String(truck.tare.kg) : "",
    tareOn: truck?.tare?.on ?? "",
    tareMethod: truck?.tare?.method ?? "weighed",
    maxPayloadKg: truck?.maxPayloadKg === null || truck?.maxPayloadKg === undefined ? "" : String(truck.maxPayloadKg),
    axles: truck?.axles ?? "",
  };
}

function TruckForm({ truck }: { truck: Truck | null }) {
  const { t } = useTranslation();
  const api = useApi();
  const withStepUp = useWithStepUp();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const toast = useToast();
  const { can } = useAccess();
  const haulers = useHaulers(true);
  const editable = can(TrucksManagePermission) && truck?.status !== "archived";
  const [plate, setPlate] = useState(truck?.plate ?? "");
  const { draft, setDraft, version, accept } = useRecordDraft(truck, truckDraft);
  const [conflict, setConflict] = useState(false);
  // Active haulers, and the truck's own even if it was archived since.
  const choices = (haulers.data ?? []).filter((h) => h.status === "active" || h.id === truck?.haulerId);

  const refresh = async (saved: Truck) => {
    await queryClient.invalidateQueries({ queryKey: trucksKey });
    queryClient.setQueryData(truckKey(saved.id), saved);
  };

  const save = useMutation({
    meta: handlesVersionConflict,
    mutationFn: async (expected?: string) => withStepUp(async () => {
      const body = truckInput(truck ? null : plate, draft);
      if (!truck) {
        return (await api.POST("/api/v1/weighbridge/trucks", { body })).data;
      }

      const tag = expected ?? version;
      return (await api.PUT("/api/v1/weighbridge/trucks/{truckId}", { params: { path: { truckId: truck.id }, header: tag ? { "If-Match": tag } : {} }, body })).data;
    }),
    onError: (error) => { setConflict(isVersionConflict(error)); },
    onSuccess: async (saved) => {
      if (!saved) return;
      accept(saved);
      setConflict(false);
      await refresh(saved);
      toast({ status: "ok", title: t("portal.trucks.saved") });
      if (!truck) {
        await navigate({ to: "/settings/trucks/$truckId", params: { truckId: saved.id } });
      }
    },
  });

  const transition = useMutation({
    mutationFn: async (action: "verify" | "archive" | "restore") => withStepUp(async () => {
      const path = { params: { path: { truckId: truck?.id ?? "" } } };
      if (action === "verify") return (await api.POST("/api/v1/weighbridge/trucks/{truckId}/verify", path)).data;
      if (action === "archive") return (await api.POST("/api/v1/weighbridge/trucks/{truckId}/archive", path)).data;
      return (await api.POST("/api/v1/weighbridge/trucks/{truckId}/restore", path)).data;
    }),
    onSuccess: async (saved, action) => {
      if (saved) {
        accept(saved);
        await refresh(saved);
        toast({ status: "ok", title: t(`portal.trucks.done.${action}`) });
      }
    },
  });

  const latest = async () => truck ? (await api.GET("/api/v1/weighbridge/trucks/{truckId}", { params: { path: { truckId: truck.id } } })).data : undefined;

  return (
    <div className="flex max-w-4xl flex-col gap-6">
      <PageHeader
        breadcrumbs={<Trail steps={[{ to: "/settings", label: t("portal.nav.settings") }, { to: "/settings/trucks", label: t("portal.trucks.title") }]} />}
        title={truck ? truck.plate : t("portal.trucks.new")}
        titleTranslate={truck ? "no" : undefined}
      >
        {truck && <div><TruckStatusChip status={truck.status} /></div>}
        {truck?.status === "archived" && <p className="text-text-muted">{t("portal.trucks.archivedHint")}</p>}
        {!can(TrucksManagePermission) && <p className="text-text-muted">{t("portal.trucks.readOnly")}</p>}
      </PageHeader>

      {truck?.status === "unverified" && (
        <section className={card} aria-labelledby="unverified-title">
          <div className="flex flex-col gap-2">
            <h2 id="unverified-title" className="text-base font-semibold text-text">{t("portal.trucks.unverified.title")}</h2>
            <p className="text-text-muted">{t("portal.trucks.unverified.body")}</p>
            {!can(TrucksVerifyPermission) && <p className="text-text-muted">{t("portal.trucks.unverified.notYours")}</p>}
          </div>
          {can(TrucksVerifyPermission) && (
            <div>
              <Button loading={transition.isPending && transition.variables === "verify"} onClick={() => { transition.mutate("verify"); }}>
                {t("portal.trucks.verify")}
              </Button>
            </div>
          )}
        </section>
      )}

      <form className={card} noValidate onSubmit={(event) => { event.preventDefault(); save.mutate(undefined); }}>
        <fieldset disabled={!editable} className="grid gap-4 md:grid-cols-2">
          <TextField
            label={t("portal.trucks.plate")}
            description={truck ? t("portal.trucks.plateFixed") : t("portal.trucks.plateHint")}
            value={plate}
            required
            autoComplete="off"
            disabled={truck !== null || !editable}
            onChange={(e) => { setPlate(e.target.value.toUpperCase()); }}
          />
          <SelectField
            label={t("portal.trucks.hauler")}
            value={draft.haulerId}
            onValueChange={(value) => { setDraft((d) => ({ ...d, haulerId: value })); }}
            options={[{ value: noHauler, label: t("portal.trucks.noHauler") }, ...choices.map((h) => ({ value: h.id, label: h.name }))]}
            disabled={!editable}
          />
          <TextField label={t("portal.trucks.tareKg")} description={t("portal.trucks.tareHint")} inputMode="numeric" value={draft.tareKg}
            onChange={(e) => { setDraft((d) => ({ ...d, tareKg: e.target.value })); }} />
          <TextField label={t("portal.trucks.tareOn")} type="date" value={draft.tareOn} onChange={(e) => { setDraft((d) => ({ ...d, tareOn: e.target.value })); }} />
          <SelectField
            label={t("portal.trucks.tareMethod")}
            value={draft.tareMethod}
            onValueChange={(value) => { setDraft((d) => ({ ...d, tareMethod: value })); }}
            options={tareMethods.map((value) => ({ value, label: t(`portal.trucks.method.${value}`) }))}
            disabled={!editable}
          />
          <TextField label={t("portal.trucks.payloadKg")} inputMode="numeric" value={draft.maxPayloadKg}
            onChange={(e) => { setDraft((d) => ({ ...d, maxPayloadKg: e.target.value })); }} />
          <TextField label={t("portal.trucks.axles")} description={t("portal.trucks.axlesHint")} value={draft.axles}
            onChange={(e) => { setDraft((d) => ({ ...d, axles: e.target.value })); }} />
        </fieldset>

        {conflict && (
          <VersionConflict
            busy={save.isPending}
            onKeepMine={() => void latest().then((current) => { if (current) { save.mutate(current.version); } })}
            onShowTheirs={() => void latest().then(async (current) => { if (current) { accept(current); setConflict(false); await refresh(current); } })}
          />
        )}

        {editable && (
          <div>
            <Button type="submit" loading={save.isPending}>{truck ? t("portal.trucks.save") : t("portal.trucks.create")}</Button>
          </div>
        )}
      </form>

      {truck && can(TrucksManagePermission) && (
        <div>
          {truck.status === "archived"
            ? <Button loading={transition.isPending} onClick={() => { transition.mutate("restore"); }}>{t("portal.trucks.restore")}</Button>
            : <Button variant="secondary" loading={transition.isPending && transition.variables === "archive"} onClick={() => { transition.mutate("archive"); }}>{t("portal.trucks.archive")}</Button>}
        </div>
      )}
    </div>
  );
}

// ── Haulers ──────────────────────────────────────────────────────────────────────────────────────────────────────

/** The companies whose trucks cross the scale: a name unique however it is written, a phone; archived ones take no new truck. */
export function HaulersPage() {
  const { t } = useTranslation();
  const { can } = useAccess();
  const [includeArchived, setIncludeArchived] = useState(false);
  const haulers = useHaulers(includeArchived);
  const [editing, setEditing] = useState<Hauler | null>(null);

  return (
    <div className="flex max-w-4xl flex-col gap-4">
      <PageHeader
        breadcrumbs={<Trail steps={[{ to: "/settings", label: t("portal.nav.settings") }, { to: "/settings/trucks", label: t("portal.trucks.title") }]} />}
        title={t("portal.haulers.title")}
      />
      {can(TrucksManagePermission) && <HaulerForm key={editing?.id ?? "new"} hauler={editing} onDone={() => { setEditing(null); }} />}
      <Checkbox label={t("portal.haulers.includeArchived")} checked={includeArchived} onChange={(e) => { setIncludeArchived(e.target.checked); }} className="self-start" />
      <DataTable<Hauler>
        caption={t("portal.haulers.title")}
        rows={haulers.data ?? []}
        rowKey={(hauler) => hauler.id}
        selectedKey={editing?.id}
        onSelect={can(TrucksManagePermission) ? (hauler) => { setEditing(hauler); } : undefined}
        loading={haulers.isPending && haulers.fetchStatus !== "idle"}
        empty={<EmptyState title={t("portal.haulers.none")} />}
        columns={[
          { key: "name", header: t("portal.haulers.name"), cell: (hauler) => <span translate="no">{hauler.name}</span> },
          { key: "phone", header: t("portal.haulers.phone"), cell: (hauler) => <span translate="no" dir="ltr">{hauler.phone ?? "—"}</span> },
          {
            key: "status",
            header: t("portal.trucks.statusFilter"),
            cell: (hauler) => hauler.status === "active"
              ? <StatusChip status="ok">{t("portal.trucks.status.active")}</StatusChip>
              : <StatusChip status="pending">{t("portal.trucks.status.archived")}</StatusChip>,
          },
        ]}
      />
    </div>
  );
}

/** A new hauler, or the chosen one: its name and phone, archived or active. */
function HaulerForm({ hauler, onDone }: { hauler: Hauler | null; onDone: () => void }) {
  const { t } = useTranslation();
  const api = useApi();
  const withStepUp = useWithStepUp();
  const queryClient = useQueryClient();
  const toast = useToast();
  const [name, setName] = useState(hauler?.name ?? "");
  const [phone, setPhone] = useState(hauler?.phone ?? "");

  const save = useMutation({
    mutationFn: async (status: "active" | "archived") => withStepUp(async () => {
      const body: HaulerInput = { name, phone: phone.trim() || null, status };
      if (!hauler) {
        return (await api.POST("/api/v1/weighbridge/haulers", { body })).data;
      }

      return (await api.PUT("/api/v1/weighbridge/haulers/{haulerId}", { params: { path: { haulerId: hauler.id }, header: { "If-Match": hauler.version } }, body })).data;
    }),
    onSuccess: async (saved) => {
      if (!saved) return;
      await queryClient.invalidateQueries({ queryKey: haulersKey });
      toast({ status: "ok", title: t("portal.haulers.saved") });
      setName("");
      setPhone("");
      onDone();
    },
  });

  return (
    <form className={card} noValidate onSubmit={(event) => { event.preventDefault(); save.mutate(hauler?.status === "archived" ? "archived" : "active"); }}>
      <h2 className="text-base font-semibold text-text">{hauler ? hauler.name : t("portal.haulers.new")}</h2>
      <div className="grid gap-4 md:grid-cols-2">
        <TextField label={t("portal.haulers.name")} description={t("portal.haulers.nameHint")} value={name} required onChange={(e) => { setName(e.target.value); }} />
        <TextField label={t("portal.haulers.phone")} type="tel" dir="ltr" value={phone} onChange={(e) => { setPhone(e.target.value); }} />
      </div>
      <div className="flex flex-wrap gap-2">
        <Button type="submit" loading={save.isPending}>{hauler ? t("portal.trucks.save") : t("portal.haulers.create")}</Button>
        {hauler && (
          <>
            <Button type="button" variant="secondary" onClick={() => { save.mutate(hauler.status === "active" ? "archived" : "active"); }}>
              {hauler.status === "active" ? t("portal.trucks.archive") : t("portal.trucks.restore")}
            </Button>
            <Button type="button" variant="ghost" onClick={onDone}>{t("portal.haulers.cancel")}</Button>
          </>
        )}
      </div>
    </form>
  );
}

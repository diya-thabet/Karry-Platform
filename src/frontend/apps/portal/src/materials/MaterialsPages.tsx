import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link, useNavigate } from "@tanstack/react-router";
import { useState } from "react";
import { useTranslation } from "@karry/i18n";
import { Button, DataTable, EmptyState, QuantityInput, SelectField, StatusChip, TextField, useToast, type MaterialState } from "@karry/ui";
import { useAccess, useApi, useCurrentTenant, useWithStepUp } from "../api";
import { addDays, todayIn } from "../sites/api";
import {
  decimalText, looseFromSwell, materialKey, materialKinds, materialsKey, MaterialsManagePermission, materialStates, quantityUnits, shiftDecimal,
  stateOptions, statesOf, swellPercent, useMaterials, versionOn, type DensityInput, type DensityVersion, type Material, type MaterialInput,
} from "./api";

/** Numbers as the person reads them (decimal comma in French); the API's values stay exact underneath. */
function useNumber() {
  const { i18n } = useTranslation();
  return (value: number | string, digits = 4) => new Intl.NumberFormat(i18n.language, { maximumFractionDigits: digits }).format(Number(value));
}

/** The company's materials (US-P1-AST-04): code, name, kind and the densities valid today; archived ones on request. */
export function MaterialsPage() {
  const { t } = useTranslation();
  const { can } = useAccess();
  const number = useNumber();
  const { tenant } = useCurrentTenant();
  const today = todayIn(tenant?.timeZone ?? "UTC");
  const [includeArchived, setIncludeArchived] = useState(false);
  const materials = useMaterials(includeArchived);
  const density = (material: Material, state: MaterialState) => {
    const value = versionOn(material, today)?.[state];
    return value === null || value === undefined ? "—" : number(value);
  };

  return (
    <div className="flex flex-col gap-4">
      <Link to="/settings" className="text-sm font-medium text-text-muted hover:text-primary">{t("portal.materials.backToSettings")}</Link>
      <div className="flex flex-wrap items-center justify-between gap-4">
        <h1 className="text-2xl font-semibold tracking-tight">{t("portal.materials.title")}</h1>
        {can(MaterialsManagePermission) && (
          <Button asChild>
            <Link to="/settings/materials/new">{t("portal.materials.new")}</Link>
          </Button>
        )}
      </div>
      <label className="flex min-h-[var(--karry-control-height)] items-center gap-3">
        <input type="checkbox" className="size-5" checked={includeArchived} onChange={(e) => { setIncludeArchived(e.target.checked); }} />
        <span>{t("portal.materials.includeArchived")}</span>
      </label>
      <DataTable<Material>
        caption={t("portal.materials.title")}
        rows={materials.data ?? []}
        rowKey={(material) => material.id}
        empty={<EmptyState title={materials.isPending && materials.fetchStatus !== "idle" ? t("common.loading") : t("portal.materials.none")} />}
        columns={[
          { key: "code", header: t("portal.materials.code"), cell: (material) => <span translate="no">{material.code}</span> },
          {
            key: "name",
            header: t("portal.materials.name"),
            cell: (material) => (
              <Link to="/settings/materials/$materialId" params={{ materialId: material.id }} className="font-semibold text-primary underline-offset-4 hover:underline" translate="no">
                {material.name}
              </Link>
            ),
          },
          { key: "kind", header: t("portal.materials.kind"), cell: (material) => t(`portal.materials.kind.${material.kind}`) },
          ...materialStates.map((state) => ({ key: state, header: t(`portal.materials.density.${state}`), cell: (material: Material) => density(material, state) })),
          { key: "status", header: t("portal.materials.status"), cell: (material) => <MaterialStatus material={material} /> },
        ]}
      />
    </div>
  );
}

function MaterialStatus({ material }: { material: Material }) {
  const { t } = useTranslation();
  return material.status === "active"
    ? <StatusChip status="ok">{t("portal.materials.status.active")}</StatusChip>
    : <StatusChip status="pending">{t("portal.materials.status.archived")}</StatusChip>;
}

/** One material: its details, density versions and a conversion panel; or a new material. */
export function MaterialPage({ materialId }: { materialId?: string }) {
  const { t } = useTranslation();
  const api = useApi();
  const material = useQuery({
    queryKey: materialKey(materialId ?? "new"),
    queryFn: async () => (await api.GET("/api/v1/materials/{materialId}", { params: { path: { materialId: materialId ?? "" } } })).data ?? null,
    enabled: materialId !== undefined,
  });
  const { loaded, tenant } = useCurrentTenant();
  const today = todayIn(tenant?.timeZone ?? "UTC");

  if (!loaded || (materialId !== undefined && material.isPending)) {
    return null;
  }

  if (materialId === undefined) {
    return <MaterialForm material={null} today={today} />;
  }

  if (!material.data) {
    return <EmptyState title={t("portal.notFound")} />;
  }

  return (
    <div className="flex flex-col gap-8">
      <MaterialForm key={material.data.changedAt} material={material.data} today={today} />
      <ConversionPanel material={material.data} today={today} />
      <Densities key={`${material.data.changedAt}-densities`} material={material.data} today={today} />
    </div>
  );
}

interface DensityDraft {
  bank: string;
  loose: string;
  compacted: string;
  /** Percent, as people state moisture and swell; the API takes fractions. */
  moisture: string;
  swell: string;
}

function draftOf(version: DensityVersion | undefined): DensityDraft {
  const text = (value: number | string | null | undefined) => (value === null || value === undefined ? "" : String(value));
  const bank = text(version?.bank);
  const loose = text(version?.loose);
  return {
    bank, loose, compacted: text(version?.compacted),
    moisture: version ? shiftDecimal(String(version.moisture), 2) : "0",
    swell: bank && loose ? swellPercent(bank, loose) ?? "" : "",
  };
}

/** The draft as the API takes it, or null while a field is not a number or no density is given. */
function densityBody(draft: DensityDraft): DensityInput | null {
  const field = (text: string) => (text.trim() === "" ? null : decimalText(text));
  const bank = field(draft.bank);
  const loose = field(draft.loose);
  const compacted = field(draft.compacted);
  const moisture = decimalText(draft.moisture.trim() === "" ? "0" : draft.moisture);
  const typed = [draft.bank, draft.loose, draft.compacted].filter((text) => text.trim() !== "");
  if (typed.length === 0 || [bank, loose, compacted].filter((value) => value !== null).length !== typed.length || moisture === null) {
    return null;
  }

  return { bank, loose, compacted, moisture: shiftDecimal(moisture, -2) };
}

/**
 * Densities in t/m³ and moisture in percent (US-P1-AST-04 AC1). Swell is derived from bank and loose (ADR-0025); typing
 * a swell instead fills the loose density from the bank one, as people often know their swell rather than ρ_loose.
 */
function DensityFields({ draft, onChange, checked }: { draft: DensityDraft; onChange: (draft: DensityDraft) => void; checked: boolean }) {
  const { t } = useTranslation();
  const set = (key: keyof DensityDraft, value: string) => {
    const next = { ...draft, [key]: value };
    const bank = decimalText(next.bank);
    if (key === "swell") {
      const swell = decimalText(value);
      next.loose = bank && swell ? looseFromSwell(bank, swell) ?? next.loose : next.loose;
    } else if (key === "bank" || key === "loose") {
      const loose = decimalText(next.loose);
      next.swell = bank && loose ? swellPercent(bank, loose) ?? "" : "";
    }

    onChange(next);
  };
  const invalid = (text: string) => checked && text.trim() !== "" && decimalText(text) === null;
  const none = checked && [draft.bank, draft.loose, draft.compacted].every((text) => text.trim() === "");

  return (
    <div className="flex flex-col gap-2">
      <div className="grid gap-4 md:grid-cols-3">
        {materialStates.map((state) => (
          <TextField
            key={state}
            label={`${t(`portal.materials.density.${state}`)} (t/m³)`}
            inputMode="decimal"
            value={draft[state]}
            error={invalid(draft[state]) ? t("portal.materials.numberInvalid") : undefined}
            onChange={(e) => { set(state, e.target.value); }}
          />
        ))}
        <TextField
          label={`${t("portal.materials.moisture")} (%)`}
          description={t("portal.materials.moistureHint")}
          inputMode="decimal"
          value={draft.moisture}
          error={invalid(draft.moisture) ? t("portal.materials.numberInvalid") : undefined}
          onChange={(e) => { set("moisture", e.target.value); }}
        />
        <TextField
          label={`${t("portal.materials.swell")} (%)`}
          description={t("portal.materials.swellHint")}
          inputMode="decimal"
          value={draft.swell}
          disabled={decimalText(draft.bank) === null}
          onChange={(e) => { set("swell", e.target.value); }}
        />
      </div>
      {none && <p role="alert" className="font-semibold text-danger">{t("portal.materials.densityRequired")}</p>}
    </div>
  );
}

function MaterialForm({ material, today }: { material: Material | null; today: string }) {
  const { t } = useTranslation();
  const api = useApi();
  const withStepUp = useWithStepUp();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const toast = useToast();
  const { can } = useAccess();
  const editable = can(MaterialsManagePermission) && material?.status !== "archived";
  const [code, setCode] = useState(material?.code ?? "");
  const [name, setName] = useState(material?.name ?? "");
  const [kind, setKind] = useState<string>(material?.kind ?? "raw-rock");
  const [effectiveFrom, setEffectiveFrom] = useState(today);
  const [densities, setDensities] = useState<DensityDraft>(() => draftOf(undefined));
  const [checked, setChecked] = useState(false);

  const refresh = async (saved: Material) => {
    await queryClient.invalidateQueries({ queryKey: materialsKey });
    queryClient.setQueryData(materialKey(saved.id), saved);
  };

  const save = useMutation({
    mutationFn: async (body: DensityInput | null) => withStepUp(async () => material
      ? (await api.PUT("/api/v1/materials/{materialId}", { params: { path: { materialId: material.id } }, body: { name, kind } })).data
      : (await api.POST("/api/v1/materials", { body: { code, name, kind, densities: body, effectiveFrom: effectiveFrom || null } satisfies MaterialInput })).data),
    onSuccess: async (saved) => {
      if (!saved) {
        return;
      }

      await refresh(saved);
      toast({ status: "ok", title: t("portal.materials.saved") });
      if (!material) {
        await navigate({ to: "/settings/materials/$materialId", params: { materialId: saved.id } });
      }
    },
  });

  const archive = useMutation({
    mutationFn: async (action: "archive" | "restore") => withStepUp(async () => action === "archive"
      ? (await api.POST("/api/v1/materials/{materialId}/archive", { params: { path: { materialId: material?.id ?? "" } } })).data
      : (await api.POST("/api/v1/materials/{materialId}/restore", { params: { path: { materialId: material?.id ?? "" } } })).data),
    onSuccess: async (saved, action) => {
      if (saved) {
        await refresh(saved);
        toast({ status: "ok", title: t(action === "archive" ? "portal.materials.archived" : "portal.materials.restored") });
      }
    },
  });

  const submit = () => {
    setChecked(true);
    const body = material ? null : densityBody(densities);
    if (material || body) {
      save.mutate(body);
    }
  };

  return (
    <div className="flex max-w-4xl flex-col gap-6">
      <div className="flex flex-col gap-1">
        <Link to="/settings/materials" className="text-sm font-medium text-text-muted hover:text-primary">{t("portal.materials.back")}</Link>
        <div className="flex flex-wrap items-center gap-3">
          <h1 className="text-2xl font-semibold tracking-tight" translate={material ? "no" : undefined}>{material ? material.name : t("portal.materials.new")}</h1>
          {material && <MaterialStatus material={material} />}
        </div>
        {material?.status === "archived" && <p className="text-text-muted">{t("portal.materials.archivedHint")}</p>}
        {!can(MaterialsManagePermission) && <p className="text-text-muted">{t("portal.materials.readOnly")}</p>}
      </div>

      <form className="flex flex-col gap-6 rounded-xl border-[length:var(--karry-border-width)] border-divider bg-surface p-5 shadow-[var(--karry-card-shadow)] md:p-6" noValidate onSubmit={(event) => { event.preventDefault(); submit(); }}>
        <fieldset disabled={!editable} className="grid gap-4 md:grid-cols-3">
          <TextField
            label={t("portal.materials.code")}
            description={material ? t("portal.materials.codeFixed") : t("portal.materials.codeHint")}
            value={code}
            required
            disabled={material !== null || !editable}
            onChange={(e) => { setCode(e.target.value.toUpperCase()); }}
          />
          <TextField label={t("portal.materials.name")} value={name} required onChange={(e) => { setName(e.target.value); }} />
          <SelectField
            label={t("portal.materials.kind")}
            value={kind}
            onValueChange={setKind}
            options={materialKinds.map((value) => ({ value, label: t(`portal.materials.kind.${value}`) }))}
            disabled={!editable}
          />
        </fieldset>

        {!material && (
          <fieldset className="flex flex-col gap-4">
            <legend className="mb-2 text-lg font-semibold">{t("portal.materials.densities")}</legend>
            <TextField
              label={t("portal.materials.effectiveFrom")}
              description={t("portal.materials.firstEffectiveFromHint")}
              type="date"
              value={effectiveFrom}
              onChange={(e) => { setEffectiveFrom(e.target.value); }}
            />
            <DensityFields draft={densities} onChange={setDensities} checked={checked} />
          </fieldset>
        )}

        {editable && (
          <div>
            <Button type="submit" loading={save.isPending}>{material ? t("portal.materials.save") : t("portal.materials.create")}</Button>
          </div>
        )}
      </form>

      {material && can(MaterialsManagePermission) && (
        <div>
          {material.status === "active"
            ? <Button variant="secondary" loading={archive.isPending} onClick={() => { archive.mutate("archive"); }}>{t("portal.materials.archive")}</Button>
            : <Button loading={archive.isPending} onClick={() => { archive.mutate("restore"); }}>{t("portal.materials.restore")}</Button>}
        </div>
      )}
    </div>
  );
}

/**
 * Density versions (US-P1-AST-04 AC2): each valid from its date until the next one. Only versions starting after the
 * company's today are set or removed, so quantities already recorded keep the densities they were computed with.
 */
function Densities({ material, today }: { material: Material; today: string }) {
  const { t, i18n } = useTranslation();
  const api = useApi();
  const withStepUp = useWithStepUp();
  const queryClient = useQueryClient();
  const toast = useToast();
  const number = useNumber();
  const { can } = useAccess();
  const editable = can(MaterialsManagePermission) && material.status === "active";
  const current = versionOn(material, today);
  const tomorrow = addDays(today, 1);
  const [effectiveFrom, setEffectiveFrom] = useState(tomorrow);
  const [draft, setDraft] = useState<DensityDraft | null>(null);
  const [checked, setChecked] = useState(false);
  const date = (value: string) => new Intl.DateTimeFormat(i18n.language, { dateStyle: "medium", timeZone: "UTC" }).format(new Date(`${value}T00:00:00Z`));
  const optional = (value: number | string | null) => (value === null ? "—" : number(value));
  const percent = (value: number | string | null) => (value === null ? "—" : `${number(Number(value) * 100, 2)} %`);

  const changed = async (title: string) => {
    await queryClient.invalidateQueries({ queryKey: materialsKey });
    toast({ status: "ok", title });
  };

  const save = useMutation({
    mutationFn: async (body: DensityInput) => withStepUp(async () => (await api.PUT("/api/v1/materials/{materialId}/densities/{effectiveFrom}", {
      params: { path: { materialId: material.id, effectiveFrom } },
      body,
    })).data),
    onSuccess: async () => {
      setDraft(null);
      setChecked(false);
      await changed(t("portal.materials.versionSaved"));
    },
  });

  const remove = useMutation({
    mutationFn: async (from: string) => withStepUp(async () => {
      await api.DELETE("/api/v1/materials/{materialId}/densities/{effectiveFrom}", { params: { path: { materialId: material.id, effectiveFrom: from } } });
    }),
    onSuccess: async () => { await changed(t("portal.materials.versionRemoved")); },
  });

  const submit = () => {
    setChecked(true);
    const body = draft ? densityBody(draft) : null;
    if (body && effectiveFrom > today) {
      save.mutate(body);
    }
  };

  return (
    <section className="flex max-w-4xl flex-col gap-4 rounded-xl border-[length:var(--karry-border-width)] border-divider bg-surface p-5 shadow-[var(--karry-card-shadow)]" aria-labelledby="material-densities">
      <h2 id="material-densities" className="text-base font-semibold">{t("portal.materials.densities")}</h2>
      <p className="text-text-muted">{t("portal.materials.densitiesHint")}</p>
      <DataTable<DensityVersion>
        caption={t("portal.materials.densities")}
        rows={material.densities}
        rowKey={(version) => version.effectiveFrom}
        empty={<EmptyState title={t("portal.materials.none")} />}
        columns={[
          {
            key: "from",
            header: t("portal.materials.effectiveFrom"),
            cell: (version) => (
              <span className="flex flex-wrap items-center gap-2">
                {date(version.effectiveFrom)}
                {version === current && <StatusChip status="ok">{t("portal.materials.current")}</StatusChip>}
                {version.effectiveFrom > today && <StatusChip status="pending">{t("portal.materials.planned")}</StatusChip>}
              </span>
            ),
          },
          ...materialStates.map((state) => ({ key: state, header: t(`portal.materials.density.${state}`), cell: (version: DensityVersion) => optional(version[state]) })),
          { key: "moisture", header: t("portal.materials.moisture"), cell: (version) => percent(version.moisture) },
          { key: "swell", header: t("portal.materials.swell"), cell: (version) => percent(version.swell) },
          ...(editable ? [{
            key: "actions",
            header: t("portal.materials.actions"),
            cell: (version: DensityVersion) => version.effectiveFrom > today && material.densities.length > 1 && (
              <Button variant="ghost" loading={remove.isPending && remove.variables === version.effectiveFrom}
                onClick={() => { remove.mutate(version.effectiveFrom); }}>
                {t("portal.materials.removeVersion", { date: date(version.effectiveFrom) })}
              </Button>
            ),
          }] : []),
        ]}
      />

      {editable && !draft && (
        <div>
          <Button variant="secondary" onClick={() => { setDraft(draftOf(material.densities.at(-1))); }}>{t("portal.materials.newVersion")}</Button>
        </div>
      )}
      {editable && draft && (
        <form className="flex flex-col gap-4" noValidate onSubmit={(event) => { event.preventDefault(); submit(); }}>
          <TextField
            label={t("portal.materials.effectiveFrom")}
            description={t("portal.materials.effectiveFromHint")}
            type="date"
            min={tomorrow}
            value={effectiveFrom}
            error={checked && !(effectiveFrom > today) ? t("portal.materials.effectiveFromInvalid") : undefined}
            onChange={(e) => { setEffectiveFrom(e.target.value); }}
          />
          <DensityFields draft={draft} onChange={setDraft} checked={checked} />
          <div className="flex flex-wrap gap-3">
            <Button type="submit" loading={save.isPending}>{t("portal.materials.saveVersion")}</Button>
            <Button variant="secondary" onClick={() => { setDraft(null); setChecked(false); }}>{t("portal.materials.cancel")}</Button>
          </div>
        </form>
      )}
    </section>
  );
}

interface Side {
  unit: string;
  state: MaterialState;
}

/**
 * Converts a quantity with the densities valid on a date (US-P1-UNI-01 AC2, AC4): the API computes it (one engine for
 * every screen, ADR-0025); the result shows the factors it used on tap. Display rounds to 3 decimals only.
 */
function ConversionPanel({ material, today }: { material: Material; today: string }) {
  const { t } = useTranslation();
  const api = useApi();
  const number = useNumber();
  const [on, setOn] = useState(today);
  const version = versionOn(material, on);
  const states = statesOf(version);
  const [value, setValue] = useState("10");
  const [from, setFrom] = useState<Side>({ unit: "m3", state: "loose" });
  const [to, setTo] = useState<Side>({ unit: "t", state: "bank" });
  const units = quantityUnits(t);
  const offered = stateOptions(t, states);
  const pick = (side: Side) => ({ unit: side.unit, state: side.unit === "m3" ? (states.includes(side.state) ? side.state : states[0] ?? null) : null });
  const source = pick(from);
  const target = pick(to);
  const quantity = decimalText(value);

  const conversion = useQuery({
    queryKey: [...materialKey(material.id), "conversion", quantity, source, target, on],
    queryFn: async () => (await api.POST("/api/v1/materials/{materialId}/conversions", {
      params: { path: { materialId: material.id } },
      body: { quantity: { value: quantity, unit: source.unit, state: source.state }, unit: target.unit, state: target.state, on, moisture: null },
    })).data ?? null,
    enabled: quantity !== null && version !== undefined && on !== "",
  });

  const result = conversion.data;
  const factor = (factorValue: number | string, unit: string) =>
    unit === "t/m3" ? `${number(factorValue)} t/m³` : unit === "fraction" ? `${number(Number(factorValue) * 100, 2)} %` : number(factorValue);

  return (
    <section className="flex max-w-4xl flex-col gap-4 rounded-xl border-[length:var(--karry-border-width)] border-divider bg-surface p-5 shadow-[var(--karry-card-shadow)]" aria-labelledby="material-conversion">
      <h2 id="material-conversion" className="text-base font-semibold">{t("portal.materials.convert")}</h2>
      <p className="text-text-muted">{t("portal.materials.convertHint")}</p>
      <div className="grid gap-4 md:grid-cols-[1fr_1fr_auto]">
        <QuantityInput
          label={t("portal.materials.quantity")}
          value={value}
          onValueChange={setValue}
          error={value.trim() !== "" && quantity === null ? t("portal.materials.numberInvalid") : undefined}
          units={units}
          unit={from.unit}
          onUnitChange={(unit) => { setFrom((side) => ({ ...side, unit })); }}
          unitGroupLabel={t("portal.materials.unitOf", { side: t("portal.materials.quantity") })}
          states={offered}
          state={source.state ?? undefined}
          onStateChange={(state) => { setFrom((side) => ({ ...side, state })); }}
          stateGroupLabel={t("portal.materials.stateOf", { side: t("portal.materials.quantity") })}
        />
        <QuantityInput
          label={t("portal.materials.result")}
          value={result && quantity !== null ? number(result.result.value, 3) : ""}
          readOnly
          aria-live="polite"
          units={units}
          unit={to.unit}
          onUnitChange={(unit) => { setTo((side) => ({ ...side, unit })); }}
          unitGroupLabel={t("portal.materials.unitOf", { side: t("portal.materials.result") })}
          states={offered}
          state={target.state ?? undefined}
          onStateChange={(state) => { setTo((side) => ({ ...side, state })); }}
          stateGroupLabel={t("portal.materials.stateOf", { side: t("portal.materials.result") })}
          factorsLabel={t("portal.materials.factors")}
          factors={result && quantity !== null ? [
            ...result.factors.map((f) => ({ name: f.name, label: t(`portal.materials.factor.${f.name}`), value: factor(f.value, f.unit) })),
            { name: "densitiesFrom", label: t("portal.materials.factor.densitiesFrom"), value: result.densitiesFrom },
          ] : undefined}
        />
        <TextField label={t("portal.materials.on")} type="date" value={on} onChange={(e) => { setOn(e.target.value); }} />
      </div>
      {!version && <p role="alert" className="font-semibold text-danger">{t("portal.materials.noVersionOn")}</p>}
    </section>
  );
}

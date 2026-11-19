import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { useState } from "react";
import type { components } from "@karry/api-client";
import { intlLocale, useTranslation } from "@karry/i18n";
import { Button, DataTable, Dialog, EmptyState, PageHeader, SelectField, StatusChip, useToast } from "@karry/ui";
import { useAccess, useApi, useWithStepUp } from "../app/api";
import { useSiteNames } from "../assets/api";
import { Trail } from "../shell/Trail";

export type Hold = components["schemas"]["HoldResponse"];

/** Holds are read with the shift entries of the same sites (ADR-0035); releasing one is a decision of its own. */
export const HoldsReadPermission = "fieldops.entries.read";
export const HoldsReleasePermission = "fieldops.holds.release";

const statuses = ["open", "released", "all"] as const;
type Status = (typeof statuses)[number];
const reasonMin = 10;
const reasonMax = 500;
const holdsKey = ["fieldops", "holds"] as const;

function useHolds(status: Status, siteId: string) {
  const api = useApi();
  const { can } = useAccess();
  return useQuery({
    queryKey: [...holdsKey, { status, siteId }],
    queryFn: async () => (await api.GET("/api/v1/fieldops/holds", { params: { query: { status, ...(siteId ? { siteId } : {}) } } })).data?.items ?? [],
    enabled: can(HoldsReadPermission),
  });
}

/**
 * Machines held as unsafe (US-P2-FO-02 AC3, ADR-0035 §5): a critical Not OK on a pre-start checklist stops the machine on
 * every tablet until a manager of its site releases it here, saying why; the decision is ledgered under their name.
 */
export function HoldsPage() {
  const { t, i18n } = useTranslation();
  const { can } = useAccess();
  const { sites, name } = useSiteNames();
  const [status, setStatus] = useState<Status>("open");
  const [siteId, setSiteId] = useState("");
  const [releasing, setReleasing] = useState<Hold | null>(null);
  const holds = useHolds(status, siteId);
  const at = (value: string) => new Intl.DateTimeFormat(intlLocale(i18n.language), { dateStyle: "medium", timeStyle: "short" }).format(new Date(value));
  const any = "any";

  return (
    <div className="flex flex-col gap-4">
      <PageHeader
        breadcrumbs={<Trail steps={[{ to: "/assets", label: t("portal.nav.assets") }]} />}
        title={t("portal.holds.title")}
        description={t("portal.holds.description")}
      />
      <div className="flex flex-wrap items-end gap-3">
        <SelectField label={t("portal.holds.status")} value={status} onValueChange={(value) => { setStatus(value as Status); }}
          options={statuses.map((value) => ({ value, label: t(`portal.holds.status.${value}`) }))} />
        <SelectField label={t("portal.holds.site")} value={siteId || any} onValueChange={(value) => { setSiteId(value === any ? "" : value); }}
          options={[{ value: any, label: t("portal.holds.anySite") }, ...sites.map((site) => ({ value: site.id, label: site.name }))]} />
      </div>
      <DataTable<Hold>
        caption={t("portal.holds.title")}
        rows={holds.data ?? []}
        rowKey={(hold) => hold.id}
        loading={holds.isPending && holds.fetchStatus !== "idle"}
        empty={<EmptyState title={t(status === "open" ? "portal.holds.noneOpen" : "portal.holds.none")} />}
        columns={[
          {
            key: "machine",
            header: t("portal.holds.machine"),
            cell: (hold) => (
              <Link to="/assets/$assetId" params={{ assetId: hold.assetId }} className="font-semibold text-primary underline-offset-4 hover:underline" translate="no">
                {hold.assetCode ?? t("portal.holds.unknownMachine")}
              </Link>
            ),
          },
          { key: "site", header: t("portal.holds.site"), cell: (hold) => name(hold.siteId) },
          { key: "items", header: t("portal.holds.items"), cell: (hold) => <span translate="no">{hold.items.join(", ")}</span> },
          { key: "since", header: t("portal.holds.since"), cell: (hold) => at(hold.openedAt) },
          {
            key: "state",
            header: t("portal.holds.state"),
            cell: (hold) => (hold.release
              ? (
                  <span className="flex flex-col gap-1">
                    <StatusChip status="ok">{t("portal.holds.releasedAt", { at: new Date(hold.release.releasedAt) })}</StatusChip>
                    <span className="text-sm text-text-muted">{hold.release.reason}</span>
                  </span>
                )
              : can(HoldsReleasePermission)
                ? <Button variant="secondary" onClick={() => { setReleasing(hold); }}>{t("portal.holds.release")}</Button>
                : <StatusChip status="error">{t("portal.holds.held")}</StatusChip>),
          },
        ]}
      />
      {releasing && <ReleaseDialog hold={releasing} onClose={() => { setReleasing(null); }} />}
    </div>
  );
}

function ReleaseDialog({ hold, onClose }: { hold: Hold; onClose: () => void }) {
  const { t } = useTranslation();
  const api = useApi();
  const withStepUp = useWithStepUp();
  const queryClient = useQueryClient();
  const toast = useToast();
  const [reason, setReason] = useState("");
  const [checked, setChecked] = useState(false);
  const length = reason.trim().length;
  const invalid = length < reasonMin || length > reasonMax;

  const release = useMutation({
    mutationFn: async () => withStepUp(async () => (await api.POST("/api/v1/fieldops/holds/{holdId}/release", {
      params: { path: { holdId: hold.id } },
      body: { reason: reason.trim() },
    })).data),
    onSuccess: async (released) => {
      if (!released) {
        return;
      }

      await queryClient.invalidateQueries({ queryKey: holdsKey });
      toast({ status: "ok", title: t("portal.holds.released", { machine: hold.assetCode ?? "" }) });
      onClose();
    },
  });

  return (
    <Dialog
      open
      onOpenChange={(open) => { if (!open) { onClose(); } }}
      title={t("portal.holds.releaseTitle", { machine: hold.assetCode ?? t("portal.holds.unknownMachine") })}
      description={t("portal.holds.releaseHint")}
      closeLabel={t("portal.holds.cancel")}
      footer={(
        <>
          <Button variant="secondary" onClick={onClose}>{t("portal.holds.cancel")}</Button>
          <Button loading={release.isPending} onClick={() => { setChecked(true); if (!invalid) { release.mutate(); } }}>{t("portal.holds.confirm")}</Button>
        </>
      )}
    >
      <label className="flex flex-col gap-2 font-medium">
        {t("portal.holds.reason")}
        <textarea value={reason} rows={4} maxLength={reasonMax} aria-invalid={checked && invalid} onChange={(e) => { setReason(e.target.value); }}
          className="rounded-lg border-[length:var(--karry-border-width)] border-divider bg-surface p-3 font-normal" />
      </label>
      {checked && invalid && <p role="alert" className="font-medium text-danger">{t("portal.holds.reasonInvalid", { min: String(reasonMin), max: String(reasonMax) })}</p>}
    </Dialog>
  );
}

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { encode } from "uqr";
import type { components } from "@karry/api-client";
import { intlLocale, useTranslation } from "@karry/i18n";
import { Button, Card, DataTable, Dialog, EmptyState, PageHeader, SelectField, StatusChip, useToast } from "@karry/ui";
import { DevicesManagePermission, useAccess, useApi } from "../api";
import { useSites } from "../sites/api";

type Device = components["schemas"]["DeviceResponse"];
type Enrollment = components["schemas"]["EnrollmentCreated"];

const devicesKey = ["identity", "devices"] as const;

/**
 * Site tablets (US-P1-OFF-02, ADR-0028): the tablets of the sites the person manages, a 15-minute enrollment code
 * (QR code and typed code) for one site, and revocation of a lost tablet.
 */
export function TabletsPage() {
  const { t, i18n } = useTranslation();
  const api = useApi();
  const { can } = useAccess();
  const manage = can(DevicesManagePermission);
  const devices = useQuery({
    queryKey: devicesKey,
    queryFn: async () => (await api.GET("/api/v1/identity/devices")).data ?? [],
    enabled: manage,
  });
  const sites = useSites(true);
  const [enrolling, setEnrolling] = useState(false);
  const [revoking, setRevoking] = useState<Device | null>(null);
  const siteLabel = (id: string) => {
    const site = sites.data?.find((s) => s.id === id);
    return site ? `${site.code} · ${site.name}` : "—";
  };
  const dateTime = (value: string | null) =>
    value ? new Intl.DateTimeFormat(intlLocale(i18n.language), { dateStyle: "medium", timeStyle: "short" }).format(new Date(value)) : "—";

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title={t("portal.tablets.title")}
        description={t("portal.tablets.description")}
        actions={manage && <Button onClick={() => { setEnrolling(true); }}>{t("portal.tablets.enrol")}</Button>}
      />
      <Card>
        <DataTable<Device>
          caption={t("portal.tablets.title")}
          rows={devices.data ?? []}
          rowKey={(d) => d.id}
          empty={<EmptyState title={devices.isPending && manage ? t("common.loading") : t("portal.tablets.empty")} />}
          columns={[
            {
              key: "name",
              header: t("portal.tablets.name"),
              cell: (d) => (
                <span className="flex flex-col">
                  <span className="font-medium">{d.name}</span>
                  {(d.model ?? d.platform) && <span className="text-sm text-text-muted">{[d.model, d.platform].filter(Boolean).join(" · ")}</span>}
                </span>
              ),
            },
            { key: "site", header: t("portal.tablets.site"), cell: (d) => siteLabel(d.siteId) },
            { key: "version", header: t("portal.tablets.appVersion"), cell: (d) => d.appVersion ?? "—" },
            { key: "enrolled", header: t("portal.tablets.enrolledAt"), cell: (d) => dateTime(d.enrolledAt) },
            { key: "seen", header: t("portal.tablets.lastSeen"), cell: (d) => dateTime(d.lastSeenAt) },
            {
              key: "status",
              header: t("portal.tablets.status"),
              cell: (d) => (
                <StatusChip status={d.status === "active" ? "ok" : "error"}>{t(`portal.tablets.status.${d.status}`, { defaultValue: d.status })}</StatusChip>
              ),
            },
            {
              key: "actions",
              header: t("portal.tablets.actions"),
              cell: (d) => d.status === "active" && (
                <Button variant="secondary" aria-label={t("portal.tablets.revokeNamed", { name: d.name })} onClick={() => { setRevoking(d); }}>
                  {t("portal.tablets.revoke")}
                </Button>
              ),
            },
          ]}
        />
      </Card>
      {enrolling && <EnrollDialog onClose={() => { setEnrolling(false); }} />}
      <RevokeDialog device={revoking} onClose={() => { setRevoking(null); }} />
    </div>
  );
}

/** Chooses the site, then shows the code once: QR code for the tablet's camera, the same code to type, and its expiry. */
function EnrollDialog({ onClose }: { onClose: () => void }) {
  const { t, i18n } = useTranslation();
  const api = useApi();
  const queryClient = useQueryClient();
  const sites = useSites(false);
  const active = (sites.data ?? []).filter((s) => s.status === "active");
  const [siteId, setSiteId] = useState<string | undefined>();
  const [expired, setExpired] = useState(false);
  const chosen = siteId ?? (active.length === 1 ? active[0]?.id : undefined);

  const create = useMutation({
    mutationFn: async (site: string) => (await api.POST("/api/v1/identity/devices/enrollments", { body: { siteId: site } })).data,
    onSuccess: () => { setExpired(false); },
  });
  const enrollment: Enrollment | undefined = create.data;

  // The code stops working at its expiry: the dialog says so instead of showing a dead QR code.
  useEffect(() => {
    if (!enrollment) {
      return undefined;
    }

    const timer = setTimeout(() => { setExpired(true); }, Math.max(0, new Date(enrollment.expiresAt).getTime() - Date.now()));
    return () => { clearTimeout(timer); };
  }, [enrollment]);

  const close = () => {
    // A tablet may have enrolled while the dialog was open.
    void queryClient.invalidateQueries({ queryKey: devicesKey });
    onClose();
  };
  const until = enrollment
    ? new Intl.DateTimeFormat(intlLocale(i18n.language), { hour: "2-digit", minute: "2-digit" }).format(new Date(enrollment.expiresAt))
    : "";

  return (
    <Dialog
      open
      onOpenChange={(open) => { if (!open) { close(); } }}
      title={t("portal.tablets.enrol")}
      description={enrollment ? t("portal.tablets.codeHint") : t("portal.tablets.enrolHint")}
      closeLabel={t("common.close")}
      footer={enrollment
        ? (
            <>
              {expired && <Button variant="secondary" loading={create.isPending} onClick={() => { create.mutate(enrollment.siteId); }}>{t("portal.tablets.newCode")}</Button>}
              <Button onClick={close}>{t("portal.tablets.done")}</Button>
            </>
          )
        : (
            <>
              <Button variant="secondary" onClick={close}>{t("portal.tablets.cancel")}</Button>
              <Button disabled={!chosen} loading={create.isPending} onClick={() => { if (chosen) { create.mutate(chosen); } }}>{t("portal.tablets.createCode")}</Button>
            </>
          )}
    >
      {!enrollment && (
        <SelectField
          label={t("portal.tablets.site")}
          options={active.map((s) => ({ value: s.id, label: `${s.code} · ${s.name}` }))}
          value={chosen}
          onValueChange={setSiteId}
          placeholder={t("portal.tablets.pickSite")}
        />
      )}
      {enrollment && !expired && (
        <div className="flex flex-col items-center gap-4">
          <QrCode value={enrollment.url} label={t("portal.tablets.qr")} />
          <p className="whitespace-nowrap text-center font-mono text-xl font-semibold tracking-wider sm:text-2xl sm:tracking-widest" translate="no" aria-label={t("portal.tablets.code")}>{enrollment.code}</p>
          <p className="text-sm text-text-muted">{t("portal.tablets.validUntil", { time: until })}</p>
          <ol className="list-decimal self-stretch ps-5 text-sm">
            <li>{t("portal.tablets.step1")}</li>
            <li>{t("portal.tablets.step2")}</li>
            <li>{t("portal.tablets.step3")}</li>
          </ol>
        </div>
      )}
      {enrollment && expired && <StatusChip status="warning">{t("portal.tablets.expired")}</StatusChip>}
    </Dialog>
  );
}

/** Revocation: the tablet is cut off at once; what it sends later is kept for review, never processed (ADR-0028 §3). */
function RevokeDialog({ device, onClose }: { device: Device | null; onClose: () => void }) {
  const { t } = useTranslation();
  const api = useApi();
  const queryClient = useQueryClient();
  const toast = useToast();
  const revoke = useMutation({
    mutationFn: async (id: string) => (await api.POST("/api/v1/identity/devices/{id}/revoke", { params: { path: { id } } })).data,
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: devicesKey });
      toast({ status: "ok", title: t("portal.tablets.revoked") });
      onClose();
    },
  });

  return (
    <Dialog
      open={device !== null}
      onOpenChange={(open) => { if (!open) { onClose(); } }}
      title={t("portal.tablets.revokeTitle", { name: device?.name ?? "" })}
      description={t("portal.tablets.revokeHint")}
      closeLabel={t("common.close")}
      footer={(
        <>
          <Button variant="secondary" onClick={onClose}>{t("portal.tablets.cancel")}</Button>
          <Button loading={revoke.isPending} onClick={() => { if (device) { revoke.mutate(device.id); } }}>{t("portal.tablets.revokeConfirm")}</Button>
        </>
      )}
    />
  );
}

/** A QR code drawn as SVG (black on white whatever the theme, with the standard quiet zone, so any camera reads it). */
export function QrCode({ value, label }: { value: string; label: string }) {
  const { data } = encode(value, { ecc: "M", border: 4 });
  const size = data.length;
  const path = data.flatMap((row, y) => row.flatMap((dark, x) => (dark ? [`M${String(x)} ${String(y)}h1v1h-1z`] : []))).join("");
  return (
    <svg role="img" aria-label={label} viewBox={`0 0 ${String(size)} ${String(size)}`} className="size-56 rounded-lg" shapeRendering="crispEdges">
      <rect width={size} height={size} fill="#fff" />
      <path d={path} fill="#000" />
    </svg>
  );
}

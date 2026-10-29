import { useMutation, useQuery } from "@tanstack/react-query";
import { useState, type SyntheticEvent } from "react";
import { useAuth } from "@karry/auth";
import { useTranslation } from "@karry/i18n";
import { Button, Card, DataTable, Dialog, EmptyState, SelectField, StatusChip, TextAreaField, TextField } from "@karry/ui";
import { useApi } from "../api";
import { tenantSupportKey, useSupportVisit, type SupportSession } from "./supportVisit";

/** The break-glass reason the API accepts (ADR-0029 §4): written, 30 to 500 characters. */
export const reasonMin = 30;
export const reasonMax = 500;

/**
 * Support access to one company (US-P1-OPS-03 AC3, ADR-0029 §4): open a session with the code the company read out,
 * or emergency access with a written reason (the company's administrators are e-mailed, and an alert pages the
 * on-call); the company's sessions, and Continue on the operator's own running one.
 */
export function SupportCard({ tenantId, tenantName }: { tenantId: string; tenantName: string }) {
  const { t, i18n } = useTranslation();
  const api = useApi();
  const { user } = useAuth();
  const { start } = useSupportVisit();
  const [opening, setOpening] = useState(false);
  const sessions = useQuery({
    queryKey: tenantSupportKey(tenantId),
    queryFn: async () => (await api.GET("/api/v1/identity/tenants/{tenantId}/support-sessions", { params: { path: { tenantId } } })).data?.items ?? [],
  });
  const dateTime = (value: string) => new Intl.DateTimeFormat(i18n.language, { dateStyle: "medium", timeStyle: "short" }).format(new Date(value));
  const visit = (session: SupportSession) => { start({ tenantId, tenantName, sessionId: session.id, endsAt: session.endsAt }); };

  return (
    <Card
      title={t("portal.operator.support.title")}
      description={t("portal.operator.support.description")}
      actions={<Button onClick={() => { setOpening(true); }}>{t("portal.operator.support.open")}</Button>}
    >
      <DataTable<SupportSession>
        caption={t("portal.support.sessions")}
        rows={sessions.data ?? []}
        rowKey={(s) => s.id}
        empty={<EmptyState title={sessions.isPending ? t("common.loading") : t("portal.support.none")} />}
        columns={[
          { key: "started", header: t("portal.support.started"), cell: (s) => <time dateTime={s.startedAt}>{dateTime(s.startedAt)}</time> },
          { key: "who", header: t("portal.support.who"), cell: (s) => <span translate="no">{s.operatorName ?? "—"}</span> },
          {
            key: "mode",
            header: t("portal.support.mode"),
            cell: (s) => (
              <span className="flex flex-col gap-1">
                <StatusChip status={s.mode === "break_glass" ? "warning" : "ok"}>{t(`portal.support.mode.${s.mode}`, { defaultValue: s.mode })}</StatusChip>
                {s.reason && <span className="text-sm text-text-muted" translate="no">{s.reason}</span>}
              </span>
            ),
          },
          { key: "status", header: t("portal.support.status"), cell: (s) => <StatusChip status={s.active ? "error" : "pending"}>{t(s.active ? "portal.support.active" : "portal.support.over")}</StatusChip> },
          {
            key: "actions",
            header: t("portal.support.actions"),
            // Only the operator's own session can be continued (the API refuses anyone else's).
            cell: (s) => s.active && s.operatorName === user?.name && (
              <Button variant="secondary" aria-label={t("portal.operator.support.continueNamed", { time: dateTime(s.startedAt) })} onClick={() => { visit(s); }}>
                {t("portal.operator.support.continue")}
              </Button>
            ),
          },
        ]}
      />
      {opening && <OpenDialog tenantId={tenantId} onOpened={visit} onClose={() => { setOpening(false); }} />}
    </Card>
  );
}

/** With the company's code, or emergency access with a reason; the visit starts as soon as the API opens the session. */
function OpenDialog({ tenantId, onOpened, onClose }: { tenantId: string; onOpened: (session: SupportSession) => void; onClose: () => void }) {
  const { t } = useTranslation();
  const api = useApi();
  const [mode, setMode] = useState<"consent" | "break_glass">("consent");
  const [code, setCode] = useState("");
  const [reason, setReason] = useState("");
  const [checked, setChecked] = useState(false);
  const trimmed = reason.trim();
  const codeError = checked && mode === "consent" && code.trim() === "" ? t("portal.operator.support.codeRequired") : undefined;
  const reasonError = checked && mode === "break_glass" && (trimmed.length < reasonMin || trimmed.length > reasonMax)
    ? t("portal.operator.support.reasonLength", { min: reasonMin, max: reasonMax })
    : undefined;
  const open = useMutation({
    mutationFn: async () => (await api.POST("/api/v1/identity/tenants/{tenantId}/support-sessions", {
      params: { path: { tenantId } },
      body: mode === "consent" ? { code: code.trim().toUpperCase(), reason: null } : { code: null, reason: trimmed },
    })).data,
    onSuccess: (session) => {
      if (session) {
        onOpened(session);
      }
    },
  });
  const submit = (event: SyntheticEvent) => {
    event.preventDefault();
    setChecked(true);
    const valid = mode === "consent" ? code.trim() !== "" : trimmed.length >= reasonMin && trimmed.length <= reasonMax;
    if (valid) {
      open.mutate();
    }
  };

  return (
    <Dialog
      open
      onOpenChange={(next) => { if (!next) { onClose(); } }}
      title={t("portal.operator.support.open")}
      description={t("portal.operator.support.openHint")}
      closeLabel={t("common.close")}
      footer={(
        <>
          <Button variant="secondary" onClick={onClose}>{t("common.cancel")}</Button>
          <Button onClick={submit} variant={mode === "break_glass" ? "danger" : "primary"} loading={open.isPending}>
            {t(mode === "break_glass" ? "portal.operator.support.openEmergency" : "portal.operator.support.open")}
          </Button>
        </>
      )}
    >
      <form noValidate onSubmit={submit} className="flex flex-col gap-4">
        <SelectField
          label={t("portal.operator.support.how")}
          value={mode}
          onValueChange={(value) => { setMode(value === "break_glass" ? "break_glass" : "consent"); setChecked(false); }}
          options={[
            { value: "consent", label: t("portal.operator.support.withCode") },
            { value: "break_glass", label: t("portal.support.mode.break_glass") },
          ]}
        />
        {mode === "consent"
          ? (
            <TextField
              label={t("portal.support.code")}
              description={t("portal.operator.support.codeHint")}
              value={code}
              onChange={(event) => { setCode(event.target.value); }}
              autoComplete="off"
              spellCheck={false}
              className="font-mono uppercase tracking-wider"
              error={codeError}
              required
            />
          )
          : (
            <>
              <p role="note" className="rounded-lg border-[length:var(--karry-border-width)] border-warning bg-warning-soft p-3 text-sm">{t("portal.operator.support.emergencyWarning")}</p>
              <TextAreaField
                label={t("portal.operator.support.reason")}
                description={t("portal.operator.support.reasonHint", { min: reasonMin, count: trimmed.length })}
                value={reason}
                onChange={(event) => { setReason(event.target.value); }}
                maxLength={reasonMax}
                error={reasonError}
                required
              />
            </>
          )}
      </form>
    </Dialog>
  );
}

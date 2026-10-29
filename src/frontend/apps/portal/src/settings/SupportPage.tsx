import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import type { components } from "@karry/api-client";
import { useTranslation } from "@karry/i18n";
import { Button, Card, DataTable, Dialog, EmptyState, PageHeader, StatusChip, useToast } from "@karry/ui";
import { SupportManagePermission, useAccess, useApi } from "../api";

type Session = components["schemas"]["SupportSessionResponse"];
type Consent = components["schemas"]["SupportConsentResponse"];

const sessionsKey = ["identity", "support", "sessions"] as const;

/**
 * Support access (US-P1-OPS-03 AC3, ADR-0029 §4) for the Tenant Admin: a 30-minute code to read to Karry support,
 * the company's support sessions (with a code, or emergency access with Karry's written reason) and "End now".
 * Karry support only looks, sees costs masked, for one hour; every request is in the audit log.
 */
export function SupportPage() {
  const { t, i18n } = useTranslation();
  const api = useApi();
  const { can } = useAccess();
  const manage = can(SupportManagePermission);
  const sessions = useQuery({
    queryKey: sessionsKey,
    queryFn: async () => (await api.GET("/api/v1/identity/support/sessions")).data?.items ?? [],
    enabled: manage,
  });
  const [creating, setCreating] = useState(false);
  const [ending, setEnding] = useState<Session | null>(null);
  const dateTime = (value: string) => new Intl.DateTimeFormat(i18n.language, { dateStyle: "medium", timeStyle: "short" }).format(new Date(value));

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        title={t("portal.support.title")}
        description={t("portal.support.description")}
        actions={manage && <Button onClick={() => { setCreating(true); }}>{t("portal.support.createCode")}</Button>}
      />
      <Card title={t("portal.support.rulesTitle")}>
        <ul className="list-disc ps-5 text-sm">
          <li>{t("portal.support.ruleReadOnly")}</li>
          <li>{t("portal.support.ruleMasked")}</li>
          <li>{t("portal.support.ruleHour")}</li>
          <li>{t("portal.support.ruleAudit")}</li>
          <li>{t("portal.support.ruleEmergency")}</li>
        </ul>
      </Card>
      <Card>
        <DataTable<Session>
          caption={t("portal.support.sessions")}
          rows={sessions.data ?? []}
          rowKey={(s) => s.id}
          empty={<EmptyState title={sessions.isPending && manage ? t("common.loading") : t("portal.support.none")} />}
          columns={[
            { key: "started", header: t("portal.support.started"), cell: (s) => <time dateTime={s.startedAt}>{dateTime(s.startedAt)}</time> },
            { key: "who", header: t("portal.support.who"), cell: (s) => (s.operatorName ? <span translate="no">{s.operatorName}</span> : t("portal.support.karry")) },
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
            { key: "until", header: t("portal.support.until"), cell: (s) => <time dateTime={s.endedAt ?? s.endsAt}>{dateTime(s.endedAt ?? s.endsAt)}</time> },
            {
              key: "status",
              header: t("portal.support.status"),
              cell: (s) => <StatusChip status={s.active ? "error" : "pending"}>{t(s.active ? "portal.support.active" : "portal.support.over")}</StatusChip>,
            },
            {
              key: "actions",
              header: t("portal.support.actions"),
              cell: (s) => s.active && (
                <Button variant="danger" aria-label={t("portal.support.endNamed", { time: dateTime(s.startedAt) })} onClick={() => { setEnding(s); }}>
                  {t("portal.support.endNow")}
                </Button>
              ),
            },
          ]}
        />
      </Card>
      {creating && <CodeDialog onClose={() => { setCreating(false); }} />}
      <EndDialog session={ending} onClose={() => { setEnding(null); }} />
    </div>
  );
}

/** The code, shown once, with its expiry; past it, the dialog says so instead of showing a dead code. */
function CodeDialog({ onClose }: { onClose: () => void }) {
  const { t, i18n } = useTranslation();
  const api = useApi();
  const queryClient = useQueryClient();
  const [expired, setExpired] = useState(false);
  const create = useMutation({
    mutationFn: async () => (await api.POST("/api/v1/identity/support/consents")).data,
    onSuccess: () => { setExpired(false); },
  });
  const consent: Consent | undefined = create.data;

  useEffect(() => {
    if (!consent) {
      return undefined;
    }

    const timer = setTimeout(() => { setExpired(true); }, Math.max(0, new Date(consent.expiresAt).getTime() - Date.now()));
    return () => { clearTimeout(timer); };
  }, [consent]);

  const close = () => {
    // Support may have opened a session with the code while the dialog was open.
    void queryClient.invalidateQueries({ queryKey: sessionsKey });
    onClose();
  };
  const until = consent ? new Intl.DateTimeFormat(i18n.language, { hour: "2-digit", minute: "2-digit" }).format(new Date(consent.expiresAt)) : "";

  return (
    <Dialog
      open
      onOpenChange={(open) => { if (!open) { close(); } }}
      title={t("portal.support.createCode")}
      description={consent ? t("portal.support.codeHint") : t("portal.support.createHint")}
      closeLabel={t("common.close")}
      footer={consent
        ? (
            <>
              {expired && <Button variant="secondary" loading={create.isPending} onClick={() => { create.mutate(); }}>{t("portal.support.newCode")}</Button>}
              <Button onClick={close}>{t("portal.support.done")}</Button>
            </>
          )
        : (
            <>
              <Button variant="secondary" onClick={close}>{t("common.cancel")}</Button>
              <Button loading={create.isPending} onClick={() => { create.mutate(); }}>{t("portal.support.createConfirm")}</Button>
            </>
          )}
    >
      {consent && !expired && (
        <div className="flex flex-col items-center gap-3">
          <p className="whitespace-nowrap text-center font-mono text-xl font-semibold tracking-wider sm:text-2xl sm:tracking-widest" translate="no" aria-label={t("portal.support.code")}>{consent.code}</p>
          <p className="text-sm text-text-muted">{t("portal.support.validUntil", { time: until })}</p>
        </div>
      )}
      {consent && expired && <StatusChip status="warning">{t("portal.support.expired")}</StatusChip>}
    </Dialog>
  );
}

/** Ends the session now: Karry support loses access at its next request. */
function EndDialog({ session, onClose }: { session: Session | null; onClose: () => void }) {
  const { t } = useTranslation();
  const api = useApi();
  const queryClient = useQueryClient();
  const toast = useToast();
  const end = useMutation({
    mutationFn: async (id: string) => (await api.POST("/api/v1/identity/support/sessions/{id}/end", { params: { path: { id } } })).data,
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: sessionsKey });
      toast({ status: "ok", title: t("portal.support.ended") });
      onClose();
    },
  });

  return (
    <Dialog
      open={session !== null}
      onOpenChange={(open) => { if (!open) { onClose(); } }}
      title={t("portal.support.endTitle")}
      description={t("portal.support.endHint")}
      closeLabel={t("common.close")}
      footer={(
        <>
          <Button variant="secondary" onClick={onClose}>{t("common.cancel")}</Button>
          <Button variant="danger" loading={end.isPending} onClick={() => { if (session) { end.mutate(session.id); } }}>{t("portal.support.endNow")}</Button>
        </>
      )}
    />
  );
}

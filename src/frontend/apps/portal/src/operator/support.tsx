import { useCallback, useEffect, useMemo, useState, type ReactNode } from "react";
import { LifeBuoy } from "lucide-react";
import { useTranslation } from "@karry/i18n";
import { Button, useToast } from "@karry/ui";
import { useApi } from "../api";
import { savedVisit, saveVisit, SupportVisitContext, useSupportVisit, type LeaveReason, type SupportVisit } from "./supportVisit";

export function SupportVisitProvider({ children }: { children: ReactNode }) {
  const { t } = useTranslation();
  const toast = useToast();
  const [visit, setVisit] = useState(savedVisit);
  const [lastCompany, setLastCompany] = useState<string>();
  const start = useCallback((next: SupportVisit) => {
    saveVisit(next);
    setVisit(next);
  }, []);
  const leave = useCallback((reason: LeaveReason) => {
    setVisit((current) => {
      if (current) {
        setLastCompany(current.tenantId);
      }

      return null;
    });
    saveVisit(null);
    toast({ status: reason === "ended" ? "ok" : "warning", title: t(`portal.operator.support.left.${reason}`) });
  }, [t, toast]);
  const value = useMemo(() => ({ visit, lastCompany, start, leave }), [visit, lastCompany, start, leave]);
  return <SupportVisitContext value={value}>{children}</SupportVisitContext>;
}

/**
 * Always on screen during a visit, and cannot be closed: whose screens these are, that they are read only, when the
 * session ends, and End. At the end of the hour the visit stops by itself.
 */
export function SupportBanner({ visit }: { visit: SupportVisit }) {
  const { t, i18n } = useTranslation();
  const api = useApi();
  const { leave } = useSupportVisit();
  const [ending, setEnding] = useState(false);
  // Outside the company's data layer (its cache belongs to the visit), so a plain call. Already ended or gone: the
  // visit is over either way.
  const end = () => {
    setEnding(true);
    void api.POST("/api/v1/identity/tenants/{tenantId}/support-sessions/{id}/end", { params: { path: { tenantId: visit.tenantId, id: visit.sessionId } } })
      .catch(() => undefined)
      .finally(() => { leave("ended"); });
  };

  useEffect(() => {
    const timer = setTimeout(() => { leave("over"); }, Math.max(0, new Date(visit.endsAt).getTime() - Date.now()));
    return () => { clearTimeout(timer); };
  }, [visit.endsAt, leave]);

  const until = new Intl.DateTimeFormat(i18n.language, { hour: "2-digit", minute: "2-digit" }).format(new Date(visit.endsAt));
  return (
    <aside
      aria-label={t("portal.operator.support.banner")}
      className="fixed inset-x-0 bottom-0 z-50 flex flex-wrap items-center justify-between gap-2 bg-danger px-4 py-2 text-sm font-medium text-danger-text shadow-lg md:px-6"
    >
      <p className="flex min-w-0 items-center gap-2">
        <LifeBuoy aria-hidden className="size-4 shrink-0" />
        <span>{t("portal.operator.support.bannerText", { company: visit.tenantName, time: until })}</span>
      </p>
      <Button variant="secondary" loading={ending} onClick={end}>{t("portal.operator.support.end")}</Button>
    </aside>
  );
}

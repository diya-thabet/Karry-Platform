import { Link } from "@tanstack/react-router";
import { Construction, SearchX } from "lucide-react";
import { useTranslation } from "@karry/i18n";
import { EmptyState, PageHeader } from "@karry/ui";
import type { SectionKey } from "./sections";

/** A section whose screens arrive in a later phase. */
export function ComingSoon({ titleKey }: { titleKey: SectionKey }) {
  const { t } = useTranslation();
  return (
    <div className="flex flex-col gap-6">
      <PageHeader title={t(titleKey)} />
      <EmptyState icon={Construction} title={t("portal.comingSoon")}>{t("portal.comingSoonHint")}</EmptyState>
    </div>
  );
}

export function NotFound() {
  const { t } = useTranslation();
  return <EmptyState icon={SearchX} title={t("portal.notFound")} action={<Link to="/" className="text-sm font-medium text-primary hover:underline">{t("portal.backHome")}</Link>} />;
}

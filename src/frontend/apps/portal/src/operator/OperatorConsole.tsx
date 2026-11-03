import { Link, Outlet } from "@tanstack/react-router";
import { useTranslation } from "@karry/i18n";
import { EmptyState } from "@karry/ui";
import { AccountMenu } from "../shell/AccountMenu";

/**
 * The operator console's shell (ADR-0029 §5): Karry staff on the platform address, with no company. No company
 * branding, navigation, plan or usage banners: those belong to a company's portal.
 */
export function OperatorLayout() {
  const { t } = useTranslation();
  return (
    <div className="min-h-screen bg-background text-text">
      <header className="sticky top-0 z-30 border-b-[length:var(--karry-border-width)] border-divider bg-surface/95 backdrop-blur">
        <div className="flex h-16 items-center justify-between gap-2 px-3 md:px-5">
          <Link to="/operator" className="flex items-center gap-2 font-semibold">
            <span translate="no">Karry</span>
            <span className="rounded-full bg-primary-soft px-2 py-0.5 text-xs font-medium text-primary">{t("portal.operator.console")}</span>
          </Link>
          <AccountMenu />
        </div>
      </header>
      <main className="mx-auto w-full max-w-7xl px-4 py-5 sm:py-6 md:px-8 md:py-8">
        <Outlet />
      </main>
    </div>
  );
}

export function OperatorNotFound() {
  const { t } = useTranslation();
  return <EmptyState title={t("portal.notFound")} action={<Link to="/operator" className="text-sm font-medium text-primary hover:underline">{t("portal.operator.backToCompanies")}</Link>} />;
}

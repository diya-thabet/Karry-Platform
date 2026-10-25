import { Link } from "@tanstack/react-router";
import { useTranslation } from "@karry/i18n";
import { EmptyState } from "@karry/ui";
import { BillingReadPermission, useAccess } from "../api";

/** Company settings: only the screens the person may use (the API checks every call anyway). */
export function SettingsPage() {
  const { t } = useTranslation();
  const { loaded, can } = useAccess();
  const links = [
    { to: "/settings/roles", label: t("portal.roles.title"), hint: t("portal.settings.rolesHint"), permission: "identity.roles.read" },
    { to: "/settings/users", label: t("portal.users.title"), hint: t("portal.settings.usersHint"), permission: "identity.users.read" },
    { to: "/settings/plan", label: t("portal.plan.title"), hint: t("portal.settings.planHint"), permission: BillingReadPermission },
  ].filter((link) => can(link.permission));

  return (
    <div className="flex flex-col gap-4">
      <h1 className="text-2xl font-bold">{t("portal.nav.settings")}</h1>
      {loaded && links.length === 0 ? (
        <EmptyState title={t("portal.settings.nothing")} />
      ) : (
        <ul className="grid gap-4 md:grid-cols-2">
          {links.map((link) => (
            <li key={link.to} className="rounded-md border-[length:var(--karry-border-width)] border-border bg-surface p-4">
              <Link to={link.to} className="text-lg font-semibold text-primary underline-offset-4 hover:underline">{link.label}</Link>
              <p className="text-text-muted">{link.hint}</p>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

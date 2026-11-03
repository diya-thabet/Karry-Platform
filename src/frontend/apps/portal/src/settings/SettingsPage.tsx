import { Link } from "@tanstack/react-router";
import { Building2, CreditCard, FileSpreadsheet, Layers, LifeBuoy, ShieldCheck, Tablet, Users } from "lucide-react";
import { useTranslation } from "@karry/i18n";
import { PageHeader } from "@karry/ui";
import { BillingReadPermission, DevicesManagePermission, SupportManagePermission, useAccess } from "../app/api";
import { ImportsPermission } from "../imports/api";
import { MaterialsReadPermission } from "../materials/api";

/** Settings: the company's (read-only without the permission) and only the other screens the person may use (the API checks every call anyway). */
export function SettingsPage() {
  const { t } = useTranslation();
  const { can } = useAccess();
  const links = [
    { to: "/settings/company", icon: Building2, label: t("portal.company.title"), hint: t("portal.settings.companyHint"), permission: null },
    { to: "/settings/roles", icon: ShieldCheck, label: t("portal.roles.title"), hint: t("portal.settings.rolesHint"), permission: "identity.roles.read" },
    { to: "/settings/users", icon: Users, label: t("portal.users.title"), hint: t("portal.settings.usersHint"), permission: "identity.users.read" },
    { to: "/settings/tablets", icon: Tablet, label: t("portal.tablets.title"), hint: t("portal.settings.tabletsHint"), permission: DevicesManagePermission },
    { to: "/settings/materials", icon: Layers, label: t("portal.materials.title"), hint: t("portal.settings.materialsHint"), permission: MaterialsReadPermission },
    { to: "/settings/imports", icon: FileSpreadsheet, label: t("portal.imports.title"), hint: t("portal.settings.importsHint"), permission: ImportsPermission },
    { to: "/settings/support", icon: LifeBuoy, label: t("portal.support.title"), hint: t("portal.settings.supportHint"), permission: SupportManagePermission },
    { to: "/settings/plan", icon: CreditCard, label: t("portal.plan.title"), hint: t("portal.settings.planHint"), permission: BillingReadPermission },
  ].filter((link) => link.permission === null || can(link.permission));

  return (
    <div className="flex flex-col gap-6">
      <PageHeader title={t("portal.nav.settings")} description={t("portal.settings.description")} />
      <ul className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
        {links.map((link) => {
          const Icon = link.icon;
          return (
            <li key={link.to} className="group relative flex h-full items-start gap-4 rounded-xl border-[length:var(--karry-border-width)] border-divider bg-surface p-5 shadow-[var(--karry-card-shadow)] hover:border-primary">
              <span className="flex size-10 shrink-0 items-center justify-center rounded-lg bg-primary-soft text-primary">
                <Icon aria-hidden className="size-5" />
              </span>
              <span className="flex flex-col gap-1">
                {/* The whole card is clickable; the link's name stays the screen's name. */}
                <Link to={link.to} className="font-semibold text-text after:absolute after:inset-0 after:rounded-xl group-hover:text-primary">{link.label}</Link>
                <span className="text-sm text-text-muted">{link.hint}</span>
              </span>
            </li>
          );
        })}
      </ul>
    </div>
  );
}

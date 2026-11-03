import { Link, type LinkProps } from "@tanstack/react-router";
import { useTranslation } from "@karry/i18n";
import { Breadcrumbs } from "@karry/ui";

export interface TrailStep {
  to: NonNullable<LinkProps["to"]>;
  label: string;
}

/** The breadcrumbs of a page below a section (UX-04): the sections above it, from the top down, as links. */
export function Trail({ steps }: { steps: TrailStep[] }) {
  const { t } = useTranslation();
  return (
    <Breadcrumbs label={t("portal.nav.breadcrumbs")}>
      {steps.map((step) => <Link key={step.to} to={step.to}>{step.label}</Link>)}
    </Breadcrumbs>
  );
}

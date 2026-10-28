import { useCallback, useMemo, useState, type ReactNode } from "react";
import { ApiError } from "@karry/api-client";
import { useAuth } from "@karry/auth";
import { useTranslation } from "@karry/i18n";
import { Button, Dialog, ErrorState } from "@karry/ui";
import { ReportContext } from "./reportError";

/** Problems the person can act on get their own words; any other shows the generic text and the support reference. */
const explained = new Set([
  "auth.step_up_required",
  "billing.feature_not_in_plan",
  "billing.limit_reached",
  "billing.subscription_restricted",
  "billing.subscription_suspended",
  "billing.subscription_inactive",
  "tenant.host_mismatch",
  "tenant.legal_name_invalid",
  "tenant.colour_invalid",
  "tenant.colour_contrast",
  "tenant.language_unsupported",
  "tenant.currency_unsupported",
  "tenant.unit_system_invalid",
  "tenant.time_zone_unknown",
  "tenant.fiscal_month_invalid",
  "tenant.logo_too_large",
  "tenant.logo_type_unsupported",
  "identity.escalation",
  "identity.scope_exceeded",
  "identity.role_in_use",
  "identity.role_exists",
  "identity.cannot_change_own_roles",
  "identity.role_needs_account",
  "identity.role_name_invalid",
  "identity.pin_invalid",
  "identity.pin_too_simple",
  "identity.not_field_user",
  "identity.user_deactivated",
  "organization.site_code_invalid",
  "organization.site_code_taken",
  "organization.site_code_fixed",
  "organization.site_name_invalid",
  "organization.site_type_invalid",
  "organization.site_address_invalid",
  "organization.site_location_invalid",
  "organization.boundary_invalid",
  "organization.boundary_not_closed",
  "organization.boundary_self_intersecting",
  "organization.boundary_too_large",
  "organization.time_zone_unknown",
  "organization.currency_unsupported",
  "organization.site_archived",
  "organization.site_not_managed",
  "organization.shifts_invalid",
  "organization.shift_code_invalid",
  "organization.shift_code_duplicate",
  "organization.shift_name_invalid",
  "organization.shift_time_invalid",
  "organization.shift_days_invalid",
  "organization.shifts_overlap",
  "organization.day_kind_invalid",
  "organization.day_name_invalid",
  "organization.date_in_past",
  "organization.range_invalid",
  "materials.code_invalid",
  "materials.material_code_taken",
  "materials.name_invalid",
  "materials.kind_invalid",
  "materials.density_required",
  "materials.density_invalid",
  "materials.moisture_invalid",
  "materials.swell_invalid",
  "materials.material_archived",
  "materials.date_in_past",
  "materials.density_version_not_found",
  "materials.last_density_version",
  "materials.no_density_version",
  "assets.code_invalid",
  "assets.asset_code_taken",
  "assets.name_invalid",
  "assets.type_invalid",
  "assets.text_too_long",
  "assets.year_invalid",
  "assets.acquired_on_invalid",
  "assets.site_invalid",
  "assets.site_forbidden",
  "assets.asset_disposed",
  "assets.photo_type_unsupported",
  "assets.photo_too_large",
  "assets.photos_unavailable",
  "assets.label_sheet_invalid",
  "assets.installed_on_invalid",
  "assets.reading_invalid",
  "assets.reading_decreased",
  "assets.reading_before_last",
  "assets.reading_in_future",
  "assets.meter_not_counting",
  "assets.meter_changed",
  "assets.meter_replacement_unavailable",
  "imports.file_type_unsupported",
  "imports.file_too_large",
  "imports.file_too_many_rows",
  "imports.file_active_content",
  "imports.file_unreadable",
  "imports.file_missing",
  "imports.file_name_invalid",
  "imports.kind_unknown",
  "imports.kind_forbidden",
  "imports.job_has_errors",
  "imports.job_not_validated",
  "imports.job_not_discardable",
  "imports.storage_unavailable",
  "units.not_convertible",
  "units.density_missing",
  "units.quantity_invalid",
]);

/**
 * One place where failed actions surface (US-P1-OPS-01 AC3): a dialog with the support reference, i.e. the
 * correlation ID the API returned. Errors without one (network down) show the same dialog without a reference.
 * An account of another company on this address (`tenant.host_mismatch`) gets a notice and a way to sign out.
 */
export function ErrorDialogProvider({ children }: { children: ReactNode }) {
  const { t } = useTranslation();
  const { signOut } = useAuth();
  const [error, setError] = useState<unknown>(null);
  const code = error instanceof ApiError && explained.has(error.code) ? error.code : undefined;
  const mismatch = code === "tenant.host_mismatch";
  const report = useCallback((reported: unknown) => { setError(reported); }, []);
  const value = useMemo(() => report, [report]);

  return (
    <ReportContext value={value}>
      {children}
      <Dialog
        open={error !== null}
        onOpenChange={(open) => { if (!open) setError(null); }}
        title={mismatch ? t("errors.tenant.host_mismatch.title") : t("common.error.title")}
        description={code ? t(`errors.${code}`) : t("common.error.body")}
        closeLabel={t("common.close")}
        footer={
          <>
            {mismatch && <Button onClick={() => void signOut()}>{t("common.error.signOut")}</Button>}
            <Button variant="secondary" onClick={() => { setError(null); }}>{t("common.close")}</Button>
          </>
        }
      >
        {!code && <ErrorState correlationId={error instanceof ApiError ? error.correlationId : undefined} />}
      </Dialog>
    </ReportContext>
  );
}

import { Button } from "@karry/ui";
import { useTranslation } from "@karry/i18n";

/**
 * Shown in a form whose save was refused: the person's entries stay in the form. They choose to save them over the
 * other change, or to see the other change instead (their entries are then dropped). Nothing is ever overwritten
 * without that choice.
 */
export function VersionConflict({ onKeepMine, onShowTheirs, busy }: { onKeepMine: () => void; onShowTheirs: () => void; busy?: boolean }) {
  const { t } = useTranslation();
  return (
    <div role="alert" className="flex flex-col gap-3 rounded-lg border-[length:var(--karry-border-width)] border-warning bg-warning-soft p-4">
      <p className="font-medium">{t("portal.conflict.title")}</p>
      <p className="text-text-muted">{t("portal.conflict.body")}</p>
      <div className="flex flex-wrap gap-2">
        <Button type="button" onClick={onKeepMine} disabled={busy}>{t("portal.conflict.keepMine")}</Button>
        <Button type="button" variant="secondary" onClick={onShowTheirs} disabled={busy}>{t("portal.conflict.showTheirs")}</Button>
      </div>
    </div>
  );
}

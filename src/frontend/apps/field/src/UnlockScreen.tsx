import { useState } from "react";
import { LockKeyhole } from "lucide-react";
import { useTranslation } from "@karry/i18n";
import { Button, TextField } from "@karry/ui";
import type { Session, UnlockFailure } from "./unlock";

/**
 * The locked tablet (US-P1-OFF-04 AC1): whoever picks it up types their employee ID and PIN. The PIN is cleared after
 * every attempt; the ID stays so a wrong PIN is quick to retry.
 */
export function Unlock({ unlock, onUnlocked }: {
  unlock: (employeeId: string, pin: string) => Promise<Session | UnlockFailure>;
  onUnlocked: (session: Session) => void;
}) {
  const { t } = useTranslation();
  const [employeeId, setEmployeeId] = useState("");
  const [pin, setPin] = useState("");
  const [failure, setFailure] = useState<UnlockFailure | null>(null);
  const [busy, setBusy] = useState(false);

  async function submit() {
    setBusy(true);
    setFailure(null);
    const result = await unlock(employeeId, pin);
    setPin("");
    setBusy(false);
    if (typeof result === "string") {
      setFailure(result);
    } else {
      onUnlocked(result);
    }
  }

  return (
    <main className="mx-auto flex min-h-screen max-w-xl flex-col gap-6 p-4">
      <header className="flex flex-col gap-2">
        <LockKeyhole aria-hidden className="size-12 text-primary" />
        <h1 className="text-3xl font-bold">{t("field.unlock.title")}</h1>
        <p className="text-text-muted">{t("field.unlock.intro")}</p>
      </header>
      <form className="flex flex-col gap-5" onSubmit={(event) => { event.preventDefault(); void submit(); }}>
        <TextField label={t("field.unlock.employeeId")} value={employeeId} onChange={(e) => { setEmployeeId(e.target.value); }}
          required autoCapitalize="characters" autoComplete="off" spellCheck={false} className="font-mono uppercase" />
        <TextField label={t("field.unlock.pin")} value={pin} onChange={(e) => { setPin(e.target.value); }}
          required type="password" inputMode="numeric" autoComplete="off" maxLength={12} className="font-mono tracking-widest" />
        {failure && <p role="alert" className="font-medium text-danger">{t(`field.unlock.failure.${failure}`)}</p>}
        <Button type="submit" block disabled={busy} className="min-h-16 text-xl">
          {busy ? t("field.unlock.busy") : t("field.unlock.submit")}
        </Button>
      </form>
    </main>
  );
}

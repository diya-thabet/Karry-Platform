import { useState } from "react";
import { TabletSmartphone } from "lucide-react";
import { useTranslation } from "@karry/i18n";
import type { DeviceRecord } from "@karry/offline";
import { Button, TextField } from "@karry/ui";
import type { EnrollFailure } from "./tablet";

/**
 * "Enrol this tablet" (US-P1-OFF-02 AC1): the code from the portal's QR (opened as `…/enroll#<code>`, so it is already
 * filled in) or typed, and a name for the tablet list. The company comes from the address; only a bare local address
 * asks for it.
 */
export function Enroll({ company, code: scanned, enroll }: {
  company: string | null;
  code: string | null;
  enroll: (input: { company: string; code: string; name: string }) => Promise<DeviceRecord | EnrollFailure>;
}) {
  const { t } = useTranslation();
  const [companyText, setCompanyText] = useState("");
  const [code, setCode] = useState(scanned ?? "");
  const [name, setName] = useState("");
  const [failure, setFailure] = useState<EnrollFailure | null>(null);
  const [busy, setBusy] = useState(false);

  async function submit() {
    setBusy(true);
    setFailure(null);
    const result = await enroll({ company: company ?? companyText, code, name });
    if (typeof result === "string") {
      setFailure(result);
      setBusy(false);
    }
  }

  return (
    <main className="mx-auto flex min-h-screen max-w-xl flex-col gap-6 p-4">
      <header className="flex flex-col gap-2">
        <TabletSmartphone aria-hidden className="size-12 text-primary" />
        <h1 className="text-3xl font-bold">{t("field.enroll.title")}</h1>
        <p className="text-text-muted">{t("field.enroll.intro")}</p>
      </header>
      <form className="flex flex-col gap-5" onSubmit={(event) => { event.preventDefault(); void submit(); }}>
        {company === null && (
          <TextField label={t("field.enroll.company")} description={t("field.enroll.companyHelp")} value={companyText}
            onChange={(e) => { setCompanyText(e.target.value); }} required autoCapitalize="none" autoComplete="off" spellCheck={false} />
        )}
        <TextField label={t("field.enroll.code")} description={t("field.enroll.codeHelp")} value={code}
          onChange={(e) => { setCode(e.target.value); }} required autoCapitalize="characters" autoComplete="off" spellCheck={false}
          className="font-mono uppercase tracking-widest" />
        <TextField label={t("field.enroll.name")} description={t("field.enroll.nameHelp")} value={name}
          onChange={(e) => { setName(e.target.value); }} required maxLength={60} />
        {failure && <p role="alert" className="font-medium text-danger">{t(`field.enroll.failure.${failure}`)}</p>}
        <Button type="submit" block disabled={busy} className="min-h-16 text-xl">
          {busy ? t("field.enroll.busy") : t("field.enroll.submit")}
        </Button>
      </form>
    </main>
  );
}

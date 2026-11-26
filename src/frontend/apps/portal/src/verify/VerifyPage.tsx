import { useEffect, useState, type DragEvent } from "react";
import { ApiError, type ApiClient } from "@karry/api-client";
import { intlLocale, useTranslation } from "@karry/i18n";
import { Card, EmptyState, ErrorState, LanguageSwitcher, StatusChip } from "@karry/ui";
import { FileSearch, ShieldCheck } from "lucide-react";
import { sha256Of, type VerifiedDocument, type VerifyAddress } from "./verify";

type Answer =
  | { state: "checking" }
  | { state: "verified"; document: VerifiedDocument }
  | { state: "not-found" }
  | { state: "limited" }
  | { state: "failed"; correlationId?: string | undefined };

type PdfCheck = { state: "idle" } | { state: "hashing" } | { state: "matches" } | { state: "differs" } | { state: "failed" };

async function verify(api: ApiClient, address: VerifyAddress, pdfHash?: string): Promise<VerifiedDocument> {
  const { data } = await api.POST("/api/v1/documents/verify/{envelopeId}", {
    params: { path: { envelopeId: address.envelopeId } },
    body: { token: address.token, pdfHash: pdfHash ?? null },
  });
  if (!data) {
    throw new Error("The verification answer has no body.");
  }

  return data;
}

function failure(error: unknown): Answer {
  if (error instanceof ApiError && error.code === "documents.not_found") {
    return { state: "not-found" };
  }

  if (error instanceof ApiError && error.status === 429) {
    return { state: "limited" };
  }

  return { state: "failed", correlationId: error instanceof ApiError ? error.correlationId : undefined };
}

/**
 * The public verification page (ADR-0040 §4, US-P2-SIG-06): opened from a document's QR, without sign-in. The token
 * comes from the address's fragment and is posted, never put in a URL. "Verified" shows what the document is and
 * whose; a dropped PDF is hashed here and compared with the issued one (TC-P2-DOC-002), the file never leaves the device.
 */
export function VerifyPage({ api, address }: { api: ApiClient; address: VerifyAddress }) {
  const { t } = useTranslation();
  const [answer, setAnswer] = useState<Answer>({ state: "checking" });
  const [pdf, setPdf] = useState<PdfCheck>({ state: "idle" });

  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    let live = true;
    verify(api, address).then(
      (document) => { if (live) setAnswer({ state: "verified", document }); },
      (error: unknown) => { if (live) setAnswer(failure(error)); },
    );
    return () => { live = false; };
  }, [api, address, attempt]);

  function retry() {
    setAnswer({ state: "checking" });
    setAttempt((n) => n + 1);
  }

  async function comparePdf(file: File | undefined) {
    if (!file) {
      return;
    }

    setPdf({ state: "hashing" });
    try {
      const document = await verify(api, address, await sha256Of(file));
      setPdf({ state: document.pdfMatches ? "matches" : "differs" });
    } catch {
      setPdf({ state: "failed" });
    }
  }

  return (
    <main className="mx-auto flex min-h-screen max-w-2xl flex-col gap-6 px-4 py-8">
      <header className="flex flex-wrap items-center justify-between gap-4">
        <h1 className="flex items-center gap-2 text-2xl font-semibold text-text">
          <ShieldCheck aria-hidden className="size-7 text-primary" />
          {t("verify.title")}
        </h1>
        <div className="w-44"><LanguageSwitcher /></div>
      </header>

      {answer.state === "checking" && <p role="status" className="text-text-muted">{t("verify.checking")}</p>}
      {answer.state === "not-found" && (
        <EmptyState title={t("verify.notFound.title")} icon={FileSearch}>{t("verify.notFound.body")}</EmptyState>
      )}
      {answer.state === "limited" && <EmptyState title={t("verify.limited.title")}>{t("verify.limited.body")}</EmptyState>}
      {answer.state === "failed" && <ErrorState correlationId={answer.correlationId} onRetry={retry} />}
      {answer.state === "verified" && (
        <>
          <Verified document={answer.document} />
          <PdfDrop check={pdf} onFile={(file) => { void comparePdf(file); }} />
        </>
      )}
    </main>
  );
}

function Verified({ document }: { document: VerifiedDocument }) {
  const { t, i18n } = useTranslation();
  const locale = intlLocale(i18n.language);
  const date = new Intl.DateTimeFormat(locale, { dateStyle: "long", timeZone: "UTC" }).format(new Date(`${document.shiftDate}T00:00:00Z`));
  const issued = new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeStyle: "short" }).format(new Date(document.issuedAt));
  const rows: [string, string, boolean][] = [
    [t("verify.field.type"), t(`verify.type.${document.type}`, { defaultValue: document.type }), false],
    [t("verify.field.company"), document.company, true],
    [t("verify.field.site"), `${document.siteCode} · ${document.siteName}`, true],
    [t("verify.field.shift"), `${date} · ${document.shiftCode}`, false],
    [t("verify.field.issued"), issued, false],
    [t("verify.field.contentHash"), document.contentHash, true],
    [t("verify.field.pdfHash"), document.pdfHash, true],
  ];
  return (
    <Card title={<span className="flex flex-wrap items-center gap-3">{t("verify.verified.title")}<StatusChip status="ok">{t("verify.verified.chip")}</StatusChip></span>}
      description={t("verify.verified.body")}>
      <dl className="grid grid-cols-1 gap-x-6 gap-y-2 sm:grid-cols-[max-content_1fr]">
        {rows.map(([label, value, data]) => (
          <div key={label} className="contents">
            <dt className="font-semibold text-text-muted">{label}</dt>
            <dd className="break-all text-text" {...(data ? { translate: "no" as const } : {})}>{value}</dd>
          </div>
        ))}
        <dt className="font-semibold text-text-muted">{t("verify.field.signature")}</dt>
        <dd className="text-text">
          {document.signature
            ? <StatusChip status="ok">{t("verify.signature.signed", { key: document.signature.keyId })}</StatusChip>
            : <StatusChip status="pending">{t("verify.signature.pending")}</StatusChip>}
        </dd>
      </dl>
    </Card>
  );
}

function PdfDrop({ check, onFile }: { check: PdfCheck; onFile: (file: File | undefined) => void }) {
  const { t } = useTranslation();
  const [over, setOver] = useState(false);
  function drop(event: DragEvent<HTMLLabelElement>) {
    event.preventDefault();
    setOver(false);
    onFile(event.dataTransfer.files[0]);
  }

  return (
    <Card title={t("verify.pdf.title")} description={t("verify.pdf.body")}>
      <label
        onDragOver={(event) => { event.preventDefault(); setOver(true); }}
        onDragLeave={() => { setOver(false); }}
        onDrop={drop}
        className={`flex cursor-pointer flex-col items-center gap-2 rounded-lg border-2 border-dashed px-4 py-8 text-center ${over ? "border-primary bg-surface-muted" : "border-divider"}`}
      >
        <span className="font-semibold text-text">{t("verify.pdf.choose")}</span>
        <input type="file" accept="application/pdf" className="sr-only" onChange={(event) => { onFile(event.target.files?.[0]); }} />
      </label>
      <div aria-live="polite">
        {check.state === "hashing" && <p className="text-text-muted">{t("verify.pdf.hashing")}</p>}
        {check.state === "matches" && <StatusChip status="ok">{t("verify.pdf.matches")}</StatusChip>}
        {check.state === "differs" && <StatusChip status="error">{t("verify.pdf.differs")}</StatusChip>}
        {check.state === "failed" && <StatusChip status="warning">{t("verify.pdf.failed")}</StatusChip>}
      </div>
    </Card>
  );
}

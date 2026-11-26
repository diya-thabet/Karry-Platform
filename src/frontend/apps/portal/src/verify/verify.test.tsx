import { fireEvent, render, screen } from "@testing-library/react";
import axe from "axe-core";
import { createApiClient } from "@karry/api-client";
import { createI18n } from "@karry/i18n";
import { VerifyApp } from "./VerifyApp";
import { isVerifyPath, sha256Of, verifyAddress, type VerifiedDocument } from "./verify";

const envelopeId = "0192f0c1-0000-7000-8000-00000000d001";
const token = "the-test-qr-token";
const issuedPdf = new Uint8Array([0x25, 0x50, 0x44, 0x46, 0x2d, 0x31]); // %PDF-1

const document: VerifiedDocument = {
  envelopeId,
  type: "shift_report",
  company: "Carrière de Kakoulima SA",
  siteCode: "CAR-KAK",
  siteName: "Carrière de Kakoulima",
  shiftDate: "2026-11-26",
  shiftCode: "M",
  issuedAt: "2026-11-26T14:05:00Z",
  contentHash: "sha256:" + "a".repeat(64),
  pdfHash: "",
  signature: null,
  pdfMatches: null,
};

interface Call { url: string; body: { token?: string | null; pdfHash?: string | null }; authorization: string | null }

/** The API as the public page sees it: one genuine envelope and token; everything else is the same "not found". */
function server(options: { status?: number; signed?: boolean } = {}) {
  const calls: Call[] = [];
  const fetch = async (input: Request) => {
    const body = (await input.json()) as Call["body"];
    calls.push({ url: input.url, body, authorization: input.headers.get("Authorization") });
    const json = (status: number, value: unknown) => new Response(JSON.stringify(value), { status, headers: { "Content-Type": status === 200 ? "application/json" : "application/problem+json" } });
    if (options.status) {
      return json(options.status, { code: "request.rate_limited", title: "Too many requests." });
    }

    if (!input.url.endsWith(`/api/v1/documents/verify/${envelopeId}`) || body.token !== token) {
      return json(404, { code: "documents.not_found", title: "No document matches this address." });
    }

    const pdfHash = await sha256Of(new Blob([issuedPdf]));
    return json(200, {
      ...document,
      pdfHash,
      signature: options.signed ? { keyId: "karry-server-v1", value: "c2ln", signedAt: "2026-11-26T14:06:00Z" } : null,
      pdfMatches: body.pdfHash ? body.pdfHash === pdfHash : null,
    });
  };
  return { calls, client: createApiClient({ baseUrl: "http://portal.test", fetch: fetch as typeof globalThis.fetch }) };
}

async function open(api: ReturnType<typeof server>, hash = `#${token}`, language: "fr" | "en" = "fr") {
  const i18n = await createI18n({ language, pseudo: false });
  render(<VerifyApp i18n={i18n} api={api.client} address={verifyAddress({ pathname: `/verify/${envelopeId}`, hash })} />);
}

describe("public verification page (ADR-0040 §4, US-P2-SIG-06)", () => {
  it("is the only path that opens without sign-in, and reads the token from the fragment only", () => {
    expect(isVerifyPath(`/verify/${envelopeId}`)).toBe(true);
    expect(isVerifyPath("/verify/")).toBe(false);
    expect(isVerifyPath(`/review/verify/${envelopeId}`)).toBe(false);
    expect(isVerifyPath(`/verify/${envelopeId}/more`)).toBe(false);
    expect(verifyAddress({ pathname: `/verify/${envelopeId}`, hash: `#${token}` })).toEqual({ envelopeId, token });
  });

  it("shows a genuine document: whose it is, its hashes, the signature still pending, no person's name", async () => {
    const api = server();
    await open(api);

    expect(await screen.findByText("Document authentique")).toBeInTheDocument();
    expect(screen.getByText("Vérifié")).toBeInTheDocument();
    expect(screen.getByText("Carrière de Kakoulima SA")).toBeInTheDocument();
    expect(screen.getByText("CAR-KAK · Carrière de Kakoulima")).toBeInTheDocument();
    expect(screen.getByText("Rapport de poste")).toBeInTheDocument();
    expect(screen.getByText("Signature en attente")).toBeInTheDocument();
    expect(api.calls).toHaveLength(1);
    expect(api.calls[0]).toMatchObject({ body: { token }, authorization: null });
    expect(api.calls[0]?.url).not.toContain(token); // the token is posted, never in a URL
    const results = await axe.run(window.document.body, { rules: { "color-contrast": { enabled: false } } });
    expect(results.violations.map((v) => v.id)).toEqual([]);
  });

  it("shows the server's signature with its key once the document is signed", async () => {
    await open(server({ signed: true }), `#${token}`, "en");

    expect(await screen.findByText("Signed (key karry-server-v1)")).toBeInTheDocument();
  });

  it("says not found for a wrong token, with nothing about the document", async () => {
    await open(server(), "#wrong-token");

    expect(await screen.findByText("Document introuvable")).toBeInTheDocument();
    expect(screen.queryByText("Carrière de Kakoulima SA")).not.toBeInTheDocument();
    expect(screen.queryByText("Comparer un PDF")).not.toBeInTheDocument();
  });

  it("asks to wait when the address has checked too often", async () => {
    await open(server({ status: 429 }));

    expect(await screen.findByText("Trop de vérifications")).toBeInTheDocument();
  });

  it("TC-P2-DOC-002: the issued PDF matches; one changed byte is not genuine; the file itself is never sent", async () => {
    const api = server();
    await open(api, `#${token}`, "en");
    const input = (await screen.findByText("Drop the PDF here or click to choose it")).parentElement?.querySelector("input");
    if (!input) {
      throw new Error("The PDF input is missing.");
    }


    fireEvent.change(input, { target: { files: [new File([issuedPdf], "report.pdf", { type: "application/pdf" })] } });
    expect(await screen.findByText("This PDF is the issued document")).toBeInTheDocument();

    const changed = issuedPdf.slice();
    changed[3] = 0x47;
    fireEvent.change(input, { target: { files: [new File([changed], "report.pdf", { type: "application/pdf" })] } });
    expect(await screen.findByText("Not genuine: this PDF differs from the issued document")).toBeInTheDocument();

    expect(api.calls.slice(1).map((call) => Object.keys(call.body).sort())).toEqual([["pdfHash", "token"], ["pdfHash", "token"]]);
    expect(api.calls[1]?.body.pdfHash).toMatch(/^sha256:[0-9a-f]{64}$/);
  });
});

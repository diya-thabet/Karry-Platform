import type { ReactNode } from "react";
import { SignaturePad, type SignatureCapture } from "@karry/ui";

/** The signature pad of a form, in its own chunk: loaded when a form opens, never on the home screen (P2.5 budget). */
export default function SignStep({ signer, summary, onConfirm, disabled }: {
  signer: ReactNode;
  summary: ReactNode;
  onConfirm: (signature: SignatureCapture) => Promise<void>;
  disabled: boolean;
}) {
  return <SignaturePad signer={signer} summary={summary} onConfirm={onConfirm} disabled={disabled} />;
}

/** Codes the operator gets an explanation for when the server refused a command; any other is shown with its code. */
export const refusalReasons: Record<string, string> = {
  "sync.blob_mismatch": "field.outbox.reason.blobMismatch",
  "sync.evidence_invalid": "field.outbox.reason.evidenceInvalid",
  "sync.signature_invalid": "field.outbox.reason.signatureInvalid",
  "sync.sequence_reused": "field.outbox.reason.sequenceReused",
};
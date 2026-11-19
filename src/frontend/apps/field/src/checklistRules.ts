import type { ChecklistItemRow, ChecklistTemplateRow, KarryOfflineDb, SafetyHoldRow } from "@karry/offline";

/** The command of a signed pre-start checklist (ADR-0035 §3) and the record type its envelope names. */
export const checklistCommand = "fieldops.checklist.submit.v1";
export const checklistRecordType = "fieldops.checklist";

export type Answer = "ok" | "not_ok" | "na";
export const answers: readonly Answer[] = ["ok", "not_ok", "na"];

/** An answer as the record writes it; `photo` is the photo's reference ("sha256:…"). */
export interface AnswerRecord {
  item: string;
  answer: string;
  photo?: string;
}

/**
 * The critical items answered Not OK when the answers fit the template's items (every item once, nothing else, a photo
 * where a Not OK asks for one, a photo only with a Not OK), else null: exactly the server's rule (ADR-0035 §3; both
 * sides read `tests/vectors/checklist-answers.json`).
 */
export function criticalNotOk(items: readonly Pick<ChecklistItemRow, "code" | "critical" | "photoOnNotOk">[], given: readonly AnswerRecord[]): string[] | null {
  const byCode = new Map(items.map((item) => [item.code, item]));
  if (given.length !== byCode.size || new Set(given.map((a) => a.item)).size !== given.length) {
    return null;
  }

  const critical: string[] = [];
  for (const answer of given) {
    const item = byCode.get(answer.item);
    if (!item || !(answers as readonly string[]).includes(answer.answer)) {
      return null;
    }

    const notOk = answer.answer === "not_ok";
    if ((notOk && item.photoOnNotOk && answer.photo === undefined) || (!notOk && answer.photo !== undefined)) {
      return null;
    }

    if (notOk && item.critical) {
      critical.push(item.code);
    }
  }

  return critical;
}

/** The item's text in the operator's language, else in the company's, else the first one written. */
export function itemText(item: ChecklistItemRow, language: string, companyLanguage: string | null): string {
  return item.texts[language] ?? (companyLanguage ? item.texts[companyLanguage] : undefined) ?? Object.values(item.texts)[0] ?? item.code;
}

/** The checklist of a machine type, if the company has one. */
export async function checklistFor(db: KarryOfflineDb, assetType: string): Promise<ChecklistTemplateRow | null> {
  return (await db.checklistTemplates.where("assetType").equals(assetType).first()) ?? null;
}

/** Why a machine is held, as the start screen says it: the critical items answered Not OK, and since when. */
export interface Hold {
  items: string[];
  since: string;
}

/**
 * Whether the machine is held as unsafe (ADR-0035 §4): an open hold of the last pull, or a critical Not OK this tablet
 * signed that the last pull does not reflect yet (not sent, or answered after that pull). A hold released by a manager
 * leaves the tablet at the next pull.
 */
export async function holdOn(db: KarryOfflineDb, machine: { id: string; type: string }): Promise<Hold | null> {
  const pulled: SafetyHoldRow | undefined = await db.safetyHolds.where("assetId").equals(machine.id).first();
  if (pulled) {
    return { items: pulled.items, since: pulled.openedAt };
  }

  const lastPull = (await db.syncState.get("safetyHolds"))?.lastSuccessAt ?? null;
  const ours = await db.outbox.where("status").anyOf("pending", "waiting", "confirmed")
    .filter((c) => c.type === checklistCommand && (c.status !== "confirmed" || lastPull === null || (c.answeredAt !== null && c.answeredAt > lastPull)))
    .toArray();
  for (const command of ours.reverse()) {
    const record = (command.payload as { record?: Partial<ChecklistRecord> }).record;
    if (record?.assetId !== machine.id || !Array.isArray(record.answers)) {
      continue;
    }

    // The version it answered, or the machine type's current one if a newer version replaced it since.
    const template = (typeof record.templateId === "string" ? await db.checklistTemplates.get(record.templateId) : undefined) ?? await checklistFor(db, machine.type);
    const critical = new Set(template?.items.filter((i) => i.critical).map((i) => i.code) ?? []);
    const items = record.answers.filter((a) => a.answer === "not_ok" && critical.has(a.item)).map((a) => a.item);
    if (items.length > 0) {
      return { items, since: typeof record.submittedAt === "string" ? record.submittedAt : command.deviceTime };
    }
  }

  return null;
}

/** A checklist record as signed (ADR-0035 §3); the operator is the envelope's signer. */
export interface ChecklistRecord {
  rootId: string;
  version: 1;
  templateId: string;
  templateVersion: number;
  assetId: string;
  submittedAt: string;
  answers: AnswerRecord[];
}

/** How long a signed checklist counts for the start that follows it (a form left by an auto-lock does not ask again). */
export const checklistValidMinutes = 60;

/** Whether this operator signed this machine's checklist on this tablet in the last {@link checklistValidMinutes}. */
export async function checkedRecently(db: KarryOfflineDb, operatorId: string, assetId: string, now: Date): Promise<boolean> {
  const since = now.getTime() - checklistValidMinutes * 60_000;
  const ours = await db.outbox.where("status").anyOf("pending", "waiting", "confirmed").filter((c) => c.type === checklistCommand).toArray();
  return ours.some((c) => {
    const payload = c.payload as { record?: Partial<ChecklistRecord>; envelope?: { signer?: { userId?: unknown } } };
    const at = typeof payload.record?.submittedAt === "string" ? Date.parse(payload.record.submittedAt) : Number.NaN;
    return payload.record?.assetId === assetId && payload.envelope?.signer?.userId === operatorId && at >= since && at <= now.getTime();
  });
}

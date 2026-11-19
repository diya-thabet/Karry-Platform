// @vitest-environment node
import "fake-indexeddb/auto";
import { readFileSync } from "node:fs";
import { openOfflineDb, type ChecklistTemplateRow, type KarryOfflineDb, type OutboxRecord } from "@karry/offline";
import { checkedRecently, checklistCommand, criticalNotOk, holdOn, itemText, type AnswerRecord } from "./checklistRules";

interface Vectors {
  template: { code: string; critical: boolean; photoOnNotOk: boolean }[];
  photo: string;
  cases: { name: string; answers: [string, string, boolean][]; critical: string[] | null }[];
}

const vectors = JSON.parse(readFileSync(new URL("../../../../../tests/vectors/checklist-answers.json", import.meta.url), "utf8")) as Vectors;
const machine = { id: "a1", type: "excavator" };
const template: ChecklistTemplateRow = {
  id: "t1", assetType: "excavator", version: 1, required: true,
  items: [{ code: "brakes", texts: { fr: "Freins", en: "Brakes" }, critical: true, photoOnNotOk: true }, { code: "lights", texts: { fr: "Feux" }, critical: false, photoOnNotOk: false }],
};

describe("checklist answers, as the server reads them (tests/vectors/checklist-answers.json)", () => {
  it.each(vectors.cases.map((c) => [c.name, c] as const))("%s", (_name, vector) => {
    const given: AnswerRecord[] = vector.answers.map(([item, answer, photo]) => (photo ? { item, answer, photo: vectors.photo } : { item, answer }));

    expect(criticalNotOk(vectors.template, given)).toEqual(vector.critical);
  });
});

describe("checklist texts", () => {
  it("shows the operator's language, else the company's, else the first one written", () => {
    const item = { code: "brakes", texts: { fr: "Freins", ar: "الفرامل" }, critical: true, photoOnNotOk: true };
    expect(itemText(item, "ar", "fr")).toBe("الفرامل");
    expect(itemText(item, "en", "fr")).toBe("Freins");
    expect(itemText(item, "en", null)).toBe("Freins");
  });
});

describe("safety holds on the tablet (ADR-0035 §4)", () => {
  let db: KarryOfflineDb;

  beforeEach(async () => {
    db = openOfflineDb(`holds-${crypto.randomUUID()}`);
    await db.checklistTemplates.put(template);
  });

  afterEach(async () => { await db.delete(); });

  const command = (status: OutboxRecord["status"], answers: AnswerRecord[], answeredAt: string | null = null, submittedAt = "2026-11-10T06:00:00.000Z"): OutboxRecord => ({
    commandId: crypto.randomUUID(), sequence: Math.floor(Math.random() * 1e9), type: checklistCommand, deviceTime: submittedAt, signature: "s", status, attempts: 0, code: null,
    flags: [], answeredAt, blobs: [],
    payload: { record: { rootId: "r1", version: 1, templateId: "t1", templateVersion: 1, assetId: "a1", submittedAt, answers }, envelope: { signer: { userId: "u1" } } },
  });
  const notOk: AnswerRecord[] = [{ item: "brakes", answer: "not_ok", photo: "sha256:x" }, { item: "lights", answer: "ok" }];

  it("holds the machine for an open hold of the pull", async () => {
    await db.safetyHolds.put({ id: "h1", assetId: "a1", items: ["brakes"], openedAt: "2026-11-10T05:00:00Z" });

    expect(await holdOn(db, machine)).toEqual({ items: ["brakes"], since: "2026-11-10T05:00:00Z" });
    expect(await holdOn(db, { id: "a2", type: "excavator" })).toBeNull();
  });

  it("holds the machine for its own critical Not OK until a pull after the server's answer", async () => {
    await db.outbox.put(command("pending", notOk));
    expect(await holdOn(db, machine)).toEqual({ items: ["brakes"], since: "2026-11-10T06:00:00.000Z" });

    await db.outbox.clear();
    await db.outbox.put(command("confirmed", notOk, "2026-11-10T06:05:00Z"));
    await db.syncState.put({ collection: "safetyHolds", cursor: "c", lastSuccessAt: "2026-11-10T06:01:00Z", lastFullAt: null });
    expect(await holdOn(db, machine), "answered after the last pull: the pull does not show the hold yet").not.toBeNull();

    await db.syncState.put({ collection: "safetyHolds", cursor: "c", lastSuccessAt: "2026-11-10T09:00:00Z", lastFullAt: null });
    expect(await holdOn(db, machine), "a later pull without the hold: a manager released it").toBeNull();
  });

  it("is not held by a Not OK that is not critical", async () => {
    await db.outbox.put(command("pending", [{ item: "brakes", answer: "ok" }, { item: "lights", answer: "not_ok" }]));

    expect(await holdOn(db, machine)).toBeNull();
  });

  it("counts a checklist signed by the same operator on the same machine within the hour", async () => {
    await db.outbox.put(command("pending", [{ item: "brakes", answer: "ok" }, { item: "lights", answer: "ok" }]));

    expect(await checkedRecently(db, "u1", "a1", new Date("2026-11-10T06:30:00Z"))).toBe(true);
    expect(await checkedRecently(db, "u1", "a1", new Date("2026-11-10T07:30:00Z"))).toBe(false);
    expect(await checkedRecently(db, "u2", "a1", new Date("2026-11-10T06:30:00Z"))).toBe(false);
    expect(await checkedRecently(db, "u1", "a2", new Date("2026-11-10T06:30:00Z"))).toBe(false);
  });
});

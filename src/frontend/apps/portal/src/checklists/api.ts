import { useQuery } from "@tanstack/react-query";
import type { components } from "@karry/api-client";
import { supportedLanguages, type Language } from "@karry/i18n";
import { useAccess, useApi } from "../app/api";

export type Checklist = components["schemas"]["ChecklistResponse"];
export type ChecklistItemInput = components["schemas"]["ChecklistItemInput"];

export const ChecklistsReadPermission = "fieldops.checklists.read";
export const ChecklistsManagePermission = "fieldops.checklists.manage";

/** The limits the server checks (ADR-0035 §2), checked here first so the person sees them next to the field. */
export const maxItems = 50;
export const maxText = 200;
const maxCode = 40;

export const checklistsKey = ["checklists"] as const;
export const checklistVersionsKey = (assetType: string) => ["checklists", assetType, "versions"] as const;

/** The newest version of each machine type's checklist. Nothing is asked without the permission. */
export function useChecklists() {
  const api = useApi();
  const { can } = useAccess();
  return useQuery({
    queryKey: checklistsKey,
    queryFn: async () => (await api.GET("/api/v1/fieldops/checklists")).data?.items ?? [],
    enabled: can(ChecklistsReadPermission),
  });
}

/** Every version of one machine type's checklist, newest first; empty when it has none yet (404). */
export function useChecklistVersions(assetType: string | undefined) {
  const api = useApi();
  const { can } = useAccess();
  return useQuery({
    queryKey: checklistVersionsKey(assetType ?? ""),
    queryFn: async () => (await api.GET("/api/v1/fieldops/checklists/{assetType}/versions", { params: { path: { assetType: assetType ?? "" } } })).data?.items ?? [],
    enabled: assetType !== undefined && can(ChecklistsReadPermission),
  });
}

/** One item as the builder edits it; `key` keeps React's rows stable while items move. */
export interface ItemDraft {
  key: string;
  code: string;
  texts: Partial<Record<Language, string>>;
  critical: boolean;
  photoOnNotOk: boolean;
}

let nextKey = 0;
export const newItem = (): ItemDraft => ({ key: `item-${String(++nextKey)}`, code: "", texts: {}, critical: false, photoOnNotOk: false });

/** The items of a published version, ready to be edited into the next one. */
export function draftsOf(checklist: Checklist | undefined): ItemDraft[] {
  return (checklist?.items ?? []).map((item) => ({ ...newItem(), code: item.code, texts: { ...item.texts }, critical: item.critical, photoOnNotOk: item.photoOnNotOk }));
}

/** The languages in the builder's order: the company's first (the tablet's fallback), then the others. */
export function languagesFor(companyLanguage: string | undefined): Language[] {
  const first = supportedLanguages.find((language) => language === companyLanguage);
  return first ? [first, ...supportedLanguages.filter((language) => language !== first)] : [...supportedLanguages];
}

/**
 * The code an item gets when none is typed: its first Latin text spelled with letters, digits and dashes
 * ("Freins / frein à main" → "freins-frein-a-main"); `item-N` when no text has Latin letters.
 */
export function codeFrom(draft: ItemDraft, position: number): string {
  const typed = draft.code.trim().toLowerCase();
  if (typed) {
    return typed;
  }

  for (const text of Object.values(draft.texts)) {
    const code = text.normalize("NFD").replace(/[̀-ͯ]/g, "").toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-+/, "").slice(0, maxCode).replace(/-+$/, "");
    if (code) {
      return code;
    }
  }

  return `item-${String(position + 1)}`;
}

export type ItemProblem = "code" | "duplicate" | "text" | "tooLong";

/** What is wrong with the items, per item key, before anything is sent; and the list-level problem, if any. */
export function checkItems(drafts: ItemDraft[]): { list: "none" | "tooMany" | null; items: Record<string, ItemProblem> } {
  const items: Record<string, ItemProblem> = {};
  const seen = new Set<string>();
  drafts.forEach((draft, position) => {
    const code = codeFrom(draft, position);
    const texts = Object.values(draft.texts).map((text) => text.trim()).filter((text) => text !== "");
    if (!/^[a-z0-9][a-z0-9-]{0,39}$/.test(code)) {
      items[draft.key] = "code";
    } else if (seen.has(code)) {
      items[draft.key] = "duplicate";
    } else if (texts.length === 0) {
      items[draft.key] = "text";
    } else if (texts.some((text) => text.length > maxText)) {
      items[draft.key] = "tooLong";
    }

    seen.add(code);
  });
  return { list: drafts.length === 0 ? "none" : drafts.length > maxItems ? "tooMany" : null, items };
}

/** The items as the API takes them: codes filled in, empty texts left out, a photo always asked for a critical Not OK. */
export function itemsBody(drafts: ItemDraft[]): ChecklistItemInput[] {
  return drafts.map((draft, position) => ({
    code: codeFrom(draft, position),
    texts: Object.fromEntries(Object.entries(draft.texts).map(([language, text]): [string, string] => [language, text.trim()]).filter(([, text]) => text !== "")),
    critical: draft.critical,
    photoOnNotOk: draft.critical || draft.photoOnNotOk,
  }));
}

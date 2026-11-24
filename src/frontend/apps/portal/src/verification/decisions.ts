import type { ShiftEntry } from "./queue";

export const ItemsReviewPermission = "verification.items.review";
export const SecondApprovePermission = "verification.items.second-approve";

/** Why an entry is rejected (US-P2-VER-03 AC1), as the API names them. */
export const rejectionReasons = ["wrong_reading", "photo_unreadable", "wrong_machine", "wrong_shift", "duplicate", "other"] as const;

/** A comment or a correction's reason: 10 to 500 characters, as the API checks it. */
export function explanationOk(text: string): boolean {
  const length = text.trim().length;
  return length >= 10 && length <= 500;
}

/** A meter value as typed ("1 250,5" → "1250.5", one decimal at most), or null when it is not one. */
export function meterText(text: string): string | null {
  const value = text.trim().replace(/\s/g, "").replace(",", ".");
  return /^\d{1,7}(\.\d)?$/.test(value) ? value : null;
}

/** A fuel level as typed: a whole percentage from 0 to 100, or null. */
export function fuelText(text: string): string | null {
  const value = text.trim();
  return /^\d{1,3}$/.test(value) && Number(value) <= 100 ? value : null;
}

/**
 * The fields of a correction that changed (US-P2-VER-04 AC1): only what the person typed differently from the entry;
 * null when something typed is not a value.
 */
export function correctedFields(entry: ShiftEntry, typed: Record<"startValue" | "endValue" | "fuelStartPercent" | "fuelEndPercent", string>)
  : Record<string, string> | null {
  const checks = { startValue: meterText, endValue: meterText, fuelStartPercent: fuelText, fuelEndPercent: fuelText } as const;
  const changed: Record<string, string> = {};
  for (const field of Object.keys(checks) as (keyof typeof checks)[]) {
    const original = entry[field];
    if (typed[field] === (original === null ? "" : String(original))) {
      continue;
    }

    const value = checks[field](typed[field]);
    if (value === null) {
      return null;
    }

    if (original === null || Number(value) !== Number(original)) {
      changed[field] = value;
    }
  }

  return changed;
}

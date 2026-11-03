import { useQuery } from "@tanstack/react-query";
import type { components } from "@karry/api-client";
import { useAccess, useApi } from "../app/api";

export type Site = components["schemas"]["SiteResponse"];
export type SiteInput = components["schemas"]["SiteInput"];
export type ShiftPattern = components["schemas"]["ShiftPatternResponse"];
export type ShiftInput = components["schemas"]["ShiftInput"];
export type NonWorkingDay = components["schemas"]["NonWorkingDayResponse"];
export type ShiftInstance = components["schemas"]["ShiftInstanceResponse"];

export const SitesReadPermission = "organization.sites.read";
export const SitesManagePermission = "organization.sites.manage";
export const ShiftsManagePermission = "organization.shifts.manage";

export const siteTypes = ["quarry", "sand-washing", "mine", "warehouse-only", "workshop"] as const;
export const dayCodes = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"] as const;
export const dayKinds = ["public-holiday", "closure", "other"] as const;

export const sitesKey = ["organization", "sites"] as const;
export const siteKey = (id: string) => ["organization", "sites", id] as const;

/** The sites the person may see (a site role: only theirs); archived ones on request. Nothing is asked without the permission. */
export function useSites(includeArchived = false) {
  const api = useApi();
  const { can } = useAccess();
  return useQuery({
    queryKey: [...sitesKey, { includeArchived }],
    queryFn: async () => (await api.GET("/api/v1/organization/sites", { params: { query: { includeArchived } } })).data?.items ?? [],
    enabled: can(SitesReadPermission),
  });
}

/** Today's date (YYYY-MM-DD) on a time zone's clock, as the API counts "the site's today". */
export function todayIn(timeZone: string, now = new Date()): string {
  try {
    return new Intl.DateTimeFormat("en-CA", { timeZone, year: "numeric", month: "2-digit", day: "2-digit" }).format(now);
  } catch {
    return now.toISOString().slice(0, 10);
  }
}

/** A date (YYYY-MM-DD) plus some days. */
export function addDays(date: string, days: number): string {
  const value = new Date(`${date}T00:00:00Z`);
  value.setUTCDate(value.getUTCDate() + days);
  return value.toISOString().slice(0, 10);
}

/** IANA time zones the browser knows (empty where it cannot list them: the field still accepts any name). */
export function timeZones(): string[] {
  try {
    return Intl.supportedValuesOf("timeZone");
  } catch {
    return [];
  }
}

/** ISO 3166-1 alpha-2 codes the API accepts (the 249 officially assigned; same list as Organization's Countries.cs). */
const countryCodes = (
  "AD AE AF AG AI AL AM AO AQ AR AS AT AU AW AX AZ BA BB BD BE BF BG BH BI BJ BL BM BN BO BQ BR BS BT BV BW BY BZ CA CC " +
  "CD CF CG CH CI CK CL CM CN CO CR CU CV CW CX CY CZ DE DJ DK DM DO DZ EC EE EG EH ER ES ET FI FJ FK FM FO FR GA GB GD " +
  "GE GF GG GH GI GL GM GN GP GQ GR GS GT GU GW GY HK HM HN HR HT HU ID IE IL IM IN IO IQ IR IS IT JE JM JO JP KE KG KH " +
  "KI KM KN KP KR KW KY KZ LA LB LC LI LK LR LS LT LU LV LY MA MC MD ME MF MG MH MK ML MM MN MO MP MQ MR MS MT MU MV MW " +
  "MX MY MZ NA NC NE NF NG NI NL NO NP NR NU NZ OM PA PE PF PG PH PK PL PM PN PR PS PT PW PY QA RE RO RS RU RW SA SB SC " +
  "SD SE SG SH SI SJ SK SL SM SN SO SR SS ST SV SX SY SZ TC TD TF TG TH TJ TK TL TM TN TO TR TT TV TW TZ UA UG UM US UY " +
  "UZ VA VC VE VG VI VN VU WF WS YE YT ZA ZM ZW ").trim().split(" ");

/** The countries the API accepts, with their names in the person's language. */
export function countries(language: string): { value: string; label: string }[] {
  const names = new Intl.DisplayNames([language], { type: "region", fallback: "code" });
  return countryCodes
    .map((code) => ({ value: code, label: names.of(code) ?? code }))
    .sort((x, y) => x.label.localeCompare(y.label, language));
}

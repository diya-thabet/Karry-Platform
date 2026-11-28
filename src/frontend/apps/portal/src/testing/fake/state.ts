import { defaultRoles, integrityCatalog, nextVersion } from "./factories";
import type {
  Asset, Checklist, Customer, Device, Hauler, Hold, ImportJob, IntegrityFlag, IntegrityRule, Invoice, Material, Meter, Notice,
  NonWorkingDay, OperatorTenant, PolicyVersion, PriceList, Product, Reading, ReviewItem, Sent, ShiftEntry, ShiftPattern, Site, Subscription,
  SupportSession, Tenant, Truck, Verification,
} from "./types";
import type { FakeApiOptions } from "./types";

/** Everything the fake API holds for one test: what the options gave, what calls changed since, and every call it saw. */
export function createState(options: FakeApiOptions) {
  // The default rules' channels (ADR-0039 §2) and the company's switches and the person's choices, all on at first.
  const noticeChannels: Record<string, string[]> = {
    "review.rejected": ["in_app", "push"],
    "review.escalated": ["in_app", "digest"],
    "asset.unsafe": ["in_app", "push"],
    "integrity.fraud_suspected": ["in_app", "digest"],
    "integrity.missing_end": ["in_app", "digest"],
  };
  const noticeRules = Object.entries(noticeChannels).map(([type, channels]) => ({
    type, enabled: !(options.noticeRulesOff ?? []).includes(type), channels, changedAt: null as string | null, changedBy: null as string | null,
  }));
  const choices: Record<string, Record<string, boolean>> = Object.fromEntries(Object.entries(noticeChannels).map(([type, channels]) =>
    [type, Object.fromEntries(channels.filter((c) => c !== "in_app").map((c) => [c, true]))]));

  const state = {
    roles: defaultRoles(),
    users: options.users ?? [],
    sent: [] as Sent[],
    tenant: {
      id: "0192f0c1-0000-7000-8000-0000000000a1", slug: "carriere-a", name: "Carrière A", createdAt: "2026-10-01T08:00:00Z",
      legalName: "Carrière A", primaryColour: null, language: "none", currency: "EUR", unitSystem: "metric", timeZone: "UTC",
      fiscalYearStartMonth: 1, logoUrl: null, settingsChangedAt: null, version: '"1"', ...options.tenant,
    },
    changes: 0,
    sites: [...(options.sites ?? [])] as Site[],
    patterns: { ...options.patterns } as Record<string, ShiftPattern[]>,
    days: { ...options.days } as Record<string, NonWorkingDay[]>,
    materials: [...(options.materials ?? [])] as Material[],
    customers: [...(options.customers ?? [])] as Customer[],
    products: [...(options.products ?? [])] as Product[],
    trucks: [...(options.trucks ?? [])] as Truck[],
    haulers: [...(options.haulers ?? [])] as Hauler[],
    priceLists: [...(options.priceLists ?? [])] as PriceList[],
    integrityRules: [...(options.integrityRules ?? integrityCatalog())] as IntegrityRule[],
    assets: [...(options.assets ?? [])] as Asset[],
    checklists: [...(options.checklists ?? [])] as Checklist[],
    holds: [...(options.holds ?? [])] as Hold[],
    reviewItems: [...(options.reviewItems ?? [])] as ReviewItem[],
    entries: [...(options.entries ?? [])] as ShiftEntry[],
    queueCalls: [] as { query: Record<string, string>; ifNoneMatch: string | null }[],
    decisions: [] as { action: string; ifMatch: string | null; body: unknown }[],
    correctionWaits: options.correctionWaits ?? false,
    settlements: [] as { action: string; ifMatch: string | null; body: unknown }[],
    notices: [...(options.notices ?? [])] as Notice[],
    noticeRules,
    choices,
    preferences: () => noticeRules.map((rule) => ({ type: rule.type, ruleEnabled: rule.enabled, channels: { ...choices[rule.type] } })),
    integrityFlags: [...(options.integrityFlags ?? [])] as IntegrityFlag[],
    policyVersions: [...(options.policyVersions ?? [])] as PolicyVersion[],
    meters: { ...options.meters } as Record<string, Meter[]>,
    readings: { ...options.readings } as Record<string, Reading[]>,
    meterEvents: {} as Record<string, unknown[]>,
    imports: [] as ImportJob[],
    importSteps: 0,
    devices: [...(options.devices ?? [])] as Device[],
    supportSessions: [...(options.supportSessions ?? [])] as SupportSession[],
    supportCalls: [] as { method: string; path: string; header: string | null }[],
    auditCalls: [] as { path: string; query: Record<string, string> }[],
    entryCalls: [] as { query: Record<string, string> }[],
    shiftReportCalls: [] as Record<string, string>[],
    requested: [] as string[],
    operatorTenants: [...(options.operatorTenants ?? [])] as OperatorTenant[],
    tenantSubscriptions: { ...options.tenantSubscriptions } as Record<string, Subscription>,
    tenantInvoices: { ...options.tenantInvoices } as Record<string, Invoice[]>,
    verification: { verification: null, verifiedBy: null, verifiedByName: null } as Verification,
    /** As the API: every saved change of the company settings (the logo included) is a new version (H-C1). */
    changed: (next: Partial<Tenant>) => {
      state.changes += 1;
      state.tenant = {
        ...state.tenant, ...next, settingsChangedAt: `2026-10-26T08:00:${String(state.changes).padStart(2, "0")}Z`, version: nextVersion(state.tenant.version),
      };
      return state.tenant;
    },
  };
  return state;
}

export type FakeState = ReturnType<typeof createState>;

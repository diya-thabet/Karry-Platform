import type { Asset, AuditEntry, Checklist, ConversionInput, Customer, DensityInput, DensityVersion, Device, Hauler, Hold, IntegrityFlag, IntegrityRule, Material, Meter, Notice, OperatorTenant, PriceList, Product, ReviewItem, Role, ShiftEntry, Site, SiteInput, Subscription, SupportSession, Truck, User } from "./types";

const fields = { "probe.quote.unit-price": "masked" };

/** The 12 default roles as the API lists them (only the permissions the tests look at). */
export function defaultRoles(): Role[] {
  const role = (code: string, office: boolean, privileged: boolean, permissions: string[]): Role => ({
    code, isDefault: true, name: null, description: null, basedOn: null, office, privileged, permissions, fieldLevels: { ...fields }, version: '"0"',
  });
  return [
    role("tenant-admin", true, true, ["identity.roles.manage", "identity.roles.read", "identity.users.manage", "identity.users.read"]),
    role("executive", true, true, ["identity.roles.read", "identity.users.read"]),
    role("site-manager", true, false, ["identity.users.manage", "identity.users.read"]),
    role("commercial", true, false, []),
    role("operator", false, false, []),
  ];
}

/**
 * An in-memory Karry API for component tests, through the real typed client (so paths, bodies and the step-up
 * problem look as in production). `needsStepUp`: while it returns true, writes answer 401 `auth.step_up_required`
 * (wire it to the test session's step-up count).

/** A notification of the signed-in person (default: an unread escalation of a review on the north site). */
export function notice(overrides: Partial<Notice> = {}): Notice {
  return {
    id: "0192f0c1-0000-7000-8000-00000000c001", type: "review.escalated", parameters: { dueAt: "2026-11-24T14:05:00Z", entryId: "0192f0c1-0000-7000-8000-00000000e101" },
    link: "/review", siteId: "0192f0c1-0000-7000-8000-000000005001", createdAt: "2026-11-24T14:10:00Z", readAt: null, ...overrides,
  };
}

/** A pending review item of a shift entry on the north site (default: unflagged, due tomorrow). */
export function reviewItem(overrides: Partial<ReviewItem> = {}): ReviewItem {
  return {
    id: "0192f0c1-0000-7000-8000-00000000e001", recordType: "fieldops.shift_entry", recordId: "0192f0c1-0000-7000-8000-00000000e101", recordVersion: 2,
    recordHash: `sha256:${"c".repeat(64)}`, siteId: "0192f0c1-0000-7000-8000-000000005001", producedBy: "0192f0c1-0000-7000-8000-000000000501",
    assetId: "0192f0c1-0000-7000-8000-000000007001", coveredFrom: "2026-11-23T06:00:00Z", coveredTo: "2026-11-23T14:00:00Z", flags: [], status: "pending",
    createdAt: "2026-11-23T14:05:00Z", version: '"1"', correctedBy: null, policyVersion: 0, dueAt: "2026-11-24T14:05:00Z", escalatedAt: null, ...overrides,
  };
}

/** A shift entry as it stands: the excavator's hour meter from 1 242.5 to 1 250 h on the morning shift. */
export function shiftEntry(overrides: Partial<ShiftEntry> = {}): ShiftEntry {
  return {
    id: "0192f0c1-0000-7000-8000-00000000e101", version: 2, status: "closed", siteId: "0192f0c1-0000-7000-8000-000000005001",
    assetId: "0192f0c1-0000-7000-8000-000000007001", meterId: "0192f0c1-0000-7000-8000-00000000a201", meterKind: "hours", unit: "h",
    operatorId: "0192f0c1-0000-7000-8000-000000000501", segmentNo: 1, previousEntryId: null,
    shift: { date: "2026-11-23", code: "M", startsAt: "2026-11-23T06:00:00Z", endsAt: "2026-11-23T14:00:00Z" },
    startedAt: "2026-11-23T06:02:00Z", startValue: 1242.5, fuelStartPercent: 80, startPhoto: `sha256:${"1".repeat(64)}`, startLocation: null,
    endedAt: "2026-11-23T13:58:00Z", endValue: 1250, usage: 7.5, fuelEndPercent: 35, endPhoto: `sha256:${"2".repeat(64)}`, endLocation: null,
    stoppages: [], remarks: null, estimated: false, flags: [], envelopeId: "0192f0c1-0000-7000-8000-00000000e201", deviceId: "0192f0c1-0000-7000-8000-00000000e301",
    receivedAt: "2026-11-23T14:00:00Z", ...overrides,
  };
}

/** An open hold on the excavator PEL-01: its brakes were answered Not OK. */
export function hold(overrides: Partial<Hold> = {}): Hold {
  return {
    id: "0192f0c1-0000-7000-8000-000000009001", assetId: "0192f0c1-0000-7000-8000-000000007001", assetCode: "PEL-01", siteId: "0192f0c1-0000-7000-8000-000000005001",
    submissionId: "0192f0c1-0000-7000-8000-000000009101", items: ["brakes"], openedAt: "2026-11-19T06:10:00Z", openedBy: "0192f0c1-0000-7000-8000-000000000501",
    release: null, ...overrides,
  };
}

/** An open RULE-FO-01 flag on a shift entry of the first site: started at 1250 h where the last shift ended at 1242.5 h. */
export function integrityFlag(overrides: Partial<IntegrityFlag> = {}): IntegrityFlag {
  return {
    id: "0192f0c1-0000-7000-8000-00000000a001", rule: "RULE-FO-01", severity: "high",
    subject: { type: "shift_entry", id: "0192f0c1-0000-7000-8000-00000000a101" }, key: "continuity", siteId: "0192f0c1-0000-7000-8000-000000005001",
    related: [{ type: "meter", id: "0192f0c1-0000-7000-8000-00000000a201" }], evidence: ["sha256:aa", "sha256:bb"],
    details: { meterKind: "hours", expectedValue: "1242.5", foundValue: "1250", difference: "7.5" },
    openedAt: "2026-11-19T06:10:00Z", closedAt: null, closeReason: null, ...overrides,
  };
}

/** Version 1 of the excavator checklist: brakes (critical) and lights. */
export function checklist(overrides: Partial<Checklist> = {}): Checklist {
  return {
    id: "0192f0c1-0000-7000-8000-000000008001", assetType: "excavator", version: 1, required: true, publishedAt: "2026-11-18T07:30:00Z",
    items: [
      { code: "brakes", texts: { fr: "Freins", en: "Brakes" }, critical: true, photoOnNotOk: true },
      { code: "lights", texts: { fr: "Feux", en: "Lights" }, critical: false, photoOnNotOk: false },
    ],
    ...overrides,
  };
}

/** An active tablet of Carrière Nord as the API lists it. */
export function device(overrides: Partial<Device> = {}): Device {
  return {
    id: "0192f0c1-0000-7000-8000-00000000d001", name: "Tablette pesage", model: "Galaxy Tab A9", platform: "Android 14", appVersion: "1.0.0",
    siteId: site().id, status: "active", enrolledAt: "2026-10-27T08:00:00Z", lastSeenAt: "2026-10-28T07:55:00Z", revokedAt: null, ...overrides,
  };
}

export function user(overrides: Partial<User> = {}): User {
  return {
    id: "0192f0c1-0000-7000-8000-000000000001", kind: "office", status: "active", firstName: "Sami", lastName: "Gharbi",
    email: "sami@example.test", phone: null, employeeId: null, roles: [{ role: "commercial", siteId: null }], requiresMfa: false,
    createdAt: "2026-10-24T08:00:00Z", deactivatedAt: null, pinSet: false, version: '"1"', linkedFieldUserId: null, ...overrides,
  };
}

export function siteFrom(id: string, input: SiteInput): Site {
  return {
    id, code: (input.code ?? "").toUpperCase(), name: input.name ?? "", type: input.type ?? "quarry",
    address: {
      line: input.address?.line ?? "", city: input.address?.city ?? "", region: input.address?.region ?? null,
      postalCode: input.address?.postalCode ?? null, country: input.address?.country ?? "",
    },
    location: { latitude: Number(input.location?.latitude ?? 0), longitude: Number(input.location?.longitude ?? 0) },
    boundary: input.boundary ? { type: "Polygon", coordinates: input.boundary } : null,
    timeZone: input.timeZone ?? "UTC", currency: input.currency ?? null, status: "active",
    createdAt: "2026-10-26T08:00:00Z", changedAt: "2026-10-26T08:00:00Z", archivedAt: null, version: '"1"',
  };
}

/** A site as the API answers it. */
export function site(overrides: Partial<Site> = {}): Site {
  return {
    ...siteFrom("0192f0c1-0000-7000-8000-000000005001", {
      code: "CAR-NORD", name: "Carrière Nord", type: "quarry",
      address: { line: "Route de Bizerte km 12", city: "Tunis", region: null, postalCode: "1000", country: "TN" },
      location: { latitude: 36.8045, longitude: 10.0056 }, boundary: null, timeZone: "Africa/Tunis", currency: null,
    }),
    ...overrides,
  };
}

export function versionFrom(effectiveFrom: string, input: DensityInput | null | undefined): DensityVersion {
  const value = (x: number | string | null | undefined) => (x === null || x === undefined ? null : Number(x));
  const bank = value(input?.bank);
  const loose = value(input?.loose);
  return {
    effectiveFrom, bank, loose, compacted: value(input?.compacted), moisture: value(input?.moisture) ?? 0,
    swell: bank !== null && loose !== null ? Math.round((bank / loose - 1) * 1e6) / 1e6 : null,
  };
}

/** A material as the API answers it: 0/31.5 with 2.2 bank, 1.6 loose, 3 % moisture from 1 January 2026. */
export function customer(overrides: Partial<Customer> = {}): Customer {
  return {
    id: "0192f0c1-0000-7000-8000-000000007001", code: "CLI-0042", legalName: "Bâtisseurs du Cap", taxId: "TN1234567",
    addresses: [{ kind: "billing", line1: "12 rue du Port", line2: null, city: "Bizerte", region: null, postalCode: null, country: "TN" }],
    contacts: [{ name: "Awa Diallo", role: null, phone: null, email: "awa@cap.example" }], paymentTermsDays: 30, basis: "t", allowedSiteIds: [],
    creditLimit: { amount: 150000, currency: "TND" }, status: "active", blockedReason: null, createdAt: "2026-11-27T08:00:00Z",
    changedAt: "2026-11-27T08:00:00Z", version: '"1"', ...overrides,
  };
}

export function product(overrides: Partial<Product> = {}): Product {
  return {
    id: "0192f0c1-0000-7000-8000-000000008001", code: "G-8/16", name: "Gravier 8/16", materialId: "0192f0c1-0000-7000-8000-000000006001",
    saleUnit: "t", status: "active", createdAt: "2026-11-27T08:00:00Z", changedAt: "2026-11-27T08:00:00Z", version: '"1"', ...overrides,
  };
}

export function truck(overrides: Partial<Truck> = {}): Truck {
  return {
    id: "0192f0c1-0000-7000-8000-000000009601", plate: "123 TU 4567", plateKey: "123TU4567", haulerId: null,
    tare: { kg: 14200, on: "2026-11-02", method: "weighed" }, maxPayloadKg: 26000, axles: "2+3", status: "active",
    createdAt: "2026-11-27T08:00:00Z", changedAt: "2026-11-27T08:00:00Z", version: '"1"', ...overrides,
  };
}

export function hauler(overrides: Partial<Hauler> = {}): Hauler {
  return {
    id: "0192f0c1-0000-7000-8000-000000009501", name: "Transports Ben Salah", phone: "+216 71 000 000", status: "active",
    createdAt: "2026-11-27T08:00:00Z", changedAt: "2026-11-27T08:00:00Z", version: '"1"', ...overrides,
  };
}

export function priceList(overrides: Partial<PriceList> = {}): PriceList {
  return {
    id: "0192f0c1-0000-7000-8000-000000009001", siteId: "0192f0c1-0000-7000-8000-000000005001", customerId: null, currency: "TND",
    effectiveFrom: "2026-11-01", status: "active", inForce: true, lines: [{ productId: "0192f0c1-0000-7000-8000-000000008001", unitPrice: 30 }],
    createdAt: "2026-11-01T08:00:00Z", changedAt: "2026-11-01T08:00:00Z", version: '"1"', ...overrides,
  };
}

export function material(overrides: Partial<Material> = {}): Material {
  return {
    id: "0192f0c1-0000-7000-8000-000000006001", code: "0/31.5", name: "Grave 0/31.5", kind: "product",
    densities: [versionFrom("2026-01-01", { bank: 2.2, loose: 1.6, compacted: null, moisture: 0.03 })],
    status: "active", createdAt: "2026-10-26T08:00:00Z", changedAt: "2026-10-26T08:00:00Z", archivedAt: null, version: '"1"', ...overrides,
  };
}

/** The rules of P2.7 as a company without settings runs them (the server's catalogue, ADR-0036 §1). */
export function integrityCatalog(): IntegrityRule[] {
  const rule = (code: string, severity: string, security: boolean, thresholds: IntegrityRule["thresholds"] = []): IntegrityRule => ({
    rule: code, severity, security, enabled: true, thresholds, changedAt: null, changedBy: null, version: '"0"',
  });
  return [
    rule("RULE-FO-01", "high", false),
    rule("RULE-FO-02", "high", false, [{ name: "toleranceHours", value: 0.5, default: 0.5, min: 0, max: 4, decimals: 1, unit: "h" }]),
    rule("RULE-FO-03", "medium", false, [{ name: "maxSkewMinutes", value: 10, default: 10, min: 1, max: 1440, decimals: 0, unit: "min" }]),
    rule("RULE-FO-04", "medium", false, [{ name: "marginMetres", value: 200, default: 200, min: 0, max: 5000, decimals: 0, unit: "m" }]),
    rule("RULE-FO-05", "high", true),
    rule("RULE-FO-06", "high", true),
    rule("RULE-FO-07", "critical", true),
    rule("RULE-FO-08", "low", false, [{ name: "graceMinutes", value: 60, default: 60, min: 15, max: 720, decimals: 0, unit: "min" }]),
    rule("RULE-FO-09", "critical", true),
    rule("RULE-FO-10", "high", true),
    rule("RULE-FO-11", "critical", true),
  ];
}

/** The next record version, as PostgreSQL's xmin moves on with every save. */
export function nextVersion(version: string): string {
  return `"${String(Number(version.replaceAll('"', "")) + 1)}"`;
}

/** The engine's formula (M = V × ρ_state × (1 + w)) in floating point: enough for screens, not for golden values. */
export function convert(found: Material, input: ConversionInput) {
  const on = input.on ?? "2026-10-26";
  const version = found.densities.filter((v) => v.effectiveFrom <= on).at(-1);
  if (!version) {
    return null;
  }

  const perTonne: Record<string, number> = { t: 1, kg: 0.001, st: 0.90718474 };
  const density = (state: string | null) => Number(version[state as "bank" | "loose" | "compacted"]);
  const w = Number(input.moisture ?? version.moisture);
  const quantity = input.quantity;
  const value = Number(quantity?.value);
  const factors: { name: string; value: number; unit: string }[] = [];
  let tonnes = value * (perTonne[quantity?.unit ?? ""] ?? 1);
  if (quantity?.unit === "m3") {
    factors.push({ name: `density.${quantity.state ?? ""}`, value: density(quantity.state), unit: "t/m3" });
    tonnes = value * density(quantity.state) * (1 + w);
  }

  if (input.unit !== "m3") {
    factors.push({ name: "moisture", value: w, unit: "fraction" });
    return { result: { value: tonnes / (perTonne[input.unit ?? ""] ?? 1), unit: input.unit, state: null }, factors, densitiesFrom: version.effectiveFrom };
  }

  factors.push({ name: `density.${input.state ?? ""}`, value: density(input.state), unit: "t/m3" }, { name: "moisture", value: w, unit: "fraction" });
  return { result: { value: tonnes / (density(input.state) * (1 + w)), unit: "m3", state: input.state }, factors, densitiesFrom: version.effectiveFrom };
}

/** A machine as the API answers it: excavator PEL-01 on Carrière Nord. */
export function asset(overrides: Partial<Asset> = {}): Asset {
  const siteId = overrides.siteId ?? "0192f0c1-0000-7000-8000-000000005001";
  return {
    id: "0192f0c1-0000-7000-8000-000000007001", code: "PEL-01", name: "Pelle Cat 336", type: "excavator", make: "Caterpillar", model: "336",
    serialNumber: "CAT0336X1234", plate: null, year: 2021, acquiredOn: "2021-03-15", siteId, status: "active",
    label: "KRY1:ABCDEFGHIJKLMNOPQRSTUVWXYZ", photo: null, sites: [{ siteId, from: "2026-01-05T08:00:00Z" }],
    createdAt: "2026-10-27T08:00:00Z", changedAt: "2026-10-27T08:00:00Z", disposedAt: null, version: '"1"', ...overrides,
  };
}

/** An hour meter at 4,250.5 h, working. */
export function meterOf(overrides: Partial<Meter> = {}): Meter {
  return {
    id: "0192f0c1-0000-7000-8000-000000008001", kind: "hours", unit: "h", initialReading: 0, installedOn: "2021-03-15", status: "ok", offset: 0,
    lastValue: 4250.5, lastReadAt: "2026-10-26T16:00:00Z", createdAt: "2026-10-27T08:00:00Z", ...overrides,
  };
}

/** An event as an audit entry carries it (the schema types it as an opaque JSON object). */
export function recordedEvent(value: Record<string, unknown>): AuditEntry["event"] {
  return value as AuditEntry["event"];
}

/** An audit entry as the API answers it: Ali changed machine PEL-01's name on Carrière Nord. */
export function auditEntry(overrides: Partial<AuditEntry> = {}): AuditEntry {
  return {
    sequence: 1, eventId: "0192f0c1-0000-7000-8000-00000000a001", eventType: "assets.asset_changed.v1", occurredAt: "2026-10-28T09:30:00Z",
    actor: "user-ali", actorName: "Ali Ben Salah", entityType: "asset", entityId: "0192f0c1-0000-7000-8000-000000007001",
    siteId: "0192f0c1-0000-7000-8000-000000005001", event: recordedEvent({ assetId: "0192f0c1-0000-7000-8000-000000007001", before: { name: "Pelle" }, after: { name: "Pelle Cat 336" } }),
    changes: [{ path: "name", before: "Pelle", after: "Pelle Cat 336" }], ...overrides,
  };
}

/** A company of the operator console: Carrière Nord on a Professional trial, 2 sites, 3 machines, 4 office users. */
export function operatorTenant(overrides: Partial<OperatorTenant> = {}): OperatorTenant {
  return {
    id: "0192f0c1-0000-7000-8000-0000000c0001", slug: "carriere-nord", name: "Carrière Nord", createdAt: "2026-10-01T08:00:00Z",
    planCode: "professional", planVersion: 1, status: "trial", trialEndsAt: "2026-10-31T08:00:00Z", mrr: 0, mrrCurrency: "EUR",
    sites: 2, assets: 3, officeUsers: 4, lastActivity: "2026-10-28T16:00:00Z", complete: true, ...overrides,
  };
}

/** A company's subscription as the API returns it (default: a professional trial on one site). */
export function subscriptionOf(overrides: Partial<Subscription> = {}): Subscription {
  return {
    id: "01970000-0000-7000-8000-0000000000bb", status: "trial", statusSince: "2026-10-01T08:00:00Z", trialEndsAt: "2026-10-31T08:00:00Z",
    unpaidSince: null, planCode: "professional", planVersion: 1, period: "monthly", sites: 1, extraAssets: 0, addOns: {}, extraFeatures: [], limitOverrides: {},
    version: '"1"', ...overrides,
  };
}

/** A support session as the API lists it (default: a running session opened with the company's code). */
export function supportSession(overrides: Partial<SupportSession> = {}): SupportSession {
  return {
    id: "0192f0c1-0000-7000-8000-00000000d001", tenantId: "0192f0c1-0000-7000-8000-0000000000a1", mode: "consent", operatorName: "Anna Support",
    reason: null, startedAt: "2026-10-29T10:00:00Z", endsAt: "2026-10-29T11:00:00Z", endedAt: null, active: true, ...overrides,
  };
}

import type { components } from "@karry/api-client";

export type Role = components["schemas"]["RoleResponse"];
export type User = components["schemas"]["UserResponse"];
export type Tenant = components["schemas"]["TenantResponse"];
export type Site = components["schemas"]["SiteResponse"];
export type SiteInput = components["schemas"]["SiteInput"];
export type ShiftPattern = components["schemas"]["ShiftPatternResponse"];
export type NonWorkingDay = components["schemas"]["NonWorkingDayResponse"];
export type ShiftInstance = components["schemas"]["ShiftInstanceResponse"];
export type Material = components["schemas"]["MaterialResponse"];
export type IntegrityRule = components["schemas"]["RuleResponse"];
export type IntegrityFlag = components["schemas"]["IntegrityFlagResponse"];
export type PolicyVersion = components["schemas"]["ReviewPolicyVersionResponse"];
export type MaterialInput = components["schemas"]["MaterialInput"];
export type DensityVersion = components["schemas"]["DensityVersionResponse"];
export type DensityInput = components["schemas"]["DensityInput"];
export type ConversionInput = components["schemas"]["ConversionInput"];
export type Asset = components["schemas"]["AssetResponse"];
export type AssetInput = components["schemas"]["AssetInput"];
export type Meter = components["schemas"]["MeterResponse"];
export type Reading = components["schemas"]["ReadingResponse"];
export type ImportJob = components["schemas"]["ImportResponse"];
export type ImportIssue = components["schemas"]["ImportIssueResponse"];
export type ImportPlannedRow = components["schemas"]["ImportRowResponse"];
export type Device = components["schemas"]["DeviceResponse"];
export type SupportSession = components["schemas"]["SupportSessionResponse"];
export type AuditEntry = components["schemas"]["AuditEntryResponse"];
export type EnvelopeVerification = components["schemas"]["EnvelopeVerification"];
export type OperatorTenant = components["schemas"]["OperatorTenantResponse"];
export type Verification = components["schemas"]["LatestVerificationResponse"];
export type Subscription = components["schemas"]["SubscriptionResponse"];
export type Invoice = components["schemas"]["InvoiceResponse"];
export type Checklist = components["schemas"]["ChecklistResponse"];
export type PublishChecklist = components["schemas"]["PublishChecklist"];
export type Hold = components["schemas"]["HoldResponse"];
export type ReviewItem = components["schemas"]["ReviewItemResponse"];
export type ShiftEntry = components["schemas"]["ShiftEntryResponse"];
export type Notice = components["schemas"]["NotificationResponse"];
export type Customer = components["schemas"]["CustomerResponse"];
export type CustomerInput = components["schemas"]["CustomerInput"];
export type Product = components["schemas"]["ProductResponse"];
export type ProductInput = components["schemas"]["ProductInput"];
export type PriceList = components["schemas"]["PriceListResponse"];
export type PriceListInput = components["schemas"]["PriceListInput"];
export type PriceQuote = components["schemas"]["PriceQuoteResponse"];
export type Truck = components["schemas"]["TruckResponse"];
export type TruckInput = components["schemas"]["TruckInput"];
export type Hauler = components["schemas"]["HaulerResponse"];
export type HaulerInput = components["schemas"]["HaulerInput"];

export interface Sent {
  method: string;
  path: string;
  body: unknown;
}

/** What a test sets up: the caller's permissions and the data each API answers with. */
export interface FakeApiOptions {
  permissions: string[];
  users?: User[];
  needsStepUp?: () => boolean;
  failWith?: { status: number; code: string };
  /** Every write answers this problem (reads still work). */
  refuseWrites?: { status: number; code: string };
  /** Feature codes of the company's plan (default: a Professional trial). */
  features?: string[];
  /** Flags of `GET /flags` (default: none set, so every flag has its default). */
  flags?: Record<string, boolean>;
  /** Subscription state in `GET /billing/entitlements` (default: trial). */
  status?: string;
  /**
   * The company of `GET /tenancy/current` (default: Carrière A with the default settings, except its language: none
   * unless given, so the test's own language stays).
   */
  tenant?: Partial<Tenant>;
  /** Usage lines of `GET /billing/usage` (default: 3 of 15 office users). */
  usage?: { metric: string; usage: number; limit: number | null; ceiling: number | null; state: string }[];
  /** Sites of `GET /organization/sites` (default: none). */
  sites?: Site[];
  /** Pattern versions by site ID. */
  patterns?: Record<string, ShiftPattern[]>;
  /** Non-working days by site ID. */
  days?: Record<string, NonWorkingDay[]>;
  /** What `GET /sites/{id}/shift-instances` answers for every site. */
  instances?: ShiftInstance[];
  /** Customers of `GET /sales/customers` (default: none). */
  customers?: Customer[];
  /** Products of `GET /sales/products` (default: none). */
  products?: Product[];
  /** Trucks of `GET /weighbridge/trucks` (default: none). */
  trucks?: Truck[];
  /** Haulers of `GET /weighbridge/haulers` (default: none). */
  haulers?: Hauler[];
  /** Price list versions of `GET /sales/price-lists` (default: none). */
  priceLists?: PriceList[];
  /** Answers of `GET /sales/prices` by product ID; a product without one has no price (404 `sales.no_price`). */
  quotes?: Record<string, PriceQuote>;
  /** Materials of `GET /materials` (default: none). */
  materials?: Material[];
  /** The company's integrity rules (default: the catalogue with its defaults, `integrityCatalog()`). */
  integrityRules?: IntegrityRule[];
  /** Published review policy versions of shift entries, oldest first (default: none, the defaults apply). */
  policyVersions?: PolicyVersion[];
  /** Integrity flags of `GET /integrity/flags`, newest first (default: none). */
  integrityFlags?: IntegrityFlag[];
  /** Safety holds, open or released (default: none). */
  holds?: Hold[];
  /** Review items of `GET /verification/items`, in creation order (default: none); the fake sorts them by risk. */
  reviewItems?: ReviewItem[];
  /** Shift entries as they stand, for `GET /fieldops/entries?ids=` and their photos (default: none). */
  entries?: ShiftEntry[];
  /** A correction is beyond the four-eyes thresholds: its next item waits for a second approval (default: no). */
  correctionWaits?: boolean;
  /** The signed-in person's notifications, newest first (default: none). */
  notices?: Notice[];
  /** Notice types the company switched off (default: none). */
  noticeRulesOff?: string[];
  /** The VAPID key while Web Push is on for the company (default: off, 404). */
  pushKey?: string;
  /** What `GET /identity/me/field-account` answers (default: linked, no namesake). */
  fieldAccount?: { linked: boolean; unlinkedNamesake: boolean };
  /** Every published checklist version, any machine type (default: none). */
  checklists?: Checklist[];
  /** Machines of `GET /assets` (default: none). */
  assets?: Asset[];
  /** Meters by asset ID. */
  meters?: Record<string, Meter[]>;
  /** Readings by meter ID, newest first. */
  readings?: Record<string, Reading[]>;
  /** What the validation of every uploaded file finds (default: one site to create, no issue). */
  importReport?: { issues: ImportIssue[]; rows: ImportPlannedRow[] };
  /** Support sessions of `GET /identity/support/sessions`, newest first (default: none). */
  supportSessions?: SupportSession[];
  /** Tablets of `GET /identity/devices` (default: none). */
  devices?: Device[];
  /** The audit log, newest first (default: empty); people's names come from the entries. */
  auditEntries?: AuditEntry[];
  /** Verifications of signature envelopes by ID (`GET /audit/envelopes/{id}/verification`); any other ID is 404. */
  envelopeVerifications?: Record<string, EnvelopeVerification>;
  /** What a run of the integrity check finds (default: an intact trail of every audit entry). */
  verifyFinds?: { valid: boolean; firstBrokenSequence: number | null; problem: string | null };
  /** The export answers `audit.export_too_large` (more than 100 000 matching entries). */
  exportTooLarge?: boolean;
  /** Companies of the operator console (`GET /tenancy/tenants`, default: none); new ones are added as trials. */
  operatorTenants?: OperatorTenant[];
  /** The operator's token has no second factor yet (`auth.mfa_required` on the operator console) while this says so. */
  operatorWithoutMfa?: () => boolean;
  /** Whether the first administrator's invitation of a new company is delivered (default: yes). */
  invitationDelivered?: boolean;
  /** Each company's subscription for the operator console, by company ID (default: a professional trial). */
  tenantSubscriptions?: Record<string, Subscription>;
  /** Each company's invoices for the operator console, by company ID (default: none). */
  tenantInvoices?: Record<string, Invoice[]>;
}

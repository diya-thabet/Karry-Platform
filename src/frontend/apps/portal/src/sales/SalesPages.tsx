import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link, useNavigate } from "@tanstack/react-router";
import { SearchX } from "lucide-react";
import { useState } from "react";
import { ApiError } from "@karry/api-client";
import { intlLocale, useTranslation } from "@karry/i18n";
import { Button, Checkbox, DataTable, EmptyState, PageHeader, PageSkeleton, SelectField, StatusChip, TextField, useToast } from "@karry/ui";
import { useAccess, useApi, useCurrentTenant, useWithStepUp } from "../app/api";
import { VersionConflict } from "../app/VersionConflict";
import { handlesVersionConflict, isVersionConflict } from "../app/conflicts";
import { useRecordDraft } from "../app/recordDraft";
import { useMaterials } from "../materials/api";
import { Trail } from "../shell/Trail";
import { todayIn, useSites } from "../sites/api";
import {
  customerKey, customersKey, CustomersManagePermission, PricesManagePermission, priceListsKey, priceText, productKey, productsKey, ProductsManagePermission,
  saleUnits, useCustomers, usePriceLists, useProducts, type Customer, type CustomerInput, type PriceList, type PriceListInput, type PriceQuote, type Product,
  type ProductInput,
} from "./api";

const card = "flex flex-col gap-6 rounded-xl border-[length:var(--karry-border-width)] border-divider bg-surface p-5 shadow-[var(--karry-card-shadow)] md:p-6";
const link = "font-semibold text-primary underline-offset-4 hover:underline";

function useFormats() {
  const { i18n } = useTranslation();
  const locale = intlLocale(i18n.language);
  return {
    money: (amount: number | string, currency: string) => new Intl.NumberFormat(locale, { style: "currency", currency, maximumFractionDigits: 4 }).format(Number(amount)),
    date: (value: string) => new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeZone: "UTC" }).format(new Date(`${value}T00:00:00Z`)),
  };
}

function Hidden() {
  const { t } = useTranslation();
  return <span title={t("portal.sales.hiddenByPolicy")} aria-label={t("portal.sales.hiddenByPolicy")}>•••</span>;
}

// ── Customers ────────────────────────────────────────────────────────────────────────────────────────────────────

/** The company's customers (US-P3-SAL-01): search by code, name or tax ID; blocked ones show their reason. */
export function CustomersPage() {
  const { t } = useTranslation();
  const { can } = useAccess();
  const [status, setStatus] = useState<"" | "active" | "blocked">("");
  const [search, setSearch] = useState("");
  const customers = useCustomers(status, search);

  return (
    <div className="flex flex-col gap-4">
      <PageHeader
        breadcrumbs={<Trail steps={[{ to: "/settings", label: t("portal.nav.settings") }]} />}
        title={t("portal.sales.customers.title")}
        actions={can(CustomersManagePermission) && (
          <Button asChild>
            <Link to="/settings/customers/new">{t("portal.sales.customers.new")}</Link>
          </Button>
        )}
      />
      <div className="grid gap-4 md:grid-cols-3">
        <TextField label={t("portal.sales.customers.search")} value={search} onChange={(e) => { setSearch(e.target.value); }} />
        <SelectField
          label={t("portal.sales.status")}
          value={status || "all"}
          onValueChange={(value) => { setStatus(value === "all" ? "" : value as "active" | "blocked"); }}
          options={[
            { value: "all", label: t("portal.sales.status.all") },
            { value: "active", label: t("portal.sales.status.active") },
            { value: "blocked", label: t("portal.sales.status.blocked") },
          ]}
        />
      </div>
      <DataTable<Customer>
        caption={t("portal.sales.customers.title")}
        rows={customers.data ?? []}
        rowKey={(customer) => customer.id}
        loading={customers.isPending && customers.fetchStatus !== "idle"}
        empty={<EmptyState title={t("portal.sales.customers.none")} />}
        columns={[
          { key: "code", header: t("portal.sales.code"), cell: (customer) => <span translate="no">{customer.code}</span> },
          {
            key: "name",
            header: t("portal.sales.customers.legalName"),
            cell: (customer) => (
              <Link to="/settings/customers/$customerId" params={{ customerId: customer.id }} className={link} translate="no">{customer.legalName}</Link>
            ),
          },
          { key: "taxId", header: t("portal.sales.customers.taxId"), cell: (customer) => <span translate="no">{customer.taxId ?? "—"}</span> },
          { key: "basis", header: t("portal.sales.customers.basis"), cell: (customer) => t(`portal.sales.unit.${customer.basis}`) },
          { key: "status", header: t("portal.sales.status"), cell: (customer) => <CustomerStatus customer={customer} /> },
        ]}
      />
    </div>
  );
}

function CustomerStatus({ customer }: { customer: Customer }) {
  const { t } = useTranslation();
  return customer.status === "active"
    ? <StatusChip status="ok">{t("portal.sales.status.active")}</StatusChip>
    : <StatusChip status="error">{t("portal.sales.status.blocked")}</StatusChip>;
}

/** One customer, or a new one. */
export function CustomerPage({ customerId }: { customerId?: string }) {
  const { t } = useTranslation();
  const api = useApi();
  const customer = useQuery({
    queryKey: customerKey(customerId ?? "new"),
    queryFn: async () => (await api.GET("/api/v1/sales/customers/{customerId}", { params: { path: { customerId: customerId ?? "" } } })).data ?? null,
    enabled: customerId !== undefined,
  });

  if (customerId !== undefined && customer.isPending) {
    return <PageSkeleton />;
  }

  if (customerId !== undefined && !customer.data) {
    return <EmptyState icon={SearchX} title={t("portal.notFound")} />;
  }

  return (
    <div className="flex max-w-4xl flex-col gap-8">
      <CustomerForm key={customer.data?.id ?? "new"} customer={customer.data ?? null} />
      {customer.data && <Blocking customer={customer.data} />}
    </div>
  );
}

interface CustomerDraft {
  legalName: string;
  taxId: string;
  paymentTermsDays: string;
  basis: string;
  line1: string;
  city: string;
  country: string;
  contactName: string;
  contactPhone: string;
  contactEmail: string;
  sites: string[];
}

function customerDraft(customer: Customer | null): CustomerDraft {
  const address = customer?.addresses[0];
  const contact = customer?.contacts[0];
  return {
    legalName: customer?.legalName ?? "", taxId: customer?.taxId ?? "", paymentTermsDays: String(customer?.paymentTermsDays ?? 30), basis: customer?.basis ?? "t",
    line1: address?.line1 ?? "", city: address?.city ?? "", country: address?.country ?? "",
    contactName: contact?.name ?? "", contactPhone: contact?.phone ?? "", contactEmail: contact?.email ?? "",
    sites: [...(customer?.allowedSiteIds ?? [])],
  };
}

function CustomerForm({ customer }: { customer: Customer | null }) {
  const { t } = useTranslation();
  const api = useApi();
  const withStepUp = useWithStepUp();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const toast = useToast();
  const { can } = useAccess();
  const { tenant } = useCurrentTenant();
  const sites = useSites();
  const editable = can(CustomersManagePermission);
  const [code, setCode] = useState(customer?.code ?? "");
  const { draft, setDraft, version, accept } = useRecordDraft(customer, customerDraft);
  const set = (key: keyof Omit<CustomerDraft, "sites">, value: string) => { setDraft((current) => ({ ...current, [key]: value })); };
  // The credit limit is a sensitive field: masked for some roles, then neither shown nor sent (left out = kept).
  const limitMasked = customer?.maskedFields?.includes("creditLimit") ?? false;
  const [limit, setLimit] = useState(customer?.creditLimit ? String(customer.creditLimit.amount) : "");
  const [conflict, setConflict] = useState(false);

  const body = (): CustomerInput => {
    // The first address and contact are edited here; any others are kept as they are.
    const otherAddresses = customer?.addresses.slice(1) ?? [];
    const otherContacts = customer?.contacts.slice(1) ?? [];
    const address = draft.line1.trim() || draft.city.trim() || draft.country.trim()
      ? [{ kind: "billing", line1: draft.line1, line2: null, city: draft.city, region: null, postalCode: null, country: draft.country }]
      : [];
    const contact = draft.contactName.trim()
      ? [{ name: draft.contactName, role: null, phone: draft.contactPhone || null, email: draft.contactEmail || null }]
      : [];
    return {
      code: customer ? null : code, legalName: draft.legalName, taxId: draft.taxId || null, paymentTermsDays: Number(draft.paymentTermsDays), basis: draft.basis,
      addresses: [...address, ...otherAddresses], contacts: [...contact, ...otherContacts], allowedSiteIds: draft.sites,
      creditLimit: limitMasked ? null : { amount: limit.trim() ? priceText(limit) : null, currency: customer?.creditLimit?.currency ?? tenant?.currency ?? "EUR" },
    };
  };

  const refresh = async (saved: Customer) => {
    await queryClient.invalidateQueries({ queryKey: customersKey });
    queryClient.setQueryData(customerKey(saved.id), saved);
  };

  const save = useMutation({
    meta: handlesVersionConflict,
    mutationFn: async (expected?: string) => withStepUp(async () => {
      if (!customer) {
        return (await api.POST("/api/v1/sales/customers", { body: body() })).data;
      }

      const tag = expected ?? version;
      return (await api.PUT("/api/v1/sales/customers/{customerId}", {
        params: { path: { customerId: customer.id }, header: tag ? { "If-Match": tag } : {} },
        body: body(),
      })).data;
    }),
    onError: (error) => { setConflict(isVersionConflict(error)); },
    onSuccess: async (saved) => {
      if (!saved) {
        return;
      }

      accept(saved);
      setConflict(false);
      await refresh(saved);
      toast({ status: "ok", title: t("portal.sales.saved") });
      if (!customer) {
        await navigate({ to: "/settings/customers/$customerId", params: { customerId: saved.id } });
      }
    },
  });

  const latest = async () => customer ? (await api.GET("/api/v1/sales/customers/{customerId}", { params: { path: { customerId: customer.id } } })).data : undefined;
  const keepMine = async () => {
    const current = await latest();
    if (current) {
      save.mutate(current.version);
    }
  };
  const showTheirs = async () => {
    const current = await latest();
    if (current) {
      accept(current);
      setConflict(false);
      await refresh(current);
    }
  };

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        breadcrumbs={<Trail steps={[{ to: "/settings", label: t("portal.nav.settings") }, { to: "/settings/customers", label: t("portal.sales.customers.title") }]} />}
        title={customer ? customer.legalName : t("portal.sales.customers.new")}
        titleTranslate={customer ? "no" : undefined}
        status={customer && <CustomerStatus customer={customer} />}
      >
        {customer?.status === "blocked" && <p className="font-semibold text-danger">{t("portal.sales.customers.blockedBecause", { reason: customer.blockedReason ?? "" })}</p>}
        {!editable && <p className="text-text-muted">{t("portal.sales.readOnly")}</p>}
      </PageHeader>

      <form className={card} noValidate onSubmit={(event) => { event.preventDefault(); save.mutate(undefined); }}>
        <fieldset disabled={!editable} className="grid gap-4 md:grid-cols-3">
          <TextField
            label={t("portal.sales.code")}
            description={customer ? t("portal.sales.codeFixed") : t("portal.sales.customers.codeHint")}
            value={code}
            required
            disabled={customer !== null || !editable}
            onChange={(e) => { setCode(e.target.value.toUpperCase()); }}
          />
          <TextField label={t("portal.sales.customers.legalName")} value={draft.legalName} required onChange={(e) => { set("legalName", e.target.value); }} />
          <TextField label={t("portal.sales.customers.taxId")} value={draft.taxId} onChange={(e) => { set("taxId", e.target.value); }} />
          <TextField label={t("portal.sales.customers.paymentTerms")} inputMode="numeric" value={draft.paymentTermsDays} onChange={(e) => { set("paymentTermsDays", e.target.value); }} />
          <SelectField
            label={t("portal.sales.customers.basis")}
            value={draft.basis}
            onValueChange={(value) => { set("basis", value); }}
            options={saleUnits.map((value) => ({ value, label: t(`portal.sales.unit.${value}`) }))}
            disabled={!editable}
          />
          {limitMasked
            ? <p className="self-end text-text-muted">{t("portal.sales.customers.creditLimit")}: <Hidden /></p>
            : (
              <TextField
                label={`${t("portal.sales.customers.creditLimit")} (${customer?.creditLimit?.currency ?? tenant?.currency ?? "EUR"})`}
                description={t("portal.sales.customers.creditLimitHint")}
                inputMode="decimal"
                value={limit}
                onChange={(e) => { setLimit(e.target.value); }}
              />
            )}
        </fieldset>

        <fieldset disabled={!editable} className="grid gap-4 md:grid-cols-3">
          <legend className="mb-2 text-lg font-semibold">{t("portal.sales.customers.billingAddress")}</legend>
          <TextField label={t("portal.sales.customers.addressLine")} value={draft.line1} onChange={(e) => { set("line1", e.target.value); }} />
          <TextField label={t("portal.sales.customers.city")} value={draft.city} onChange={(e) => { set("city", e.target.value); }} />
          <TextField label={t("portal.sales.customers.country")} value={draft.country} onChange={(e) => { set("country", e.target.value.toUpperCase()); }} />
        </fieldset>

        <fieldset disabled={!editable} className="grid gap-4 md:grid-cols-3">
          <legend className="mb-2 text-lg font-semibold">{t("portal.sales.customers.contact")}</legend>
          <TextField label={t("portal.sales.customers.contactName")} value={draft.contactName} onChange={(e) => { set("contactName", e.target.value); }} />
          <TextField label={t("portal.sales.customers.contactPhone")} value={draft.contactPhone} onChange={(e) => { set("contactPhone", e.target.value); }} />
          <TextField label={t("portal.sales.customers.contactEmail")} type="email" value={draft.contactEmail} onChange={(e) => { set("contactEmail", e.target.value); }} />
        </fieldset>

        <fieldset disabled={!editable} className="flex flex-col gap-2">
          <legend className="mb-2 text-lg font-semibold">{t("portal.sales.customers.sites")}</legend>
          <p className="text-text-muted">{t("portal.sales.customers.sitesHint")}</p>
          <div className="flex flex-wrap gap-4">
            {(sites.data ?? []).map((site) => (
              <Checkbox
                key={site.id}
                label={site.name}
                checked={draft.sites.includes(site.id)}
                onChange={(e) => {
                  const checked = e.target.checked;
                  setDraft((current) => ({ ...current, sites: checked ? [...current.sites, site.id] : current.sites.filter((id) => id !== site.id) }));
                }}
              />
            ))}
          </div>
        </fieldset>

        {conflict && <VersionConflict busy={save.isPending} onKeepMine={() => void keepMine()} onShowTheirs={() => void showTheirs()} />}

        {editable && (
          <div>
            <Button type="submit" loading={save.isPending}>{customer ? t("portal.sales.save") : t("portal.sales.customers.create")}</Button>
          </div>
        )}
      </form>
    </div>
  );
}

/** US-P3-SAL-01 AC2: a blocked customer gets no new ticket; the reason is what the weighmaster reads. */
function Blocking({ customer }: { customer: Customer }) {
  const { t } = useTranslation();
  const api = useApi();
  const withStepUp = useWithStepUp();
  const queryClient = useQueryClient();
  const toast = useToast();
  const { can } = useAccess();
  const [reason, setReason] = useState("");
  const [checked, setChecked] = useState(false);

  const act = useMutation({
    mutationFn: async (action: "block" | "unblock") => withStepUp(async () => action === "block"
      ? (await api.POST("/api/v1/sales/customers/{customerId}/block", { params: { path: { customerId: customer.id } }, body: { reason } })).data
      : (await api.POST("/api/v1/sales/customers/{customerId}/unblock", { params: { path: { customerId: customer.id } } })).data),
    onSuccess: async (saved, action) => {
      if (saved) {
        setReason("");
        setChecked(false);
        await queryClient.invalidateQueries({ queryKey: customersKey });
        queryClient.setQueryData(customerKey(saved.id), saved);
        toast({ status: "ok", title: t(action === "block" ? "portal.sales.customers.blocked" : "portal.sales.customers.unblocked") });
      }
    },
  });

  if (!can(CustomersManagePermission)) {
    return null;
  }

  if (customer.status === "blocked") {
    return (
      <div>
        <Button loading={act.isPending} onClick={() => { act.mutate("unblock"); }}>{t("portal.sales.customers.unblock")}</Button>
      </div>
    );
  }

  const missing = checked && reason.trim() === "";
  return (
    <form className={card} noValidate onSubmit={(event) => { event.preventDefault(); setChecked(true); if (reason.trim()) { act.mutate("block"); } }}>
      <h2 className="text-lg font-semibold">{t("portal.sales.customers.block")}</h2>
      <TextField
        label={t("portal.sales.customers.blockReason")}
        description={t("portal.sales.customers.blockReasonHint")}
        value={reason}
        required
        error={missing ? t("portal.sales.customers.blockReasonRequired") : undefined}
        onChange={(e) => { setReason(e.target.value); }}
      />
      <div>
        <Button type="submit" variant="secondary" loading={act.isPending}>{t("portal.sales.customers.block")}</Button>
      </div>
    </form>
  );
}

// ── Products ─────────────────────────────────────────────────────────────────────────────────────────────────────

/** What the company sells (US-P3-SAL-02 AC1): each a material fraction with its sale unit. */
export function ProductsPage() {
  const { t } = useTranslation();
  const { can } = useAccess();
  const [includeArchived, setIncludeArchived] = useState(false);
  const products = useProducts(includeArchived);
  const materials = useMaterials(true);
  const materialName = (id: string) => materials.data?.find((m) => m.id === id)?.code ?? "—";

  return (
    <div className="flex flex-col gap-4">
      <PageHeader
        breadcrumbs={<Trail steps={[{ to: "/settings", label: t("portal.nav.settings") }]} />}
        title={t("portal.sales.products.title")}
        actions={can(ProductsManagePermission) && (
          <Button asChild>
            <Link to="/settings/products/new">{t("portal.sales.products.new")}</Link>
          </Button>
        )}
      />
      <Checkbox label={t("portal.sales.includeArchived")} checked={includeArchived} onChange={(e) => { setIncludeArchived(e.target.checked); }} className="self-start" />
      <DataTable<Product>
        caption={t("portal.sales.products.title")}
        rows={products.data ?? []}
        rowKey={(product) => product.id}
        loading={products.isPending && products.fetchStatus !== "idle"}
        empty={<EmptyState title={t("portal.sales.products.none")} />}
        columns={[
          { key: "code", header: t("portal.sales.code"), cell: (product) => <span translate="no">{product.code}</span> },
          {
            key: "name",
            header: t("portal.sales.name"),
            cell: (product) => <Link to="/settings/products/$productId" params={{ productId: product.id }} className={link} translate="no">{product.name}</Link>,
          },
          { key: "material", header: t("portal.sales.products.material"), cell: (product) => <span translate="no">{materialName(product.materialId)}</span> },
          { key: "unit", header: t("portal.sales.products.saleUnit"), cell: (product) => t(`portal.sales.unit.${product.saleUnit}`) },
          {
            key: "status",
            header: t("portal.sales.status"),
            cell: (product) => product.status === "active"
              ? <StatusChip status="ok">{t("portal.sales.status.active")}</StatusChip>
              : <StatusChip status="pending">{t("portal.sales.status.archived")}</StatusChip>,
          },
        ]}
      />
    </div>
  );
}

/** One product, or a new one. */
export function ProductPage({ productId }: { productId?: string }) {
  const { t } = useTranslation();
  const api = useApi();
  const product = useQuery({
    queryKey: productKey(productId ?? "new"),
    queryFn: async () => (await api.GET("/api/v1/sales/products/{productId}", { params: { path: { productId: productId ?? "" } } })).data ?? null,
    enabled: productId !== undefined,
  });

  if (productId !== undefined && product.isPending) {
    return <PageSkeleton />;
  }

  if (productId !== undefined && !product.data) {
    return <EmptyState icon={SearchX} title={t("portal.notFound")} />;
  }

  return <ProductForm key={product.data?.id ?? "new"} product={product.data ?? null} />;
}

function ProductForm({ product }: { product: Product | null }) {
  const { t } = useTranslation();
  const api = useApi();
  const withStepUp = useWithStepUp();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const toast = useToast();
  const { can } = useAccess();
  const materials = useMaterials();
  const editable = can(ProductsManagePermission) && product?.status !== "archived";
  const [code, setCode] = useState(product?.code ?? "");
  const { draft, setDraft, version, accept } = useRecordDraft(product, (record) => ({
    name: record?.name ?? "", materialId: record?.materialId ?? "", saleUnit: record?.saleUnit ?? "t",
  }));
  // Only fractions that are sold (raw rock is not a product); the product's own material stays listed even if archived since.
  const fractions = (materials.data ?? []).filter((m) => m.kind === "product" || m.id === product?.materialId);

  const refresh = async (saved: Product) => {
    await queryClient.invalidateQueries({ queryKey: productsKey });
    queryClient.setQueryData(productKey(saved.id), saved);
  };
  const [conflict, setConflict] = useState(false);

  const save = useMutation({
    meta: handlesVersionConflict,
    mutationFn: async (expected?: string) => withStepUp(async () => {
      const body: ProductInput = { code: product ? null : code, name: draft.name, materialId: draft.materialId || null, saleUnit: draft.saleUnit };
      if (!product) {
        return (await api.POST("/api/v1/sales/products", { body })).data;
      }

      const tag = expected ?? version;
      return (await api.PUT("/api/v1/sales/products/{productId}", { params: { path: { productId: product.id }, header: tag ? { "If-Match": tag } : {} }, body })).data;
    }),
    onError: (error) => { setConflict(isVersionConflict(error)); },
    onSuccess: async (saved) => {
      if (!saved) {
        return;
      }

      accept(saved);
      setConflict(false);
      await refresh(saved);
      toast({ status: "ok", title: t("portal.sales.saved") });
      if (!product) {
        await navigate({ to: "/settings/products/$productId", params: { productId: saved.id } });
      }
    },
  });

  const archive = useMutation({
    mutationFn: async (action: "archive" | "restore") => withStepUp(async () => action === "archive"
      ? (await api.POST("/api/v1/sales/products/{productId}/archive", { params: { path: { productId: product?.id ?? "" } } })).data
      : (await api.POST("/api/v1/sales/products/{productId}/restore", { params: { path: { productId: product?.id ?? "" } } })).data),
    onSuccess: async (saved, action) => {
      if (saved) {
        await refresh(saved);
        toast({ status: "ok", title: t(action === "archive" ? "portal.sales.archived" : "portal.sales.restored") });
      }
    },
  });

  const latest = async () => product ? (await api.GET("/api/v1/sales/products/{productId}", { params: { path: { productId: product.id } } })).data : undefined;

  return (
    <div className="flex max-w-4xl flex-col gap-6">
      <PageHeader
        breadcrumbs={<Trail steps={[{ to: "/settings", label: t("portal.nav.settings") }, { to: "/settings/products", label: t("portal.sales.products.title") }]} />}
        title={product ? product.name : t("portal.sales.products.new")}
        titleTranslate={product ? "no" : undefined}
      >
        {product?.status === "archived" && <p className="text-text-muted">{t("portal.sales.products.archivedHint")}</p>}
        {!can(ProductsManagePermission) && <p className="text-text-muted">{t("portal.sales.readOnly")}</p>}
      </PageHeader>

      <form className={card} noValidate onSubmit={(event) => { event.preventDefault(); save.mutate(undefined); }}>
        <fieldset disabled={!editable} className="grid gap-4 md:grid-cols-2">
          <TextField
            label={t("portal.sales.code")}
            description={product ? t("portal.sales.codeFixed") : t("portal.sales.products.codeHint")}
            value={code}
            required
            disabled={product !== null || !editable}
            onChange={(e) => { setCode(e.target.value.toUpperCase()); }}
          />
          <TextField label={t("portal.sales.name")} value={draft.name} required onChange={(e) => { setDraft((d) => ({ ...d, name: e.target.value })); }} />
          <SelectField
            label={t("portal.sales.products.material")}
            description={t("portal.sales.products.materialHint")}
            value={draft.materialId}
            onValueChange={(value) => { setDraft((d) => ({ ...d, materialId: value })); }}
            options={fractions.map((m) => ({ value: m.id, label: `${m.code} — ${m.name}` }))}
            disabled={!editable}
          />
          <SelectField
            label={t("portal.sales.products.saleUnit")}
            value={draft.saleUnit}
            onValueChange={(value) => { setDraft((d) => ({ ...d, saleUnit: value })); }}
            options={saleUnits.map((value) => ({ value, label: t(`portal.sales.unit.${value}`) }))}
            disabled={!editable}
          />
        </fieldset>

        {conflict && (
          <VersionConflict
            busy={save.isPending}
            onKeepMine={() => void latest().then((current) => { if (current) { save.mutate(current.version); } })}
            onShowTheirs={() => void latest().then(async (current) => { if (current) { accept(current); setConflict(false); await refresh(current); } })}
          />
        )}

        {editable && (
          <div>
            <Button type="submit" loading={save.isPending}>{product ? t("portal.sales.save") : t("portal.sales.products.create")}</Button>
          </div>
        )}
      </form>

      {product && can(ProductsManagePermission) && (
        <div>
          {product.status === "active"
            ? <Button variant="secondary" loading={archive.isPending} onClick={() => { archive.mutate("archive"); }}>{t("portal.sales.archive")}</Button>
            : <Button loading={archive.isPending} onClick={() => { archive.mutate("restore"); }}>{t("portal.sales.restore")}</Button>}
        </div>
      )}
    </div>
  );
}

// ── Price lists ──────────────────────────────────────────────────────────────────────────────────────────────────

/**
 * Price list versions per site (US-P3-SAL-02 AC2, ADR-0042): the site's prices and customers' own, each from a date on the
 * company's clock until the next version. A version in force never changes; a new version corrects it. Unit prices are
 * hidden from roles that may not read them.
 */
export function PriceListsPage() {
  const { t } = useTranslation();
  const sites = useSites();
  const [siteId, setSiteId] = useState("");
  // Until the person picks one, the first site.
  const site = siteId !== "" ? siteId : (sites.data?.[0]?.id ?? "");

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        breadcrumbs={<Trail steps={[{ to: "/settings", label: t("portal.nav.settings") }]} />}
        title={t("portal.sales.prices.title")}
        description={t("portal.sales.prices.description")}
      />
      {(sites.data ?? []).length === 0
        ? <EmptyState title={t("portal.sales.prices.noSites")} />
        : (
          <>
            <div className="max-w-sm">
              <SelectField
                label={t("portal.sales.prices.site")}
                value={site}
                onValueChange={setSiteId}
                options={(sites.data ?? []).map((s) => ({ value: s.id, label: s.name }))}
              />
            </div>
            <Versions key={`versions-${site}`} siteId={site} />
            <NewVersion key={`new-${site}`} siteId={site} />
            <PricePreview key={`preview-${site}`} siteId={site} />
          </>
        )}
    </div>
  );
}

function Versions({ siteId }: { siteId: string }) {
  const { t } = useTranslation();
  const api = useApi();
  const withStepUp = useWithStepUp();
  const queryClient = useQueryClient();
  const toast = useToast();
  const { can } = useAccess();
  const { money, date } = useFormats();
  const lists = usePriceLists(siteId);
  const products = useProducts(true);
  const customers = useCustomers();
  const productCode = (id: string) => products.data?.find((p) => p.id === id)?.code ?? "—";
  const customerName = (id: string | null) => (id === null ? t("portal.sales.prices.everyCustomer") : customers.data?.find((c) => c.id === id)?.legalName ?? "—");

  const withdraw = useMutation({
    mutationFn: async (id: string) => withStepUp(async () => (await api.POST("/api/v1/sales/price-lists/{priceListId}/withdraw", { params: { path: { priceListId: id } } })).data),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: priceListsKey });
      toast({ status: "ok", title: t("portal.sales.prices.withdrawn") });
    },
  });

  const state = (list: PriceList) => {
    if (list.status === "withdrawn") {
      return <StatusChip status="warning">{t("portal.sales.prices.state.withdrawn")}</StatusChip>;
    }

    return list.inForce
      ? <StatusChip status="ok">{t("portal.sales.prices.state.inForce")}</StatusChip>
      : <StatusChip status="pending">{t("portal.sales.prices.state.scheduled")}</StatusChip>;
  };

  return (
    <DataTable<PriceList>
      caption={t("portal.sales.prices.versions")}
      rows={lists.data ?? []}
      rowKey={(list) => list.id}
      loading={lists.isPending && lists.fetchStatus !== "idle"}
      empty={<EmptyState title={t("portal.sales.prices.none")} />}
      columns={[
        { key: "from", header: t("portal.sales.prices.effectiveFrom"), cell: (list) => date(list.effectiveFrom) },
        { key: "customer", header: t("portal.sales.prices.customer"), cell: (list) => <span translate="no">{customerName(list.customerId)}</span> },
        {
          key: "lines",
          header: t("portal.sales.prices.lines"),
          cell: (list) => (
            <ul className="flex flex-col gap-1">
              {list.lines.map((line) => (
                <li key={line.productId}>
                  <span translate="no">{productCode(line.productId)}</span>
                  {" "}
                  {line.unitPrice === undefined || line.unitPrice === null ? <Hidden /> : money(line.unitPrice, list.currency)}
                </li>
              ))}
            </ul>
          ),
        },
        { key: "state", header: t("portal.sales.status"), cell: state },
        {
          key: "actions",
          header: t("portal.sales.actions"),
          cell: (list) => can(PricesManagePermission) && list.status === "active" && !list.inForce
            ? <Button variant="secondary" loading={withdraw.isPending} onClick={() => { withdraw.mutate(list.id); }}>{t("portal.sales.prices.withdraw")}</Button>
            : null,
        },
      ]}
    />
  );
}

/** A new version: from today or later, a price per product (an empty price leaves the product out of this list). */
function NewVersion({ siteId }: { siteId: string }) {
  const { t } = useTranslation();
  const api = useApi();
  const withStepUp = useWithStepUp();
  const queryClient = useQueryClient();
  const toast = useToast();
  const { can } = useAccess();
  const { tenant } = useCurrentTenant();
  const products = useProducts();
  const customers = useCustomers();
  const today = todayIn(tenant?.timeZone ?? "UTC");
  const [customerId, setCustomerId] = useState("");
  const [currency, setCurrency] = useState(tenant?.currency ?? "EUR");
  const [effectiveFrom, setEffectiveFrom] = useState(today);
  const [prices, setPrices] = useState<Record<string, string>>({});
  const [checked, setChecked] = useState(false);

  const filled = Object.entries(prices).filter(([, text]) => text.trim() !== "");
  const invalid = filled.some(([, text]) => priceText(text) === null);

  const create = useMutation({
    mutationFn: async () => withStepUp(async () => {
      const body: PriceListInput = {
        siteId, customerId: customerId || null, currency, effectiveFrom,
        lines: filled.map(([productId, text]) => ({ productId, unitPrice: priceText(text) })),
      };
      return (await api.POST("/api/v1/sales/price-lists", { body })).data;
    }),
    onSuccess: async (created) => {
      if (created) {
        setPrices({});
        setChecked(false);
        await queryClient.invalidateQueries({ queryKey: priceListsKey });
        toast({ status: "ok", title: t("portal.sales.prices.created") });
      }
    },
  });

  if (!can(PricesManagePermission)) {
    return null;
  }

  return (
    <form
      className={card}
      noValidate
      onSubmit={(event) => {
        event.preventDefault();
        setChecked(true);
        if (filled.length > 0 && !invalid) {
          create.mutate();
        }
      }}
    >
      <h2 className="text-lg font-semibold">{t("portal.sales.prices.new")}</h2>
      <p className="text-text-muted">{t("portal.sales.prices.newHint")}</p>
      <div className="grid gap-4 md:grid-cols-3">
        <SelectField
          label={t("portal.sales.prices.customer")}
          value={customerId || "all"}
          onValueChange={(value) => { setCustomerId(value === "all" ? "" : value); }}
          options={[{ value: "all", label: t("portal.sales.prices.everyCustomer") }, ...(customers.data ?? []).map((c) => ({ value: c.id, label: `${c.code} — ${c.legalName}` }))]}
        />
        <TextField label={t("portal.sales.prices.currency")} value={currency} maxLength={3} onChange={(e) => { setCurrency(e.target.value.toUpperCase()); }} />
        <TextField
          label={t("portal.sales.prices.effectiveFrom")}
          description={t("portal.sales.prices.effectiveFromHint")}
          type="date"
          min={today}
          value={effectiveFrom}
          onChange={(e) => { setEffectiveFrom(e.target.value); }}
        />
      </div>
      <div className="grid gap-4 md:grid-cols-3">
        {(products.data ?? []).map((product) => {
          const text = prices[product.id] ?? "";
          return (
            <TextField
              key={product.id}
              label={`${product.code} (${currency}/${t(`portal.sales.unitShort.${product.saleUnit}`)})`}
              inputMode="decimal"
              value={text}
              error={checked && text.trim() !== "" && priceText(text) === null ? t("portal.sales.prices.priceInvalid") : undefined}
              onChange={(e) => { setPrices((current) => ({ ...current, [product.id]: e.target.value })); }}
            />
          );
        })}
      </div>
      {checked && filled.length === 0 && <p role="alert" className="font-semibold text-danger">{t("portal.sales.prices.linesRequired")}</p>}
      <div>
        <Button type="submit" loading={create.isPending}>{t("portal.sales.prices.create")}</Button>
      </div>
    </form>
  );
}

/** "Price on date": the answer the weighbridge will get (customer's version, else the site's, else none). */
function PricePreview({ siteId }: { siteId: string }) {
  const { t } = useTranslation();
  const api = useApi();
  const { tenant } = useCurrentTenant();
  const { money, date } = useFormats();
  const products = useProducts();
  const customers = useCustomers();
  const [productId, setProductId] = useState("");
  const [customerId, setCustomerId] = useState("");
  const [day, setDay] = useState(todayIn(tenant?.timeZone ?? "UTC"));
  const [quote, setQuote] = useState<PriceQuote | null | undefined>(undefined);

  const ask = useMutation({
    mutationFn: async () => {
      // Midday UTC of the chosen day: its date on the company's clock is that day for every time zone within ±12 h.
      try {
        const response = await api.GET("/api/v1/sales/prices", {
          params: { query: { siteId, productId, ...(customerId ? { customerId } : {}), at: `${day}T12:00:00Z` } },
        });
        return response.data ?? null;
      } catch (error) {
        // No list prices it: an answer, not a failure (the desk shows the same).
        if (error instanceof ApiError && error.code === "sales.no_price") {
          return null;
        }

        throw error;
      }
    },
    onSuccess: (answer) => { setQuote(answer); },
  });

  return (
    <form className={card} noValidate onSubmit={(event) => { event.preventDefault(); if (productId) { ask.mutate(); } }}>
      <h2 className="text-lg font-semibold">{t("portal.sales.prices.preview")}</h2>
      <div className="grid gap-4 md:grid-cols-3">
        <SelectField
          label={t("portal.sales.prices.product")}
          value={productId}
          onValueChange={(value) => { setProductId(value); setQuote(undefined); }}
          options={(products.data ?? []).map((p) => ({ value: p.id, label: `${p.code} — ${p.name}` }))}
        />
        <SelectField
          label={t("portal.sales.prices.customer")}
          value={customerId || "all"}
          onValueChange={(value) => { setCustomerId(value === "all" ? "" : value); setQuote(undefined); }}
          options={[{ value: "all", label: t("portal.sales.prices.anyCustomer") }, ...(customers.data ?? []).map((c) => ({ value: c.id, label: `${c.code} — ${c.legalName}` }))]}
        />
        <TextField label={t("portal.sales.prices.date")} type="date" value={day} onChange={(e) => { setDay(e.target.value); setQuote(undefined); }} />
      </div>
      <div>
        <Button type="submit" variant="secondary" loading={ask.isPending} disabled={!productId}>{t("portal.sales.prices.ask")}</Button>
      </div>
      {quote === null && <p role="status" className="font-semibold">{t("portal.sales.prices.noPrice")}</p>}
      {quote && (
        <p role="status" className="font-semibold">
          {quote.unitPrice === undefined || quote.unitPrice === null ? <Hidden /> : money(quote.unitPrice, quote.currency)}
          {" / "}
          {t(`portal.sales.unitShort.${quote.saleUnit}`)}
          {" — "}
          {t(`portal.sales.prices.source.${quote.source}`, { date: date(quote.effectiveFrom) })}
        </p>
      )}
    </form>
  );
}

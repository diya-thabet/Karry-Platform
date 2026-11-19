import { useMutation, useQueryClient } from "@tanstack/react-query";
import { Link, useNavigate } from "@tanstack/react-router";
import { ArrowDown, ArrowUp, Trash2 } from "lucide-react";
import { useState } from "react";
import { intlLocale, languageDirection, useTranslation } from "@karry/i18n";
import { Button, Checkbox, DataTable, EmptyState, PageHeader, PageSkeleton, SelectField, StatusChip, TextField, useToast } from "@karry/ui";
import { useAccess, useApi, useCurrentTenant, useWithStepUp } from "../app/api";
import { useAssetTypes } from "../assets/api";
import { Trail } from "../shell/Trail";
import {
  checkItems, checklistsKey, ChecklistsManagePermission, checklistVersionsKey, draftsOf, itemsBody, languagesFor, maxItems, maxText, newItem,
  useChecklists, useChecklistVersions, type Checklist, type ItemDraft, type ItemProblem,
} from "./api";

function useDateTime() {
  const { i18n } = useTranslation();
  return (value: string) => new Intl.DateTimeFormat(intlLocale(i18n.language), { dateStyle: "medium", timeStyle: "short" }).format(new Date(value));
}

function Required({ checklist }: { checklist: Checklist }) {
  const { t } = useTranslation();
  return checklist.required
    ? <StatusChip status="ok">{t("portal.checklists.required.yes")}</StatusChip>
    : <StatusChip status="pending">{t("portal.checklists.required.no")}</StatusChip>;
}

/** The newest checklist of each machine type (US-P2-FO-03): what tablets use before a start. */
export function ChecklistsPage() {
  const { t } = useTranslation();
  const { can } = useAccess();
  const dateTime = useDateTime();
  const checklists = useChecklists();

  return (
    <div className="flex flex-col gap-4">
      <PageHeader
        breadcrumbs={<Trail steps={[{ to: "/settings", label: t("portal.nav.settings") }]} />}
        title={t("portal.checklists.title")}
        description={t("portal.checklists.description")}
        actions={can(ChecklistsManagePermission) && (
          <Button asChild>
            <Link to="/settings/checklists/new">{t("portal.checklists.new")}</Link>
          </Button>
        )}
      />
      <DataTable<Checklist>
        caption={t("portal.checklists.title")}
        rows={checklists.data ?? []}
        rowKey={(checklist) => checklist.id}
        loading={checklists.isPending && checklists.fetchStatus !== "idle"}
        empty={<EmptyState title={t("portal.checklists.none")} />}
        columns={[
          {
            key: "type",
            header: t("portal.checklists.assetType"),
            cell: (checklist) => (
              <Link to="/settings/checklists/$assetType" params={{ assetType: checklist.assetType }} className="font-semibold text-primary underline-offset-4 hover:underline">
                {t(`portal.assets.type.${checklist.assetType}`, { defaultValue: checklist.assetType })}
              </Link>
            ),
          },
          { key: "version", header: t("portal.checklists.version"), cell: (checklist) => String(checklist.version), numeric: true },
          { key: "items", header: t("portal.checklists.items"), cell: (checklist) => String(checklist.items.length), numeric: true },
          { key: "required", header: t("portal.checklists.required"), cell: (checklist) => <Required checklist={checklist} /> },
          { key: "published", header: t("portal.checklists.publishedAt"), cell: (checklist) => dateTime(checklist.publishedAt) },
        ]}
      />
    </div>
  );
}

/** One machine type's checklist: the builder for its next version and the versions published so far; or a new one. */
export function ChecklistPage({ assetType }: { assetType?: string }) {
  const { t } = useTranslation();
  const versions = useChecklistVersions(assetType);
  const { loaded } = useCurrentTenant();

  if (!loaded || (assetType !== undefined && versions.isPending)) {
    return <PageSkeleton />;
  }

  const list = versions.data ?? [];
  return (
    <div className="flex max-w-4xl flex-col gap-8">
      <ChecklistBuilder key={`${assetType ?? "new"}-${String(list[0]?.version ?? 0)}`} assetType={assetType} versions={list} />
      {list.length > 0 && <Versions versions={list} />}
      {assetType !== undefined && list.length === 0 && <p className="text-text-muted">{t("portal.checklists.noneForType")}</p>}
    </div>
  );
}

function ChecklistBuilder({ assetType, versions }: { assetType: string | undefined; versions: Checklist[] }) {
  const { t } = useTranslation();
  const api = useApi();
  const withStepUp = useWithStepUp();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const toast = useToast();
  const { can } = useAccess();
  const { tenant } = useCurrentTenant();
  const types = useAssetTypes();
  const existing = useChecklists();
  const editable = can(ChecklistsManagePermission);
  const newest = versions[0];
  const [type, setType] = useState(assetType ?? "");
  const [required, setRequired] = useState(newest?.required ?? true);
  const [items, setItems] = useState<ItemDraft[]>(() => (newest ? draftsOf(newest) : [newItem()]));
  const [checked, setChecked] = useState(false);
  const nextVersion = Number(newest?.version ?? 0) + 1;
  const problems = checkItems(items);
  const taken = new Set((existing.data ?? []).map((checklist) => checklist.assetType));
  const typeOptions = (types.data ?? []).filter((option) => !taken.has(option.code)).map((option) => ({ value: option.code, label: t(`portal.assets.type.${option.code}`) }));
  const typeLabel = assetType === undefined ? "" : t(`portal.assets.type.${assetType}`, { defaultValue: assetType });

  const publish = useMutation({
    mutationFn: async () => withStepUp(async () => (await api.POST("/api/v1/fieldops/checklists/{assetType}/versions", {
      params: { path: { assetType: type } },
      body: { required, items: itemsBody(items) },
    })).data),
    onSettled: async () => {
      await queryClient.invalidateQueries({ queryKey: checklistsKey });
    },
    onSuccess: async (published) => {
      if (!published) {
        return;
      }

      toast({ status: "ok", title: t("portal.checklists.published", { version: String(published.version) }) });
      queryClient.setQueryData(checklistVersionsKey(published.assetType), (current: Checklist[] | undefined) => [published, ...(current ?? [])]);
      if (assetType === undefined) {
        await navigate({ to: "/settings/checklists/$assetType", params: { assetType: published.assetType } });
      }
    },
  });

  const submit = () => {
    setChecked(true);
    if (type && problems.list === null && Object.keys(problems.items).length === 0) {
      publish.mutate();
    }
  };

  const change = (key: string, update: (draft: ItemDraft) => ItemDraft) => {
    setItems((current) => current.map((draft) => (draft.key === key ? update(draft) : draft)));
  };
  const move = (index: number, by: number) => {
    setItems((current) => {
      const next = [...current];
      const [moved] = next.splice(index, 1);
      if (moved) {
        next.splice(index + by, 0, moved);
      }

      return next;
    });
  };

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        breadcrumbs={<Trail steps={[{ to: "/settings", label: t("portal.nav.settings") }, { to: "/settings/checklists", label: t("portal.checklists.title") }]} />}
        title={assetType === undefined ? t("portal.checklists.new") : typeLabel}
        status={newest && <Required checklist={newest} />}
      >
        <p className="text-text-muted">{editable ? t("portal.checklists.frozenHint") : t("portal.checklists.readOnly")}</p>
      </PageHeader>

      <form className="flex flex-col gap-6 rounded-xl border-[length:var(--karry-border-width)] border-divider bg-surface p-5 shadow-[var(--karry-card-shadow)] md:p-6" noValidate onSubmit={(event) => { event.preventDefault(); submit(); }}>
        <fieldset disabled={!editable} className="flex flex-col gap-4">
          {assetType === undefined && (
            <SelectField
              label={t("portal.checklists.assetType")}
              value={type || undefined}
              onValueChange={setType}
              placeholder={t("portal.checklists.chooseType")}
              options={typeOptions}
              error={checked && !type ? t("portal.checklists.typeRequired") : undefined}
            />
          )}
          <Checkbox
            label={t("portal.checklists.requiredLabel")}
            checked={required}
            onChange={(e) => { setRequired(e.target.checked); }}
            className="self-start"
          />
          <p className="-mt-3 text-sm text-text-muted">{t("portal.checklists.requiredHint")}</p>
        </fieldset>

        <fieldset disabled={!editable} className="flex flex-col gap-4">
          <legend className="mb-2 text-lg font-semibold">{t("portal.checklists.items")}</legend>
          <ol className="flex flex-col gap-4">
            {items.map((draft, index) => (
              <ItemEditor
                key={draft.key}
                draft={draft}
                number={index + 1}
                count={items.length}
                languages={languagesFor(tenant?.language)}
                problem={checked ? problems.items[draft.key] : undefined}
                onChange={(update) => { change(draft.key, update); }}
                onMove={(by) => { move(index, by); }}
                onRemove={() => { setItems((current) => current.filter((item) => item.key !== draft.key)); }}
              />
            ))}
          </ol>
          {checked && problems.list && (
            <p role="alert" className="font-semibold text-danger">{t(problems.list === "none" ? "portal.checklists.noItems" : "portal.checklists.tooMany", { max: String(maxItems) })}</p>
          )}
          {editable && items.length < maxItems && (
            <Button variant="secondary" className="self-start" onClick={() => { setItems((current) => [...current, newItem()]); }}>{t("portal.checklists.addItem")}</Button>
          )}
        </fieldset>

        {editable && (
          <div>
            <Button type="submit" loading={publish.isPending}>{t("portal.checklists.publish", { version: String(nextVersion) })}</Button>
          </div>
        )}
      </form>
    </div>
  );
}

const problemKeys: Record<ItemProblem, string> = {
  code: "portal.checklists.codeInvalid",
  duplicate: "portal.checklists.codeDuplicate",
  text: "portal.checklists.textRequired",
  tooLong: "portal.checklists.textTooLong",
};

function ItemEditor({ draft, number, count, languages, problem, onChange, onMove, onRemove }: {
  draft: ItemDraft;
  number: number;
  count: number;
  languages: ReturnType<typeof languagesFor>;
  problem: ItemProblem | undefined;
  onChange: (update: (draft: ItemDraft) => ItemDraft) => void;
  onMove: (by: number) => void;
  onRemove: () => void;
}) {
  const { t } = useTranslation();
  const heading = t("portal.checklists.item", { number: String(number) });
  return (
    <li className="flex flex-col gap-3 rounded-lg border-[length:var(--karry-border-width)] border-divider p-4" aria-label={heading}>
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h3 className="font-semibold">{heading}</h3>
        <div className="flex gap-1">
          <Button variant="ghost" aria-label={t("portal.checklists.moveUp", { number: String(number) })} disabled={number === 1} onClick={() => { onMove(-1); }}>
            <ArrowUp aria-hidden className="size-4" />
          </Button>
          <Button variant="ghost" aria-label={t("portal.checklists.moveDown", { number: String(number) })} disabled={number === count} onClick={() => { onMove(1); }}>
            <ArrowDown aria-hidden className="size-4" />
          </Button>
          <Button variant="ghost" aria-label={t("portal.checklists.removeItem", { number: String(number) })} disabled={count === 1} onClick={onRemove}>
            <Trash2 aria-hidden className="size-4" />
          </Button>
        </div>
      </div>
      <div className="grid gap-3 md:grid-cols-2">
        {languages.map((language) => (
          <TextField
            key={language}
            label={t("portal.checklists.text", { language: t(`common.language.${language}`) })}
            lang={language}
            dir={languageDirection(language)}
            maxLength={maxText + 1}
            value={draft.texts[language] ?? ""}
            onChange={(e) => { const value = e.target.value; onChange((current) => ({ ...current, texts: { ...current.texts, [language]: value } })); }}
          />
        ))}
      </div>
      <div className="grid gap-3 md:grid-cols-2">
        <TextField
          label={t("portal.checklists.code")}
          description={t("portal.checklists.codeHint")}
          translate="no"
          value={draft.code}
          onChange={(e) => { const value = e.target.value; onChange((current) => ({ ...current, code: value })); }}
        />
        <div className="flex flex-col">
          <Checkbox label={t("portal.checklists.critical")} checked={draft.critical} onChange={(e) => { const value = e.target.checked; onChange((current) => ({ ...current, critical: value })); }} />
          <Checkbox
            label={t("portal.checklists.photo")}
            checked={draft.critical || draft.photoOnNotOk}
            disabled={draft.critical}
            onChange={(e) => { const value = e.target.checked; onChange((current) => ({ ...current, photoOnNotOk: value })); }}
          />
          {draft.critical && <p className="text-sm text-text-muted">{t("portal.checklists.photoForced")}</p>}
        </div>
      </div>
      {problem && <p role="alert" className="font-semibold text-danger">{t(problemKeys[problem], { max: String(maxText) })}</p>}
    </li>
  );
}

/** The versions published so far, newest first (US-P2-FO-03 AC2): each stays as it was; a submission names its version. */
function Versions({ versions }: { versions: Checklist[] }) {
  const { t } = useTranslation();
  const dateTime = useDateTime();
  return (
    <section className="flex flex-col gap-3">
      <h2 className="text-lg font-semibold">{t("portal.checklists.versions")}</h2>
      <DataTable<Checklist>
        caption={t("portal.checklists.versions")}
        rows={versions}
        rowKey={(checklist) => checklist.id}
        empty={null}
        columns={[
          { key: "version", header: t("portal.checklists.version"), cell: (checklist) => String(checklist.version), numeric: true },
          { key: "published", header: t("portal.checklists.publishedAt"), cell: (checklist) => dateTime(checklist.publishedAt) },
          { key: "required", header: t("portal.checklists.required"), cell: (checklist) => <Required checklist={checklist} /> },
          { key: "items", header: t("portal.checklists.items"), cell: (checklist) => checklist.items.map((item) => item.code).join(", ") },
        ]}
      />
    </section>
  );
}

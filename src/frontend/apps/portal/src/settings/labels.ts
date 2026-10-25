import type { useTranslation } from "@karry/i18n";

type TFunction = ReturnType<typeof useTranslation>["t"];

/** Name of a role: a custom role's own name, a default role's translation (unknown codes show as they are). */
export function roleName(t: TFunction, role: { code: string; name?: string | null | undefined }): string {
  return role.name ?? t(`roles.${role.code}`, { defaultValue: role.code });
}

/** A permission in plain words; a module that ships later without translations shows the code. */
export function permissionLabel(t: TFunction, code: string): string {
  return t(`permissions.${code}`, { defaultValue: code });
}

export function moduleLabel(t: TFunction, module: string): string {
  return t(`modules.${module}`, { defaultValue: module });
}

export function fieldLabel(t: TFunction, code: string): string {
  return t(`fields.${code}`, { defaultValue: code });
}

export const fieldLevels = ["hidden", "masked", "read", "write"] as const;
export type FieldLevel = (typeof fieldLevels)[number];

/** Permissions grouped by module, in catalogue order (the editor shows one group per module, US-P1-IAM-05 AC1). */
export function byModule<T extends { module: string }>(items: readonly T[]): [string, T[]][] {
  const groups = new Map<string, T[]>();
  for (const item of items) {
    groups.set(item.module, [...(groups.get(item.module) ?? []), item]);
  }

  return [...groups.entries()];
}

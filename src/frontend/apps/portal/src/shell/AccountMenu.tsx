import { ChevronDown, Contrast, Globe, LogOut, Moon, Palette, Sun } from "lucide-react";
import { useAuth } from "@karry/auth";
import { isLanguage, supportedLanguages, useTranslation } from "@karry/i18n";
import { chooseLanguage, Menu, themeNames, useTheme, type ThemeName } from "@karry/ui";

const themeIcons: Record<ThemeName, typeof Sun> = { light: Sun, dark: Moon, outdoor: Contrast };

function initials(name: string): string {
  const letters = name.split(/\s+/).filter(Boolean).map((part) => part[0]?.toUpperCase() ?? "");
  return letters.length > 1 ? `${letters[0] ?? ""}${letters.at(-1) ?? ""}` : (letters[0] ?? "?");
}

/**
 * Who is signed in (US-P1-IAM-01) and their own settings: the language and the theme each one row showing the current
 * value and opening its choices (UX-01, ui-ux-review §5), then sign-out, last and apart.
 */
export function AccountMenu() {
  const { t, i18n } = useTranslation();
  const { user, signOut } = useAuth();
  const { theme, setTheme } = useTheme();
  const name = user?.name ?? "";
  return (
    <Menu
      label={t("common.account.signedInAs", { name })}
      trigger={(
        <button type="button" className="flex min-h-10 items-center gap-2.5 rounded-lg px-2 text-sm transition-colors hover:bg-surface-muted data-[state=open]:bg-surface-muted">
          <span aria-hidden className="flex size-8 items-center justify-center rounded-full bg-primary text-xs font-semibold text-primary-text">{initials(name)}</span>
          <span className="hidden font-medium text-text sm:inline" translate="no">{name}</span>
          <ChevronDown aria-hidden className="size-4 text-text-muted" />
        </button>
      )}
      header={(
        <div className="flex items-center gap-3">
          <span aria-hidden className="flex size-10 shrink-0 items-center justify-center rounded-full bg-primary text-sm font-semibold text-primary-text">{initials(name)}</span>
          <span className="flex min-w-0 flex-col">
            <span className="truncate font-semibold text-text" translate="no">{name}</span>
            {user?.email && <span className="truncate text-xs text-text-muted" translate="no">{user.email}</span>}
          </span>
        </div>
      )}
      groups={[
        {
          items: [
            {
              kind: "choice", key: "language", label: t("common.language"), icon: <Globe aria-hidden className="size-4 text-text-muted" />,
              value: i18n.language,
              options: supportedLanguages.map((language) => ({ value: language, label: t(`common.language.${language}`), lang: language })),
              onValueChange: (language) => { if (isLanguage(language)) chooseLanguage(i18n, language); },
            },
            {
              kind: "choice", key: "theme", label: t("common.theme"), icon: <Palette aria-hidden className="size-4 text-text-muted" />,
              value: theme,
              options: themeNames.map((name) => {
                const Icon = themeIcons[name];
                return { value: name, label: t(`common.theme.${name}`), icon: <Icon aria-hidden className="size-4 text-text-muted" /> };
              }),
              onValueChange: (name) => { setTheme(name as ThemeName); },
            },
          ],
        },
        { items: [{ key: "sign-out", label: t("common.account.signOut"), icon: <LogOut aria-hidden className="size-4 text-text-muted" />, onSelect: () => void signOut() }] },
      ]}
    />
  );
}

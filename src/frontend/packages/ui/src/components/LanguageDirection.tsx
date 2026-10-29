import { DirectionProvider } from "@radix-ui/react-direction";
import type { ReactNode } from "react";
import { languageDirection, useTranslation } from "@karry/i18n";

/**
 * Menus, selects and dialogs follow the writing direction of the language (Arabic and N'Ko right to left): which
 * arrow key moves where, which side a menu opens and aligns on. The page's own `dir` is set by the i18n instance.
 */
export function LanguageDirection({ children }: { children: ReactNode }) {
  const { i18n } = useTranslation();
  return <DirectionProvider dir={languageDirection(i18n.language)}>{children}</DirectionProvider>;
}

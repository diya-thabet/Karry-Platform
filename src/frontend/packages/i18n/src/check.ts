import { IntlMessageFormat } from "intl-messageformat";

/** Problems that make the catalogs unfit to ship (TC-P1-OFF-007). An empty list means the catalogs are consistent. */
export function checkCatalogs(catalogs: Record<string, Record<string, string>>, usedKeys: Iterable<string> = []): string[] {
  const problems: string[] = [];
  const languages = Object.keys(catalogs);
  const allKeys = new Set(languages.flatMap((language) => Object.keys(catalogs[language] ?? {})));

  for (const key of [...allKeys].sort()) {
    let reference: { language: string; arguments: string } | undefined;
    for (const language of languages) {
      const message = catalogs[language]?.[key];
      if (message === undefined) {
        problems.push(`${language}: missing key "${key}"`);
        continue;
      }

      if (message.trim() === "") {
        problems.push(`${language}: empty message "${key}"`);
        continue;
      }

      let names: string;
      try {
        names = [...argumentNames(new IntlMessageFormat(message, language).getAst())].sort().join(",");
      } catch (error) {
        problems.push(`${language}: invalid ICU message "${key}": ${(error as Error).message}`);
        continue;
      }

      if (reference === undefined) {
        reference = { language, arguments: names };
      } else if (reference.arguments !== names) {
        problems.push(`"${key}": placeholders differ (${reference.language}: {${reference.arguments}}, ${language}: {${names}})`);
      }
    }
  }

  for (const key of new Set(usedKeys)) {
    if (!allKeys.has(key)) {
      problems.push(`source uses unknown key "${key}"`);
    }
  }

  return problems;
}

/** Keys passed as string literals to t("…") in source code (dynamic keys are not detected). */
export function findUsedKeys(source: string): string[] {
  return [...source.matchAll(/\bt\(\s*["']([a-z][\w.-]*)["']/g)].map((match) => match[1] ?? "");
}

interface AstNode {
  value?: unknown;
  options?: Record<string, { value: AstNode[] }>;
  children?: AstNode[];
  type: number;
}

function* argumentNames(nodes: unknown): Generator<string> {
  for (const node of nodes as AstNode[]) {
    // Literal (0) and pound (7) carry no argument; tags (8) wrap children.
    if (node.type !== 0 && node.type !== 7 && node.type !== 8 && typeof node.value === "string") {
      yield node.value;
    }

    for (const option of Object.values(node.options ?? {})) {
      yield* argumentNames(option.value);
    }

    if (node.children) {
      yield* argumentNames(node.children);
    }
  }
}

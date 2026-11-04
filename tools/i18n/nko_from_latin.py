#!/usr/bin/env python3
"""Drafts the N'Ko catalog (nqo.json) from the Latin-script Malinké catalog (emk.json).

Malinké has two writing systems, and the owner wants both (2026-10-29). Writing the N'Ko catalog by hand would let the
two drift apart, so this script spells every Latin message in N'Ko letters, keeping the ICU syntax intact:
placeholders (`{name}`), plural and select options, `#`, and date/number arguments are copied as they are; only the
words people read are spelled. Codes, examples and acronyms stay in Latin letters (Karry, CSV, PEL-01, Africa/Tunis,
#RRGGBB, assets.asset_changed.v1), as N'Ko texts do with foreign names. Numbers use N'Ko digits, like `Intl` for `nqo`.

What it cannot know: the tones. Latin Malinké spelling leaves them out, N'Ko marks them. Long vowels get the long high
tone mark (U+07EF) only to keep them long. The output is therefore a DRAFT for a native reader, who corrects nqo.json
directly. Once someone has edited nqo.json, regenerating would lose their work: the script refuses to overwrite an
existing file unless --force (use it only while nqo.json is still the untouched draft). New messages are added with
--missing: only keys of emk.json that nqo.json lacks are spelled; keys gone from emk.json are dropped; the rest is kept.
--check (CI) fails when a message of nqo.json is not the spelling of its emk.json message, so the two scripts never say
different things; a message a native reader has corrected is listed in nko-reviewed.txt and left alone.

Usage: python3 tools/i18n/nko_from_latin.py [--missing | --force]
"""
from __future__ import annotations

import argparse
import json
import pathlib
import re
import sys

CATALOGS = pathlib.Path(__file__).resolve().parents[2] / "src/frontend/packages/i18n/src/catalogs"

VOWELS = {"a": "ߊ", "e": "ߋ", "i": "ߌ", "o": "ߏ", "u": "ߎ", "ɛ": "ߍ", "ɔ": "ߐ",
          "à": "ߊ", "â": "ߊ", "é": "ߋ", "è": "ߍ", "ê": "ߍ", "ë": "ߍ", "î": "ߌ", "ï": "ߌ", "ô": "ߏ", "û": "ߎ", "ù": "ߎ"}
CONSONANTS = {"b": "ߓ", "p": "ߔ", "t": "ߕ", "j": "ߖ", "c": "ߗ", "d": "ߘ", "r": "ߙ", "s": "ߛ", "f": "ߝ", "k": "ߞ",
              "l": "ߟ", "m": "ߡ", "ɲ": "ߢ", "n": "ߣ", "h": "ߤ", "w": "ߥ", "y": "ߦ", "ŋ": "ߒ", "q": "ߞ", "ç": "ߛ",
              # Sounds of borrowed words: the base letter with the dot above, as N'Ko writes them.
              "g": "ߜ߭", "v": "ߝ߭", "z": "ߖ߭", "x": "ߞߛ"}
DIGRAPHS = {"gb": "ߜ", "ny": "ߢ"}
NASAL = "߲"  # NKO COMBINING NASALIZATION MARK
LONG = "߯"  # NKO COMBINING LONG HIGH TONE: marks the second vowel of a long vowel
SYLLABIC_N = "ߒ"
PUNCTUATION = {",": "߸", "!": "߹", "?": "؟", ";": "؛"}
DIGITS = str.maketrans("0123456789", "߀߁߂߃߄߅߆߇߈߉")

# Kept in Latin letters: product and file-format names, acronyms, and anything that is a code or an example to type.
KEEP = {"karry", "excel", "data", "instructions", "edge", "webhooklu", "api", "ia", "starter", "professional",
        "business", "enterprise", "english", "français", "h", "min", "mm", "km", "kg"}
TECHNICAL = re.compile(r"[#\w][\w.\-/@#'’]*")
WORD = re.compile(r"[^\W\d_]+(?:['’][^\W\d_]+)*", re.UNICODE)


def is_technical(token: str) -> bool:
    if token.lower() in KEEP:
        return True
    if re.search(r"[./_@#]", token.strip(".")) or "-" in token:
        return True
    letters = [c for c in token if c.isalpha()]
    if any(c.isdigit() for c in token) and letters:
        return True  # A4, PEL01, 3D
    return sum(1 for c in letters if c.isupper()) >= 2  # CSV, QR, PIN, MRR, WebP


def spell_word(word: str) -> str:
    text = word.lower().replace("'", "").replace("’", "")
    out: list[str] = []
    i = 0
    while i < len(text):
        pair = text[i:i + 2]
        if pair in DIGRAPHS:
            out.append(DIGRAPHS[pair])
            i += 2
            continue
        c = text[i]
        if c in VOWELS:
            if i > 0 and text[i - 1] == c:
                out.append(LONG)  # aa → ߊ߯
            else:
                out.append(VOWELS[c])
        elif c == "n":
            following = text[i + 1] if i + 1 < len(text) else ""
            previous = text[i - 1] if i > 0 else ""
            if following in VOWELS:
                out.append("ߣ")
            elif previous in VOWELS:
                out.append(NASAL)  # kan → ߞߊ߲
            else:
                out.append(SYLLABIC_N)  # n, nka → ߒ
        elif c in CONSONANTS:
            out.append(CONSONANTS[c])
        else:
            out.append(c)
        i += 1
    return "".join(out)


def spell_text(text: str) -> str:
    """Plain text between ICU arguments: technical tokens stay, words are spelled, digits and punctuation follow N'Ko."""
    out: list[str] = []
    position = 0
    for match in TECHNICAL.finditer(text):
        out.append(spell_plain(text[position:match.start()]))
        token = match.group(0)
        core = token.rstrip(".")
        trailing = token[len(core):]
        if is_technical(core):
            out.append(core)
        else:
            out.append(spell_plain(core))
        out.append(trailing)
        position = match.end()
    out.append(spell_plain(text[position:]))
    return "".join(out)


def spell_plain(text: str) -> str:
    spelled = WORD.sub(lambda m: m.group(0) if m.group(0).lower() in KEEP else spell_word(m.group(0)), text)
    return "".join(PUNCTUATION.get(c, c) for c in spelled).translate(DIGITS)


def transliterate(message: str) -> str:
    """Spells an ICU message in N'Ko, copying its syntax: arguments, plural/select options, `#`, styles."""
    result, end = _message(message, 0, nested=False)
    if end != len(message):
        raise ValueError(f"unbalanced braces in {message!r}")
    return result


def _message(s: str, i: int, nested: bool) -> tuple[str, int]:
    out: list[str] = []
    text_start = i
    while i < len(s):
        c = s[i]
        if c == "{":
            out.append(spell_text(s[text_start:i]))
            argument, i = _argument(s, i)
            out.append(argument)
            text_start = i
        elif c == "}" and nested:
            break
        else:
            i += 1
    out.append(spell_text(s[text_start:i]))
    return "".join(out), i


def _argument(s: str, i: int) -> tuple[str, int]:
    close = _matching(s, i)
    body = s[i + 1:close]
    parts = [p.strip() for p in body.split(",", 2)]
    if len(parts) < 3 or parts[1] not in ("plural", "select", "selectordinal"):
        return s[i:close + 1], close + 1  # {name}, {at, date, medium}: copied as is
    head_end = i + 1 + body.index(",", body.index(",") + 1) + 1
    out = [s[i:head_end]]
    j = head_end
    while j < close:
        brace = s.index("{", j)
        out.append(s[j:brace + 1])  # " one {"
        inner, k = _message(s, brace + 1, nested=True)
        out.append(inner + "}")
        j = k + 1
        if s[j:close].strip() == "":
            out.append(s[j:close])
            j = close
    out.append("}")
    return "".join(out), close + 1


def _matching(s: str, i: int) -> int:
    depth = 0
    for j in range(i, len(s)):
        if s[j] == "{":
            depth += 1
        elif s[j] == "}":
            depth -= 1
            if depth == 0:
                return j
    raise ValueError(f"unbalanced braces in {s!r}")


def merge(latin: dict[str, str], existing: dict[str, str]) -> dict[str, str]:
    """The N'Ko catalog in the Latin catalog's key order: existing (possibly corrected) messages kept, new ones spelled."""
    return {key: existing[key] if key in existing else spell_message(key, value) for key, value in latin.items()}


def drift(latin: dict[str, str], nko: dict[str, str], reviewed: set[str]) -> list[str]:
    """Keys whose N'Ko message is missing, extra, or not the spelling of the Latin one (reviewed keys excepted)."""
    keys = [key for key in latin if key not in reviewed and nko.get(key) != spell_message(key, latin[key])]
    return keys + [key for key in nko if key not in latin]


def reviewed_keys(text: str) -> set[str]:
    return {line.strip() for line in text.splitlines() if line.strip() and not line.lstrip().startswith("#")}


def spell_message(key: str, value: str) -> str:
    return value if key.startswith("common.language.") else transliterate(value)


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--missing", action="store_true", help="spell only the keys nqo.json lacks; keep every other message")
    mode.add_argument("--force", action="store_true", help="overwrite an existing nqo.json (loses any hand corrections)")
    mode.add_argument("--check", action="store_true", help="fail if nqo.json is not the spelling of emk.json (keys in nko-reviewed.txt excepted)")
    args = parser.parse_args(argv)
    target = CATALOGS / "nqo.json"
    latin = json.loads((CATALOGS / "emk.json").read_text(encoding="utf-8"))
    if args.check:
        reviewed = reviewed_keys((pathlib.Path(__file__).parent / "nko-reviewed.txt").read_text(encoding="utf-8"))
        stale = drift(latin, json.loads(target.read_text(encoding="utf-8")), reviewed)
        for key in stale:
            print(f"  {key}")
        print(f"nqo.json: {len(stale)} message(s) differ from emk.json spelled in N'Ko" + (
            " (remove them from nqo.json and run --missing; a native reader's correction goes in tools/i18n/nko-reviewed.txt)" if stale else ""))
        return 1 if stale else 0
    if target.exists() and not (args.force or args.missing):
        print("nqo.json exists and may hold a reader's corrections: not overwritten (--missing to add new keys, --force to replace it)")
        return 1

    existing = json.loads(target.read_text(encoding="utf-8")) if args.missing and target.exists() else {}
    nko = merge(latin, existing)
    target.write_text(json.dumps(nko, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    added = [key for key in nko if key not in existing]
    print(f"nqo.json: {len(added)} of {len(nko)} messages spelled from emk.json (draft: tones to be added by a native reader)")
    for key in added if args.missing else []:
        print(f"  + {key}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))

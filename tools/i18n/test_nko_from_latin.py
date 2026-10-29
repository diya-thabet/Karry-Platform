"""The N'Ko draft keeps every ICU construct and every code intact and spells only the words (nko_from_latin.py)."""
from __future__ import annotations

import re
import unittest

from nko_from_latin import merge, transliterate


def arguments(message: str) -> list[str]:
    """Argument names and plural selectors: what the catalog check compares between languages."""
    return re.findall(r"\{\s*(\w+)\s*[,}]|(=\d+|zero|one|two|few|many|other)\s*\{", message)


class Spelling(unittest.TestCase):
    def test_letters_vowels_and_nasals(self) -> None:
        self.assertEqual(transliterate("Kan"), "ߞߊ߲")  # final n nasalises the vowel
        self.assertEqual(transliterate("Sɛbɛnnen"), "ߛߍߓߍ߲ߣߋ߲")  # n before a vowel is a consonant
        self.assertEqual(transliterate("Baarayɔrɔ"), "ߓߊ߯ߙߊߦߐߙߐ")  # long vowel: one letter and the length mark
        self.assertEqual(transliterate("n ka ɲini"), "ߒ ߞߊ ߢߌߣߌ")  # syllabic n, ɲ
        self.assertEqual(transliterate("I n'a fɔ"), "ߌ ߣߊ ߝߐ")  # the elision is read as one syllable

    def test_digits_and_punctuation_follow_nko(self) -> None:
        self.assertEqual(transliterate("miniti 15, sisan!"), "ߡߌߣߌߕߌ ߁߅߸ ߛߌߛߊ߲߹")

    def test_codes_examples_and_acronyms_stay_latin(self) -> None:
        for kept in ["PEL-01", "Africa/Tunis", "#RRGGBB", "assets.asset_changed.v1", "carriere-sfax", "CSV", "QR", "PIN",
                     "WebP", "Karry", "Excel", "e-mail", "km²", "A4", "{slug}.karry.app"]:
            with self.subTest(kept=kept):
                self.assertIn(kept, transliterate(f"a {kept} la"))


class IcuSyntax(unittest.TestCase):
    def test_placeholders_and_formats_are_copied(self) -> None:
        message = "Kunnafonilu kurayara {at, date, medium} {at, time, short}, {name} fɛ"
        spelled = transliterate(message)

        self.assertIn("{at, date, medium}", spelled)
        self.assertIn("{at, time, short}", spelled)
        self.assertIn("{name}", spelled)
        self.assertNotRegex(spelled, r"[a-zɛɔ]{3,}(?![^{]*\})")  # no Latin word left outside the arguments

    def test_plural_options_are_spelled_inside_and_kept_outside(self) -> None:
        message = "{count, plural, =0 {Fɛn si tɛ} one {fɛn #} other {fɛn #}} ani {users, plural, one {mɔɔ #} other {mɔɔ #}}"
        spelled = transliterate(message)

        self.assertEqual(spelled, "{count, plural, =0 {ߝߍ߲ ߛߌ ߕߍ} one {ߝߍ߲ #} other {ߝߍ߲ #}} ߊߣߌ {users, plural, one {ߡߐ߯ #} other {ߡߐ߯ #}}")
        self.assertEqual(arguments(spelled), arguments(message))

    def test_unbalanced_braces_are_refused(self) -> None:
        with self.assertRaises(ValueError):
            transliterate("{count, plural, one {#}")


class AddingMessages(unittest.TestCase):
    def test_only_new_keys_are_spelled_corrections_are_kept_and_removed_keys_dropped(self) -> None:
        latin = {"a": "Kan", "b": "Fɛn kura", "common.language.nqo": "ߒߞߏ"}
        existing = {"a": "ߞߊ߲߬ (corrected)", "gone": "ߝߍ߲"}

        self.assertEqual(merge(latin, existing), {"a": "ߞߊ߲߬ (corrected)", "b": "ߝߍ߲ ߞߎߙߊ", "common.language.nqo": "ߒߞߏ"})


if __name__ == "__main__":
    unittest.main()

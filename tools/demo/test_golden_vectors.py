"""The committed Phase 3 golden vectors (tests/vectors) are exactly what the generators produce (drift guard, P3.0).

The Fuel and Weighbridge modules' tests (.NET) read these files as their expected answers. If a generator or an oracle
changes, these tests fail until the vectors are regenerated on purpose:

    python tools/demo/generate_fuel.py --seed 1 --out tests/vectors/fuel-golden-month.json
    python tools/demo/generate_weighbridge.py --seed 1 --days 2 --out tests/vectors/weighbridge-golden-days.json
"""
from __future__ import annotations

import io
import sys
import unittest
from contextlib import redirect_stdout
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import generate_fuel  # noqa: E402
import generate_weighbridge  # noqa: E402

VECTORS = Path(__file__).resolve().parents[2] / "tests" / "vectors"


def produced(main, argv: list[str]) -> str:
    out = io.StringIO()
    with redirect_stdout(out):
        main(argv)
    return out.getvalue()


class GoldenVectors(unittest.TestCase):
    def test_the_fuel_month_is_current(self) -> None:
        committed = (VECTORS / "fuel-golden-month.json").read_text(encoding="utf-8")
        self.assertEqual(committed, produced(generate_fuel.main, ["--seed", "1"]))

    def test_the_weighbridge_days_are_current(self) -> None:
        committed = (VECTORS / "weighbridge-golden-days.json").read_text(encoding="utf-8")
        self.assertEqual(committed, produced(generate_weighbridge.main, ["--seed", "1", "--days", "2"]))


if __name__ == "__main__":
    unittest.main()

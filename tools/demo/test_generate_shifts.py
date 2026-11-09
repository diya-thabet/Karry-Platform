"""Tests of the shift data generator (P2.0): deterministic from the seed, anomaly rates respected, ground truth true."""
from __future__ import annotations

import io
import json
import sys
import unittest
from contextlib import redirect_stdout
from datetime import datetime, timedelta
from decimal import Decimal
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import generate_shifts as gen  # noqa: E402


def entries(**kwargs) -> list[dict]:
    return gen.generate(**kwargs)["entries"]


def delta(entry: dict) -> Decimal:
    return Decimal(entry["end"]["reading"]) - Decimal(entry["start"]["reading"])


class Determinism(unittest.TestCase):
    def test_the_same_seed_gives_the_same_bytes(self) -> None:
        first, second = io.StringIO(), io.StringIO()
        with redirect_stdout(first):
            gen.main(["--seed", "7", "--days", "3"])
        with redirect_stdout(second):
            gen.main(["--seed", "7", "--days", "3"])
        self.assertEqual(first.getvalue(), second.getvalue())
        json.loads(first.getvalue())

    def test_another_seed_gives_other_data(self) -> None:
        self.assertNotEqual(entries(seed=1), entries(seed=2))

    def test_the_size_is_sites_by_assets_by_days_by_three_shifts(self) -> None:
        self.assertEqual(len(entries(sites=3, assets=4, days=5)), 3 * 4 * 5 * 3)


class Anomalies(unittest.TestCase):
    def test_without_anomalies_every_entry_continues_the_previous_one(self) -> None:
        data = gen.generate(seed=3, days=10, anomaly_rate=0, replacement_rate=0)
        by_asset: dict[str, list[dict]] = {}
        for entry in data["entries"]:
            by_asset.setdefault(entry["assetId"], []).append(entry)
        for asset in data["assets"]:
            previous = asset["meter"]["initial"]
            for entry in by_asset[asset["id"]]:
                self.assertEqual(entry["expected_flags"], [])
                self.assertEqual(entry["start"]["reading"], previous)
                self.assertGreater(delta(entry), 0)
                if entry["unit"] == "h":
                    self.assertLessEqual(delta(entry), gen.SHIFT_HOURS + Decimal("0.5"))
                skew = datetime.fromisoformat(entry["start"]["deviceTime"]) - datetime.fromisoformat(entry["start"]["at"])
                self.assertLessEqual(abs(skew), gen.SKEW_LIMIT)
                previous = entry["end"]["reading"]

    def test_the_anomaly_rate_is_respected(self) -> None:
        found = entries(seed=11, sites=4, assets=10, days=30, anomaly_rate=0.1)  # 3 600 entries
        share = sum(1 for e in found if e["expected_flags"]) / len(found)
        self.assertAlmostEqual(share, 0.1, delta=0.02)
        kinds = {k for e in found for k in e["expected_flags"]}
        self.assertEqual(kinds, set(gen.ANOMALIES))

    def test_only_the_kinds_asked_for_are_drawn(self) -> None:
        found = entries(seed=5, days=20, anomaly_rate=0.5, anomalies=("clock_skew",))
        self.assertEqual({k for e in found for k in e["expected_flags"]}, {"clock_skew"})

    def test_each_flag_is_true_of_its_entry(self) -> None:
        data = gen.generate(seed=13, sites=3, assets=6, days=20, anomaly_rate=0.3, replacement_rate=0)
        previous_end: dict[str, str | None] = {a["id"]: a["meter"]["initial"] for a in data["assets"]}
        photos: set[str] = set()
        for entry in data["entries"]:
            flags = entry["expected_flags"]
            if previous_end[entry["assetId"]] is not None:  # after a missing end there is nothing to continue
                continues = entry["start"]["reading"] == previous_end[entry["assetId"]]
                self.assertEqual(not continues, "continuity_gap" in flags, entry["id"])
            if entry["end"] is None:
                self.assertEqual(flags, ["missing_end"])
            else:
                self.assertEqual(delta(entry) < 0, "negative_delta" in flags)
                oversized = entry["unit"] == "h" and delta(entry) > gen.SHIFT_HOURS + Decimal("0.5")
                self.assertEqual(oversized, "oversized_delta" in flags)
                self.assertEqual(entry["end"]["photo"] in photos | {entry["start"]["photo"]}, "reused_photo" in flags)
                previous_end[entry["assetId"]] = entry["end"]["reading"]
            skew = datetime.fromisoformat(entry["start"]["deviceTime"]) - datetime.fromisoformat(entry["start"]["at"])
            self.assertEqual(abs(skew) > gen.SKEW_LIMIT, "clock_skew" in flags)
            photos.add(entry["start"]["photo"])
            if entry["end"]:
                photos.add(entry["end"]["photo"])
            if entry["end"] is None:
                previous_end[entry["assetId"]] = None  # the next start is compared with nothing recorded

    def test_unknown_kinds_and_bad_rates_are_refused(self) -> None:
        with self.assertRaises(ValueError):
            gen.generate(anomalies=("teleport",))
        with self.assertRaises(ValueError):
            gen.generate(anomaly_rate=1.5)


class EdgeCases(unittest.TestCase):
    def test_night_shifts_cross_midnight_and_are_marked(self) -> None:
        for entry in entries(seed=2, days=2, anomaly_rate=0):
            start = datetime.fromisoformat(entry["start"]["at"])
            end = datetime.fromisoformat(entry["end"]["at"])
            self.assertEqual(end - start, timedelta(hours=gen.SHIFT_HOURS))
            self.assertEqual(entry["shift"] == "night", "crosses_midnight" in entry["edge_cases"])
            self.assertEqual(end.date() != start.date(), entry["shift"] == "night")
            self.assertIsNotNone(start.utcoffset())

    def test_a_meter_replacement_is_a_recorded_event_and_not_a_flag(self) -> None:
        data = gen.generate(seed=4, sites=2, assets=5, days=30, anomaly_rate=0, replacement_rate=0.05)
        replaced = [e for e in data["entries"] if "meter_replacement" in e["edge_cases"]]
        self.assertTrue(replaced)
        self.assertEqual(len(replaced), len(data["meterEvents"]))
        for entry, event in zip(replaced, data["meterEvents"]):
            self.assertEqual(entry["assetId"], event["assetId"])
            self.assertEqual(entry["start"]["reading"], event["newInitial"])
            self.assertEqual(entry["expected_flags"], [])

    def test_units_are_explicit_and_readings_are_decimal_strings(self) -> None:
        data = gen.generate(seed=6)
        self.assertEqual({a["meter"]["unit"] for a in data["assets"]}, {"h", "km"})
        for entry in data["entries"]:
            self.assertIn(entry["unit"], {"h", "km"})
            self.assertIsInstance(entry["start"]["reading"], str)
            Decimal(entry["start"]["reading"])


if __name__ == "__main__":
    unittest.main()

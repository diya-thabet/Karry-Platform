"""Tests of the weighbridge generator (P3.0): deterministic, physically sound, ground truth exact on many seeds."""
from __future__ import annotations

import io
import json
import sys
import unittest
from contextlib import redirect_stdout
from datetime import datetime, time
from decimal import Decimal
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import generate_weighbridge as gen  # noqa: E402

RULE = {"tare_deviation": "RULE-WB-01", "overload": "RULE-WB-02", "impossible_turnaround": "RULE-WB-03",
        "after_hours": "RULE-WB-04", "missing_photo": "RULE-WB-05", "blocked_customer": "RULE-WB-07"}


class Determinism(unittest.TestCase):
    def test_the_same_seed_gives_the_same_bytes(self) -> None:
        first, second = io.StringIO(), io.StringIO()
        with redirect_stdout(first):
            gen.main(["--seed", "5", "--days", "2"])
        with redirect_stdout(second):
            gen.main(["--seed", "5", "--days", "2"])
        self.assertEqual(first.getvalue(), second.getvalue())
        json.loads(first.getvalue())


class Physics(unittest.TestCase):
    def test_weights_are_on_the_scale_division_and_the_net_is_gross_minus_tare(self) -> None:
        data = gen.generate(seed=2, days=2)
        for item in data["tickets"]:
            first, second = item["first_pass"]["weight_kg"], item["second_pass"]["weight_kg"]
            self.assertEqual(first % gen.DIVISION_KG, 0)
            self.assertEqual(second % gen.DIVISION_KG, 0)
            self.assertEqual(item["net_kg"], second - first)
            self.assertGreater(item["net_kg"], 0, "a ticket's net is never negative (reversals carry the sign)")
            self.assertEqual(Decimal(item["net_t"]), (Decimal(item["net_kg"]) / 1000).quantize(Decimal("0.001")))
            self.assertLess(item["first_pass"]["at"], item["second_pass"]["at"])

    def test_numbers_are_unique_per_site(self) -> None:
        data = gen.generate(seed=3, sites=2, days=3)
        for site in data["sites"]:
            numbers = [item["number"] for item in data["tickets"] if item["site_id"] == site["id"]]
            self.assertEqual(len(numbers), len(set(numbers)))

    def test_every_ticket_but_the_seeded_one_is_within_operating_hours(self) -> None:
        data = gen.generate(seed=4, days=3)
        late = {entry["ticket_id"] for entry in data["seeded"] if entry["kind"] == "after_hours"}
        for item in data["tickets"]:
            if item["id"] not in late:
                self.assertTrue(time(6) <= datetime.fromisoformat(item["second_pass"]["at"]).time() <= time(18))


class GroundTruth(unittest.TestCase):
    def test_the_rules_flag_exactly_the_seeded_tickets_on_many_seeds(self) -> None:
        for seed in range(1, 16):
            with self.subTest(seed=seed):
                data = gen.generate(seed=seed, sites=2)
                expected = sorted((RULE[entry["kind"]], entry["ticket_id"]) for entry in data["seeded"])
                found = sorted((flag["rule"], flag["ticket_id"]) for flag in data["golden"]["flags"])
                self.assertEqual(found, expected)

    def test_every_anomaly_kind_is_seeded_once_per_site(self) -> None:
        data = gen.generate(seed=1, sites=3)
        for site in data["sites"]:
            kinds = sorted(entry["kind"] for entry in data["seeded"] if entry["site_id"] == site["id"])
            self.assertEqual(kinds, sorted(gen.ANOMALIES))

    def test_a_single_reversal_is_recorded_with_both_signatures(self) -> None:
        data = gen.generate(seed=1)
        self.assertEqual(len(data["reversals"]), 1)
        reversal = data["reversals"][0]
        self.assertTrue(reversal["weighmaster_signed"] and reversal["controller_countersigned"])
        self.assertIn(reversal["original_ticket_id"], {item["id"] for item in data["tickets"]})


if __name__ == "__main__":
    unittest.main()

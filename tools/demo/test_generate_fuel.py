"""Tests of the fuel generator and its oracle (P3.0): deterministic, plain arithmetic right, ground truth exact.

The oracle (fuel_oracle.py) is what the Fuel module's engine will be judged against (TC-P3-FUEL-002, 004 … 008), so its
own arithmetic is checked here against hand-computed values, and the generator's seeded anomalies against what the oracle
flags: every seeded anomaly found, nothing else, on many seeds.
"""
from __future__ import annotations

import io
import json
import sys
import unittest
import uuid
from contextlib import redirect_stdout
from decimal import Decimal
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import fuel_oracle as oracle  # noqa: E402
import generate_fuel as gen  # noqa: E402

D = Decimal


class OracleArithmetic(unittest.TestCase):
    def test_median_and_mad_by_hand(self) -> None:
        values = [D("10"), D("12"), D("11"), D("40"), D("13")]
        self.assertEqual(oracle.median(values), D("12"))
        # deviations from 12: 2, 0, 1, 28, 1 → median 1
        self.assertEqual(oracle.mad(values), D("1"))
        self.assertEqual(oracle.median([D("1"), D("2"), D("3"), D("4")]), D("2.5"))

    def test_the_median_ignores_the_order_of_intervals(self) -> None:
        values = [D(str(v)) for v in (17.2, 18.9, 16.4, 21.0, 18.1, 17.7)]
        self.assertEqual(oracle.median(values), oracle.median(list(reversed(values))))
        self.assertEqual(oracle.mad(values), oracle.mad(sorted(values)))

    def test_calibration_interpolates_linearly_and_refuses_out_of_range(self) -> None:
        table = [(D("0"), D("0")), (D("10"), D("100")), (D("20"), D("300"))]
        self.assertEqual(oracle.litres_at(table, D("5")), D("50"))
        self.assertEqual(oracle.litres_at(table, D("15")), D("200"))  # TC-P3-FUEL-003
        self.assertEqual(oracle.litres_at(table, D("20")), D("300"))
        with self.assertRaises(ValueError):
            oracle.litres_at(table, D("20.1"))

    def test_a_dip_step_is_worth_the_tables_slope(self) -> None:
        table = [(D("0"), D("0")), (D("10"), D("100")), (D("20"), D("300"))]
        self.assertEqual(oracle.dip_step_litres(table, D("5")), D("1"))   # 10 L per cm × 0.1 cm
        self.assertEqual(oracle.dip_step_litres(table, D("15")), D("2"))  # 20 L per cm × 0.1 cm

    def test_an_anomaly_needs_both_the_deviation_and_the_robust_z(self) -> None:
        def month(rates: list[str]) -> dict:
            meter = D("1000")
            dispenses = [{"id": "d0", "asset_id": "a", "at": "2026-11-01T15:00:00", "litres": "100", "meter": str(meter)}]
            for index, rate in enumerate(rates, start=1):
                meter += D("10")
                dispenses.append({"id": f"d{index}", "asset_id": "a", "at": f"2026-11-{index + 1:02d}T15:00:00",
                                  "litres": str(D(rate) * 10), "meter": str(meter)})
            return {"assets": [{"id": "a", "type": "loader"}], "dispenses": dispenses}

        steady = ["18", "18.2", "17.8", "18.1", "17.9", "18", "18.3", "17.7"]
        # 30 % above a steady machine: deviation and z both over their limits.
        result = oracle.consumption(month(steady + ["23.4"]))["intervals"]
        self.assertTrue(result[-1]["anomaly"])
        # Fewer than 8 earlier intervals: no baseline, no verdict (TC-P3-FUEL-004).
        early = oracle.consumption(month(steady[:5] + ["40"]))["intervals"]
        self.assertIsNone(early[-1]["baseline"])
        self.assertFalse(early[-1]["anomaly"])
        # 10 % above: normal variation (TC-P3-FUEL-006).
        normal = oracle.consumption(month(steady + ["19.8"]))["intervals"]
        self.assertFalse(normal[-1]["anomaly"])

    def test_a_refuel_without_usage_is_not_a_rate(self) -> None:
        data = {"assets": [{"id": "a", "type": "loader"}], "dispenses": [
            {"id": "d0", "asset_id": "a", "at": "2026-11-01T15:00:00", "litres": "100", "meter": "1000"},
            {"id": "d1", "asset_id": "a", "at": "2026-11-01T18:00:00", "litres": "40", "meter": "1000"},
        ]}
        interval = oracle.consumption(data)["intervals"][0]
        self.assertIsNone(interval["rate"])
        self.assertFalse(interval["anomaly"])


class Determinism(unittest.TestCase):
    def test_the_same_seed_gives_the_same_bytes(self) -> None:
        first, second = io.StringIO(), io.StringIO()
        with redirect_stdout(first):
            gen.main(["--seed", "7", "--days", "20"])
        with redirect_stdout(second):
            gen.main(["--seed", "7", "--days", "20"])
        self.assertEqual(first.getvalue(), second.getvalue())
        json.loads(first.getvalue())

    def test_ids_are_uuid_v7_and_numbers_are_strings(self) -> None:
        data = gen.generate(seed=3, days=10)
        for dispense in data["dispenses"]:
            self.assertEqual((uuid.UUID(dispense["id"]).int >> 76) & 0xF, 7)
            self.assertIsInstance(dispense["litres"], str)
            Decimal(dispense["litres"])


class GroundTruth(unittest.TestCase):
    """Every seeded anomaly is found by the oracle, and nothing else is flagged (many seeds)."""

    def check(self, seed: int) -> None:
        data = gen.generate(seed=seed)
        golden, seeded = data["golden"], data["seeded"]
        kinds = {entry["kind"] for entry in seeded}
        self.assertEqual(kinds, {"overconsumption", "refuel_without_usage", "frequent_small_refuels", "over_capacity",
                                 "tank_variance", "delivery_variance"})

        allowed = {item for entry in seeded for item in entry.get("dispense_ids", [])}
        heavy = next(entry for entry in seeded if entry["kind"] == "overconsumption")
        anomalies = [interval for interval in golden["intervals"] if interval["anomaly"]]
        stray = [interval for interval in anomalies if interval["dispense_id"] not in allowed and interval["asset_id"] != heavy["asset_id"]]
        self.assertEqual(stray, [], f"seed {seed}: anomalies nobody seeded")
        heavy_hits = [interval for interval in anomalies if interval["asset_id"] == heavy["asset_id"]]
        self.assertTrue(heavy_hits, f"seed {seed}: the 25 % overconsumption was not detected (TC-P3-FUEL-005)")
        self.assertTrue(all(interval["at_or_after"] for interval in self.after(data, heavy_hits, heavy["from_day"])),
                        f"seed {seed}: the over-consuming machine flagged before it started")

        seeded_days = {(entry["storage_id"], entry["day"]) for entry in seeded if entry["kind"] in ("tank_variance", "delivery_variance")}
        flagged_days = {(day["storage_id"], day["day"]) for day in golden["days"] if day["flag"]}
        self.assertEqual(flagged_days, seeded_days, f"seed {seed}: tank reconciliation flags")
        self.assertEqual(sum(1 for item in golden["deliveries"] if item["flag"]), 1, f"seed {seed}: delivery checks")
        self.assertEqual(sum(1 for interval in golden["intervals"] if interval["rate"] is None), 1, f"seed {seed}: refuels without usage")

    @staticmethod
    def after(data: dict, intervals: list[dict], from_day: str) -> list[dict]:
        moments = {dispense["id"]: dispense["at"] for dispense in data["dispenses"]}
        return [{"at_or_after": moments[interval["dispense_id"]][:10] >= from_day} for interval in intervals]

    def test_seeds_1_to_12(self) -> None:
        for seed in range(1, 13):
            with self.subTest(seed=seed):
                self.check(seed)

    def test_reconciliation_identity_holds_every_day(self) -> None:
        data = gen.generate(seed=4)
        for day in data["golden"]["days"]:
            identity = D(day["opening"]) + D(day["delivered"]) - D(day["dispensed"]) - D(day["closing"])
            self.assertEqual(identity.quantize(D("0.01")), D(day["variance"]))


if __name__ == "__main__":
    unittest.main()

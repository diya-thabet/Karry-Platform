"""Each threat claimed for Phase 1 cites tests that exist (hardening plan H-W3)."""
from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

import threat_traceability

HEADER = "| ID | STRIDE | Threat | Mitigation | Verified by |\n|---|---|---|---|---|\n"


def repo(rows: str, files: dict[str, str]) -> Path:
    root = Path(tempfile.mkdtemp())
    model = root / threat_traceability.THREAT_MODEL
    model.parent.mkdir(parents=True)
    model.write_text(HEADER + rows, encoding="utf-8")
    for name, content in files.items():
        (root / name).parent.mkdir(parents=True, exist_ok=True)
        (root / name).write_text(content, encoding="utf-8")
    return root


class ThreatTraceabilityTests(unittest.TestCase):
    def test_a_claim_traced_to_a_test_case_and_a_test_class_passes(self) -> None:
        root = repo(
            "| T-01 | Spoofing | x | y | US-P1-OFF-04; **verified P1.15**: TC-P1-OFF-003, `DeviceApiTests` |\n",
            {
                "src/backend/Karry.Identity.Tests/DeviceApiTests.cs": "public sealed class DeviceApiTests {}",
                "src/frontend/apps/field/src/unlock.test.ts": 'it("TC-P1-OFF-003 asks to sign in again after 72 h")',
            },
        )

        self.assertEqual(threat_traceability.main(["--root", str(root)]), 0)

    def test_a_threat_left_to_a_later_phase_is_not_checked(self) -> None:
        root = repo("| T-15 | Info disclosure | x | y | ADR-0013, US-P8-AI-11, TC-P8-AI-004 |\n", {})

        self.assertEqual(threat_traceability.main(["--root", str(root)]), 0)

    def test_a_claim_without_evidence_fails(self) -> None:
        root = repo("| T-17 | Info disclosure | x | y | US-P1-OFF-02/04 |\n", {})

        self.assertEqual(threat_traceability.main(["--root", str(root)]), 1)

    def test_a_cited_test_case_or_class_that_no_test_names_fails(self) -> None:
        cited_case = repo("| T-09 | Info | x | y | **verified P1.5**: TC-P1-TEN-001/002 |\n", {"tests/Suite/IsolationSuite.cs": "TC-P1-TEN-001"})
        cited_class = repo("| T-12 | DoS | x | y | P1.18a, `RateLimitTests` |\n", {"tests/Api/OtherTests.cs": "class OtherTests {}"})

        self.assertEqual(threat_traceability.main(["--root", str(cited_case)]), 1)
        self.assertEqual(threat_traceability.main(["--root", str(cited_class)]), 1)

    def test_a_case_mentioned_only_outside_test_code_does_not_count(self) -> None:
        root = repo(
            "| T-05 | Tampering | x | y | **verified P1.15**: TC-P1-OFF-005 |\n",
            {"src/backend/Karry.Sync/SyncUploads.cs": "// TC-P1-OFF-005", "tests/Api/bin/Debug/Docs.xml": "TC-P1-OFF-005"},
        )

        self.assertEqual(threat_traceability.main(["--root", str(root)]), 1)

    def test_lists_and_ranges_of_cases_are_expanded(self) -> None:
        threats = threat_traceability.claimed_threats(HEADER + "| T-06 | Tampering | x | y | TC-P1-AUD-001/003, TC-P1-TEN-007..009 |\n")

        self.assertIsNotNone(threats)
        self.assertEqual(threats[0].cases, ["TC-P1-AUD-001", "TC-P1-AUD-003", "TC-P1-TEN-007", "TC-P1-TEN-008", "TC-P1-TEN-009"])

    def test_refuses_a_model_without_a_threat_table(self) -> None:
        self.assertEqual(threat_traceability.main(["--root", tempfile.mkdtemp()]), 2)

    def test_writes_the_step_summary(self) -> None:
        root = repo("| T-17 | Info disclosure | x | y | US-P1-OFF-02/04 |\n", {})
        summary = root / "summary.md"

        threat_traceability.main(["--root", str(root), "--summary", str(summary)])

        text = summary.read_text(encoding="utf-8")
        self.assertIn("1 threats claimed for Phase 1", text)
        self.assertIn("T-17: claimed for Phase 1 but cites no test case", text)


if __name__ == "__main__":
    unittest.main()

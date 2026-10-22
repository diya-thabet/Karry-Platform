"""The merge guard refuses red, incomplete or unapproved sensitive pull requests (prevents a repeat of #7)."""
from __future__ import annotations

import unittest

from merge_guard import REQUIRED_CHECKS, evaluate_checks, parse_checks, sensitive_files


def checks(state: str = "SUCCESS", **overrides: str) -> list[dict[str, str]]:
    result = [{"name": name, "state": overrides.get(name.replace(" ", "_").replace("-", "_"), state)} for name in REQUIRED_CHECKS]
    return result


class EvaluateChecksTests(unittest.TestCase):
    def test_all_required_green_passes(self) -> None:
        self.assertEqual(evaluate_checks(checks()), ("pass", []))

    def test_one_failed_required_check_fails(self) -> None:
        verdict, reasons = evaluate_checks(checks(security="FAILURE"))
        self.assertEqual(verdict, "fail")
        self.assertEqual(reasons, ["security: FAILURE"])

    def test_missing_required_check_is_not_a_pass(self) -> None:
        partial = [c for c in checks() if c["name"] != "local-stack"]
        verdict, reasons = evaluate_checks(partial)
        self.assertEqual(verdict, "pending")
        self.assertIn("local-stack: not reported yet", reasons)

    def test_running_check_is_pending(self) -> None:
        self.assertEqual(evaluate_checks(checks(backend="IN_PROGRESS"))[0], "pending")

    def test_failure_wins_over_pending(self) -> None:
        self.assertEqual(evaluate_checks(checks(backend="IN_PROGRESS", frontend="FAILURE"))[0], "fail")

    def test_failing_optional_check_also_blocks(self) -> None:
        verdict, reasons = evaluate_checks([*checks(), {"name": "extensions on pg18", "state": "FAILURE"}])
        self.assertEqual(verdict, "fail")
        self.assertIn("extensions on pg18: FAILURE", reasons)

    def test_a_rerun_failure_is_not_hidden_by_a_success(self) -> None:
        self.assertEqual(evaluate_checks([*checks(), {"name": "backend", "state": "FAILURE"}])[0], "fail")

    def test_cancelled_counts_as_failure(self) -> None:
        self.assertEqual(evaluate_checks(checks(tooling="CANCELLED"))[0], "fail")


class ParseChecksTests(unittest.TestCase):
    PENDING_JSON = '[{"name":"backend","state":"IN_PROGRESS"}]'

    def test_pending_exit_code_is_parsed_not_raised(self) -> None:
        # gh exits 8 while checks are pending; the guard must keep waiting, not crash (seen on PR #15).
        self.assertEqual(parse_checks(8, self.PENDING_JSON, ""), [{"name": "backend", "state": "IN_PROGRESS"}])

    def test_failed_exit_code_is_parsed(self) -> None:
        self.assertEqual(parse_checks(1, '[{"name":"security","state":"FAILURE"}]', "")[0]["state"], "FAILURE")

    def test_no_checks_yet_is_empty(self) -> None:
        self.assertEqual(parse_checks(1, "", "no checks reported on the 'x' branch"), [])

    def test_unexpected_error_fails_closed(self) -> None:
        with self.assertRaises(RuntimeError):
            parse_checks(4, "", "HTTP 401: Bad credentials")


class SensitiveFilesTests(unittest.TestCase):
    def test_sensitive_paths_are_detected(self) -> None:
        paths = [
            "src/backend/Modules/Tenancy/Karry.Modules.Tenancy.Domain/Tenant.cs",
            "src/backend/Modules/Trust/Karry.Modules.Trust.Infrastructure/Ledger.cs",
            ".github/workflows/ci.yml",
            "src/frontend/packages/crypto/src/keys.ts",
            "renovate.json",
        ]
        self.assertEqual(sensitive_files(paths), paths)

    def test_ordinary_paths_are_not_sensitive(self) -> None:
        paths = [
            "src/backend/Modules/Assets/Karry.Modules.Assets.Domain/Asset.cs",
            "src/frontend/apps/portal/src/App.tsx",
            "docs/04-phases/phase-01-platform-foundation/tracker.md",
            "README.md",
        ]
        self.assertEqual(sensitive_files(paths), [])


if __name__ == "__main__":
    unittest.main()

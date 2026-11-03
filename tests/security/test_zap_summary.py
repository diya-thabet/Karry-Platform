"""Tests of zap_summary.py: which ZAP findings fail the H-W1 run (python3 -m unittest discover -s tests/security)."""
import contextlib
import io
import json
import pathlib
import tempfile
import unittest

import zap_summary


def alert(plugin, risk, name="finding"):
    return {"pluginid": plugin, "riskcode": risk, "alert": name, "count": "1", "instances": [{"uri": "http://localhost:5173/"}]}


class ZapSummaryTests(unittest.TestCase):
    def judge(self, alerts, accepted=""):
        with tempfile.TemporaryDirectory() as folder:
            results = pathlib.Path(folder)
            (results / "portal.json").write_text(json.dumps({"site": [{"alerts": alerts}]}), encoding="utf-8")
            (results / "accepted.tsv").write_text(accepted, encoding="utf-8")
            with contextlib.redirect_stdout(io.StringIO()) as printed:
                code = zap_summary.main([str(results), str(results / "accepted.tsv")])
            return code, printed.getvalue()

    def test_no_findings_pass(self):
        self.assertEqual(self.judge([])[0], 0)

    def test_a_high_finding_fails_even_when_listed_as_accepted(self):
        self.assertEqual(self.judge([alert("40012", "3")], "40012\tnever\n")[0], 1)

    def test_a_medium_finding_fails_unless_accepted(self):
        self.assertEqual(self.judge([alert("10038", "2")])[0], 1)
        code, printed = self.judge([alert("10038", "2")], "# comment\n10038\tthe reason\n")
        self.assertEqual(code, 0)
        self.assertIn("accepted", printed)

    def test_low_and_informational_findings_are_listed_but_never_fail(self):
        code, printed = self.judge([alert("10096", "1", "Timestamp disclosure"), alert("10027", "0")])
        self.assertEqual(code, 0)
        self.assertIn("Timestamp disclosure", printed)

    def test_an_accepted_finding_needs_a_reason(self):
        with self.assertRaises(SystemExit):
            self.judge([alert("10038", "2")], "10038\t\n")


if __name__ == "__main__":
    unittest.main()

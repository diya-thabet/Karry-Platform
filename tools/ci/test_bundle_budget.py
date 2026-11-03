"""The field app's initial JavaScript is measured gzipped against its budget; later chunks do not count."""
from __future__ import annotations

import os
import tempfile
import unittest
from pathlib import Path

import bundle_budget


def dist(files: dict[str, bytes], html: str) -> Path:
    root = Path(tempfile.mkdtemp())
    (root / "assets").mkdir()
    for name, content in files.items():
        (root / name).write_bytes(content)
    (root / "index.html").write_text(html, encoding="utf-8")
    return root


class BundleBudgetTests(unittest.TestCase):
    def test_counts_the_entry_and_its_preloads_not_lazy_chunks(self) -> None:
        root = dist(
            {"assets/index.js": os.urandom(4096), "assets/vendor.js": os.urandom(2048), "assets/catalog-ar.js": os.urandom(50_000)},
            '<script type="module" crossorigin src="/assets/index.js"></script><link rel="modulepreload" href="/assets/vendor.js">',
        )

        self.assertEqual([p.name for p in bundle_budget.initial_scripts(root)], ["index.js", "vendor.js"])
        self.assertEqual(bundle_budget.main([str(root), "--max-kb", "7"]), 0)

    def test_fails_over_the_budget(self) -> None:
        root = dist({"assets/index.js": os.urandom(20_000)}, '<script type="module" src="/assets/index.js"></script>')

        self.assertEqual(bundle_budget.main([str(root), "--max-kb", "10"]), 1)

    def test_refuses_a_missing_build(self) -> None:
        self.assertEqual(bundle_budget.main([tempfile.mkdtemp()]), 2)

    def test_writes_the_step_summary(self) -> None:
        root = dist({"assets/index.js": b"x" * 1000}, '<script type="module" src="/assets/index.js"></script>')
        summary = root / "summary.md"

        bundle_budget.main([str(root), "--summary", str(summary)])
        self.assertIn("initial JS", summary.read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()

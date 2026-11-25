"""TC-P1-PLT-005: the coverage gate fails below the threshold and merges reports correctly."""
from __future__ import annotations

import os
import tempfile
import unittest

import coverage_gate


def report(package: str, hits: dict[int, int], filename: str = "Order.cs") -> str:
    lines = "".join(f'<line number="{n}" hits="{h}"/>' for n, h in hits.items())
    xml = (
        f'<coverage><packages><package name="{package}"><classes>'
        f'<class name="C" filename="{filename}"><lines>{lines}</lines></class>'
        f"</classes></package></packages></coverage>"
    )
    fd, path = tempfile.mkstemp(suffix=".cobertura.xml")
    with os.fdopen(fd, "w", encoding="utf-8") as fh:
        fh.write(xml)
    return path


class CoverageGateTests(unittest.TestCase):
    def test_domain_below_threshold_fails(self) -> None:
        # 7 of 10 lines covered = 70 % < 80 %
        path = report("Karry.Modules.Fuel.Domain", {n: (1 if n <= 7 else 0) for n in range(1, 11)})
        self.assertEqual(coverage_gate.main([path]), 1)

    def test_domain_at_threshold_passes(self) -> None:
        path = report("Karry.Modules.Fuel.Domain", {n: (1 if n <= 8 else 0) for n in range(1, 11)})
        self.assertEqual(coverage_gate.main([path]), 0)

    def test_lines_covered_by_another_test_project_count(self) -> None:
        # Each report alone is 50 %; together every line is covered.
        a = report("Karry.Modules.Fuel.Domain", {1: 1, 2: 1, 3: 0, 4: 0})
        b = report("Karry.Modules.Fuel.Domain", {1: 0, 2: 0, 3: 2, 4: 5})
        self.assertEqual(coverage_gate.main([a, b]), 0)
        [merged] = coverage_gate.merge([a, b], coverage_gate.re.compile(r"\.Domain$"))
        self.assertEqual((merged.covered, merged.total), (4, 4))

    def test_same_line_number_in_different_files_is_not_merged(self) -> None:
        a = report("Karry.Modules.Fuel.Domain", {1: 1}, filename="A.cs")
        b = report("Karry.Modules.Fuel.Domain", {1: 0}, filename="B.cs")
        [merged] = coverage_gate.merge([a, b], coverage_gate.re.compile(r"\.Domain$"))
        self.assertEqual((merged.covered, merged.total), (1, 2))

    def test_non_domain_assemblies_are_not_gated(self) -> None:
        path = report("Karry.Modules.Fuel.Infrastructure", {1: 0, 2: 0})
        self.assertEqual(coverage_gate.main([path]), 0)

    def test_one_failing_domain_among_passing_ones_fails(self) -> None:
        good = report("Karry.Modules.Fuel.Domain", {1: 1})
        bad = report("Karry.Modules.Assets.Domain", {1: 1, 2: 0})
        self.assertEqual(coverage_gate.main([good, bad]), 1)


class SolutionCoverageTests(unittest.TestCase):
    @staticmethod
    def solution(*projects: str) -> str:
        items = "".join(f'<Project Path="src/backend/Modules/X/{p}/{p}.csproj" />' for p in projects)
        fd, path = tempfile.mkstemp(suffix=".slnx")
        with os.fdopen(fd, "w", encoding="utf-8") as fh:
            fh.write(f'<Solution><Folder Name="/src/">{items}</Folder></Solution>')
        return path

    def test_domain_project_not_exercised_by_any_test_fails(self) -> None:
        # The untested assembly is absent from every report: without --solution this would pass silently.
        covered = report("Karry.Modules.Fuel.Domain", {1: 1})
        sln = self.solution("Karry.Modules.Fuel.Domain", "Karry.Modules.Assets.Domain", "Karry.Modules.Assets.Api")
        self.assertEqual(coverage_gate.main([covered]), 0)
        self.assertEqual(coverage_gate.main(["--solution", sln, covered]), 1)

    def test_every_domain_project_reported_and_covered_passes(self) -> None:
        a = report("Karry.Modules.Fuel.Domain", {1: 1})
        b = report("Karry.Modules.Assets.Domain", {1: 1})
        sln = self.solution("Karry.Modules.Fuel.Domain", "Karry.Modules.Assets.Domain")
        self.assertEqual(coverage_gate.main(["--solution", sln, a, b]), 0)

    def test_solution_without_domain_projects_passes(self) -> None:
        sln = self.solution("Karry.Host", "Karry.BuildingBlocks")
        self.assertEqual(coverage_gate.main(["--solution", sln, report("Karry.Host", {1: 0})]), 0)

    def test_a_project_is_expected_under_its_assembly_name(self) -> None:
        # tools/ledger-verifier builds karry-ledger: the report names the assembly, not the project file.
        directory = tempfile.mkdtemp()
        project = os.path.join(directory, "tools", "Karry.LedgerVerifier")
        os.makedirs(project)
        with open(os.path.join(project, "Karry.LedgerVerifier.csproj"), "w", encoding="utf-8") as fh:
            fh.write("<Project><PropertyGroup><AssemblyName>karry-ledger</AssemblyName></PropertyGroup></Project>")
        sln = os.path.join(directory, "K.slnx")
        with open(sln, "w", encoding="utf-8") as fh:
            fh.write('<Solution><Project Path="tools/Karry.LedgerVerifier/Karry.LedgerVerifier.csproj" /></Solution>')
        gate = ["--pattern", r"^(Karry\.|karry-ledger$)", "--threshold", "90", "--solution", sln]

        self.assertEqual(coverage_gate.solution_projects(sln, coverage_gate.re.compile(".")), ["karry-ledger"])
        self.assertEqual(coverage_gate.main([*gate, report("karry-ledger", {1: 1})]), 0)
        self.assertEqual(coverage_gate.main([*gate, report("Karry.Other", {1: 1})]), 1, "karry-ledger is not in any report")


class GeneratedCodeTests(unittest.TestCase):
    """Only what people wrote counts: the OpenAPI generator's file alone took Api assemblies from ~100 % to ~40 %."""

    def test_generated_files_are_left_out_by_default(self) -> None:
        written = report("Karry.Modules.Fuel.Api", {1: 1, 2: 1}, filename="/src/Karry.Modules.Fuel.Api/FuelModule.cs")
        generated = [
            report("Karry.Modules.Fuel.Api", {n: 0 for n in range(1, 50)}, filename=name)
            for name in (
                "/src/Karry.Modules.Fuel.Api/obj/Release/net10.0/OpenApiXmlCommentSupport.generated.cs",
                "/src/Karry.Modules.Fuel.Api/Regexes.g.cs",
                "C:\\src\\Karry.Modules.Fuel.Api\\obj\\Debug\\Logging.cs",
                "/src/Karry.Modules.Fuel.Api/Migrations/20261101000000_Initial.Designer.cs",
            )
        ]
        gate = ["--pattern", r"\.Api$", "--threshold", "90"]

        self.assertEqual(coverage_gate.main([*gate, written, *generated]), 0)
        self.assertEqual(coverage_gate.main([*gate, "--exclude-files", "", written, *generated]), 1, "kept on request")

    def test_a_hand_written_file_named_like_obj_is_kept(self) -> None:
        path = report("Karry.Modules.Fuel.Domain", {1: 0}, filename="/src/Karry.Modules.Fuel.Domain/objects/Order.cs")
        self.assertEqual(coverage_gate.main([path]), 1)


class TotalThresholdTests(unittest.TestCase):
    def test_every_assembly_above_its_floor_but_the_total_below_fails(self) -> None:
        a = report("Karry.A", {n: (1 if n <= 9 else 0) for n in range(1, 11)})  # 90 %
        b = report("Karry.B", {n: (1 if n <= 9 else 0) for n in range(1, 11)})  # 90 %
        gate = ["--pattern", r"^Karry\.", "--threshold", "90"]

        self.assertEqual(coverage_gate.main([*gate, a, b]), 0)
        self.assertEqual(coverage_gate.main([*gate, "--total-threshold", "90", a, b]), 0)
        self.assertEqual(coverage_gate.main([*gate, "--total-threshold", "95", a, b]), 1)

    def test_the_total_weighs_assemblies_by_their_lines(self) -> None:
        big = report("Karry.Big", {n: 1 for n in range(1, 100)} | {100: 0})  # 99 of 100
        small = report("Karry.Small", {1: 0, 2: 1})  # 1 of 2
        self.assertEqual(coverage_gate.main(["--pattern", r"^Karry\.", "--threshold", "0", "--total-threshold", "97", big, small]), 0)


if __name__ == "__main__":
    unittest.main()

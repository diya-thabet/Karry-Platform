using System.Globalization;
using Karry.BuildingBlocks.Imports;

namespace Karry.Modules.Assets.Application;

/// <summary>A mistake planted in the demo fleet file: the sheet row, the column, what was typed and the problem it gives.</summary>
public sealed record DemoFleetMistake(int Row, string Column, string? Typed, string Code);

/// <summary>
/// The P1.14 demo scenario (US-P1-ORG-03): 60 machines of demo company A (sites <c>CAR-NORD</c> and <c>LAV-SUD</c>) as
/// an onboarding customer would fill the assets template, first with 8 everyday mistakes (a forgotten name, a type typed
/// in words, a misspelt site, a code used twice, a letter O in a year, a date that does not exist, a year with a slip of
/// the finger, a forbidden character), then fixed. The codes do not collide with the seeded machines, so the fixed file
/// creates 60 machines next to them (68 in all, within the trial plan's 2 × 50). Makes and models are common ones in the
/// region; serials and plates are invented.
/// </summary>
public static class DemoFleetFile
{
    public const string Language = "fr";

    /// <summary>The mistakes, in sheet order. The code used twice (row 20) also reports the row it repeats (row 3).</summary>
    public static IReadOnlyList<DemoFleetMistake> Mistakes { get; } =
    [
        new(5, "name", null, ImportCodes.ValueRequired),
        new(9, "type", "pelle", "assets.type_invalid"),
        new(14, "site", "CAR-NROD", "assets.site_invalid"),
        new(20, "code", "PEL-03", ImportCodes.KeyDuplicate),
        new(27, "year", "2O19", ImportCodes.NumberInvalid),
        new(33, "acquiredOn", "31/02/2020", ImportCodes.DateInvalid),
        new(41, "year", "1019", "assets.year_invalid"),
        new(52, "code", "LAV_03", "assets.code_invalid"),
    ];

    /// <summary>The template headers, in the demo file's language.</summary>
    public static IReadOnlyList<string> Headers { get; } = [.. AssetImport.AssetColumns.Select(c => c.HeaderIn(Language))];

    /// <summary>The 60 machines, each as its cells in column order (null for an empty cell).</summary>
    public static IReadOnlyList<IReadOnlyList<string?>> Rows(bool withMistakes)
    {
        var rows = Fleet().Select(m => (string?[])m).ToList();
        if (withMistakes)
        {
            foreach (var mistake in Mistakes)
            {
                rows[mistake.Row - 2][ColumnIndex(mistake.Column)] = mistake.Typed;
            }
        }

        return rows;
    }

    /// <summary>The problems the file with mistakes must give, as (row, cell, code), in sheet order.</summary>
    public static IReadOnlyList<(int Row, string Cell, string Code)> ExpectedIssues { get; } =
    [
        .. Mistakes.Select(m => (m.Row, CellOf(m.Row, m.Column), m.Code))
            .Append((3, CellOf(3, "code"), ImportCodes.KeyDuplicate))
            .OrderBy(i => i.Item1),
    ];

    private static int ColumnIndex(string column) => AssetImport.AssetColumns.Select((c, i) => (c.Key, i)).Single(c => c.Key == column).i;

    private static string CellOf(int row, string column) => $"{(char)('A' + ColumnIndex(column))}{row.ToString(CultureInfo.InvariantCulture)}";

    private static IEnumerable<string?[]> Fleet()
    {
        // (prefix, first number, count, name, type, site, make, model, plated, first year)
        (string Prefix, int First, int Count, string Name, string Type, string Site, string Make, string Model, bool Plated, int Year)[] groups =
        [
            ("PEL", 2, 10, "Pelle", "excavator", "CAR-NORD", "Caterpillar", "336", false, 2014),
            ("CHG", 2, 8, "Chargeuse", "wheel-loader", "CAR-NORD", "Volvo", "L150H", false, 2015),
            ("TOMB", 2, 16, "Tombereau", "haul-truck", "CAR-NORD", "Komatsu", "HD465-8", true, 2012),
            ("FOR", 2, 4, "Foreuse", "drill-rig", "CAR-NORD", "Epiroc", "SmartROC T35", false, 2018),
            ("BUL", 1, 2, "Bulldozer", "dozer", "CAR-NORD", "Caterpillar", "D8T", false, 2016),
            ("NIV", 1, 1, "Niveleuse", "grader", "CAR-NORD", "Caterpillar", "140M", false, 2017),
            ("CON", 2, 1, "Concasseur à cône", "crusher-cone", "CAR-NORD", "Metso", "HP300", false, 2019),
            ("CON", 3, 1, "Concasseur à percussion", "crusher-impact", "CAR-NORD", "Sandvik", "CI722", false, 2020),
            ("CON", 4, 1, "Concasseur à mâchoires", "crusher-jaw", "CAR-NORD", "Metso", "C120", false, 2021),
            ("GEN", 1, 1, "Groupe électrogène", "generator", "CAR-NORD", "SDMO", "X550C", false, 2016),
            ("CIT", 1, 1, "Camion citerne", "fuel-bowser", "CAR-NORD", "Mercedes-Benz", "Actros 3336", true, 2019),
            ("CRB", 2, 3, "Crible", "screen", "LAV-SUD", "Metso", "ES303", false, 2020),
            ("LAV", 2, 2, "Laveur à vis", "washer", "LAV-SUD", "CDE", "ScrewWash", false, 2021),
            ("CNV", 1, 5, "Convoyeur", "conveyor", "LAV-SUD", "Terex", "TC80", false, 2020),
            ("CHG", 10, 2, "Chargeuse", "wheel-loader", "LAV-SUD", "Liebherr", "L566", false, 2019),
            ("VL", 2, 2, "Pick-up", "light-vehicle", "LAV-SUD", "Toyota", "Hilux", true, 2022),
        ];

        var serial = 0;
        foreach (var g in groups)
        {
            for (var n = g.First; n < g.First + g.Count; n++)
            {
                serial++;
                var year = g.Year + (n % 4);
                var code = $"{g.Prefix}-{n.ToString("00", CultureInfo.InvariantCulture)}";
                yield return
                [
                    code,
                    $"{g.Name} {g.Make} {g.Model} n°{n.ToString(CultureInfo.InvariantCulture)}",
                    g.Type,
                    g.Site,
                    g.Make,
                    g.Model,
                    $"KD{year.ToString(CultureInfo.InvariantCulture)}{serial.ToString("0000", CultureInfo.InvariantCulture)}",
                    g.Plated ? $"{(180 + serial).ToString(CultureInfo.InvariantCulture)} TU {(4000 + (serial * 37)).ToString(CultureInfo.InvariantCulture)}" : null,
                    year.ToString(CultureInfo.InvariantCulture),
                    $"{year.ToString(CultureInfo.InvariantCulture)}-{(1 + (serial % 12)).ToString("00", CultureInfo.InvariantCulture)}-15",
                ];
            }
        }
    }
}

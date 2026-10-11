using System.Text.RegularExpressions;

namespace Stockpile.ArchitectureTests;

/// <summary>
/// security-test-matrix I5 — "raw SQL with interpolated input anywhere in
/// code" must fail the build. The raw ADO/EF string APIs
/// (<c>FromSqlRaw</c>, <c>ExecuteSqlRaw(Async)</c>, <c>SqlQueryRaw</c>) take
/// a SQL string whose text is parsed by the server, so any interpolation or
/// concatenation into one is an injection site; EF's INTERPOLATED overloads
/// (<c>FromSql($"…")</c>, <c>FromSqlInterpolated</c>) are deliberately not
/// flagged — they compose parameters, which is the sanctioned way to embed
/// input (architecture §10).
/// <para>
/// The scan reads the SHIPPED tree (<c>backend/src</c>, obj/bin skipped);
/// test-side SQL is fixture data, not a shipped injection site. Shipped code
/// contains zero raw-SQL call sites today (grep-verified at authoring), so
/// the scan is a future-code guard — like design-qa's source rules (1.54),
/// it ships with a selftest proving the classifier can actually fire.
/// </para>
/// </summary>
public sealed class RawSqlInterpolationTests
{
    /// <summary>
    /// The raw string-SQL APIs. The interpolated overloads
    /// (<c>FromSql(</c>, <c>ExecuteSql(</c>, <c>FromSqlAsync(</c>,
    /// <c>ExecuteSqlAsync(</c>) are absent from this list on purpose: they
    /// bind interpolated holes as SQL parameters.
    /// </summary>
    private const string RawApiPattern =
        @"\b(?<api>FromSqlRaw|ExecuteSqlRaw|ExecuteSqlRawAsync|SqlQueryRaw)\s*\(";

    [Fact]
    public void No_raw_sql_call_site_interpolates_or_concatenates_its_sql()
    {
        var offenders = new List<string>();
        foreach (var file in SourceFiles())
        {
            var text = File.ReadAllText(file);
            foreach (Match call in Regex.Matches(text, RawApiPattern))
            {
                var argument = ArgumentOf(text, call.Index + call.Length - 1);
                if (!IsConstantOrName(argument))
                {
                    offenders.Add(
                        $"{Relative(file)}: …{Snippet(text, call.Index, argument)}… — raw SQL argument must be a literal or a const name");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Raw SQL call sites whose argument is not a constant (I5):"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// The classifier must fire: a constant or a bare const name passes,
    /// interpolation, concatenation, helper calls and aliased verbatim
    /// interpolation do not. Without this, a scanner that silently matched
    /// nothing would look identical to one that worked.
    /// </summary>
    [Fact]
    public void The_scan_classifier_recognises_poisoned_and_clean_arguments()
    {
        Assert.True(IsConstantOrName(@"""SELECT 1"""));
        Assert.True(IsConstantOrName(@"@""SELECT * FROM Users"""));
        Assert.True(IsConstantOrName("SomeConstSql"));
        Assert.False(IsConstantOrName(@"$""SELECT * FROM Users WHERE Id = {id}"""));
        Assert.False(IsConstantOrName(@"@$""SELECT {id}"""));
        Assert.False(IsConstantOrName(@"$@""SELECT {id}"""));
        Assert.False(IsConstantOrName("sql + suffix"));
        Assert.False(IsConstantOrName("BuildSql(userId)"));
        Assert.False(IsConstantOrName("sql")); // camelCase local — likely a built string
    }

    /// <summary>
    /// A single string literal (plain with backslash escapes, or verbatim
    /// with doubled quotes — braces inside either are text, not holes) or a
    /// single PascalCase identifier (a `const` by naming convention, whose
    /// declaration is one reviewable site). Everything else — interpolation,
    /// concatenation, helper calls, and camelCase locals that likely hold a
    /// built string — is flagged. A PascalCase variable built from
    /// interpolation elsewhere passes as a name; that residue is covered by
    /// review, and by the fact that the shipped tree currently has no raw
    /// SQL at all.
    /// </summary>
    private static bool IsConstantOrName(string argument) =>
        Regex.IsMatch(argument, "^\"(?:[^\"\\\\]|\\\\.)*\"$")
        || Regex.IsMatch(argument, "^@\"(?:[^\"]|\"\")*\"$")
        || Regex.IsMatch(argument, "^[A-Z][A-Za-z0-9_]*$");

    /// <summary>The balanced-paren argument text of the call whose '(' is at <paramref name="openParen"/>.</summary>
    private static string ArgumentOf(string text, int openParen)
    {
        var depth = 0;
        var start = openParen + 1;
        for (var i = openParen; i < text.Length; i++)
        {
            if (text[i] == '(')
            {
                depth++;
            }
            else if (text[i] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return text[start..i].Trim();
                }
            }
        }

        return text[start..].Trim(); // unbalanced — let the constant check fail it
    }

    private static string Snippet(string text, int callStart, string argument) =>
        text[callStart..Math.Min(text.Length, callStart + argument.Length + 30)]
            .Replace('\r', ' ').Replace('\n', ' ');

    private static string Relative(string file) =>
        Path.GetRelativePath(RepoRoot(), file).Replace('\\', '/');

    /// <summary>All shipped .cs files (backend/src, obj/bin excluded).</summary>
    private static IEnumerable<string> SourceFiles() =>
        Directory
            .EnumerateFiles(Path.Combine(RepoRoot(), "backend", "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    /// <summary>
    /// Walks up from the test binary until the repo's backend/src marker is
    /// found — the suite runs from bin/Debug/net10.0, so no relative path
    /// from the assembly location is stable on its own.
    /// </summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "backend", "src", "Stockpile.Api", "Stockpile.Api.csproj")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"No repo root above {AppContext.BaseDirectory} — backend/src/Stockpile.Api/Stockpile.Api.csproj not found.");
    }
}

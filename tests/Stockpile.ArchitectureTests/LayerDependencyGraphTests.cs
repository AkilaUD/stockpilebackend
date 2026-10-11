using System.Text.RegularExpressions;

namespace Stockpile.ArchitectureTests;

/// <summary>
/// Task 1.33 — architecture.md §3.1 dependency graph: every DIRECT project
/// reference in <c>backend/src</c> must fall inside the layer graph,
/// </summary>
/// <remarks>
/// <code>
/// Domain          → (nothing)
/// Contracts       → (nothing)
/// Application     → Domain, Contracts
/// Infrastructure  → Application, Domain, Contracts
/// Api             → Application, Contracts, Infrastructure (composition only)
/// Worker          → Application, Contracts, Infrastructure (composition only)
/// </code>
/// The graph is permissive ("may reference"), so a project may use a SUBSET of
/// its allowed set — Infrastructure currently reaches Contracts transitively
/// via Application and stays out of the pull loop. What it must never do is
/// reference anything outside that set, or anything that would create a
/// reference cycle. This is a project-graph check only; rule 1 (no EF /
/// AspNetCore / logging packages in Domain or Contracts) is enforced by the
/// package manifests and the build itself, and type-leak rules follow in the
/// service-contract tests.
/// </remarks>
public sealed class LayerDependencyGraphTests
{
    private static readonly Dictionary<string, string[]> AllowedReferences = new()
    {
        ["Stockpile.Domain"] = [],
        ["Stockpile.Contracts"] = [],
        ["Stockpile.Application"] = ["Stockpile.Domain", "Stockpile.Contracts"],
        ["Stockpile.Infrastructure"] = ["Stockpile.Application", "Stockpile.Domain", "Stockpile.Contracts"],
        ["Stockpile.Api"] = ["Stockpile.Application", "Stockpile.Contracts", "Stockpile.Infrastructure"],
        ["Stockpile.Worker"] = ["Stockpile.Application", "Stockpile.Contracts", "Stockpile.Infrastructure"],
    };

    private static readonly Regex ProjectReference = new(
        @"<ProjectReference\s+Include=[""'](?:\.\.[\\/])?([^\\/""']+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Fact]
    public void Every_src_project_reference_stays_inside_the_section_3_1_graph()
    {
        string srcRoot = Path.Combine(FindRepositoryRoot("Stockpile.slnx"), "src");
        var failures = new List<string>();
        var discovered = new HashSet<string>(StringComparer.Ordinal);

        foreach (string csproj in Directory.EnumerateFiles(srcRoot, "*.csproj", SearchOption.AllDirectories))
        {
            string project = Path.GetFileNameWithoutExtension(csproj);
            discovered.Add(project);

            if (!AllowedReferences.TryGetValue(project, out string[]? allowed))
            {
                failures.Add($"{project}: project not listed in the §3.1 manifest");
                continue;
            }

            string text = File.ReadAllText(csproj);
            foreach (Match match in ProjectReference.Matches(text))
            {
                string referenced = match.Groups[1].Value;
                if (!allowed.Contains(referenced, StringComparer.Ordinal))
                {
                    failures.Add($"{project} → {referenced} (forbidden by §3.1)");
                }
            }
        }

        // Guard against a vacuous pass: every manifest project must have been
        // scanned, and scanning must not have missed any new src project.
        foreach (string expected in AllowedReferences.Keys)
        {
            if (!discovered.Contains(expected))
            {
                failures.Add($"manifest project not found for scanning: {expected}");
            }
        }

        Assert.True(
            failures.Count == 0,
            "Layer graph (§3.1) violated:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
        Assert.Equal(AllowedReferences.Count, discovered.Count);
    }

    private static string FindRepositoryRoot(string marker)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, marker)))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException($"Could not locate repository root marker '{marker}'.");
    }
}

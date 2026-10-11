namespace Stockpile.ArchitectureTests;

/// <summary>
/// Task 1.29, security-test-matrix X9: <c>AuditLog</c> and <c>SecurityEvent</c>
/// are append-only accountability records (domain-model §10). Nothing in
/// production code may update, delete, or bulk-modify their rows; the only
/// write path is the <c>Add</c> performed by the audit services. This test
/// scans the whole <c>backend/src</c> tree (Api, Worker, Application,
/// Infrastructure, Domain, Contracts) for mutation calls against the two
/// tables, so a future "clean-up" feature cannot quietly land.
/// </summary>
public sealed class AuditAppendOnlyTests
{
    [Fact]
    public void No_update_or_delete_path_exists_for_audit_tables_in_src()
    {
        string srcRoot = Path.Combine(FindRepositoryRoot("Stockpile.slnx"), "src");
        string[] tables = ["AuditLogs", "SecurityEvents"];
        string[] mutations =
        [
            ".Remove(", ".RemoveRange(", ".Update(", ".UpdateRange(",
            "ExecuteDelete(", "ExecuteUpdate(", // EF7+ bulk ops
            ".FromSqlRaw(", ".FromSqlInterpolated(", // raw SQL could bypass the guard
        ];

        var offenders = new List<string>();
        foreach (string file in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (IsBuildArtifact(file))
            {
                continue;
            }

            string text = File.ReadAllText(file);
            foreach (string table in tables)
            {
                foreach (string mutation in mutations)
                {
                    string pattern = table + mutation;
                    if (text.Contains(pattern, StringComparison.Ordinal))
                    {
                        offenders.Add($"{Path.GetRelativePath(srcRoot, file)}: {pattern}");
                    }
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Append-only contract (X9) violated in src:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    private static bool IsBuildArtifact(string file) =>
        file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

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

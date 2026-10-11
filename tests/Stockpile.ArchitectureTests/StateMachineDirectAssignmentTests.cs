using System.Text.RegularExpressions;

namespace Stockpile.ArchitectureTests;

/// <summary>
/// Task 1.30, security-test-matrix X6 / S10 — domain-model §11.11: every state
/// transition must pass through <see cref="Stockpile.Domain.StateMachines.StateMachine{TState}.Evaluate"/>;
/// a DIRECT assignment of a workflow-state enum to a status property outside
/// the machine is a §7 bypass. This test greps the whole <c>backend/src</c> tree
/// for such assignments; a future handler that writes <c>rfq.Status = RFQStatus.X</c>
/// without the evaluator fails the build (and is forced through the transition
/// helper instead).
/// </summary>
public sealed class StateMachineDirectAssignmentTests
{
    // Matches "Status = <WorkflowEnum>." in either `.Status = ` or
    // `Status = ` form. Property declarations (`Status { get; set; }`) and
    // non-workflow statuses (OutboxMessageStatus) deliberately do not match.
    private static readonly Regex WorkflowStatusAssignment = new(
        @"(?:\.|)Status\s*=\s*(RFQStatus|QuoteStatus|PurchaseOrderStatus|SupplierStatus|ProductStatus|SubscriptionStatus|RFQMatchStatus)\.",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Fact]
    public void No_direct_workflow_status_assignment_outside_the_state_machine()
    {
        string srcRoot = Path.Combine(FindRepositoryRoot("Stockpile.slnx"), "src");
        var offenders = new List<string>();

        foreach (string file in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (IsBuildArtifact(file))
            {
                continue;
            }

            string text = File.ReadAllText(file);
            foreach (Match match in WorkflowStatusAssignment.Matches(text))
            {
                offenders.Add($"{Path.GetRelativePath(srcRoot, file)}: {match.Value.Trim()}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Direct workflow-status assignment (X6/S10) in src:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
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

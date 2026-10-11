using System.Collections;
using System.Reflection;
using Stockpile.Contracts.Permissions;
using Stockpile.Domain.StateMachines;

namespace Stockpile.ArchitectureTests;

/// <summary>
/// Task 1.30 — the transition-table ↔ permission-catalogue contract
/// (domain-model §11 / §2.2; security-test-matrix S10-adjacent): every
/// capability code referenced by a declared edge must exist in
/// <see cref="Permissions.All"/>. A drifted literal would silently rename a
/// transition's permission, so the build fails instead.
/// </summary>
public sealed class TransitionPermissionTests
{
    [Fact]
    public void Every_transition_permission_code_exists_in_the_permission_catalogue()
    {
        HashSet<string> known = Permissions.All.Select(permission => permission.Code).ToHashSet();
        List<string> codes = TransitionEngine()
            .Select(edge => edge.GetType().GetProperty("PermissionCode")?.GetValue(edge) as string)
            .Where(code => code is not null)
            .Cast<string>()
            .Distinct()
            .OrderBy(code => code)
            .ToList();

        Assert.NotEmpty(codes); // the tables really are being read

        string[] missing = codes.Where(code => !known.Contains(code)).ToArray();
        Assert.True(
            missing.Length == 0,
            "Transition edges reference permission codes absent from the catalogue (Stockpile.Contracts.Permissions):"
            + Environment.NewLine + string.Join(Environment.NewLine, missing));
    }

    private static object[] TransitionEngine()
    {
        // Workflow machines are nested classes on TransitionTables, each exposing a
        // static IReadOnlyList<StateTransitionDef<T>> field named 'All'.
        FieldInfo[] allFields = typeof(TransitionTables)
            .GetNestedTypes(BindingFlags.Public)
            .Select(type => type.GetField("All", BindingFlags.Public | BindingFlags.Static))
            .OfType<FieldInfo>()
            .ToArray();

        Assert.NotEmpty(allFields);

        return allFields
            .Select(field => field.GetValue(null))
            .OfType<IEnumerable>()
            .SelectMany(rows => rows.Cast<object>())
            .ToArray();
    }
}

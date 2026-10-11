using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Stockpile.Contracts.Permissions;

namespace Stockpile.ArchitectureTests;

/// <summary>
/// Task 1.41/1.42 (security-test-matrix F10 + X2, threat-model T6.1,
/// architecture §8.2): deny by default is an ENFORCEMENT, not a convention.
/// Every shipped action carries an explicit decision — a permission-constant
/// policy or a reviewed <c>[AllowAnonymous]</c> — every named policy is a
/// <c>Contracts.Permissions</c> code the <c>PermissionPolicyProvider</c>
/// knows, and no action gates on a role name (policies are built from
/// permission constants, never roles). At runtime the fallback policy would
/// answer 401 for anything undeclared and an unknown policy name fails
/// closed; these tests stop such an endpoint from shipping in the first
/// place — F10's "build fails" gate.
/// </summary>
public sealed class AuthorizationDeclarationsTests
{
    [Fact]
    public void Every_action_declares_an_authorization_decision()
    {
        var offenders = ControllerActions()
            .Where(action => !DeclaresAuthorization(action))
            .Select(action => action.DeclaringType!.FullName + "." + action.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Actions with no explicit authorization decision (X2 deny-by-default — add a permission"
            + " [Authorize(Policy = ...)] or a reviewed [AllowAnonymous]):"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void Every_named_policy_is_a_known_permission_code()
    {
        HashSet<string> known = Permissions.All
            .Select(definition => definition.Code)
            .ToHashSet(StringComparer.Ordinal);

        var unknown = ControllerActions()
            .SelectMany(PolicyDeclarationsOf)
            .Where(attribute => attribute.Policy is not null)
            .Select(attribute => attribute.Policy!)
            .Distinct(StringComparer.Ordinal)
            .Where(policy => !known.Contains(policy))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            unknown.Count == 0,
            "Policies the PermissionPolicyProvider would resolve to deny (F10 — name a Contracts"
            + " permission code): " + string.Join(", ", unknown));
    }

    [Fact]
    public void Authorization_is_never_declared_on_role_names()
    {
        var offenders = ControllerActions()
            .SelectMany(action => PolicyDeclarationsOf(action)
                .Where(attribute => !string.IsNullOrWhiteSpace(attribute.Roles))
                .Select(attribute => action.DeclaringType!.FullName + "." + action.Name + " -> " + attribute.Roles))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Role-name gates (architecture §8.2: policies from permission constants, not role names):"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>Every action of every shipped controller in the API assembly.</summary>
    private static IEnumerable<MethodInfo> ControllerActions() =>
        typeof(Program).Assembly.GetTypes()
            .Where(type => type.IsClass && !type.IsAbstract && typeof(ControllerBase).IsAssignableFrom(type))
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly));

    /// <summary>
    /// True when the action or its controller declares an authorization
    /// decision: any <c>[Authorize]</c> (bare, policy, or roles) or an
    /// explicit <c>[AllowAnonymous]</c> — the two shapes the deny-by-default
    /// floor (fallback policy) recognizes as deliberate.
    /// </summary>
    private static bool DeclaresAuthorization(MethodInfo action) =>
        action.GetCustomAttributes<AuthorizeAttribute>(inherit: true).Any()
        || action.DeclaringType!.GetCustomAttributes<AuthorizeAttribute>(true).Any()
        || action.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true).Any()
        || action.DeclaringType!.GetCustomAttributes<AllowAnonymousAttribute>(true).Any();

    /// <summary>Every <c>[Authorize]</c> on the action or its controller (own attributes first).</summary>
    private static IEnumerable<AuthorizeAttribute> PolicyDeclarationsOf(MethodInfo action) =>
        action.GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .Concat(action.DeclaringType!.GetCustomAttributes<AuthorizeAttribute>(true));
}

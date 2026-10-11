using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.RateLimiting;
using Stockpile.Api.RateLimiting;

namespace Stockpile.ArchitectureTests;

/// <summary>
/// Task 1.26 (security-test-matrix X13): every state-changing endpoint must
/// declare a rate-limit policy. Controllers place
/// <c>[EnableRateLimiting(...)]</c> — or the explicit, reviewed opt-out
/// <c>[DisableRateLimiting]</c> — on each mutation action or its controller,
/// and the named policy must exist in <see cref="RateLimitPolicies"/> so a typo
/// cannot silently skip throttling. Phases land controllers progressively; the
/// rule is locked in from the first controller so new mutations cannot forget.
/// </summary>
public sealed class RateLimitedMutationsTests
{
    private static readonly string[] MutationVerbs =
        [HttpMethods.Post, HttpMethods.Put, HttpMethods.Patch, HttpMethods.Delete];

    [Fact]
    public void Every_mutation_action_declares_a_rate_limit_policy()
    {
        var offenders = ControllerMutations()
            .Where(action => RateLimitDeclarationOf(action) is null)
            .Select(action => action.DeclaringType!.FullName + "." + action.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Mutation actions without a rate-limit declaration (X13):"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void Declared_policies_exist_in_the_registry()
    {
        var unknown = ControllerMutations()
            .Select(RateLimitDeclarationOf)
            .Where(declaration => declaration is not null && declaration != OptOutMarker)
            .Select(declaration => declaration!)
            .Distinct(StringComparer.Ordinal)
            .Where(policy => !RateLimitPolicies.All.Contains(policy, StringComparer.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            unknown.Count == 0,
            "Endpoints name rate-limit policies missing from RateLimitPolicies: "
            + string.Join(", ", unknown));
    }

    /// <summary>The X13 reviewed opt-out marker returned by <see cref="RateLimitDeclarationOf"/>.</summary>
    private const string OptOutMarker = "[DisableRateLimiting]";

    /// <summary>All mutation actions in the shipped API assembly.</summary>
    private static IEnumerable<MethodInfo> ControllerMutations() =>
        typeof(Program).Assembly.GetTypes()
            .Where(type => type.IsClass && !type.IsAbstract && typeof(ControllerBase).IsAssignableFrom(type))
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            .Where(IsMutation);

    private static bool IsMutation(MethodInfo method) =>
        method.GetCustomAttributes(inherit: true)
            .OfType<HttpMethodAttribute>()
            .SelectMany(attribute => attribute.HttpMethods)
            .Any(verb => MutationVerbs.Contains(verb, StringComparer.Ordinal));

    /// <summary>
    /// Policy name when the action is throttled (own or controller-level
    /// <c>[EnableRateLimiting]</c>), a marker when explicitly opted out, and
    /// null when entirely undeclared.
    /// </summary>
    private static string? RateLimitDeclarationOf(MethodInfo action)
    {
        var declaringType = action.DeclaringType!;

        if (action.GetCustomAttributes<DisableRateLimitingAttribute>(inherit: true).Any()
            || declaringType.GetCustomAttributes<DisableRateLimitingAttribute>(true).Any())
        {
            return OptOutMarker;
        }

        return action.GetCustomAttributes<EnableRateLimitingAttribute>(inherit: true)
            .Concat(declaringType.GetCustomAttributes<EnableRateLimitingAttribute>(true))
            .FirstOrDefault(attribute => !string.IsNullOrWhiteSpace(attribute.PolicyName))
            ?.PolicyName;
    }
}

using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Stockpile.Api.Idempotency;

namespace Stockpile.ArchitectureTests;

/// <summary>
/// Task 1.27 (security-test-matrix X14): every endpoint on the §14 critical-
/// write list (spec §14, api.md §1.7 — RFQ submit, quote submit, quote revise,
/// quote accept, order create, payment callbacks, webhooks) must declare
/// <c>[Idempotent]</c> so the <see cref="IdempotencyFilter"/> guards it.
/// Phases land controllers progressively; this rule locks in the list from the
/// first critical action so a new critical write cannot silently skip the
/// header requirement.
/// </summary>
public sealed class IdempotencyDeclarationsTests
{
    [Fact]
    public void Every_critical_write_action_declares_idempotency()
    {
        var offenders = CriticalWriteActions()
            .Where(action => !DeclaresIdempotency(action))
            .Select(action => action.DeclaringType!.FullName + "." + action.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Critical-write actions without [Idempotent] (X14):"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>All controller actions whose route matches the §14 critical-write list.</summary>
    private static IEnumerable<MethodInfo> CriticalWriteActions() =>
        typeof(Program).Assembly.GetTypes()
            .Where(type => type.IsClass && !type.IsAbstract && typeof(ControllerBase).IsAssignableFrom(type))
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            .Where(IsCriticalWrite);

    private static bool IsCriticalWrite(MethodInfo method)
    {
        var controllerPrefix = method.DeclaringType!
            .GetCustomAttributes<RouteAttribute>(true)
            .Select(route => route.Template ?? string.Empty)
            .FirstOrDefault() ?? string.Empty;

        foreach (var attribute in method.GetCustomAttributes<HttpMethodAttribute>(inherit: true))
        {
            var template = (controllerPrefix + "/" + attribute.Template)
                .Trim('/')
                .ToLowerInvariant();

            var verb = attribute.HttpMethods.FirstOrDefault() ?? string.Empty;
            if (IsCriticalRoute(verb, template))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCriticalRoute(string verb, string template) =>
        verb == "post" && (
            template.Contains("webhooks", StringComparison.Ordinal)
            || template.StartsWith("quotes", StringComparison.Ordinal)
            || template.StartsWith("orders", StringComparison.Ordinal)
            || (template.StartsWith("rfqs/", StringComparison.Ordinal)
                && template.EndsWith("/submit", StringComparison.Ordinal))
            || (template.StartsWith("subscriptions/", StringComparison.Ordinal)
                && template.EndsWith("/payments", StringComparison.Ordinal)));

    private static bool DeclaresIdempotency(MethodInfo action) =>
        action.GetCustomAttributes<IdempotentAttribute>(inherit: true).Any()
        || action.DeclaringType!.GetCustomAttributes<IdempotentAttribute>(true).Any();
}

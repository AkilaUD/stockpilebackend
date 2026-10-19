using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Stockpile.Api.Filters;
using Stockpile.Api.Idempotency;

namespace Stockpile.ArchitectureTests;

/// <summary>
/// A provider posts a webhook without a session or a double-submit token, so
/// <see cref="WebhookCsrfExemptAttribute"/> switches the antiforgery check off for
/// the action. That is only safe where the request is authenticated by something
/// else — the signed body — so the exemption is confined to POST routes under
/// <c>webhooks/</c> that are anonymous, signature-verified and <c>[Idempotent]</c>.
/// Anywhere else the attribute would silently remove CSRF protection from a
/// cookie-authenticated endpoint.
/// </summary>
public sealed class WebhookCsrfExemptionTests
{
    [Fact]
    public void The_exemption_is_used_somewhere_so_this_rule_is_not_vacuous()
    {
        Assert.NotEmpty(ExemptActions());
    }

    [Fact]
    public void The_exemption_is_confined_to_anonymous_idempotent_webhook_posts()
    {
        List<string> offenders = [];
        foreach (MethodInfo action in ExemptActions())
        {
            string name = action.DeclaringType!.FullName + "." + action.Name;
            string controllerPrefix = action.DeclaringType.GetCustomAttributes<RouteAttribute>(true)
                .Select(route => route.Template)
                .FirstOrDefault() ?? string.Empty;

            HttpMethodAttribute[] verbs = [.. action.GetCustomAttributes<HttpMethodAttribute>(true)];
            bool onlyPost = verbs.Length > 0 && verbs.All(verb => verb.HttpMethods.All(method => method == "POST"));
            bool webhookRoute = verbs.All(verb =>
                (controllerPrefix + "/" + verb.Template).ToLowerInvariant().Contains("/webhooks/", StringComparison.Ordinal));

            if (!onlyPost)
            {
                offenders.Add($"{name}: exempt but not POST-only");
            }

            if (!webhookRoute)
            {
                offenders.Add($"{name}: exempt but not under a webhooks/ route");
            }

            if (action.GetCustomAttribute<AllowAnonymousAttribute>() is null)
            {
                offenders.Add($"{name}: exempt but not [AllowAnonymous] (a session-bearing action must keep CSRF)");
            }

            if (action.GetCustomAttribute<IdempotentAttribute>() is null)
            {
                offenders.Add($"{name}: exempt but not [Idempotent] (webhook deliveries are retried)");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "CSRF exemption outside its safe envelope:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void The_exemption_cannot_be_applied_to_a_whole_controller()
    {
        AttributeUsageAttribute usage = typeof(WebhookCsrfExemptAttribute).GetCustomAttribute<AttributeUsageAttribute>()!;

        Assert.Equal(AttributeTargets.Method, usage.ValidOn);
    }

    private static IEnumerable<MethodInfo> ExemptActions() =>
        typeof(Program).Assembly.GetTypes()
            .Where(type => type.IsClass && !type.IsAbstract && typeof(ControllerBase).IsAssignableFrom(type))
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            .Where(method => method.GetCustomAttribute<WebhookCsrfExemptAttribute>() is not null);
}

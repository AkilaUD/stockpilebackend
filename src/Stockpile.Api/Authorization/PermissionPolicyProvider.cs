using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Stockpile.Contracts.Permissions;

namespace Stockpile.Api.Authorization;

/// <summary>
/// Task 1.42 — the policy provider of architecture §8.2 layer 2: policies are
/// built from permission CONSTANTS, never role names. Three resolutions, in
/// order (threat-model T6.1 "default is deny"):
/// <list type="number">
/// <item>a policy registered explicitly in <see cref="AuthorizationOptions"/>
/// (today the single <c>mfa.required</c> MFA hook, task 1.44) answers as-is;</item>
/// <item>a name equal to a <see cref="Permissions"/> code becomes
/// <c>RequireAuthenticatedUser + PermissionRequirement(code)</c> — the exact
/// shape every permission-gated action declares;</item>
/// <item>anything else resolves to an unsatisfiable
/// <see cref="DenyRequirement"/> policy, so a typo'd policy name can never
/// authorize anyone (the F10 architecture test fails the build first).</item>
/// </list>
/// The default ([Authorize] with no name) and fallback (endpoints declaring no
/// metadata at all — which the F10 test forbids shipping) policies delegate to
/// the framework provider over the same options: both are
/// "must be signed in", the deny-by-default floor.
/// </summary>
public sealed class PermissionPolicyProvider : IAuthorizationPolicyProvider
{
    private static readonly HashSet<string> PermissionCodes =
        Permissions.All.Select(definition => definition.Code).ToHashSet(StringComparer.Ordinal);

    private readonly AuthorizationOptions _options;
    private readonly DefaultAuthorizationPolicyProvider _defaults;

    /// <summary>Reads the registered policies and default/fallback policies from the same options the pipeline uses.</summary>
    public PermissionPolicyProvider(IOptions<AuthorizationOptions> options)
    {
        _options = options.Value;
        _defaults = new DefaultAuthorizationPolicyProvider(options);
    }

    /// <inheritdoc />
    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (_options.GetPolicy(policyName) is { } registered)
        {
            return Task.FromResult<AuthorizationPolicy?>(registered);
        }

        if (PermissionCodes.Contains(policyName))
        {
            return Task.FromResult<AuthorizationPolicy?>(
                new AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser()
                    .AddRequirements(new PermissionRequirement(policyName))
                    .Build());
        }

        // Unknown name = deny (T6.1). The F10 architecture test asserts every
        // declared policy is a known permission code, so this branch is the
        // runtime backstop, not a reachable configuration.
        return Task.FromResult<AuthorizationPolicy?>(
            new AuthorizationPolicyBuilder()
                .AddRequirements(DenyRequirement.Instance)
                .Build());
    }

    /// <inheritdoc />
    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _defaults.GetDefaultPolicyAsync();

    /// <inheritdoc />
    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _defaults.GetFallbackPolicyAsync();
}

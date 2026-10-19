using Stockpile.Domain.Enums;

namespace Stockpile.Application.Organizations;

/// <summary>Shared organisation-scope reads for use cases that confirm buyer-side ownership.</summary>
public static class OrganizationStoreExtensions
{
    /// <summary>
    /// The organisations the user belongs to with an ACCEPTED invitation — the
    /// only memberships that confer object access (a pending invite grants none).
    /// </summary>
    public static async Task<HashSet<Guid>> GetAcceptedOrganizationIdsAsync(
        this IOrganizationStore store, Guid userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);

        return (await store.GetMembershipsAsync(userId, cancellationToken))
            .Where(membership => membership.Status == InvitationStatus.Accepted)
            .Select(membership => membership.OrganizationId)
            .ToHashSet();
    }
}

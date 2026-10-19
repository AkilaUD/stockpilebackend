using Stockpile.Domain.Enums;

namespace Stockpile.Application.Procurement;

/// <summary>
/// The exact wire names the quote contract accepts (api.md §2.7, domain-model
/// §6.2). One map shared by the request validator and the use case, so a value the
/// filter accepts can never be rejected — or silently numeric-parsed — by the
/// service, and the two lists cannot drift apart.
/// </summary>
internal static class QuoteWireNames
{
    /// <summary>Coverage the supplier declares: <c>Full | Partial</c>.</summary>
    internal static readonly IReadOnlyDictionary<string, QuoteCoverage> Coverage =
        new Dictionary<string, QuoteCoverage>(StringComparer.Ordinal)
        {
            [nameof(QuoteCoverage.Full)] = QuoteCoverage.Full,
            [nameof(QuoteCoverage.Partial)] = QuoteCoverage.Partial,
        };

    /// <summary>How the quoted prices treat tax: <c>TaxIncluded | TaxExcluded | TaxExempt</c>.</summary>
    internal static readonly IReadOnlyDictionary<string, TaxState> TaxStates =
        new Dictionary<string, TaxState>(StringComparer.Ordinal)
        {
            [nameof(TaxState.TaxIncluded)] = TaxState.TaxIncluded,
            [nameof(TaxState.TaxExcluded)] = TaxState.TaxExcluded,
            [nameof(TaxState.TaxExempt)] = TaxState.TaxExempt,
        };

    /// <summary>The asks a buyer can attach to a revision request (business-flows §8.1).</summary>
    internal static readonly IReadOnlyDictionary<string, NegotiationKind> NegotiationKinds =
        new Dictionary<string, NegotiationKind>(StringComparer.Ordinal)
        {
            [nameof(NegotiationKind.PriceRequest)] = NegotiationKind.PriceRequest,
            [nameof(NegotiationKind.LeadTimeRequest)] = NegotiationKind.LeadTimeRequest,
            [nameof(NegotiationKind.QuantityClarification)] = NegotiationKind.QuantityClarification,
            [nameof(NegotiationKind.AlternativeRequest)] = NegotiationKind.AlternativeRequest,
            [nameof(NegotiationKind.DeliveryTermsRequest)] = NegotiationKind.DeliveryTermsRequest,
        };

    /// <summary>
    /// What a supplier may label a revision as (business-flows §8.2). <c>Initial</c>
    /// is deliberately absent: only the first revision is ever initial.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, QuoteRevisionKind> RevisionKinds =
        new Dictionary<string, QuoteRevisionKind>(StringComparer.Ordinal)
        {
            [nameof(QuoteRevisionKind.ResponseToRequest)] = QuoteRevisionKind.ResponseToRequest,
            [nameof(QuoteRevisionKind.PriceRevision)] = QuoteRevisionKind.PriceRevision,
            [nameof(QuoteRevisionKind.LeadTimeRevision)] = QuoteRevisionKind.LeadTimeRevision,
            [nameof(QuoteRevisionKind.ScopeRevision)] = QuoteRevisionKind.ScopeRevision,
        };

    internal static bool TryParseCoverage(string? value, out QuoteCoverage coverage)
    {
        if (value is not null && Coverage.TryGetValue(value, out coverage))
        {
            return true;
        }

        coverage = default;
        return false;
    }

    internal static bool TryParseTaxState(string? value, out TaxState taxState)
    {
        if (value is not null && TaxStates.TryGetValue(value, out taxState))
        {
            return true;
        }

        taxState = default;
        return false;
    }
}

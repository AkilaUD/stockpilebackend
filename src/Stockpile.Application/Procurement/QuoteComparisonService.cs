using System.Globalization;
using Stockpile.Application.Common.Errors;
using Stockpile.Application.Organizations;
using Stockpile.Contracts.Errors;
using Stockpile.Contracts.Procurement;
using Stockpile.Domain.Entities.Procurement;
using Stockpile.Domain.Enums;

namespace Stockpile.Application.Procurement;

/// <summary>
/// The buyer's comparison workspace for one RFQ (business-flows §9, api.md §2.7
/// <c>GET /rfqs/{id}/comparison</c>). This slice is deliberately minimal and
/// factual: it lays the live quotes side by side and surfaces neutral facts —
/// a coverage gap carries the same weight as a price, and nothing ranks or
/// crowns a quote (§3.11: no "best value", cheapest is never implied to be best).
/// <para>
/// With fewer than two live quotes there is nothing to compare, so the response is
/// honestly non-comparable and produces no cross-quote annotation. Sorting,
/// selected-supplier filters, per-item pivots and export are later slices.
/// </para>
/// </summary>
public sealed class QuoteComparisonService(IQuotesStore quotes, IOrganizationStore organizations)
{
    /// <summary>Comparing needs at least two competing offers.</summary>
    private const int MinimumQuotesToCompare = 2;

    /// <summary>
    /// The quote statuses that still compete. Withdrawn, rejected and expired
    /// quotes are history, not options — comparing them would mislead.
    /// </summary>
    private static bool IsLive(QuoteStatus status)
        => status is QuoteStatus.Submitted
            or QuoteStatus.UnderReview
            or QuoteStatus.RevisionRequested
            or QuoteStatus.Revised
            or QuoteStatus.Accepted;

    public async Task<RfqComparisonResponse> GetAsync(Guid userId, Guid rfqId, CancellationToken cancellationToken)
    {
        HashSet<Guid> organizationIds = await organizations.GetAcceptedOrganizationIdsAsync(userId, cancellationToken);
        RfqQuotes? rfqQuotes = organizationIds.Count == 0
            ? null
            : await quotes.LoadRfqQuotesForBuyerAsync(organizationIds, rfqId, cancellationToken);

        // api.md §1.6 BOLA: another tenant's RFQ and a missing one are one 404.
        if (rfqQuotes is null)
        {
            throw new AppException(ErrorCodes.ResourceNotFound);
        }

        DateTime now = DateTime.UtcNow;
        Rfq rfq = rfqQuotes.Rfq;
        Dictionary<Guid, int> lineNumbers = rfqQuotes.Lines.ToDictionary(line => line.Item.PublicId, line => line.Item.LineNumber);

        List<ComparisonQuote> columns = rfqQuotes.Quotes
            .Where(detail => IsLive(QuoteMapper.EffectiveStatus(detail.Quote, now)))
            .Select(detail => ToColumn(detail, rfqQuotes.Lines, lineNumbers, now))
            .ToList();

        bool comparable = columns.Count >= MinimumQuotesToCompare;

        return new RfqComparisonResponse(
            rfq.PublicId.ToString(),
            rfq.ReferenceCode,
            columns.Count,
            comparable,
            rfqQuotes.Lines
                .OrderBy(line => line.Item.LineNumber)
                .Select(line => new ComparisonRequestedLine(
                    line.Item.PublicId.ToString(), line.Item.LineNumber, line.Item.Description, line.Item.Quantity))
                .ToList(),
            columns,
            comparable ? Annotate(columns) : []);
    }

    private static ComparisonQuote ToColumn(
        QuoteDetail detail,
        IReadOnlyList<QuoteLineContext> rfqLines,
        IReadOnlyDictionary<Guid, int> lineNumbers,
        DateTime now)
    {
        QuoteRevisionDetail current = detail.Current;
        HashSet<Guid> quoted = current.Items
            .Where(item => item.RfqItemId is not null)
            .Select(item => item.RfqItemId!.Value)
            .ToHashSet();

        return new ComparisonQuote(
            detail.Quote.PublicId.ToString(),
            detail.Quote.QuoteNumber,
            detail.Quote.SupplierId.ToString(),
            detail.SupplierName,
            QuoteMapper.EffectiveStatus(detail.Quote, now).ToString(),
            detail.Quote.CurrentRevisionNumber,
            QuoteMapper.Totals(current.Revision),
            current.Revision.LeadTimeDays,
            current.Revision.ValidUntil,
            current.Revision.PaymentTerms,
            current.Revision.DeliveryTerms,
            new QuoteCoverageResponse(
                rfqLines.Count,
                quoted.Count,
                [.. rfqLines
                    .Where(line => !quoted.Contains(line.Item.PublicId))
                    .Select(line => line.Item.LineNumber)
                    .Order()]),
            current.Items.Select(item => QuoteMapper.ToItem(item, lineNumbers)).ToList());
    }

    /// <summary>
    /// Neutral facts only. Coverage gaps come first and are listed for every quote
    /// that has one — they must never be less prominent than price (§3.11).
    /// </summary>
    private static List<ComparisonAnnotation> Annotate(IReadOnlyList<ComparisonQuote> columns)
    {
        List<ComparisonAnnotation> annotations = [];

        foreach (ComparisonQuote column in columns.Where(column => column.Coverage.MissingLineNumbers.Count > 0))
        {
            annotations.Add(new ComparisonAnnotation(
                "coverage_gap",
                column.QuoteId,
                $"Does not price line {string.Join(", ", column.Coverage.MissingLineNumbers.Select(line => line.ToString(CultureInfo.InvariantCulture)))}."));
        }

        foreach (ComparisonQuote column in columns)
        {
            List<int> substituted = column.Items
                .Where(item => item.SubstitutionNote is not null && item.LineNumber is not null)
                .Select(item => item.LineNumber!.Value)
                .Order()
                .ToList();
            if (substituted.Count > 0)
            {
                annotations.Add(new ComparisonAnnotation(
                    "substitution_offered",
                    column.QuoteId,
                    $"Offers a substitute on line {string.Join(", ", substituted.Select(line => line.ToString(CultureInfo.InvariantCulture)))}."));
            }
        }

        List<ComparisonQuote> withLeadTime = columns.Where(column => column.LeadTimeDays is not null).ToList();
        if (withLeadTime.Count >= MinimumQuotesToCompare)
        {
            int shortest = withLeadTime.Min(column => column.LeadTimeDays!.Value);
            foreach (ComparisonQuote column in withLeadTime.Where(column => column.LeadTimeDays == shortest))
            {
                annotations.Add(new ComparisonAnnotation(
                    "shortest_stated_lead_time",
                    column.QuoteId,
                    $"Shortest stated lead time: {shortest.ToString(CultureInfo.InvariantCulture)} days."));
            }
        }

        return annotations;
    }
}

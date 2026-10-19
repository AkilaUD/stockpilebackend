using Stockpile.Application.Audit;
using Stockpile.Application.Authentication;
using Stockpile.Application.Common.Errors;
using Stockpile.Application.Suppliers;
using Stockpile.Contracts.Errors;
using Stockpile.Contracts.Procurement;
using Stockpile.Domain.Entities.Procurement;
using Stockpile.Domain.Enums;
using Stockpile.Domain.StateMachines;

namespace Stockpile.Application.Procurement;

/// <summary>
/// Use cases for the supplier's side of a quotation (api.md §2.7,
/// business-flows §7). Submitting resolves the caller's supplier first, then the
/// lead scoped to that supplier, so a foreign match id matches no row and answers
/// one 404 <c>RESOURCE_NOT_FOUND</c> — another supplier's lead is never inferable
/// (BOLA, api.md §1.6).
/// <para>
/// Every number is computed from the item lines: the subtotal from
/// <c>Quantity × UnitPrice</c>, the discount from the submitted percentage and the
/// tax from the platform's rate. No total is ever accepted from the client
/// (domain-model §6.2, §3.12 step 6).
/// </para>
/// </summary>
public sealed class QuotesService(
    IQuotesStore quotes,
    IRfqStore rfqs,
    ISupplierSelfStore suppliers,
    IUserPermissionStore permissions,
    IAuditService audit)
{
    private static readonly StateMachine<QuoteStatus> QuoteMachine = new(TransitionTables.Quote.All);
    private static readonly StateMachine<RFQMatchStatus> MatchMachine = new(TransitionTables.RfqMatch.All);

    /// <summary>
    /// The unit a quote line falls back to when its RFQ line carries none: §6.2
    /// makes <c>QuoteRevisionItem.UnitOfMeasureId</c> non-null while the RFQ
    /// contract makes a line's unit optional, so the platform's seeded default
    /// stands in rather than leaving a required column empty.
    /// </summary>
    private const string DefaultUnitOfMeasureCode = "each";

    /// <summary>Rounding for every computed money value: decimal(18,4), half away from zero, so a total always round-trips through its column unchanged.</summary>
    private const int MoneyScale = 4;

    /// <summary>
    /// The platform tax rate applied to quoted values. The reference model carries
    /// no rate (no request field, no country column, no platform setting), so the
    /// rate is zero until a configuration surface exists: a quote's tax stays
    /// <c>0.0000</c> instead of inventing a number (§6.2 leaves tax to the platform).
    /// </summary>
    private const decimal TaxRate = 0m;

    /// <summary>
    /// Creates the quote for one lead (api.md §2.7 <c>POST /quotes</c>,
    /// business-flows §7): validates what the RFQ can say about the offer, computes
    /// the totals server-side, walks the lead to <c>Quoting</c>, and commits the
    /// quote with its first revision, its lines and the lead's response in one
    /// transaction.
    /// </summary>
    public async Task<QuoteResponse> SubmitAsync(
        Guid userId, CreateQuoteRequest request, CancellationToken cancellationToken)
    {
        Guid supplierId = await suppliers.ResolveSupplierIdAsync(userId, cancellationToken)
            ?? throw NotFound();

        QuoteSubmission submission = await quotes.LoadSubmissionAsync(supplierId, request.RfqMatchId, cancellationToken)
            ?? throw NotFound();

        // §6.1 unique (RfqId, SupplierId): a supplier quotes each RFQ once, so a
        // second offer is directed to revise. Checked before the state walk so a
        // duplicate submit sees the documented conflict, not a transition error.
        if (submission.ExistingQuote is not null)
        {
            throw new AppException(ErrorCodes.QuoteExistsForRfq);
        }

        RequireRfqAnswerable(submission.Rfq);

        QuoteCoverage declaredCoverage = ParseCoverage(request.Coverage);
        TaxState taxState = ParseTaxState(request.TaxState);
        int validityDays = request.ValidityDays is int days && days > 0
            ? days
            : throw new AppException(ErrorCodes.QuoteValidityRequired); // §7: validate validityDays > 0

        IReadOnlyList<CreateQuoteItemRequest> submitted = request.Items ?? [];
        if (submitted.Count == 0)
        {
            throw new AppException(ErrorCodes.QuoteCoverageEmpty); // §7: coverage ≥ 1 line
        }

        Dictionary<Guid, QuoteLineContext> linesById = submission.Lines.ToDictionary(line => line.Item.PublicId);
        List<QuoteRevisionItem> items = await BuildItemsAsync(
            submission, submitted, linesById, cancellationToken);

        RequireDeclaredCoverage(submission.Lines, items, declaredCoverage);

        decimal subtotal = Round(items.Sum(item => item.LineTotal));
        decimal discountTotal = Round(subtotal * ((request.DiscountPercent ?? 0m) / 100m));
        decimal deliveryCost = Round(request.DeliveryCost ?? 0m);
        decimal netBeforeTax = Round(subtotal - discountTotal + deliveryCost);
        decimal taxAmount = ComputeTax(taxState, netBeforeTax);
        // TaxIncluded prices already carry the tax, so the grand total is the net
        // value and the computed amount is the portion reported inside it.
        decimal grandTotal = taxState == TaxState.TaxIncluded ? netBeforeTax : Round(netBeforeTax + taxAmount);

        DateTime now = DateTime.UtcNow;
        DateTime? validUntil = now.AddDays(validityDays);

        Quote quote = new()
        {
            RfqId = submission.Rfq.PublicId,
            RfqMatchId = submission.Match.PublicId,
            SupplierId = supplierId,
            QuoteNumber = await NextQuoteNumberAsync(cancellationToken),
            Status = QuoteStatus.Draft,
            CurrentRevisionNumber = 1,
            ValidUntil = validUntil,
            CreatedBy = userId,
            CreatedAt = now,
        };

        QuoteRevision revision = new()
        {
            QuoteId = quote.PublicId,
            RevisionNumber = 1,
            PreviousRevisionId = null,
            CreatedBy = userId,
            RevisionKind = QuoteRevisionKind.Initial,
            CurrencyCode = request.Currency.ToUpperInvariant(),
            Subtotal = subtotal,
            DiscountTotal = discountTotal,
            DeliveryCost = deliveryCost,
            TaxAmount = taxAmount,
            GrandTotal = grandTotal,
            TaxState = taxState,
            LeadTimeDays = request.LeadTimeDays,
            ValidUntil = validUntil,
            PaymentTerms = Trim(request.PaymentTerms),
            DeliveryTerms = Trim(request.DeliveryTerms),
            PackSizeNote = Trim(request.PackSizeNote),
            MoqNote = Trim(request.MoqNote),
            TechnicalNotes = Trim(request.TechnicalNotes),
            SupplierNote = Trim(request.SupplierNote),
            Status = QuoteRevisionStatus.Active,
            CreatedAt = now,
        };

        foreach (QuoteRevisionItem item in items)
        {
            item.RevisionId = revision.PublicId;
        }

        // Draft → Submitted is §11.3's declared edge under the caller's own
        // capability; the evaluator — never a literal — sets the state (X6/S10).
        ApplyQuote(quote, QuoteStatus.Submitted, userId, await GrantedAsync(userId, cancellationToken));

        // Quoting means the lead was opened and answered: walk §11.2's own chain
        // Matched → Notified → Viewed → Quoting, one evaluator call per edge.
        WalkToQuoting(submission.Match, now);

        await quotes.AddSubmissionAsync(quote, revision, items, submission.Match, cancellationToken);
        await audit.RecordAsync(
            new AuditEntry("quote.submitted", "Quote", quote.PublicId.ToString(), AuditResult.Success),
            cancellationToken);

        return ToResponse(quote, revision, submission.Lines, items);
    }

    // ---- Internals ----------------------------------------------------------

    /// <summary>
    /// A lead can only be quoted while its RFQ is open: a past deadline is the
    /// documented <c>RFQ_EXPIRED</c> (its message covers quoting explicitly) and a
    /// cancelled, closed, awarded or ordered RFQ is not in a state that accepts a
    /// submission at all.
    /// </summary>
    private static void RequireRfqAnswerable(Rfq rfq)
    {
        if (rfq.ExpiryAt is DateTime expiry && expiry <= DateTime.UtcNow)
        {
            throw new AppException(ErrorCodes.RfqExpired);
        }

        if (rfq.Status is RFQStatus.Cancelled
            or RFQStatus.Closed
            or RFQStatus.Expired
            or RFQStatus.Awarded
            or RFQStatus.PartiallyAwarded
            or RFQStatus.Ordered)
        {
            throw new AppException(ErrorCodes.RfqNotSubmittable);
        }
    }

    /// <summary>
    /// Builds the revision's lines from the RFQ's own rows — never from the
    /// request's — and snapshots what was quoted (domain-model §6.2). A line the
    /// RFQ does not have is a client mistake (422), and a substitution note on an
    /// RFQ that forbids substitutions is the documented refusal (business-flows §7).
    /// </summary>
    private async Task<List<QuoteRevisionItem>> BuildItemsAsync(
        QuoteSubmission submission,
        IReadOnlyList<CreateQuoteItemRequest> submitted,
        Dictionary<Guid, QuoteLineContext> linesById,
        CancellationToken cancellationToken)
    {
        Guid? fallbackUnitId = null;
        List<QuoteRevisionItem> items = [];

        for (int index = 0; index < submitted.Count; index++)
        {
            CreateQuoteItemRequest line = submitted[index];
            if (!linesById.TryGetValue(line.RfqItemId, out QuoteLineContext? context))
            {
                throw FieldErrorAt(
                    index, "rfqItemId", "unknown_line", "This line is not part of the RFQ.");
            }

            RfqItem rfqItem = context.Item;
            string? substitutionNote = Trim(line.SubstitutionNote);
            if (substitutionNote is not null && !submission.Rfq.SubstitutionsAllowed)
            {
                throw new AppException(
                    ErrorCodes.SubstitutionNotPermitted,
                    [
                        new FieldError(
                            $"items[{index}].substitutionNote", "substitution_not_permitted",
                            "This RFQ does not allow substitutes."),
                    ],
                    "This RFQ does not allow substitutions.");
            }

            Guid unitId = rfqItem.UnitOfMeasureId
                ?? (fallbackUnitId ??= await ResolveDefaultUnitAsync(cancellationToken));

            items.Add(new QuoteRevisionItem
            {
                RfqItemId = rfqItem.PublicId,
                ProductId = line.ProductId,
                ProductNameSnapshot = context.ProductName ?? rfqItem.Description,
                SkuSnapshot = context.ProductSku,
                Description = rfqItem.Description,
                Quantity = line.Quantity,
                UnitPrice = line.UnitPrice,
                UnitOfMeasureId = unitId,
                LineDiscountPct = null, // no per-line discount is on the create contract in this phase
                LineTotal = Round(line.Quantity * line.UnitPrice),
                SubstitutionNote = substitutionNote,
            });
        }

        return items;
    }

    private async Task<Guid> ResolveDefaultUnitAsync(CancellationToken cancellationToken)
        => await rfqs.FindUnitOfMeasureIdByCodeAsync(DefaultUnitOfMeasureCode, cancellationToken)
            ?? throw new AppException(ErrorCodes.InternalError);

    /// <summary>
    /// A declared <c>Full</c> coverage must actually price every RFQ line; a
    /// <c>Partial</c> one may omit lines, and those omissions are what the response
    /// reports (<c>missingLineNumbers</c>, api.md §2.7).
    /// </summary>
    private static void RequireDeclaredCoverage(
        IReadOnlyList<QuoteLineContext> rfqLines,
        IReadOnlyList<QuoteRevisionItem> items,
        QuoteCoverage declared)
    {
        if (declared != QuoteCoverage.Full)
        {
            return;
        }

        HashSet<Guid> quoted = QuotedLineIds(items);
        if (rfqLines.Any(line => !quoted.Contains(line.Item.PublicId)))
        {
            throw new AppException(
                ErrorCodes.ValidationFailed,
                [
                    new FieldError(
                        "coverage", "coverage_incomplete",
                        "Full coverage must price every RFQ line, or declare Partial."),
                ],
                "Full coverage must price every RFQ line.");
        }
    }

    /// <summary>
    /// Tax follows the declared state, never a client amount: <c>TaxExcluded</c>
    /// reports the rate on top of the discounted value, <c>TaxIncluded</c> reports
    /// the same rate as the portion already inside it, and <c>TaxExempt</c> adds
    /// nothing.
    /// </summary>
    private static decimal ComputeTax(TaxState taxState, decimal netBeforeTax)
        => taxState switch
        {
            TaxState.TaxExempt => 0m,
            TaxState.TaxExcluded => Round(netBeforeTax * TaxRate),
            TaxState.TaxIncluded => Round(netBeforeTax * TaxRate / (1m + TaxRate)),
            _ => 0m,
        };

    private static decimal Round(decimal value) => Math.Round(value, MoneyScale, MidpointRounding.AwayFromZero);

    private async Task<IReadOnlySet<string>> GrantedAsync(Guid userId, CancellationToken cancellationToken)
        => (await permissions.GetPermissionCodesAsync(userId, cancellationToken))
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Walks the lead along §11.2's declared chain to <c>Quoting</c> — one
    /// evaluator call per edge, so the machine authorizes every hop — and stamps
    /// the lead's first response (quoting IS a response, §5.4). A lead that already
    /// resolved (Declined, Expired, Blocked) has no declared edge back, so it stays
    /// the standard invalid-transition conflict.
    /// </summary>
    private static void WalkToQuoting(RfqMatch match, DateTime now)
    {
        while (match.Status != RFQMatchStatus.Quoting)
        {
            RFQMatchStatus next = match.Status switch
            {
                RFQMatchStatus.Matched => RFQMatchStatus.Notified,
                RFQMatchStatus.Notified => RFQMatchStatus.Viewed,
                _ => RFQMatchStatus.Quoting,
            };

            TransitionResult<RFQMatchStatus> result =
                MatchMachine.Evaluate(match.Status, next, ActorContext.System, reason: null);
            if (!result.IsAllowed)
            {
                throw new AppException(ErrorCodes.StateTransitionInvalid);
            }

            match.Status = result.Edge!.To;
        }

        match.ViewedAt ??= now;
        match.RespondedAt ??= now;
    }

    private static void ApplyQuote(
        Quote quote, QuoteStatus to, Guid userId, IReadOnlySet<string> granted)
    {
        TransitionResult<QuoteStatus> result = QuoteMachine.Evaluate(
            quote.Status, to, ActorContext.ForUser(userId, granted), reason: null);
        if (!result.IsAllowed)
        {
            throw result.Kind == TransitionResultKind.PermissionDenied
                ? new AppException(ErrorCodes.InsufficientCapability)
                : new AppException(ErrorCodes.StateTransitionInvalid);
        }

        quote.Status = result.Edge!.To; // the evaluator's matched edge — never a literal (X6/S10)
    }

    /// <summary>
    /// The quote's display reference (§6.1): the platform's reference codes are
    /// opaque and unique — a year prefix plus a random suffix checked against the
    /// unique index, never a sequence that would leak platform volume.
    /// </summary>
    private async Task<string> NextQuoteNumberAsync(CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            string code = $"QT-{DateTime.UtcNow.Year}-{Guid.NewGuid():N}"[..17].ToUpperInvariant();
            if (!await quotes.QuoteNumberExistsAsync(code, cancellationToken))
            {
                return code;
            }
        }

        throw new AppException(ErrorCodes.InternalError);
    }

    private static QuoteResponse ToResponse(
        Quote quote,
        QuoteRevision revision,
        IReadOnlyList<QuoteLineContext> rfqLines,
        IReadOnlyList<QuoteRevisionItem> items)
    {
        HashSet<Guid> quoted = QuotedLineIds(items);

        return new QuoteResponse(
            quote.PublicId.ToString(),
            quote.QuoteNumber,
            quote.Status.ToString(),
            revision.RevisionNumber,
            new QuoteTotalsResponse(
                revision.Subtotal,
                revision.DiscountTotal,
                revision.DeliveryCost,
                revision.TaxAmount,
                revision.GrandTotal,
                revision.CurrencyCode),
            new QuoteCoverageResponse(
                rfqLines.Count,
                quoted.Count,
                [.. rfqLines
                    .Where(line => !quoted.Contains(line.Item.PublicId))
                    .Select(line => line.Item.LineNumber)
                    .Order()]));
    }

    private static HashSet<Guid> QuotedLineIds(IReadOnlyList<QuoteRevisionItem> items)
        => items
            .Where(item => item.RfqItemId is not null)
            .Select(item => item.RfqItemId!.Value)
            .ToHashSet();

    private static QuoteCoverage ParseCoverage(string? value)
        => QuoteWireNames.TryParseCoverage(value, out QuoteCoverage coverage)
            ? coverage
            : throw new AppException(
                ErrorCodes.ValidationFailed,
                [new FieldError("coverage", "invalid_coverage", "Choose Full or Partial coverage.")],
                "The declared coverage is not recognised.");

    private static TaxState ParseTaxState(string? value)
        => QuoteWireNames.TryParseTaxState(value, out TaxState taxState)
            ? taxState
            : throw new AppException(
                ErrorCodes.ValidationFailed,
                [
                    new FieldError(
                        "taxState", "invalid_tax_state",
                        "Choose TaxIncluded, TaxExcluded or TaxExempt."),
                ],
                "The declared tax state is not recognised.");

    /// <summary>A field-scoped 422 for one submitted line, named by its index in the request.</summary>
    private static AppException FieldErrorAt(int index, string field, string code, string message)
        => new(
            ErrorCodes.ValidationFailed,
            [new FieldError($"items[{index}].{field}", code, message)],
            "A quoted line does not belong to this RFQ.");

    private static string? Trim(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>The one 404 of this surface: a caller with no supplier and a lead they do not own are indistinguishable (api.md §1.6).</summary>
    private static AppException NotFound() => new(ErrorCodes.ResourceNotFound);
}

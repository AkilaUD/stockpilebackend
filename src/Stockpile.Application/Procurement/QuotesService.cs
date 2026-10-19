using Stockpile.Application.Audit;
using Stockpile.Application.Authentication;
using Stockpile.Application.Common.Errors;
using Stockpile.Application.Organizations;
using Stockpile.Application.Suppliers;
using Stockpile.Contracts.Errors;
using Stockpile.Contracts.Permissions;
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
    IOrganizationStore organizations,
    IUserPermissionStore permissions,
    IAuditService audit,
    ICurrentRequestContext requestContext)
{
    /// <summary>
    /// How many revisions one quote may carry before the supplier is directed to
    /// issue a new quote (<c>QUOTE_REVISION_LIMIT</c>). A platform safeguard — the
    /// reference model states the error but no number — kept as one constant so a
    /// later settings surface can replace it.
    /// </summary>
    private const int MaxRevisionsPerQuote = 10;

    /// <summary>Cap on a quote list page: the lists are newest-first and bounded until cursors land.</summary>
    private const int ListLimit = 100;

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

        DateTime now = DateTime.UtcNow;
        BuiltOffer offer = await BuildOfferAsync(
            submission.Rfq,
            submission.Lines,
            new OfferTerms(
                request.Coverage, request.Currency, request.LeadTimeDays, request.ValidityDays,
                request.PaymentTerms, request.DeliveryTerms, request.DeliveryCost, request.TaxState,
                request.DiscountPercent, request.PackSizeNote, request.MoqNote, request.TechnicalNotes,
                request.SupplierNote, request.Items),
            now,
            cancellationToken);
        QuoteRevision revision = offer.Revision;
        List<QuoteRevisionItem> items = offer.Items;

        Quote quote = new()
        {
            RfqId = submission.Rfq.PublicId,
            RfqMatchId = submission.Match.PublicId,
            SupplierId = supplierId,
            QuoteNumber = await NextQuoteNumberAsync(cancellationToken),
            CurrentRevisionNumber = 1,
            ValidUntil = revision.ValidUntil,
            CreatedBy = userId,
            CreatedAt = now,
        };

        revision.QuoteId = quote.PublicId;
        revision.RevisionNumber = 1;
        revision.PreviousRevisionId = null;
        revision.CreatedBy = userId;
        revision.RevisionKind = QuoteRevisionKind.Initial;

        // Draft → Submitted is §11.3's declared edge under the caller's own
        // capability; the evaluator — never a literal — sets the state (X6/S10).
        ApplyQuote(
            quote, quote.Status, QuoteStatus.Submitted, userId,
            await GrantedAsync(userId, cancellationToken), reason: null);

        // Quoting means the lead was opened and answered: walk §11.2's own chain
        // Matched → Notified → Viewed → Quoting, one evaluator call per edge.
        WalkToQuoting(submission.Match, now);

        quote.Raise("quote.submitted", userId, requestContext.CorrelationId, EventPayload(quote, revision));

        await quotes.AddSubmissionAsync(quote, revision, items, submission.Match, cancellationToken);
        await audit.RecordAsync(
            new AuditEntry("quote.submitted", "Quote", quote.PublicId.ToString(), AuditResult.Success),
            cancellationToken);

        return ToResponse(quote, revision, submission.Lines, items);
    }

    /// <summary>
    /// The supplier answers a buyer's revision request (api.md §2.7
    /// <c>POST /quotes/{id}/revisions</c>, business-flows §8.2) with a NEW
    /// immutable revision: <c>N+1</c>, chained to <c>N</c>, totals recomputed
    /// server-side. No existing revision row is touched — the previous one reads as
    /// superseded because it is no longer the current one. The only edge that
    /// carries <c>quote.revise</c> is <c>RevisionRequested → Revised</c>, so a
    /// revision nobody asked for is the standard invalid-transition conflict.
    /// </summary>
    public async Task<QuoteResponse> ReviseAsync(
        Guid userId, Guid quoteId, ReviseQuoteRequest request, CancellationToken cancellationToken)
    {
        Guid supplierId = await suppliers.ResolveSupplierIdAsync(userId, cancellationToken)
            ?? throw NotFound();
        QuoteDetail detail = await quotes.LoadForSupplierAsync(supplierId, quoteId, forUpdate: true, cancellationToken)
            ?? throw NotFound();
        Quote quote = detail.Quote;

        RequireOpenQuote(quote);
        RequireRfqAnswerable(detail.Rfq);

        if (quote.CurrentRevisionNumber >= MaxRevisionsPerQuote)
        {
            throw new AppException(ErrorCodes.QuoteRevisionLimit);
        }

        IReadOnlySet<string> granted = await GrantedAsync(userId, cancellationToken);
        DateTime now = DateTime.UtcNow;
        ApplyQuote(
            quote, QuoteMapper.EffectiveStatus(quote, now), QuoteStatus.Revised, userId, granted, reason: null);

        BuiltOffer offer = await BuildOfferAsync(
            detail.Rfq,
            detail.Lines,
            new OfferTerms(
                request.Coverage, request.Currency, request.LeadTimeDays, request.ValidityDays,
                request.PaymentTerms, request.DeliveryTerms, request.DeliveryCost, request.TaxState,
                request.DiscountPercent, request.PackSizeNote, request.MoqNote, request.TechnicalNotes,
                request.SupplierNote, request.Items),
            now,
            cancellationToken);

        QuoteRevision previous = detail.Current.Revision;
        QuoteRevision revision = offer.Revision;
        revision.QuoteId = quote.PublicId;
        revision.RevisionNumber = previous.RevisionNumber + 1;
        revision.PreviousRevisionId = previous.PublicId;
        revision.CreatedBy = userId;
        revision.RevisionKind = QuoteWireNames.RevisionKinds.TryGetValue(request.RevisionKind ?? string.Empty, out QuoteRevisionKind kind)
            ? kind
            : QuoteRevisionKind.ResponseToRequest;

        // The revision answers the buyer's latest ask (business-flows §8.2): the
        // link is what lets the timeline render "in response to …".
        NegotiationMessage? ask = detail.Messages
            .Where(message => message.Direction == NegotiationDirection.BuyerToSupplier)
            .MaxBy(message => message.CreatedAt);
        revision.RequestedChangeRef = ask?.PublicId;

        NegotiationMessage? reply = revision.SupplierNote is { Length: > 0 } note
            ? new NegotiationMessage
            {
                QuoteId = quote.PublicId,
                AgainstRevisionNumber = revision.RevisionNumber,
                Direction = NegotiationDirection.SupplierToBuyer,
                Kind = NegotiationKind.General,
                Body = note,
                CreatedBy = userId,
                CreatedAt = now,
            }
            : null;

        quote.CurrentRevisionNumber = revision.RevisionNumber;
        quote.ValidUntil = revision.ValidUntil;
        Touch(quote, userId, now);
        quote.Raise("quote.revised", userId, requestContext.CorrelationId, EventPayload(quote, revision));

        await quotes.AddRevisionAsync(quote, revision, offer.Items, reply, cancellationToken);
        await audit.RecordAsync(
            new AuditEntry("quote.revised", "Quote", quote.PublicId.ToString(), AuditResult.Success),
            cancellationToken);

        return ToResponse(quote, revision, detail.Lines, offer.Items);
    }

    /// <summary>
    /// The supplier withdraws a quote that is still open (api.md §2.7,
    /// business-flows §8.4). Withdrawal is a status change only: no revision is
    /// edited or removed, and the current one reads as <c>Withdrawn</c>. A withdrawn
    /// quote cannot be revived — the (RFQ, supplier) pair is already used.
    /// </summary>
    public async Task<QuoteSummaryResponse> WithdrawAsync(
        Guid userId, Guid quoteId, CancellationToken cancellationToken)
    {
        Guid supplierId = await suppliers.ResolveSupplierIdAsync(userId, cancellationToken)
            ?? throw NotFound();
        QuoteDetail detail = await quotes.LoadForSupplierAsync(supplierId, quoteId, forUpdate: true, cancellationToken)
            ?? throw NotFound();
        Quote quote = detail.Quote;

        RequireOpenQuote(quote);

        DateTime now = DateTime.UtcNow;
        ApplyQuote(
            quote, QuoteMapper.EffectiveStatus(quote, now), QuoteStatus.Withdrawn, userId,
            await GrantedAsync(userId, cancellationToken), reason: null);
        Touch(quote, userId, now);
        quote.Raise("quote.withdrawn", userId, requestContext.CorrelationId, EventPayload(quote, detail.Current.Revision));

        await quotes.SaveAsync(cancellationToken);
        await audit.RecordAsync(
            new AuditEntry("quote.withdrawn", "Quote", quote.PublicId.ToString(), AuditResult.Success),
            cancellationToken);

        return QuoteMapper.ToSummary(detail, now);
    }

    /// <summary>
    /// The buyer declines a quote (api.md §2.7, business-flows §8.4). The reason is
    /// required and stored on the quote; the buyer's organisation must own the RFQ.
    /// </summary>
    public async Task<QuoteSummaryResponse> DeclineAsync(
        Guid userId, Guid quoteId, DeclineQuoteRequest request, CancellationToken cancellationToken)
    {
        QuoteDetail detail = await LoadBuyerQuoteForUpdateAsync(userId, quoteId, cancellationToken);
        Quote quote = detail.Quote;
        string reason = Trim(request.Reason) ?? throw new AppException(ErrorCodes.ValidationFailed);

        DateTime now = DateTime.UtcNow;
        ApplyQuote(
            quote, QuoteMapper.EffectiveStatus(quote, now), QuoteStatus.Rejected, userId,
            await GrantedAsync(userId, cancellationToken), reason: null);
        quote.DeclinedAt = now;
        quote.RejectedAt = now;
        quote.DeclineReason = reason;
        Touch(quote, userId, now);
        quote.Raise("quote.declined", userId, requestContext.CorrelationId, EventPayload(quote, detail.Current.Revision));

        await quotes.SaveAsync(cancellationToken);
        await audit.RecordAsync(
            new AuditEntry("quote.declined", "Quote", quote.PublicId.ToString(), AuditResult.Success),
            cancellationToken);

        return QuoteMapper.ToSummary(detail, now);
    }

    /// <summary>
    /// The buyer asks the supplier to revise (api.md §2.7, business-flows §8.1):
    /// the quote moves to <c>RevisionRequested</c> (a reason is required by the
    /// edge) and a typed <see cref="NegotiationMessage"/> records the ask, in one
    /// save.
    /// </summary>
    public async Task<QuoteSummaryResponse> RequestRevisionAsync(
        Guid userId, Guid quoteId, RequestQuoteRevisionRequest request, CancellationToken cancellationToken)
    {
        QuoteDetail detail = await LoadBuyerQuoteForUpdateAsync(userId, quoteId, cancellationToken);
        Quote quote = detail.Quote;
        RequireRfqAnswerable(detail.Rfq);

        string reason = Trim(request.Reason) ?? throw new AppException(ErrorCodes.ValidationFailed);
        NegotiationKind kind = QuoteWireNames.NegotiationKinds.TryGetValue(request.Kind ?? string.Empty, out NegotiationKind parsed)
            ? parsed
            : throw new AppException(
                ErrorCodes.ValidationFailed,
                [new FieldError("kind", "invalid_kind", "Choose what you are asking the supplier to change.")]);

        DateTime now = DateTime.UtcNow;
        ApplyQuote(
            quote, QuoteMapper.EffectiveStatus(quote, now), QuoteStatus.RevisionRequested, userId,
            await GrantedAsync(userId, cancellationToken), reason);

        NegotiationMessage message = new()
        {
            QuoteId = quote.PublicId,
            AgainstRevisionNumber = quote.CurrentRevisionNumber,
            Direction = NegotiationDirection.BuyerToSupplier,
            Kind = kind,
            Body = reason,
            CreatedBy = userId,
            CreatedAt = now,
        };

        Touch(quote, userId, now);
        quote.Raise("quote.revision_requested", userId, requestContext.CorrelationId, EventPayload(quote, detail.Current.Revision));

        await quotes.AddMessageAsync(quote, message, cancellationToken);
        await audit.RecordAsync(
            new AuditEntry("quote.revision_requested", "Quote", quote.PublicId.ToString(), AuditResult.Success),
            cancellationToken);

        return QuoteMapper.ToSummary(detail, now);
    }

    /// <summary>
    /// One quote with its full revision history and negotiation thread (api.md §2.7
    /// <c>GET /quotes/{id}</c>). The caller sees it as the supplier that owns it or
    /// as a member of the buyer organisation that owns the RFQ — and nothing else:
    /// any other id answers the same 404 as a missing one (BOLA, api.md §1.6).
    /// </summary>
    public async Task<QuoteDetailResponse> GetAsync(Guid userId, Guid quoteId, CancellationToken cancellationToken)
    {
        IReadOnlySet<string> granted = await GrantedAsync(userId, cancellationToken);
        bool canReadAsSupplier = granted.Contains(Permissions.QuoteReadSupplier);
        bool canReadAsBuyer = granted.Contains(Permissions.QuoteReadBuyer);
        if (!canReadAsSupplier && !canReadAsBuyer)
        {
            throw new AppException(ErrorCodes.InsufficientCapability);
        }

        DateTime now = DateTime.UtcNow;
        if (canReadAsSupplier
            && await suppliers.ResolveSupplierIdAsync(userId, cancellationToken) is Guid supplierId
            && await quotes.LoadForSupplierAsync(supplierId, quoteId, forUpdate: false, cancellationToken) is { } asSupplier)
        {
            return QuoteMapper.ToDetail(asSupplier, "supplier", now);
        }

        if (canReadAsBuyer)
        {
            HashSet<Guid> organizationIds = await organizations.GetAcceptedOrganizationIdsAsync(userId, cancellationToken);
            if (organizationIds.Count > 0
                && await quotes.LoadForBuyerAsync(organizationIds, quoteId, forUpdate: false, cancellationToken) is { } asBuyer)
            {
                return QuoteMapper.ToDetail(asBuyer, "buyer", now);
            }
        }

        throw NotFound();
    }

    /// <summary>The caller's supplier's own quotes, newest first (api.md §2.9 <c>GET /suppliers/me/quotes</c>).</summary>
    public async Task<IReadOnlyList<QuoteSummaryResponse>> ListForSupplierAsync(
        Guid userId, CancellationToken cancellationToken)
    {
        Guid supplierId = await suppliers.ResolveSupplierIdAsync(userId, cancellationToken)
            ?? throw NotFound();

        DateTime now = DateTime.UtcNow;
        return (await quotes.ListForSupplierAsync(supplierId, ListLimit, cancellationToken))
            .Select(detail => QuoteMapper.ToSummary(detail, now))
            .ToList();
    }

    /// <summary>Quotes against the caller's organisations' RFQs, newest first (api.md §2.7 <c>GET /me/quotes</c>).</summary>
    public async Task<IReadOnlyList<QuoteSummaryResponse>> ListForBuyerAsync(
        Guid userId, CancellationToken cancellationToken)
    {
        HashSet<Guid> organizationIds = await organizations.GetAcceptedOrganizationIdsAsync(userId, cancellationToken);
        if (organizationIds.Count == 0)
        {
            return [];
        }

        DateTime now = DateTime.UtcNow;
        return (await quotes.ListForBuyerAsync(organizationIds, ListLimit, cancellationToken))
            .Select(detail => QuoteMapper.ToSummary(detail, now))
            .ToList();
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

    /// <summary>The commercial terms a supplier states, whether on a first quote or on a revision.</summary>
    private sealed record OfferTerms(
        string Coverage,
        string Currency,
        int? LeadTimeDays,
        int? ValidityDays,
        string? PaymentTerms,
        string? DeliveryTerms,
        decimal? DeliveryCost,
        string TaxState,
        decimal? DiscountPercent,
        string? PackSizeNote,
        string? MoqNote,
        string? TechnicalNotes,
        string? SupplierNote,
        IReadOnlyList<CreateQuoteItemRequest>? Items);

    /// <summary>A computed revision and its lines, not yet attached to a quote (the caller sets quote id, number and chain).</summary>
    private sealed record BuiltOffer(QuoteRevision Revision, List<QuoteRevisionItem> Items);

    /// <summary>
    /// The one place an offer's numbers are computed — used by both the first
    /// submit and every revision, so a revision can never be priced by different
    /// rules than the quote it replaces. Validates the terms against the RFQ,
    /// builds the lines from the RFQ's own rows and derives every total.
    /// </summary>
    private async Task<BuiltOffer> BuildOfferAsync(
        Rfq rfq,
        IReadOnlyList<QuoteLineContext> rfqLines,
        OfferTerms terms,
        DateTime now,
        CancellationToken cancellationToken)
    {
        QuoteCoverage declaredCoverage = ParseCoverage(terms.Coverage);
        TaxState taxState = ParseTaxState(terms.TaxState);
        int validityDays = terms.ValidityDays is int days && days > 0
            ? days
            : throw new AppException(ErrorCodes.QuoteValidityRequired); // §7: validate validityDays > 0

        IReadOnlyList<CreateQuoteItemRequest> submitted = terms.Items ?? [];
        if (submitted.Count == 0)
        {
            throw new AppException(ErrorCodes.QuoteCoverageEmpty); // §7: coverage ≥ 1 line
        }

        Dictionary<Guid, QuoteLineContext> linesById = rfqLines.ToDictionary(line => line.Item.PublicId);
        List<QuoteRevisionItem> items = await BuildItemsAsync(rfq, submitted, linesById, cancellationToken);

        RequireDeclaredCoverage(rfqLines, items, declaredCoverage);

        decimal subtotal = Round(items.Sum(item => item.LineTotal));
        decimal discountTotal = Round(subtotal * ((terms.DiscountPercent ?? 0m) / 100m));
        decimal deliveryCost = Round(terms.DeliveryCost ?? 0m);
        decimal netBeforeTax = Round(subtotal - discountTotal + deliveryCost);
        decimal taxAmount = ComputeTax(taxState, netBeforeTax);
        // TaxIncluded prices already carry the tax, so the grand total is the net
        // value and the computed amount is the portion reported inside it.
        decimal grandTotal = taxState == TaxState.TaxIncluded ? netBeforeTax : Round(netBeforeTax + taxAmount);

        QuoteRevision revision = new()
        {
            CurrencyCode = terms.Currency.ToUpperInvariant(),
            Subtotal = subtotal,
            DiscountTotal = discountTotal,
            DeliveryCost = deliveryCost,
            TaxAmount = taxAmount,
            GrandTotal = grandTotal,
            TaxState = taxState,
            LeadTimeDays = terms.LeadTimeDays,
            ValidUntil = now.AddDays(validityDays),
            PaymentTerms = Trim(terms.PaymentTerms),
            DeliveryTerms = Trim(terms.DeliveryTerms),
            PackSizeNote = Trim(terms.PackSizeNote),
            MoqNote = Trim(terms.MoqNote),
            TechnicalNotes = Trim(terms.TechnicalNotes),
            SupplierNote = Trim(terms.SupplierNote),
            Status = QuoteRevisionStatus.Active,
            CreatedAt = now,
        };

        foreach (QuoteRevisionItem item in items)
        {
            item.RevisionId = revision.PublicId;
        }

        return new BuiltOffer(revision, items);
    }

    /// <summary>
    /// Terminal quotes are frozen: an accepted one answers its own conflict code and
    /// a rejected, withdrawn or expired one is immutable (business-flows §8.4).
    /// </summary>
    private static void RequireOpenQuote(Quote quote)
    {
        switch (quote.Status)
        {
            case QuoteStatus.Accepted:
                throw new AppException(ErrorCodes.QuoteAlreadyAccepted);
            case QuoteStatus.Rejected:
            case QuoteStatus.Withdrawn:
            case QuoteStatus.Expired:
                throw new AppException(ErrorCodes.QuoteImmutable);
            case QuoteStatus.Draft:
            case QuoteStatus.Submitted:
            case QuoteStatus.UnderReview:
            case QuoteStatus.RevisionRequested:
            case QuoteStatus.Revised:
                return;
            default:
                throw new InvalidOperationException($"Unhandled quote status '{quote.Status}'.");
        }
    }

    /// <summary>
    /// The quote a BUYER acts on, tracked for update: resolved only through the
    /// caller's accepted organisations, so another tenant's quote id is the same 404
    /// as a missing one (BOLA, api.md §1.6).
    /// </summary>
    private async Task<QuoteDetail> LoadBuyerQuoteForUpdateAsync(
        Guid userId, Guid quoteId, CancellationToken cancellationToken)
    {
        HashSet<Guid> organizationIds = await organizations.GetAcceptedOrganizationIdsAsync(userId, cancellationToken);
        if (organizationIds.Count == 0)
        {
            throw NotFound();
        }

        return await quotes.LoadForBuyerAsync(organizationIds, quoteId, forUpdate: true, cancellationToken)
            ?? throw NotFound();
    }

    private static void Touch(Quote quote, Guid userId, DateTime now)
    {
        quote.UpdatedAt = now;
        quote.UpdatedBy = userId;
    }

    /// <summary>Outbox payload: ids and non-secret facts only — never a note, price list or address (Z4).</summary>
    private static object EventPayload(Quote quote, QuoteRevision revision) => new
    {
        quoteId = quote.PublicId,
        rfqId = quote.RfqId,
        supplierId = quote.SupplierId,
        revisionNumber = revision.RevisionNumber,
    };

    /// <summary>
    /// Builds the revision's lines from the RFQ's own rows — never from the
    /// request's — and snapshots what was quoted (domain-model §6.2). A line the
    /// RFQ does not have is a client mistake (422), and a substitution note on an
    /// RFQ that forbids substitutions is the documented refusal (business-flows §7).
    /// </summary>
    private async Task<List<QuoteRevisionItem>> BuildItemsAsync(
        Rfq rfq,
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
            if (substitutionNote is not null && !rfq.SubstitutionsAllowed)
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

    /// <summary>
    /// Runs one quote edge through the machine. <paramref name="from"/> is the
    /// status the actor SEES (the effective one, so a quote whose validity lapsed
    /// is judged as <c>Expired</c>, which has no onward edge) and the state is set
    /// from the evaluator's matched edge, never a literal (X6/S10).
    /// </summary>
    private static void ApplyQuote(
        Quote quote, QuoteStatus from, QuoteStatus to, Guid userId, IReadOnlySet<string> granted, string? reason)
    {
        TransitionResult<QuoteStatus> result = QuoteMachine.Evaluate(
            from, to, ActorContext.ForUser(userId, granted), reason);
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

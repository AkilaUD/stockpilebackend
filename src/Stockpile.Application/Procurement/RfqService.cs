using System.Globalization;
using Stockpile.Application.Audit;
using Stockpile.Application.Authentication;
using Stockpile.Application.Common.Errors;
using Stockpile.Application.Organizations;
using Stockpile.Contracts.Errors;
using Stockpile.Contracts.Procurement;
using Stockpile.Domain.Entities.Procurement;
using Stockpile.Domain.Enums;
using Stockpile.Domain.StateMachines;

namespace Stockpile.Application.Procurement;

/// <summary>
/// The RFQ use cases (implementation-plan, business-flows §3.8, api.md §2.6):
/// creating a draft, submitting it (which runs the matching pipeline), and the
/// buyer's own read surfaces.
/// <para>
/// Ownership is organisational, never per-user (domain-model §2.3): when the
/// caller already belongs to an accepted organisation the request joins it;
/// when they belong to none — a fresh buyer who never completed the
/// company step — the platform provisions the buyer company in the same call
/// through <see cref="OrganizationService"/>, so the wizard's write is reused
/// rather than reinvented.
/// </para>
/// <para>
/// Status changes flow only through <c>StateMachine&lt;RFQStatus&gt;.Evaluate</c>
/// (X6/S10); each allowed edge appends an <c>RfqStatusHistory</c> row.
/// </para>
/// </summary>
public sealed class RfqService(
    IRfqStore store,
    IOrganizationStore organizations,
    OrganizationService organizationService,
    AuthenticationService authentication,
    IUserPermissionStore permissions,
    IAuditService audit,
    ICurrentRequestContext requestContext)
{
    private static readonly StateMachine<RFQStatus> Machine = new(TransitionTables.Rfq.All);

    /// <summary>
    /// Creates a Draft RFQ with its line items (api.md §2.6 <c>POST /rfqs</c>).
    /// Items may be revisited until submit; the draft is the buyer's scratchpad.
    /// </summary>
    public async Task<RfqResponse> DraftAsync(
        Guid actorId, CreateRfqRequest request, CancellationToken cancellationToken)
    {
        Guid organizationId = await ResolveOrganizationAsync(actorId, request.Company, cancellationToken);

        List<FieldError> errors = [];
        List<RfqItem> items = [];
        for (int i = 0; i < request.Items.Count; i++)
        {
            CreateRfqItemRequest draft = request.Items[i];

            Guid? categoryId = null;
            if (!string.IsNullOrWhiteSpace(draft.CategorySlug))
            {
                categoryId = await store.FindActiveCategoryIdBySlugAsync(draft.CategorySlug.Trim(), cancellationToken);
                if (categoryId is null)
                {
                    errors.Add(new FieldError(
                        $"items[{i}].categorySlug", "unknown_category", "Unknown or inactive department."));
                }
            }

            Guid? unitId = null;
            if (!string.IsNullOrWhiteSpace(draft.UnitOfMeasureCode))
            {
                unitId = await store.FindUnitOfMeasureIdByCodeAsync(draft.UnitOfMeasureCode.Trim(), cancellationToken);
                if (unitId is null)
                {
                    errors.Add(new FieldError(
                        $"items[{i}].unitOfMeasureCode", "unknown_unit", "Unknown unit of measure."));
                }
            }

            items.Add(new RfqItem
            {
                LineNumber = i + 1,
                CategoryId = categoryId,
                Description = draft.Description.Trim(),
                Specification = Trim(draft.Specification),
                Quantity = draft.Quantity,
                UnitOfMeasureId = unitId,
            });
        }

        if (errors.Count > 0)
        {
            throw new AppException(ErrorCodes.ValidationFailed, errors);
        }

        var rfq = new Rfq
        {
            OrganizationId = organizationId,
            ReferenceCode = await NextReferenceCodeAsync(cancellationToken),
            Title = request.Title.Trim(),
            DeliveryLocationText = Trim(request.DeliveryLocationText),
            SubstitutionsAllowed = request.SubstitutionsAllowed,
            SpecialInstructions = Trim(request.SpecialInstructions),
            Visibility = RfqVisibility.MatchedOnly,
            Source = RfqSource.Manual,
            ExpiryAt = request.ExpiryDays is int days && days > 0 ? DateTime.UtcNow.AddDays(days) : null,
        };
        foreach (RfqItem item in items)
        {
            item.RfqId = rfq.PublicId;
        }

        await store.AddAsync(rfq, items, cancellationToken);
        await audit.RecordAsync(
            new AuditEntry("rfq.created", "RFQ", rfq.PublicId.ToString(), AuditResult.Success), cancellationToken);

        List<RfqItemDetail> itemDetails = [];
        for (int i = 0; i < items.Count; i++)
        {
            itemDetails.Add(new RfqItemDetail(items[i], Trim(request.Items[i].CategorySlug), Trim(request.Items[i].UnitOfMeasureCode)));
        }

        return ToResponse(rfq, itemDetails, []);
    }

    /// <summary>
    /// Submits a draft (api.md §2.6 <c>POST /rfqs/{id}/submit</c>) and runs the
    /// matching pipeline: the buyer's own capability drives
    /// Draft → Submitted, then the platform's system actor drives
    /// Submitted → Matching → Dispatched, writing one match per active supplier
    /// that covers one of the requested departments (or an ancestor).
    /// </summary>
    public async Task<RfqResponse> SubmitAsync(Guid actorId, Guid rfqId, CancellationToken cancellationToken)
    {
        // A11 (threat-model line 169): email ownership is proven BEFORE a request
        // is dispatched to suppliers, not before browsing or sign-in.
        await authentication.EnsureEmailVerifiedAsync(actorId, cancellationToken);

        RfqDetail detail = await LoadOwnedAsync(actorId, rfqId, cancellationToken);
        Rfq rfq = detail.Rfq;

        if (rfq.Status != RFQStatus.Draft)
        {
            throw new AppException(ErrorCodes.RfqNotSubmittable);
        }

        IReadOnlySet<string> granted = (await permissions.GetPermissionCodesAsync(actorId, cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        List<RfqStatusHistory> history = [];

        Apply(rfq, RFQStatus.Submitted, ActorContext.ForUser(actorId, granted), reason: null, history);
        rfq.SubmittedAt = DateTime.UtcNow;

        Apply(rfq, RFQStatus.Matching, ActorContext.System, reason: null, history);
        List<RfqMatchDetail> matches = await MatchAsync(rfq, detail.Items, cancellationToken);
        Apply(rfq, RFQStatus.Dispatched, ActorContext.System, reason: null, history);

        await store.SaveSubmissionAsync(
            rfq, matches.Select(match => match.Match).ToList(), history, cancellationToken);
        await audit.RecordAsync(
            new AuditEntry("rfq.submitted", "RFQ", rfq.PublicId.ToString(), AuditResult.Success), cancellationToken);

        return ToResponse(rfq, detail.Items, matches);
    }

    /// <summary>The caller's organisations' RFQs, newest first (api.md §2.6 <c>GET /me/rfqs</c>).</summary>
    public async Task<IReadOnlyList<RfqResponse>> ListAsync(Guid actorId, CancellationToken cancellationToken)
    {
        HashSet<Guid> organizationIds = await AcceptedOrganizationIdsAsync(actorId, cancellationToken);
        if (organizationIds.Count == 0)
        {
            return [];
        }

        IReadOnlyList<Guid> ids = await store.GetOrganizationRfqIdsAsync(organizationIds, cancellationToken);
        if (ids.Count == 0)
        {
            return [];
        }

        return (await store.LoadManyAsync(ids, cancellationToken))
            .Select(detail => ToResponse(detail.Rfq, detail.Items, detail.Matches))
            .ToList();
    }

    /// <summary>One RFQ the caller's organisation owns; another tenant's id answers the BOLA 404 (api.md §1.6).</summary>
    public async Task<RfqResponse> GetAsync(Guid actorId, Guid rfqId, CancellationToken cancellationToken)
    {
        RfqDetail detail = await LoadOwnedAsync(actorId, rfqId, cancellationToken);
        return ToResponse(detail.Rfq, detail.Items, detail.Matches);
    }

    // ---- Internals ----------------------------------------------------------

    private async Task<RfqDetail> LoadOwnedAsync(Guid actorId, Guid rfqId, CancellationToken cancellationToken)
    {
        RfqDetail? detail = await store.LoadAsync(rfqId, cancellationToken);
        HashSet<Guid> organizationIds = await AcceptedOrganizationIdsAsync(actorId, cancellationToken);
        if (detail is null || !organizationIds.Contains(detail.Rfq.OrganizationId))
        {
            // api.md §1.6 BOLA: a foreign id and a missing id are indistinguishable.
            throw new AppException(ErrorCodes.ResourceNotFound);
        }

        return detail;
    }

    private async Task<HashSet<Guid>> AcceptedOrganizationIdsAsync(Guid actorId, CancellationToken cancellationToken)
        => (await organizations.GetMembershipsAsync(actorId, cancellationToken))
            .Where(membership => membership.Status == InvitationStatus.Accepted)
            .Select(membership => membership.OrganizationId)
            .ToHashSet();

    /// <summary>
    /// The organisation the request belongs to. An accepted membership is used
    /// as-is; with none, the buyer company is provisioned from the request's own
    /// company block via the wizard's own use case (business-flows §2 step 3).
    /// </summary>
    private async Task<Guid> ResolveOrganizationAsync(
        Guid actorId, RfqCompanyDraft? company, CancellationToken cancellationToken)
    {
        OrganizationMembership? existing = (await organizations.GetMembershipsAsync(actorId, cancellationToken))
            .FirstOrDefault(membership => membership.Status == InvitationStatus.Accepted);
        if (existing is not null)
        {
            return existing.OrganizationId;
        }

        if (company is null)
        {
            throw new AppException(ErrorCodes.ValidationFailed,
                [new FieldError("company", "required", "Add your company details for your first request for quote.")]);
        }

        var created = await organizationService.CreateOrganizationAsync(
            actorId,
            new CreateOrganizationRequest(
                new OrganizationCompanyDraft(company.Name, company.RegistrationNumber, null, company.Website),
                Addresses: null,
                Members: null),
            cancellationToken);

        return Guid.Parse(created.Id);
    }

    private async Task<List<RfqMatchDetail>> MatchAsync(
        Rfq rfq, IReadOnlyList<RfqItemDetail> items, CancellationToken cancellationToken)
    {
        List<RfqMatchDetail> matches = [];
        HashSet<Guid> requestedCategories = items
            .Where(item => item.Item.CategoryId.HasValue)
            .Select(item => item.Item.CategoryId!.Value)
            .ToHashSet();
        if (requestedCategories.Count == 0)
        {
            return matches;
        }

        IReadOnlyList<Guid> expanded = await store.ExpandToAncestorsAsync(requestedCategories, cancellationToken);
        foreach (SupplierMatch supplier in await store.FindQuotableSuppliersAsync(expanded, cancellationToken))
        {
            matches.Add(new RfqMatchDetail(
                new RfqMatch
                {
                    RfqId = rfq.PublicId,
                    SupplierId = supplier.SupplierId,
                    MatchScore = supplier.MatchedCategorySlugs.Count,
                    MatchReasons = string.Join(
                        " · ",
                        supplier.MatchedCategorySlugs
                            .Order(StringComparer.Ordinal)
                            .Select(slug => $"category:{slug}")),
                    ExpiryAt = rfq.ExpiryAt,
                },
                supplier.DisplayName,
                supplier.Slug));
        }

        return matches;
    }

    /// <summary>
    /// Applies one transition through the evaluator and appends its history row.
    /// A rejected edge surfaces the precise business error: a missing capability
    /// is 403, anything else is the standard invalid-transition 409.
    /// </summary>
    private void Apply(
        Rfq rfq, RFQStatus to, ActorContext actor, string? reason, List<RfqStatusHistory> history)
    {
        RFQStatus from = rfq.Status;
        TransitionResult<RFQStatus> result = Machine.Evaluate(from, to, actor, reason);
        if (!result.IsAllowed)
        {
            throw result.Kind == TransitionResultKind.PermissionDenied
                ? new AppException(ErrorCodes.InsufficientCapability)
                : new AppException(ErrorCodes.StateTransitionInvalid);
        }

        rfq.Status = result.Edge!.To; // value from the evaluator's matched edge — never a literal (X6/S10)
        history.Add(new RfqStatusHistory
        {
            RfqId = rfq.PublicId,
            FromState = from,
            ToState = rfq.Status,
            ActorId = actor.UserId,
            Reason = reason,
            CorrelationId = requestContext.CorrelationId,
        });
    }

    private async Task<string> NextReferenceCodeAsync(CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            string code = $"RFQ-{DateTime.UtcNow.Year}-{Guid.NewGuid():N}"[..17].ToUpperInvariant();
            if (!await store.ReferenceCodeExistsAsync(code, cancellationToken))
            {
                return code;
            }
        }

        throw new AppException(ErrorCodes.InternalError);
    }

    private static RfqResponse ToResponse(
        Rfq rfq, IReadOnlyList<RfqItemDetail> items, IReadOnlyList<RfqMatchDetail> matches)
        => new(
            rfq.PublicId.ToString(),
            rfq.ReferenceCode,
            rfq.Title,
            rfq.Status.ToString(),
            rfq.OrganizationId.ToString(),
            rfq.Visibility.ToString(),
            rfq.Source.ToString(),
            rfq.SubstitutionsAllowed,
            rfq.DeliveryLocationText,
            rfq.SpecialInstructions,
            rfq.SubmittedAt?.ToString("O", CultureInfo.InvariantCulture),
            rfq.ExpiryAt?.ToString("O", CultureInfo.InvariantCulture),
            items.Select(item => new RfqItemResponse(
                item.Item.PublicId.ToString(),
                item.Item.LineNumber,
                item.Item.Description,
                item.Item.Quantity,
                item.CategorySlug,
                item.UnitCode,
                item.Item.Specification)).ToList(),
            matches.Select(match => new RfqMatchSummary(
                match.Match.SupplierId.ToString(),
                match.SupplierName,
                match.SupplierSlug,
                match.Match.Status.ToString(),
                match.Match.MatchScore,
                match.Match.MatchReasons)).ToList());

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

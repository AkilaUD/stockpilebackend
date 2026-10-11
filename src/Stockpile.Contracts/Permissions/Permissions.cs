namespace Stockpile.Contracts.Permissions;

/// <summary>
/// Canonical permission codes (domain-model §2.2). Codes are dotted, stable,
/// and never renamed once seeded: they appear on state transitions, policy
/// names, and audit rows. A permission grants *capability* only — object access
/// is always confirmed by an ownership query (architecture §8.2 layer 4).
/// </summary>
public static class Permissions
{
    // ---- RFQ (buyer-driven procurement) ----
    public const string RfqCreate = "rfq.create";
    public const string RfqReadOwn = "rfq.read.own";
    public const string RfqReadAny = "rfq.read.any";
    public const string RfqSubmit = "rfq.submit";
    public const string RfqCancel = "rfq.cancel";
    public const string RfqClose = "rfq.close";
    public const string RfqAward = "rfq.award";
    public const string RfqMatch = "rfq.match";
    public const string RfqMatchDecline = "rfq.match.decline";
    public const string RfqMatchModerate = "rfq.match.moderate";

    // ---- Quotes ----
    public const string QuoteSubmit = "quote.submit";
    public const string QuoteRevise = "quote.revise";
    public const string QuoteReadSupplier = "quote.read.supplier";
    public const string QuoteReadBuyer = "quote.read.buyer";
    public const string QuoteAccept = "quote.accept";
    public const string QuoteDecline = "quote.decline";
    public const string QuoteRequestRevision = "quote.requestRevision";
    public const string QuoteWithdraw = "quote.withdraw";

    // ---- Orders ----
    public const string OrderCreate = "order.create";
    public const string OrderReadOwn = "order.read.own";
    public const string OrderFulfill = "order.fulfill";
    public const string OrderCancel = "order.cancel";
    public const string OrderDispute = "order.dispute";

    // ---- Catalogue ----
    public const string ProductCreate = "product.create";
    public const string ProductUpdateOwn = "product.update.own";
    public const string ProductPublish = "product.publish";
    public const string ProductModerate = "product.moderate";

    // ---- Suppliers ----
    public const string SupplierVerify = "supplier.verify";
    public const string SupplierSuspend = "supplier.suspend";
    public const string SupplierReadInternal = "supplier.read.internal";
    public const string SupplierUpdateOwn = "supplier.update.own";

    // ---- Subscriptions ----
    public const string SubscriptionManage = "subscription.manage";
    public const string SubscriptionBill = "subscription.bill";

    // ---- Content & engagement ----
    public const string ContentPublish = "content.publish";
    public const string ContentModerate = "content.moderate";
    public const string ReviewCreate = "review.create";
    public const string ReviewModerate = "review.moderate";

    // ---- Platform ----
    public const string AuditRead = "audit.read";
    public const string SecurityRead = "security.read";
    public const string AdminUsersUnlock = "admin.users.unlock";

    // ---- Organisations ----
    public const string OrganizationMemberManage = "organization.member.manage";

    /// <summary>
    /// Every code with its admin-UI description, in category order. This is the
    /// seed source (task 1.15); the category is the code's first segment.
    /// </summary>
    public static readonly IReadOnlyList<PermissionDefinition> All =
    [
        new(RfqCreate, "Create a draft RFQ"),
        new(RfqReadOwn, "Read RFQs owned by the caller's organisation"),
        new(RfqReadAny, "Read any RFQ across the platform (internal operator)"),
        new(RfqSubmit, "Submit a draft RFQ for matching"),
        new(RfqCancel, "Cancel an RFQ before award"),
        new(RfqClose, "Close an RFQ after award"),
        new(RfqAward, "Award an RFQ to a quote"),
        new(RfqMatch, "Run RFQ matching and dispatch"),
        new(RfqMatchDecline, "Decline a matched buying lead"),
        new(RfqMatchModerate, "Block or moderate a match (internal operator)"),

        new(QuoteSubmit, "Submit a quote"),
        new(QuoteRevise, "Submit a revised quote"),
        new(QuoteReadSupplier, "Read quotes belonging to the caller's supplier"),
        new(QuoteReadBuyer, "Read quotes against the caller's organisation's RFQs"),
        new(QuoteAccept, "Accept a quote"),
        new(QuoteDecline, "Decline a quote"),
        new(QuoteRequestRevision, "Request a quote revision"),
        new(QuoteWithdraw, "Withdraw a submitted quote (supplier)"),

        new(OrderCreate, "Place an order from an accepted quote"),
        new(OrderReadOwn, "Read orders owned by the caller's organisation"),
        new(OrderFulfill, "Confirm and fulfil an order (supplier)"),
        new(OrderCancel, "Cancel an order before fulfilment"),
        new(OrderDispute, "Raise a dispute on an order in progress"),

        new(ProductCreate, "Create a product"),
        new(ProductUpdateOwn, "Edit or archive the caller's supplier's products"),
        new(ProductPublish, "Request publication of a product"),
        new(ProductModerate, "Approve, reject, or unpublish any product (internal operator)"),

        new(SupplierVerify, "Verify or re-activate a supplier (internal operator)"),
        new(SupplierSuspend, "Suspend or close a supplier (internal operator)"),
        new(SupplierReadInternal, "Read supplier records including verification detail (internal operator)"),
        new(SupplierUpdateOwn, "Edit and submit the caller's supplier profile"),

        new(SubscriptionManage, "Manage the caller's supplier subscription"),
        new(SubscriptionBill, "Bill, invoice, and adjust subscriptions (internal operator)"),

        new(ContentPublish, "Publish editorial content"),
        new(ContentModerate, "Review or unpublish editorial content"),
        new(ReviewCreate, "Create a supplier review"),
        new(ReviewModerate, "Approve, hide, or reject reviews"),

        new(AuditRead, "Read audit logs (internal operator)"),
        new(SecurityRead, "Read security events (internal operator)"),
        new(AdminUsersUnlock, "Release a lockout on a platform account (internal administrator)"),

        new(OrganizationMemberManage, "Invite, change and remove members of the caller's organisation"),
    ];
}

/// <summary>A permission row: stable code plus its admin-UI description.</summary>
public sealed record PermissionDefinition(string Code, string Description);

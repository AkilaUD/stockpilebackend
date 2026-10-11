namespace Stockpile.Contracts.Errors;

/// <summary>
/// Canonical API error codes (api.md §5) and their envelope metadata (§1.4).
/// Codes are locale-independent and never renamed once shipped — the frontend
/// maps them to §47 context-aware messages. Every failure path resolves a code
/// here: no magic strings, no ad-hoc statuses.
/// <para>
/// <c>Retryable</c> is true only when replaying the *identical* request may
/// succeed unchanged (transient server fault, rate limit that expires);
/// business conflicts require the client to change something first.
/// </para>
/// </summary>
public static class ErrorCodes
{
    // ---- Authentication & session ----
    public const string InvalidCredentials = "INVALID_CREDENTIALS";
    public const string AccountLocked = "ACCOUNT_LOCKED";
    public const string EmailNotVerified = "EMAIL_NOT_VERIFIED";
    public const string VerificationTokenInvalid = "INVALID_VERIFICATION_TOKEN";
    public const string SessionExpired = "SESSION_EXPIRED";
    public const string Unauthenticated = "UNAUTHENTICATED";
    public const string AntiforgeryFailed = "ANTIFORGERY_FAILED";
    public const string ResetTokenInvalid = "INVALID_RESET_TOKEN";

    // ---- Authorization ----
    public const string InsufficientCapability = "INSUFFICIENT_CAPABILITY";
    public const string NotOrganizationMember = "NOT_ORGANIZATION_MEMBER";
    public const string ResourceNotFound = "RESOURCE_NOT_FOUND";
    public const string CrossTenantDenied = "CROSS_TENANT_DENIED";

    // ---- RFQs ----
    public const string RfqNotSubmittable = "RFQ_NOT_SUBMITTABLE";
    public const string RfqItemsImmutable = "RFQ_ITEMS_IMMUTABLE";
    public const string RfqAlreadySubmitted = "RFQ_ALREADY_SUBMITTED";
    public const string RfqNoItems = "RFQ_NO_ITEMS";
    public const string RfqExpired = "RFQ_EXPIRED";

    // ---- Quotes ----
    public const string QuoteExpired = "QUOTE_EXPIRED";
    public const string QuoteAlreadyAccepted = "QUOTE_ALREADY_ACCEPTED";
    public const string QuoteImmutable = "QUOTE_IMMUTABLE";
    public const string QuoteCoverageEmpty = "QUOTE_COVERAGE_EMPTY";
    public const string QuoteValidityRequired = "QUOTE_VALIDITY_REQUIRED";
    public const string QuoteRevisionLimit = "QUOTE_REVISION_LIMIT";

    // ---- Orders ----
    public const string OrderCreateFailed = "ORDER_CREATE_FAILED";
    public const string StockChanged = "STOCK_CHANGED";
    public const string InsufficientStock = "INSUFFICIENT_STOCK";
    public const string NegativeStockBlocked = "NEGATIVE_STOCK_BLOCKED";

    // ---- Catalogue ----
    public const string RequiredAttributeMissing = "REQUIRED_ATTRIBUTE_MISSING";
    public const string AttributeTypeInvalid = "ATTRIBUTE_TYPE_INVALID";
    public const string AttributeNotInCategory = "ATTRIBUTE_NOT_IN_CATEGORY";
    public const string ProductNotPublishable = "PRODUCT_NOT_PUBLISHABLE";
    public const string SkuDuplicate = "SKU_DUPLICATE";

    // ---- Uploads ----
    public const string FileTooLarge = "FILE_TOO_LARGE";
    public const string FileTypeNotAllowed = "FILE_TYPE_NOT_ALLOWED";
    public const string FileSignatureMismatch = "FILE_SIGNATURE_MISMATCH";
    public const string DoubleExtensionRejected = "DOUBLE_EXTENSION_REJECTED";
    public const string FileScanBlocked = "FILE_SCAN_BLOCKED";
    public const string DownloadForbidden = "DOWNLOAD_FORBIDDEN";

    // ---- Subscriptions & webhooks ----
    public const string EntitlementLimitReached = "ENTITLEMENT_LIMIT_REACHED";
    public const string NoActiveSubscription = "NO_ACTIVE_SUBSCRIPTION";
    public const string PlanNotActive = "PLAN_NOT_ACTIVE";
    public const string WebhookSignatureInvalid = "WEBHOOK_SIGNATURE_INVALID";
    public const string WebhookReplay = "WEBHOOK_REPLAY";

    // ---- Platform ----
    public const string IdempotencyKeyRequired = "IDEMPOTENCY_KEY_REQUIRED";
    public const string IdempotencyKeyReuse = "IDEMPOTENCY_KEY_REUSE";
    public const string ConcurrencyConflict = "CONCURRENCY_CONFLICT";
    public const string RateLimited = "RATE_LIMITED";
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string StateTransitionInvalid = "STATE_TRANSITION_INVALID";

    // ---- Transport-level (api.md §1.5; no business code applies) ----
    public const string InternalError = "INTERNAL_ERROR";
    public const string MalformedRequest = "MALFORMED_REQUEST";

    /// <summary>
    /// Every code with its default message, HTTP status (§1.5 rows), and retry
    /// flag. The seed source for envelope construction; a unit test asserts it
    /// covers exactly the constants above.
    /// </summary>
    public static readonly IReadOnlyList<ErrorDefinition> All =
    [
        new(InvalidCredentials, "Email or password is incorrect.", 401),
        new(AccountLocked, "This account is locked after repeated failed sign-ins. Wait for the lock to expire or reset your password.", 403),
        new(EmailNotVerified, "Verify your email address before submitting an RFQ.", 403),
        new(SessionExpired, "Your session has expired. Sign in again to continue.", 401),
        new(Unauthenticated, "Sign in to continue.", 401),
        new(AntiforgeryFailed, "This form is no longer valid. Refresh the page and try again.", 400),
        // One identical answer for an invalid, expired, re-used or cross-account
        // reset token (A5/A6 non-enumeration); the message stays actionable.
        new(ResetTokenInvalid, "The reset link is invalid or has expired. Request a new one.", 400),
        // Same discipline for the one-time verification token (task 1.38): the
        // reason a token is dead is never revealed.
        new(VerificationTokenInvalid, "The verification link is invalid or has expired. Request a new one.", 400),

        new(InsufficientCapability, "Your role does not allow this action. Ask an organisation administrator if you need access.", 403),
        new(NotOrganizationMember, "You are not a member of this organisation.", 403),
        new(ResourceNotFound, "That item does not exist, or you do not have access to it.", 404),
        new(CrossTenantDenied, "That item does not exist, or you do not have access to it.", 404),

        new(RfqNotSubmittable, "This RFQ is not in a state that allows submission.", 409),
        new(RfqItemsImmutable, "Items are locked because this RFQ has already been submitted.", 409),
        new(RfqAlreadySubmitted, "This RFQ has already been submitted.", 409),
        new(RfqNoItems, "Add at least one line item before submitting the RFQ.", 422),
        new(RfqExpired, "This RFQ has expired and can no longer be submitted or quoted.", 409),

        new(QuoteExpired, "This quote has expired and can no longer be accepted.", 403),
        new(QuoteAlreadyAccepted, "This quote has already been accepted.", 409),
        new(QuoteImmutable, "This quote can no longer be changed.", 409),
        new(QuoteCoverageEmpty, "The quote must cover every line item on the RFQ.", 422),
        new(QuoteValidityRequired, "Set how long the quote stays valid before submitting it.", 422),
        new(QuoteRevisionLimit, "This quote has reached its revision limit. Issue a new quote instead.", 409),

        new(OrderCreateFailed, "The order could not be created. Refresh and check stock before retrying.", 409),
        new(StockChanged, "Stock changed while you were ordering. Review the new quantities and try again.", 409),
        new(InsufficientStock, "There is not enough stock to fulfil this quantity.", 409),
        new(NegativeStockBlocked, "Quantities must be zero or greater.", 422),

        new(RequiredAttributeMissing, "Fill in the required attributes for this category before continuing.", 422),
        new(AttributeTypeInvalid, "Enter a value that matches the attribute's type.", 422),
        new(AttributeNotInCategory, "That attribute does not belong to this category.", 422),
        new(ProductNotPublishable, "Complete the required product details before publishing.", 409),
        new(SkuDuplicate, "That SKU is already used by another product.", 409),

        new(FileTooLarge, "The file exceeds the size limit for this upload.", 413),
        new(FileTypeNotAllowed, "That file type is not accepted here.", 415),
        new(FileSignatureMismatch, "The file content does not match its extension, so it was rejected.", 415),
        new(DoubleExtensionRejected, "File names with two extensions are not accepted.", 415),
        new(FileScanBlocked, "The file did not pass the malware scan and was discarded.", 415),
        new(DownloadForbidden, "You do not have access to that file.", 403),

        new(EntitlementLimitReached, "You have reached your plan's limit. Upgrade your plan to continue.", 409),
        new(NoActiveSubscription, "This action needs an active subscription.", 409),
        new(PlanNotActive, "That plan is not currently available.", 409),
        new(WebhookSignatureInvalid, "The webhook signature could not be verified.", 401),
        new(WebhookReplay, "This webhook delivery has already been processed.", 409),

        new(IdempotencyKeyRequired, "Send an Idempotency-Key header on this request.", 400),
        new(IdempotencyKeyReuse, "This Idempotency-Key was already used with a different payload.", 409),
        new(ConcurrencyConflict, "Someone else changed this record. Reload it and reapply your edits.", 409),
        new(RateLimited, "Too many requests. Wait for the retry-after interval and try again.", 429, Retryable: true),
        new(ValidationFailed, "Some fields need attention. Fix them and resubmit.", 422),
        new(StateTransitionInvalid, "This action is not allowed for the item's current status.", 409),

        new(InternalError, "Something went wrong handling this request. Retry once; if it keeps failing, contact support with the support reference.", 500, Retryable: true),
        new(MalformedRequest, "The request could not be read. Check the payload format and try again.", 400),
    ];

    private static readonly Dictionary<string, ErrorDefinition> ByCode = BuildLookup();

    /// <summary>Ordinal lookup by code; null when the code is not registered.</summary>
    public static ErrorDefinition? Resolve(string code) =>
        ByCode.TryGetValue(code, out var definition) ? definition : null;

    private static Dictionary<string, ErrorDefinition> BuildLookup() =>
        All.ToDictionary(static definition => definition.Code, StringComparer.Ordinal);
}

/// <summary>
/// Registry row for one error code: the default message, the HTTP status the
/// API returns for it (api.md §1.5), and whether an identical retry may succeed.
/// </summary>
public sealed record ErrorDefinition(string Code, string DefaultMessage, int HttpStatusCode, bool Retryable = false);

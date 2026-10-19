using System.Collections.Generic;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Stockpile.Api.RateLimiting;

/// <summary>
/// Task 1.26 — named rate-limit policies (api.md §1.8, spec §13.7). Every
/// limit is per (subjectId | clientIp) partition via <see
/// cref="RateLimitPartitionKeys"/>. Policies attach to endpoints with
/// <c>[EnableRateLimiting]</c> as those endpoints land (auth in 1D, RFQ/quote
/// in phases 3+); security-test-matrix X13 (enforced by
/// RateLimitedMutationsTests) keeps every future mutation action declared.
/// The test-only policy is registered solely under the Testing environment and
/// can never reach a shipped deployment.
/// </summary>
public static class RateLimitPolicies
{
    public const string AuthLogin = "auth.login"; // 10 / 5 min — credential stuffing (§13.7)
    public const string AuthReset = "auth.reset"; // 3 / hour
    public const string AuthRegister = "auth.register"; // 5 / hour
    public const string AuthVerify = "auth.verify"; // 10 / hour — email verify + resend share one budget
    public const string AuthMfa = "auth.mfa"; // 10 / hour — MFA enroll + verify share one budget (code guessing)
    public const string AuthRefresh = "auth.refresh"; // 60 / hour — refresh rotation (stolen-token probing)
    public const string AuthDraft = "auth.draft"; // 240 / hour — anonymous registration-draft saves (autosave is chatty by design)
    public const string AdminUnlock = "admin.users.unlock"; // 30 / hour — admin account unlocks
    public const string SearchPublic = "search.public"; // 60 / min
    public const string RfqCreate = "rfq.create"; // 20 / hour
    public const string RfqSubmit = "rfq.submit"; // 10 / hour
    public const string QuoteSubmit = "quote.submit"; // 60 / hour
    public const string QuoteRevision = "quote.revision"; // 20 / hour
    public const string QuoteRespond = "quote.respond"; // 60 / hour — withdraw, decline, request-revision
    public const string QuoteAccept = "quote.accept"; // 30 / hour — accept (creates an order)
    public const string OrderWrite = "order.write"; // 60 / hour — order acknowledgement
    public const string SubscriptionWrite = "subscription.write"; // 30 / hour — subscribe, cancel, initiate payment, operator billing
    public const string PaymentWebhook = "payment.webhook"; // 120 / min — anonymous, signature-verified provider callbacks
    public const string ContactIntent = "contact.intent"; // 30 / hour
    public const string PriceAlertCreate = "price.alert.create"; // 30 / day
    public const string ReviewCreate = "review.create"; // 5 / day
    public const string UploadPublic = "upload.public"; // 20 / hour
    public const string PublicApi = "public.api"; // 300 / min — anonymous reads

    // ---- Organisations & addresses (task 1.43) ----
    public const string OrganizationCreate = "organization.create"; // 10 / hour — wizard step 3 (spam: each call is a DB-visible org)
    public const string OrganizationMemberInvite = "organization.member.invite"; // 30 / hour — team invitations
    public const string AddressWrite = "address.write"; // 60 / hour — /me/addresses POST/PATCH/DELETE

    // ---- Buyer wizard profile/preferences (task 1.43 continued) ----
    public const string MeProfileWrite = "me.profile.write"; // 60 / hour — PATCH /me/profile
    public const string MePreferencesWrite = "me.preferences.write"; // 60 / hour — PATCH /me/preferences
    public const string MeNotificationPreferencesWrite = "me.notification-preferences.write"; // 60 / hour — PATCH /me/notification-preferences

    // ---- Supplier self-service onboarding (business-flows §3.3 stages 2–5) ----
    public const string SupplierProfileWrite = "supplier.profile.write"; // 60 / hour — /suppliers/me profile, categories, service-areas

    // ---- Supplier buying-lead inbox (business-flows §6, api.md §2.9) ----
    public const string SupplierLeadWrite = "supplier.lead.write"; // 60 / hour — /suppliers/me/leads/{id} view + decline

    /// <summary>Test-only policy (2/min) — registered only under Testing.</summary>
    public const string TestTight = "test.tight";

    /// <summary>
    /// Sensitive flows whose rejection clusters are abuse signals
    /// (threat-model §7): login/reset/register (credential stuffing),
    /// email verify (token-guessing noise + resend-driven email flooding),
    /// MFA enroll/verify (second-factor code guessing against a live session),
    /// refresh (stolen-token probing — a burst of 401s is theft attempts),
    /// search (scraping), upload (storage abuse). A rejection is already
    /// abnormal for these; a cluster raises an alert through
    /// <see cref="RateLimitBurstMonitor"/>.
    /// </summary>
    public static readonly HashSet<string> Sensitive =
        new(StringComparer.Ordinal)
        {
            AuthLogin, AuthReset, AuthRegister, AuthVerify, AuthMfa, AuthRefresh, AdminUnlock, SearchPublic,
            UploadPublic,
        };

    /// <summary>All named policies, including the test-only one.</summary>
    public static readonly string[] All =
    [
        AuthLogin, AuthReset, AuthRegister, AuthVerify, AuthMfa, AuthRefresh, AuthDraft, AdminUnlock, SearchPublic,
        RfqCreate,
        RfqSubmit, QuoteSubmit, QuoteRevision, QuoteRespond, QuoteAccept, OrderWrite, SubscriptionWrite, PaymentWebhook,
        ContactIntent, PriceAlertCreate, ReviewCreate,
        UploadPublic, PublicApi,
        OrganizationCreate, OrganizationMemberInvite, AddressWrite,
        MeProfileWrite, MePreferencesWrite, MeNotificationPreferencesWrite,
        SupplierProfileWrite,
        SupplierLeadWrite,
        TestTight,
    ];

    /// <summary>
    /// Registrations backing the 429 response headers (api.md §1.8): the limit
    /// for the violated policy and its window. Window end (Reset/Retry-After)
    /// is derived from the fixed window, never stored — the limiter itself is
    /// the single source of the counting.
    /// </summary>
    private static readonly Dictionary<string, (int PermitLimit, TimeSpan Window)> Specs =
        new(StringComparer.Ordinal)
        {
            [AuthLogin] = (10, TimeSpan.FromMinutes(5)),
            [AuthReset] = (3, TimeSpan.FromHours(1)),
            [AuthRegister] = (5, TimeSpan.FromHours(1)),
            [AuthVerify] = (10, TimeSpan.FromHours(1)),
            [AuthMfa] = (10, TimeSpan.FromHours(1)),
            [AuthRefresh] = (60, TimeSpan.FromHours(1)),
            [AuthDraft] = (240, TimeSpan.FromHours(1)),
            [AdminUnlock] = (30, TimeSpan.FromHours(1)),
            [SearchPublic] = (60, TimeSpan.FromMinutes(1)),
            [RfqCreate] = (20, TimeSpan.FromHours(1)),
            [RfqSubmit] = (10, TimeSpan.FromHours(1)),
            [QuoteSubmit] = (60, TimeSpan.FromHours(1)),
            [QuoteRevision] = (20, TimeSpan.FromHours(1)),
            [QuoteRespond] = (60, TimeSpan.FromHours(1)),
            [QuoteAccept] = (30, TimeSpan.FromHours(1)),
            [OrderWrite] = (60, TimeSpan.FromHours(1)),
            [SubscriptionWrite] = (30, TimeSpan.FromHours(1)),
            [PaymentWebhook] = (120, TimeSpan.FromMinutes(1)),
            [ContactIntent] = (30, TimeSpan.FromHours(1)),
            [PriceAlertCreate] = (30, TimeSpan.FromDays(1)),
            [ReviewCreate] = (5, TimeSpan.FromDays(1)),
            [UploadPublic] = (20, TimeSpan.FromHours(1)),
            [PublicApi] = (300, TimeSpan.FromMinutes(1)),
            [OrganizationCreate] = (10, TimeSpan.FromHours(1)),
            [OrganizationMemberInvite] = (30, TimeSpan.FromHours(1)),
            [AddressWrite] = (60, TimeSpan.FromHours(1)),
            [MeProfileWrite] = (60, TimeSpan.FromHours(1)),
            [MePreferencesWrite] = (60, TimeSpan.FromHours(1)),
            [MeNotificationPreferencesWrite] = (60, TimeSpan.FromHours(1)),
            [SupplierProfileWrite] = (60, TimeSpan.FromHours(1)),
            [SupplierLeadWrite] = (60, TimeSpan.FromHours(1)),
            [TestTight] = (2, TimeSpan.FromMinutes(1)),
        };

    /// <summary>Permit limit for a policy (0 when unknown — a bug to fix, not fail on).</summary>
    internal static int PermitLimitOf(string policy) =>
        Specs.TryGetValue(policy, out var spec) ? spec.PermitLimit : 0;

    /// <summary>Fixed window for a policy (one second when unknown).</summary>
    internal static TimeSpan WindowOf(string policy) =>
        Specs.TryGetValue(policy, out var spec) ? spec.Window : TimeSpan.FromSeconds(1);

    /// <summary>
    /// Registers every policy as a fixed-window limiter partitioned by
    /// (subjectId | clientIp), excess requests dropped (QueueLimit 0). The
    /// test-only policy registers only when <paramref name="testingOnly"/> is
    /// true so a production container can never carry it.
    /// <para>
    /// <paramref name="relaxCredentialBudgets"/> exists for ONE caller: the
    /// security suite boots a host with <c>RateLimiting:RelaxUnderTesting=false</c>
    /// so a login flood can exhaust the CANONICAL 10 / 5-minute budget over
    /// HTTP (security-test-matrix A2/R1). Outside Testing it is ignored — the
    /// relaxation only ever applies when <paramref name="testingOnly"/> is
    /// true, so the knob can only shrink a relaxation, never grow a limit.
    /// </para>
    /// </summary>
    internal static void AddPolicies(RateLimiterOptions options, bool testingOnly, bool relaxCredentialBudgets = true)
    {
        foreach (var policy in All)
        {
            if (policy == TestTight && !testingOnly)
            {
                continue;
            }

            var (permitLimit, window) = Specs[policy];
            if (testingOnly && relaxCredentialBudgets && RelaxedUnderTesting.TryGetValue(policy, out int relaxed))
            {
                permitLimit = relaxed;
            }

            options.AddPolicy(policy, httpContext => RateLimitPartition.GetFixedWindowLimiter(
                RateLimitPartitionKeys.For(httpContext),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitLimit,
                    Window = window,
                    QueueLimit = 0,
                }));
        }
    }

    /// <summary>
    /// Integration-suite budgets (Testing only). The shared test host sees every
    /// request from the same client identity — rate limiting runs deliberately
    /// BEFORE authentication (so a flood is refused before any ticket is read),
    /// which makes the <c>ip:</c> partition in <see cref="RateLimitPartitionKeys"/>
    /// the only partition the suite ever reaches. So the REAL §13.7 budgets —
    /// 5/hour register, 10/5-min login, 3/hour password-reset, 10/hour
    /// verify/MFA — would be exhausted by the suite's own assertions and produce
    /// flaky 429s; and so would the business writes a suite drives end-to-end
    /// (<c>rfq.create</c> 20/h, <c>rfq.submit</c> 10/h, <c>quote.submit</c>
    /// 60/h): one shared partition turns a per-subject budget into a suite-wide
    /// one, which no single test can observe in isolation.
    /// Only these budgets are relaxed; the endpoints still declare their
    /// policies (X13 asserts the declaration), the real 429 mechanics stay proven
    /// over HTTP by the test-only <c>test.tight</c> policy, and the canonical
    /// limits in <see cref="Specs"/> — and api.md §1.8 — remain authoritative
    /// for every other environment.
    /// </summary>
    private static readonly Dictionary<string, int> RelaxedUnderTesting = new(StringComparer.Ordinal)
    {
        [AuthLogin] = 1000,
        [AuthRegister] = 1000,
        [AuthReset] = 1000,
        [AuthVerify] = 1000,
        [AuthMfa] = 1000,
        [AuthRefresh] = 1000,
        [RfqCreate] = 1000,
        [RfqSubmit] = 1000,
        [QuoteSubmit] = 1000,
        [QuoteRevision] = 1000,
        [QuoteRespond] = 1000,
        [QuoteAccept] = 1000,
        [OrderWrite] = 1000,
        [SubscriptionWrite] = 1000,
        [PaymentWebhook] = 1000,
    };
}

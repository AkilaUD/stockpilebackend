using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Stockpile.Application.Common.Errors;
using Stockpile.Application.Subscriptions;
using Stockpile.Application.Subscriptions.Payments;
using Stockpile.Contracts.Errors;
using Stockpile.Contracts.Subscriptions;
using Stockpile.IntegrationTests.Testing;
using static Stockpile.IntegrationTests.Testing.MarketplaceTestKit;

namespace Stockpile.IntegrationTests.Subscriptions;

/// <summary>
/// The supplier subscription slice over real HTTP (business-flows §12, domain-model
/// §3.14), with the dev file-sink standing in for a payment gateway — no card is
/// ever charged and no live provider exists.
/// <para>
/// What this pins: the four reference plans are served publicly with the Customized
/// one sales-assisted and unpriced; subscribing issues the first invoice and grants
/// NO entitlement until it is paid; a payment is settled only by a correctly signed,
/// fresh, amount-matching webhook, exactly once however often it is delivered; an
/// operator can settle by hand with a recorded reason; and the entitlement helper
/// answers <c>NO_ACTIVE_SUBSCRIPTION</c> / <c>ENTITLEMENT_LIMIT_REACHED</c> from the
/// supplier's paid plan.
/// </para>
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SubscriptionApiTests(StockpileApiFactory factory)
{
    private const string WebhookRoute = $"{ApiBase}/webhooks/payments/filesink";

    private readonly MarketplaceTestKit _kit = new(factory);

    [Fact]
    public async Task The_public_plan_list_serves_the_four_reference_plans()
    {
        using HttpClient anonymous = AntiforgeryTestSupport.HttpsClient(factory);
        using HttpResponseMessage response = await anonymous.GetAsync($"{ApiBase}/subscriptions/plans");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ApiResult list = new(response.StatusCode, await response.Content.ReadAsStringAsync(), false);
        List<PlanResponse> plans = list.As<List<PlanResponse>>();

        foreach (string code in new[] { "starter", "standard", "enterprise", "customized" })
        {
            Assert.Contains(plans, plan => plan.Code == code);
        }

        PlanResponse customized = plans.Single(plan => plan.Code == "customized");
        Assert.True(customized.IsSalesAssisted);
        Assert.Null(customized.PriceAmount);

        PlanResponse enterprise = plans.Single(plan => plan.Code == "enterprise");
        Assert.False(enterprise.IsSalesAssisted);
        Assert.Equal(20000m, enterprise.PriceAmount);
        Assert.Contains(enterprise.Entitlements, e => e is { Key: "directCalls", Unlimited: true });
    }

    [Fact]
    public async Task Subscribing_issues_the_first_invoice_and_grants_nothing_until_it_is_paid()
    {
        Actor supplier = await _kit.RegisterSupplierAsync();

        ApiResult empty = await GetAsync(supplier, $"{ApiBase}/suppliers/me/subscription");
        Assert.Equal(HttpStatusCode.OK, empty.Status);
        Assert.Null(empty.As<SupplierSubscriptionResponse>().Subscription);

        SubscriptionResponse subscription = await SubscribeAsync(supplier, "starter");
        Assert.Equal("Trial", subscription.Status);
        Assert.False(subscription.Entitled);
        InvoiceResponse invoice = Assert.Single(subscription.Invoices);
        Assert.Equal("Issued", invoice.Status);
        Assert.Equal(10000m, invoice.Amount);
        Assert.Equal("LKR", invoice.Currency);

        // A second subscription while one is live, and a sales-assisted plan, are both refused.
        ApiResult again = await PostAsync(supplier, $"{ApiBase}/suppliers/me/subscription", new { planCode = "standard" });
        Assert.Equal(HttpStatusCode.Conflict, again.Status);
        Assert.Equal(ErrorCodes.SubscriptionExists, again.Code);

        Actor other = await _kit.RegisterSupplierAsync();
        ApiResult sales = await PostAsync(other, $"{ApiBase}/suppliers/me/subscription", new { planCode = "customized" });
        Assert.Equal(HttpStatusCode.Conflict, sales.Status);
        Assert.Equal(ErrorCodes.PlanSalesAssisted, sales.Code);
        ApiResult unknown = await PostAsync(other, $"{ApiBase}/suppliers/me/subscription", new { planCode = "no-such-plan" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.Status);
    }

    [Fact]
    public async Task A_buyer_without_a_company_cannot_subscribe()
    {
        Actor buyer = await _kit.RegisterBuyerAsync();
        ApiResult result = await PostAsync(buyer, $"{ApiBase}/suppliers/me/subscription", new { planCode = "starter" });
        Assert.Equal(HttpStatusCode.Forbidden, result.Status);
    }

    [Fact]
    public async Task Initiating_payment_records_to_the_file_sink_and_is_stable_while_pending()
    {
        Actor supplier = await _kit.RegisterSupplierAsync();
        SubscriptionResponse subscription = await SubscribeAsync(supplier, "starter");

        PaymentResponse payment = await InitiatePaymentAsync(supplier, subscription.Id);
        Assert.Equal("Pending", payment.Status);
        Assert.Equal("filesink", payment.Provider);
        Assert.Equal(subscription.Invoices[0].Total, payment.Amount);
        Assert.True(File.Exists(Path.Combine(StockpileApiFactory.PaymentSinkDirectory, $"{payment.ProviderRef}.intent.json")));

        // Asking again while one attempt is pending hands back the same attempt.
        PaymentResponse again = await InitiatePaymentAsync(supplier, subscription.Id);
        Assert.Equal(payment.Id, again.Id);
        Assert.Equal(payment.ProviderRef, again.ProviderRef);

        // Another supplier cannot pay (or even see) this subscription: the 404 of any foreign id.
        Actor stranger = await _kit.RegisterSupplierAsync();
        ApiResult probe = await PostAsync(stranger, $"{ApiBase}/subscriptions/{subscription.Id}/payments", new { });
        Assert.Equal(HttpStatusCode.NotFound, probe.Status);
    }

    [Fact]
    public async Task A_signed_webhook_settles_the_invoice_once_however_often_it_is_delivered()
    {
        Actor supplier = await _kit.RegisterSupplierAsync();
        SubscriptionResponse subscription = await SubscribeAsync(supplier, "standard");
        PaymentResponse payment = await InitiatePaymentAsync(supplier, subscription.Id);
        string body = SucceededBody(payment);

        ApiResult first = await SendWebhookAsync(body);
        Assert.Equal(HttpStatusCode.OK, first.Status);
        Assert.False(first.As<WebhookAckResponse>().Replayed);

        SubscriptionResponse settled = await ReadSubscriptionAsync(supplier);
        Assert.Equal("Active", settled.Status);
        Assert.True(settled.Entitled);
        Assert.Equal("Paid", Assert.Single(settled.Invoices).Status);
        Assert.NotNull(settled.Invoices[0].PaidAt);

        // The provider re-delivers (a different delivery key, the same payment): acknowledged, not applied twice.
        DateTime paidAt = settled.Invoices[0].PaidAt!.Value;
        ApiResult redelivered = await SendWebhookAsync(body);
        Assert.Equal(HttpStatusCode.OK, redelivered.Status);
        Assert.True(redelivered.As<WebhookAckResponse>().Replayed);

        // The same delivery key replays the original answer.
        string key = Guid.NewGuid().ToString();
        ApiResult duplicateKey = await SendWebhookAsync(body, idempotencyKey: key);
        ApiResult duplicateKeyAgain = await SendWebhookAsync(body, idempotencyKey: key);
        Assert.Equal(HttpStatusCode.OK, duplicateKey.Status);
        Assert.True(duplicateKeyAgain.Replayed);

        SubscriptionResponse after = await ReadSubscriptionAsync(supplier);
        Assert.Equal(paidAt, after.Invoices[0].PaidAt);
        Assert.Equal(settled.CurrentPeriodEnd, after.CurrentPeriodEnd);

        // The sink recorded the delivery.
        Assert.NotEmpty(Directory.GetFiles(StockpileApiFactory.PaymentSinkDirectory, $"{payment.ProviderRef}.webhook-*.json"));
    }

    [Fact]
    public async Task A_failed_payment_event_leaves_the_invoice_open()
    {
        Actor supplier = await _kit.RegisterSupplierAsync();
        SubscriptionResponse subscription = await SubscribeAsync(supplier, "starter");
        PaymentResponse payment = await InitiatePaymentAsync(supplier, subscription.Id);

        ApiResult failed = await SendWebhookAsync(SignedBody(payment, "payment.failed"));
        Assert.Equal(HttpStatusCode.OK, failed.Status);

        SubscriptionResponse read = await ReadSubscriptionAsync(supplier);
        Assert.False(read.Entitled);
        Assert.Equal("Issued", Assert.Single(read.Invoices).Status);

        // A fresh attempt can then be started and settled.
        PaymentResponse retry = await InitiatePaymentAsync(supplier, subscription.Id);
        Assert.NotEqual(payment.Id, retry.Id);
        Assert.Equal(HttpStatusCode.OK, (await SendWebhookAsync(SucceededBody(retry))).Status);
        Assert.True((await ReadSubscriptionAsync(supplier)).Entitled);
    }

    [Fact]
    public async Task Webhooks_that_fail_verification_change_nothing()
    {
        Actor supplier = await _kit.RegisterSupplierAsync();
        SubscriptionResponse subscription = await SubscribeAsync(supplier, "starter");
        PaymentResponse payment = await InitiatePaymentAsync(supplier, subscription.Id);
        string body = SucceededBody(payment);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Wrong secret, tampered body, no header, stale and future timestamps: all one 401.
        string[] bad =
        [
            Sign("some-other-secret", now, body),
            Sign(StockpileApiFactory.PaymentWebhookSecret, now - 3600, body),
            Sign(StockpileApiFactory.PaymentWebhookSecret, now + 3600, body),
            "garbage",
            string.Empty,
        ];
        foreach (string header in bad)
        {
            ApiResult rejected = await SendWebhookAsync(body, signatureHeader: header);
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.Status);
            Assert.Equal(ErrorCodes.WebhookSignatureInvalid, rejected.Code);
        }

        // A body altered after signing (the amount raised) no longer matches its signature.
        string tampered = SignedBody(payment, "payment.succeeded", amount: payment.Amount + 1m);
        ApiResult altered = await SendWebhookAsync(
            tampered, signatureHeader: Sign(StockpileApiFactory.PaymentWebhookSecret, now, body));
        Assert.Equal(HttpStatusCode.Unauthorized, altered.Status);

        SubscriptionResponse read = await ReadSubscriptionAsync(supplier);
        Assert.False(read.Entitled);
        Assert.Equal("Issued", Assert.Single(read.Invoices).Status);

        // A good signature still works afterwards: the rejected deliveries burned no key.
        Assert.Equal(HttpStatusCode.OK, (await SendWebhookAsync(body)).Status);
    }

    [Fact]
    public async Task A_signed_webhook_with_the_wrong_amount_or_an_unknown_payment_is_refused()
    {
        Actor supplier = await _kit.RegisterSupplierAsync();
        SubscriptionResponse subscription = await SubscribeAsync(supplier, "starter");
        PaymentResponse payment = await InitiatePaymentAsync(supplier, subscription.Id);

        string wrongAmount = SignedBody(payment, "payment.succeeded", amount: payment.Amount - 1m);
        ApiResult mismatch = await SendWebhookAsync(wrongAmount);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, mismatch.Status);

        string unknown = $$"""{"providerRef":"filesink-does-not-exist","event":"payment.succeeded","amount":1,"currency":"LKR"}""";
        Assert.Equal(HttpStatusCode.NotFound, (await SendWebhookAsync(unknown)).Status);

        Assert.False((await ReadSubscriptionAsync(supplier)).Entitled);

        // An unconfigured provider name does not exist as a route target.
        ApiResult noProvider = await SendWebhookAsync(unknown, route: $"{ApiBase}/webhooks/payments/no-such-provider");
        Assert.Equal(HttpStatusCode.NotFound, noProvider.Status);
    }

    [Fact]
    public async Task A_webhook_without_an_idempotency_key_is_refused()
    {
        Actor supplier = await _kit.RegisterSupplierAsync();
        SubscriptionResponse subscription = await SubscribeAsync(supplier, "starter");
        PaymentResponse payment = await InitiatePaymentAsync(supplier, subscription.Id);

        ApiResult result = await SendWebhookAsync(SucceededBody(payment), idempotencyKey: string.Empty);
        Assert.Equal(HttpStatusCode.BadRequest, result.Status);
        Assert.False((await ReadSubscriptionAsync(supplier)).Entitled);
    }

    [Fact]
    public async Task A_finance_operator_settles_an_invoice_by_hand_once_with_a_reason()
    {
        Actor supplier = await _kit.RegisterSupplierAsync();
        SubscriptionResponse subscription = await SubscribeAsync(supplier, "starter");
        string invoiceId = subscription.Invoices[0].Id;
        Actor finance = await _kit.RegisterOperatorAsync("financeOperator");

        // The supplier cannot settle its own invoice, and an operator must give a reason.
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await PostAsync(supplier, $"{ApiBase}/admin/invoices/{invoiceId}/mark-paid", new { reason = "I paid" })).Status);
        Assert.Equal(
            HttpStatusCode.UnprocessableEntity,
            (await PostAsync(finance, $"{ApiBase}/admin/invoices/{invoiceId}/mark-paid", new { reason = "" })).Status);

        ApiResult paid = await PostAsync(
            finance, $"{ApiBase}/admin/invoices/{invoiceId}/mark-paid", new { reason = "Bank transfer received", reference = "BT-1001" });
        Assert.Equal(HttpStatusCode.OK, paid.Status);
        Assert.Equal("Paid", paid.As<InvoiceResponse>().Status);

        SubscriptionResponse read = await ReadSubscriptionAsync(supplier);
        Assert.Equal("Active", read.Status);
        Assert.True(read.Entitled);

        ApiResult twice = await PostAsync(
            finance, $"{ApiBase}/admin/invoices/{invoiceId}/mark-paid", new { reason = "Again" });
        Assert.Equal(HttpStatusCode.Conflict, twice.Status);
        Assert.Equal(ErrorCodes.StateTransitionInvalid, twice.Code);

        // It appears on the operator's list with the supplier it bills.
        ApiResult list = await GetAsync(finance, $"{ApiBase}/admin/invoices?status=Paid");
        Assert.Contains(list.As<List<AdminInvoiceResponse>>(), row => row.Invoice.Id == invoiceId);
    }

    [Fact]
    public async Task An_operator_manages_plans_and_the_public_list_follows()
    {
        Actor finance = await _kit.RegisterOperatorAsync("financeOperator");
        string code = $"test-{Guid.NewGuid():N}"[..20];

        ApiResult created = await PostAsync(finance, $"{ApiBase}/admin/subscription-plans", new
        {
            code,
            name = "Test plan",
            priceAmount = 2500m,
            currency = "LKR",
            billingPeriod = "Monthly",
            isSalesAssisted = false,
            isActive = true,
            sortOrder = 90,
            entitlements = new[] { new { key = "productUploads", limit = (int?)3, unlimited = false, value = (string?)null } },
        });
        Assert.Equal(HttpStatusCode.Created, created.Status);
        PlanResponse plan = created.As<PlanResponse>();
        Assert.Equal(3, Assert.Single(plan.Entitlements).Limit);

        // A duplicate code is a field error, not a 500.
        ApiResult duplicate = await PostAsync(finance, $"{ApiBase}/admin/subscription-plans", new
        {
            code,
            name = "Again",
            priceAmount = 1m,
            currency = "LKR",
            billingPeriod = "Monthly",
            isSalesAssisted = false,
            isActive = true,
            sortOrder = 91,
            entitlements = Array.Empty<object>(),
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, duplicate.Status);

        ApiResult updated = await SendAsync(finance, HttpMethod.Put, $"{ApiBase}/admin/subscription-plans/{plan.Id}", new
        {
            name = "Test plan (renamed)",
            priceAmount = 3000m,
            currency = "LKR",
            billingPeriod = "Monthly",
            isSalesAssisted = false,
            isActive = false,
            sortOrder = 90,
            entitlements = new[] { new { key = "productUploads", limit = (int?)7, unlimited = false, value = (string?)null } },
        });
        Assert.Equal(HttpStatusCode.OK, updated.Status);
        PlanResponse after = updated.As<PlanResponse>();
        Assert.Equal(3000m, after.PriceAmount);
        Assert.Equal(7, Assert.Single(after.Entitlements).Limit);

        // Inactive: gone from the public list, still on the operator's.
        using HttpClient anonymous = AntiforgeryTestSupport.HttpsClient(factory);
        string publicBody = await anonymous.GetStringAsync($"{ApiBase}/subscriptions/plans");
        Assert.DoesNotContain(code, publicBody, StringComparison.Ordinal);
        Assert.Contains(
            (await GetAsync(finance, $"{ApiBase}/admin/subscription-plans")).As<List<PlanResponse>>(),
            row => row.Code == code);

        // A supplier has no say over plans.
        Actor supplier = await _kit.RegisterSupplierAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(supplier, $"{ApiBase}/admin/subscription-plans")).Status);
    }

    [Fact]
    public async Task Cancelling_before_the_first_payment_voids_the_open_invoice()
    {
        Actor supplier = await _kit.RegisterSupplierAsync();
        SubscriptionResponse subscription = await SubscribeAsync(supplier, "starter");

        ApiResult cancelled = await PostAsync(supplier, $"{ApiBase}/suppliers/me/subscription/cancel", new { reason = "Changed my mind" });
        Assert.Equal(HttpStatusCode.OK, cancelled.Status);
        SubscriptionResponse read = cancelled.As<SubscriptionResponse>();
        Assert.Equal("Cancelled", read.Status);
        Assert.False(read.Entitled);
        Assert.Equal("Void", Assert.Single(read.Invoices).Status);

        // A cancelled subscription is terminal, so the supplier can start afresh.
        SubscriptionResponse fresh = await SubscribeAsync(supplier, "standard");
        Assert.NotEqual(subscription.Id, fresh.Id);
    }

    [Fact]
    public async Task The_entitlement_helper_follows_the_suppliers_paid_plan()
    {
        Actor finance = await _kit.RegisterOperatorAsync("financeOperator");
        string code = $"quota-{Guid.NewGuid():N}"[..20];
        ApiResult plan = await PostAsync(finance, $"{ApiBase}/admin/subscription-plans", new
        {
            code,
            name = "Two uploads",
            priceAmount = 100m,
            currency = "LKR",
            billingPeriod = "Monthly",
            isSalesAssisted = false,
            isActive = true,
            sortOrder = 95,
            entitlements = new[] { new { key = "productUploads", limit = (int?)2, unlimited = false, value = (string?)null } },
        });
        Assert.Equal(HttpStatusCode.Created, plan.Status);

        Actor supplier = await _kit.RegisterSupplierAsync();
        Guid supplierId = supplier.SupplierId!.Value;
        using IServiceScope scope = factory.Services.CreateScope();
        EntitlementService entitlements = scope.ServiceProvider.GetRequiredService<EntitlementService>();

        // No subscription at all.
        AppException none = await Assert.ThrowsAsync<AppException>(
            () => entitlements.RequireActiveSubscriptionAsync(supplierId, CancellationToken.None));
        Assert.Equal(ErrorCodes.NoActiveSubscription, none.Code);

        // Subscribed but unpaid still grants nothing.
        SubscriptionResponse subscription = await SubscribeAsync(supplier, code);
        none = await Assert.ThrowsAsync<AppException>(
            () => entitlements.RequireQuotaAsync(supplierId, "productUploads", 0, CancellationToken.None));
        Assert.Equal(ErrorCodes.NoActiveSubscription, none.Code);

        // Paid: under the quota passes, at it is refused, an unlisted quota is a limit of zero.
        ApiResult settled = await PostAsync(
            finance, $"{ApiBase}/admin/invoices/{subscription.Invoices[0].Id}/mark-paid", new { reason = "Bank transfer received" });
        Assert.Equal(HttpStatusCode.OK, settled.Status);

        await entitlements.RequireQuotaAsync(supplierId, "productUploads", 1, CancellationToken.None);
        AppException full = await Assert.ThrowsAsync<AppException>(
            () => entitlements.RequireQuotaAsync(supplierId, "productUploads", 2, CancellationToken.None));
        Assert.Equal(ErrorCodes.EntitlementLimitReached, full.Code);
        AppException unlisted = await Assert.ThrowsAsync<AppException>(
            () => entitlements.RequireQuotaAsync(supplierId, "directCalls", 0, CancellationToken.None));
        Assert.Equal(ErrorCodes.EntitlementLimitReached, unlisted.Code);
    }

    // ---- Helpers ----------------------------------------------------------

    private static async Task<SubscriptionResponse> SubscribeAsync(Actor supplier, string planCode)
    {
        ApiResult created = await PostAsync(supplier, $"{ApiBase}/suppliers/me/subscription", new { planCode });
        Assert.Equal(HttpStatusCode.Created, created.Status);
        return created.As<SubscriptionResponse>();
    }

    private static async Task<SubscriptionResponse> ReadSubscriptionAsync(Actor supplier)
    {
        ApiResult read = await GetAsync(supplier, $"{ApiBase}/suppliers/me/subscription");
        return read.As<SupplierSubscriptionResponse>().Subscription
            ?? throw new InvalidOperationException("The supplier has no subscription.");
    }

    private static async Task<PaymentResponse> InitiatePaymentAsync(Actor supplier, string subscriptionId)
    {
        ApiResult result = await PostAsync(supplier, $"{ApiBase}/subscriptions/{subscriptionId}/payments", new { });
        Assert.Equal(HttpStatusCode.Created, result.Status);
        return result.As<PaymentResponse>();
    }

    private static string Sign(string secret, long unixSeconds, string body)
        => WebhookSignature.Sign(secret, DateTimeOffset.FromUnixTimeSeconds(unixSeconds), Encoding.UTF8.GetBytes(body));

    private static string SucceededBody(PaymentResponse payment) => SignedBody(payment, "payment.succeeded");

    /// <summary>The raw JSON a provider would post for <paramref name="payment"/> (compact, so the signed bytes are exactly what is sent).</summary>
    private static string SignedBody(PaymentResponse payment, string eventName, decimal? amount = null)
        => $$"""{"providerRef":"{{payment.ProviderRef}}","event":"{{eventName}}","amount":{{(amount ?? payment.Amount).ToString(System.Globalization.CultureInfo.InvariantCulture)}},"currency":"{{payment.Currency}}"}""";

    /// <summary>
    /// Delivers a webhook as the provider would: anonymous (no cookie, no CSRF
    /// token — a provider holds neither), signed over the exact raw body, with its
    /// own delivery key.
    /// </summary>
    private async Task<ApiResult> SendWebhookAsync(
        string body, string? signatureHeader = null, string? idempotencyKey = null, string? route = null)
    {
        using HttpClient client = AntiforgeryTestSupport.HttpsClient(factory);
        using HttpRequestMessage request = new(HttpMethod.Post, route ?? WebhookRoute)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string signature = signatureHeader ?? Sign(StockpileApiFactory.PaymentWebhookSecret, now, body);
        if (signature.Length > 0)
        {
            request.Headers.Add(WebhookSignature.HeaderName, signature);
        }

        string key = idempotencyKey ?? Guid.NewGuid().ToString();
        if (key.Length > 0)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        using HttpResponseMessage response = await client.SendAsync(request);
        return new ApiResult(
            response.StatusCode,
            await response.Content.ReadAsStringAsync(),
            response.Headers.Contains("Idempotency-Replayed"));
    }
}

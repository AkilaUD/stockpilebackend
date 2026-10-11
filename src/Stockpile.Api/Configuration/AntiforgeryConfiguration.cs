using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Stockpile.Api.Filters;

namespace Stockpile.Api.Configuration;

/// <summary>
/// Task 1.35 — antiforgery on every unsafe verb (security-test-matrix C1–C5,
/// threat-model §13.5, architecture §8.1). The SPA reads the double-submit
/// cookie (HttpOnly=false by design — it must echo its claim) and sends it back
/// as the <c>X-CSRF-TOKEN</c> header on every POST/PUT/PATCH/DELETE. The
/// scheme is driven by the same <c>Authentication:RequireHttps</c> knob as the
/// session cookie, so both cookies are Always-Secure in Production (enforced
/// at the authentication gate; re-checked here so the antiforgery cookie can
/// never be served without Secure on its own). A violation — missing, stale,
/// or foreign token, or an Origin that does not match the request's own
/// scheme+host — answers 400 + ANTIFORGERY_FAILED via the api.md §1.4 envelope
/// (never the framework's bare 400). Antiforgery runs before model binding and
/// before the action filters; GET/HEAD/OPTIONS/TRACE stay unvalidated by the
/// framework's own safe-verb rule, and 405-for-GET-on-a-write-route (C5) comes
/// from routing itself.
/// </summary>
public static class AntiforgeryConfiguration
{
    /// <summary>Header that must carry the double-submit token (api.md §1.1).</summary>
    public const string HeaderName = "X-CSRF-TOKEN";

    /// <summary>Double-submit cookie (architecture §8.1 "Antiforgery" row).</summary>
    public const string CookieName = ".Stockpile.Antiforgery";

    /// <summary>Form-field fallback used only by non-SPA (HTML form) clients.</summary>
    public const string FormFieldName = "__RequestVerificationToken";

    public static void AddStockpileAntiforgery(
        IServiceCollection services, IConfiguration configuration, string environmentName)
    {
        bool requireHttps = configuration.GetValue(AuthenticationConfiguration.RequireHttpsKey, false);
        bool isProduction =
            string.Equals(environmentName, "Production", StringComparison.OrdinalIgnoreCase);
        if (isProduction && !requireHttps)
        {
            // The authentication gate throws first (same key), but this keeps
            // the antiforgery cookie's own guarantee self-contained.
            throw new InvalidOperationException(
                $"'{AuthenticationConfiguration.RequireHttpsKey}' must be true in Production: the " +
                $"antiforgery double-submit cookie must carry the Secure attribute too " +
                $"(security-test-matrix C1-C5, architecture §8.1).");
        }

        services.AddAntiforgery(options =>
        {
            options.HeaderName = HeaderName;
            options.FormFieldName = FormFieldName;
            options.Cookie.Name = CookieName;
            // Double-submit: the SPA must be able to read the cookie to echo its
            // claim as the header. HttpOnly would defeat the whole pattern.
            options.Cookie.HttpOnly = false;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = requireHttps
                ? CookieSecurePolicy.Always
                : CookieSecurePolicy.SameAsRequest;
            options.Cookie.Path = "/";
        });

        // Plan 1.35 / C1-C4: validate EVERY unsafe verb, authenticated or not —
        // a cross-site POST cannot hide behind an anonymous 422. The filter
        // chain runs after CORS (preflights are safe verbs and never reach it)
        // and before the action filters (validation, idempotency).
        services.Configure<MvcOptions>(options => options.Filters.Add<AntiforgeryEnvelopeFilter>());
    }
}

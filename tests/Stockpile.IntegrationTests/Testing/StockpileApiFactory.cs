using FluentValidation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Stockpile.Application.Outbox;
using Stockpile.IntegrationTests.Outbox;

namespace Stockpile.IntegrationTests.Testing;

/// <summary>
/// Boots the real API pipeline (correlation → request logging → exception
/// handler → controllers) against Stockpile_Test.
/// <para>
/// Program reads its connection string eagerly (before the host builds), and
/// WebApplicationFactory's <c>ConfigureAppConfiguration</c> callbacks are
/// applied too late to be seen there — so the overrides go in as environment
/// variables, the one source every configuration builder reads from the very
/// start. No appsettings.Testing.json exists, so these variables are the only
/// route to a connection string: a test can never reach Stockpile_Dev
/// (architecture §9.2, shared-machine rule).
/// </para>
/// </summary>
public sealed class StockpileApiFactory : WebApplicationFactory<Program>
{
    static StockpileApiFactory()
    {
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__Stockpile",
            "Server=(localdb)\\MSSQLLocalDB;Database=Stockpile_Test;Integrated Security=true;TrustServerCertificate=true");
        Environment.SetEnvironmentVariable("Database__AutoMigrate", "true");

        // One allowlisted origin so the CORS tests can assert both sides of
        // the policy (C6 unlisted vs C8 listed); everything else stays denied.
        Environment.SetEnvironmentVariable("Cors__AllowedOrigins__0", "https://allowed.test");

        // Cookie auth (task 1.34): force Secure on the session cookie so the
        // A14 flag assertion can be made over the test host's plain HTTP.
        Environment.SetEnvironmentVariable("Authentication__RequireHttps", "true");

        // Testing is a Development-shaped surface for the integration suite:
        // the versioned OpenAPI document (1.31) is served so its tests can
        // assert the real pipeline output.
        Environment.SetEnvironmentVariable("OpenApi__Enabled", "true");

        // Task 1.37 (A6): a short reset-token TTL lets the expiry test drive the
        // REAL row expiry over HTTP (mint → wait past the TTL → complete).
        // 10 seconds is comfortably longer than the sub-second gaps
        // every other reset test keeps between minting and using a token.
        Environment.SetEnvironmentVariable("Auth__PasswordResetTokenMinutes", "0.17");

        // Task 1.38 (A11): the same sub-minute window for verification links, so
        // the email-verification expiry test drives the real EmailVerificationToken
        // row expiry; every other verification test mints and verifies in
        // sub-second gaps.
        Environment.SetEnvironmentVariable("Auth__EmailVerificationTokenMinutes", "0.17");

        // Task 1.39 (A1): a sub-minute base lets the episode-growth test drive the
        // REAL exponential window over HTTP (5 failures → episode 1 → wait past the
        // window → a 6th failure → episode 2, whose window is ~2× base). Other
        // lockout tests assert the 403 within the first window, milliseconds later.
        Environment.SetEnvironmentVariable("Auth__LockoutBaseMinutes", "0.17");

        // Task 1.40 (A8/A9): the same sub-minute window for refresh tokens, so
        // the refresh-expiry test drives the REAL RefreshSession row expiry over
        // HTTP (mint → wait past the TTL → refresh answers 401); every other
        // refresh test rotates in sub-second gaps.
        Environment.SetEnvironmentVariable("Auth__RefreshTokenMinutes", "0.17");
    }

    /// <inheritdoc />
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Same value as the static ctor — declarative for readers of this
        // method; the env var covers the early read, this the late ones.
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            // MVC only discovers controllers from application parts; the
            // test-only endpoints live in this assembly.
            services.AddControllers()
                .AddApplicationPart(typeof(TestOnlyController).Assembly);

            // Validators from Application are registered by Program; the
            // test-request validator registers alongside them.
            services.AddScoped<IValidator<TestOnlyRequest>, TestOnlyRequestValidator>();

            // Outbox consumers used by OutboxProcessorTests (task 1.28): each
            // matches only its own event types, so the rest of the suite is
            // unaffected. Recording adds deliveries to a static bag; Failing
            // throws on test.fail.* rows to drive the backoff/dead-letter path.
            services.AddScoped<IOutboxConsumer, RecordingTestEventHandler>();
            services.AddScoped<IOutboxConsumer, FailingTestEventHandler>();
        });
    }
}

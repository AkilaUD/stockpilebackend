using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Asp.Versioning;
using FluentValidation;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Formatting.Json;
using Stockpile.Api.Configuration;
using Stockpile.Api.Errors;
using Stockpile.Api.Filters;
using Stockpile.Api.Health;
using Stockpile.Api.Idempotency;
using Stockpile.Api.Logging;
using Stockpile.Api.Middleware;
using Stockpile.Api.Persistence;
using Stockpile.Api.Audit;
using Stockpile.Api.RateLimiting;
using Stockpile.Application.Audit;
using Stockpile.Application.Authentication;
using Stockpile.Application.Catalogue;
using Stockpile.Application.Common.Errors;
using Stockpile.Application.Organizations;
using Stockpile.Contracts.Errors;
using Stockpile.Infrastructure.Audit;
using Stockpile.Infrastructure.Configuration;
using Stockpile.Infrastructure.Outbox;
using Stockpile.Infrastructure.Persistence;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((context, services, configuration) =>
    {
        // Levels live in appsettings ("Serilog" section); the sink is chosen
        // here in code so that every environment provably has a destination —
        // a config-only sink silently drops every event when the section is
        // missing or an environment has no settings file (the state before
        // task 1.23: an API that logged nothing at all).
        configuration
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("service", "stockpile-api");

        // Task 1.23 (Z4) — must come after FromLogContext so it also sees
        // scope-pushed properties (correlation id, request enrichment).
        configuration.Enrich.With(new SensitivePropertyEnricher());

        // architecture §14 — "JSON sink in prod, console in dev". QA is treated
        // as production-shaped: machine-readable logs for pipeline ingestion.
        if (context.HostingEnvironment.IsProduction()
            || context.HostingEnvironment.IsEnvironment("Qa"))
        {
            configuration.WriteTo.Console(new JsonFormatter());
        }
        else
        {
            configuration.WriteTo.Console(
                outputTemplate:
                "{Timestamp:HH:mm:ss.fff} {Level:u3} {Message:lj} {Properties:j}{NewLine}{Exception}");
        }
    });

    // ---- Controllers / serialization -------------------------------------
    // camelCase on the wire, enum-as-string (state machines are enums, §7).
    builder.Services.AddControllers(o =>
    {
        // Task 1.21 — FluentValidation for every action argument; failures
        // answer 422 with fieldErrors before the action runs.
        o.Filters.Add<ValidationActionFilter>();

        // Task 1.27 — critical writes marked [Idempotent] get the
        // Idempotency-Key guard (api.md §1.7, architecture §9.4).
        o.Filters.Add<IdempotencyFilter>();
    }).AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());

        // api.md §1.4 — optional envelope members are omitted, never null.
        o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    });

    // [ApiController] model-binding failures (bad GUID, wrong JSON type) answer
    // 400 MALFORMED_REQUEST with fieldErrors — never the ASP.NET default error
    // body (api.md §1.4/§1.5).
    builder.Services.Configure<ApiBehaviorOptions>(o =>
    {
        o.InvalidModelStateResponseFactory = context =>
        {
            var fieldErrors = context.ModelState
                .Where(entry => entry.Value is not null)
                .SelectMany(entry => entry.Value!.Errors
                    .Select(error => new FieldError(
                        ErrorEnvelopeFactory.WireFieldName(entry.Key),
                        "Invalid",
                        error.ErrorMessage)))
                .ToList();

            var envelope = ErrorEnvelopeFactory.Create(
                context.HttpContext, ErrorCodes.MalformedRequest, fieldErrors: fieldErrors);

            return new ObjectResult(envelope) { StatusCode = StatusCodes.Status400BadRequest };
        };
    });

    // ---- API versioning (task 1.31, api.md §4) ---------------------------
    // Controllers declare [ApiVersion] + a versioned route
    // ("api/v{version:apiVersion}/[controller]"); unannotated controllers and
    // minimal endpoints resolve to v1 (AssumeDefaultVersionWhenUnspecified).
    // ReportApiVersions drives the api-supported-versions header on every
    // versioned response. Breaking changes ship as /api/v2 with both served
    // for ≥ 2 release cycles, after which deprecated endpoints answer with
    // Deprecation + Sunset (api.md §4) — wired when those endpoints land.
    var apiVersioning = builder.Services.AddApiVersioning(o =>
    {
        o.DefaultApiVersion = new ApiVersion(1, 0);
        o.ReportApiVersions = true;
        // Versioning is by URL segment only (/api/v1/...); an explicit reader
        // beats the default multi-reader (headers + query string) — AV0015.
        // No AssumeDefaultVersionWhenUnspecified: this is a greenfield API and
        // every real endpoint carries its version explicitly — AV0016.
        o.ApiVersionReader = new UrlSegmentApiVersionReader();
    }).AddMvc();

    // ---- OpenAPI (task 1.31, api.md §4, T4.6/H7) --------------------------
    // The versioned document is generated FROM CODE when OpenApi:Enabled is
    // true. The versioning-aware ApiExplorer replaces the default one and
    // collates endpoints into per-version documents, so the document reflects
    // exactly the API versions that exist — a versioned minimal endpoint
    // never vanishes from the spec.
    bool openApiEnabled = OpenApiConfiguration.IsEnabled(builder.Configuration);

    if (openApiEnabled)
    {
        apiVersioning
            .AddApiExplorer(options =>
            {
                // Document group names read 'v1', and {version:apiVersion}
                // route segments substitute the concrete version in the
                // generated paths (api.md §4).
                options.GroupNameFormat = "'v'VVV";
                options.SubstituteApiVersionInUrl = true;
            })
            .AddOpenApi(options => options.Document.AddDocumentTransformer((document, context, cancellationToken) =>
            {
                // Product identity for the generated spec; the versioned
                // generator supplies Info.Version from the API version set.
                document.Info.Title = "Stockpile API";
                document.Info.Description =
                    "Stockpile.lk — B2B construction-material marketplace API (api.md §4). "
                    + "Admin-only operations are excluded from this public document (api.md §61).";
                return Task.CompletedTask;
            }));
    }

    // ---- Error envelope (task 1.19) --------------------------------------
    builder.Services.AddExceptionHandler<ApiExceptionHandler>();

    // Validators live with their use cases (spec §8); the anchor type only
    // identifies the Application assembly to scan.
    builder.Services.AddValidatorsFromAssembly(typeof(AppException).Assembly);

    // ---- Health (task 1.24) ----------------------------------------------
    // Liveness runs no checks (process up = healthy). Readiness runs the
    // "ready"-tagged registrations: database now; search provider identity and
    // outbox depth join when those subsystems exist (architecture §14).
    builder.Services.AddHealthChecks()
        .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);

    // ---- OpenTelemetry (task 1.24, spec §41) -----------------------------
    // Traces: ASP.NET Core (request spans, route-tagged), HttpClient,
    // SqlClient (DB call spans — SQL text excluded, Z4). Metrics: request
    // duration by route, HttpClient, SqlClient (db.client.operation.duration =
    // the "database timing" requirement), runtime.
    //
    // Export only when an OTLP endpoint is configured (Telemetry:OtlpEndpoint
    // or the standard OTEL_EXPORTER_OTLP_ENDPOINT); with no endpoint no
    // exporter is registered and nothing leaves the process (architecture §14:
    // "exported to OTLP endpoint when configured; no-op otherwise").
    //
    // Business instruments — outbox depth, notification/webhook failure
    // counts, search latency, RFQ/quote submit failures — arrive with their
    // subsystems (1.28+). "Stockpile" is pre-registered here so those future
    // meters/sources cannot be silently dropped by a missing AddMeter/AddSource.
    string? otlpEndpoint = builder.Configuration["Telemetry:OtlpEndpoint"];
    if (string.IsNullOrWhiteSpace(otlpEndpoint))
    {
        otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
    }

    Uri? otlpUri = null;
    if (!string.IsNullOrWhiteSpace(otlpEndpoint)
        && !Uri.TryCreate(otlpEndpoint, UriKind.Absolute, out otlpUri))
    {
        throw new InvalidOperationException(
            "Telemetry OTLP endpoint must be an absolute URI (e.g. http://localhost:4317): "
            + $"'{otlpEndpoint}'.");
    }

    builder.Services.AddOpenTelemetry()
        // CreateDefault resource semantics already pick up OTEL_RESOURCE_ATTRIBUTES.
        .ConfigureResource(resource =>
            resource.AddService("stockpile-api", serviceInstanceId: Environment.MachineName))
        .WithTracing(traces =>
        {
            traces
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()

                // Z4: no configuration — SqlClient instrumentation captures no
                // query parameter values (SetDbQueryParameters defaults to
                // false) and no opt-in is made here.
                .AddSqlClientInstrumentation()
                .AddSource("Stockpile");

            if (otlpUri is not null)
            {
                traces.AddOtlpExporter(o => o.Endpoint = otlpUri);
            }
        })
        .WithMetrics(metrics =>
        {
            metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddSqlClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter("Stockpile");

            if (otlpUri is not null)
            {
                metrics.AddOtlpExporter(o => o.Endpoint = otlpUri);
            }
        });

    // ---- CORS (task 1.25, spec §13.6, threat-model §6) -------------------
    // Default deny: only origins from configuration (Cors:AllowedOrigins) are
    // ever admitted, always with credentials for cookie auth; '*' fails
    // startup (C7/X12). One origin in production means the policy has nothing
    // to allow there (architecture D2). CORS is not CSRF protection (§13.5) —
    // the antiforgery check (1D) is independent.
    var corsAllowedOrigins = CorsConfiguration.ReadAllowedOrigins(builder.Configuration);
    builder.Services.AddCors(o => o.AddPolicy(CorsConfiguration.PolicyName, policy => policy
        .SetIsOriginAllowed(corsAllowedOrigins.Contains)
        .AllowCredentials()
        .AllowAnyHeader()
        .AllowAnyMethod()
        .WithExposedHeaders(CorsConfiguration.ExposedHeaders)));

    // ---- Rate limiting (task 1.26, spec §13.7, threat-model §7) ----------
    // api.md §1.8 named policies (RateLimitPolicies) keyed by
    // (subjectId | clientIp), attached to endpoints with [EnableRateLimiting]
    // as they land. The test-only policy registers only under Testing.
    // Rejection answers 429 with the api.md §1.4 envelope and the four headers
    // (Retry-After, X-RateLimit-Limit/-Remaining/-Reset). Clusters of
    // rejections on sensitive flows raise a burst signal through
    // RateLimitBurstMonitor, persisted as a RateLimitHit security event (1.29,
    // Z4). CORS sits above this middleware, so even 429s carry
    // Access-Control-Allow-Origin for allowlisted origins.
    var rateLimitBurstMonitor = new RateLimitBurstMonitor(
        burstThreshold: 3, burstWindow: TimeSpan.FromSeconds(60));
    builder.Services.AddSingleton(rateLimitBurstMonitor);

    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        // Headers first (they exist even when the body writer fails), then the
        // envelope, then burst bookkeeping for sensitive policies. No exception
        // may escape — the middleware is below the exception handler, so a
        // throw here would still answer 500, but the 429 contract is cheaper
        // to keep by never throwing.
        options.OnRejected = async (rejectionContext, cancellationToken) =>
        {
            var httpContext = rejectionContext.HttpContext;
            // OnRejectedContext carries no policy name, so it is read from the
            // endpoint metadata the middleware dispatched on.
            var policy = httpContext.GetEndpoint()
                ?.Metadata.GetMetadata<EnableRateLimitingAttribute>()
                ?.PolicyName
                ?? "unknown";
            var now = TimeProvider.System.GetUtcNow();
            var nowSeconds = now.ToUnixTimeSeconds();
            var windowSeconds = (long)RateLimitPolicies.WindowOf(policy).TotalSeconds;
            var windowEnd = (nowSeconds / windowSeconds + 1) * windowSeconds;

            httpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            httpContext.Response.Headers["Retry-After"] =
                Math.Max(1, windowEnd - nowSeconds).ToString(CultureInfo.InvariantCulture);
            httpContext.Response.Headers["X-RateLimit-Limit"] =
                RateLimitPolicies.PermitLimitOf(policy).ToString(CultureInfo.InvariantCulture);
            httpContext.Response.Headers["X-RateLimit-Remaining"] = "0";
            httpContext.Response.Headers["X-RateLimit-Reset"] =
                windowEnd.ToString(CultureInfo.InvariantCulture);

            var envelope = ErrorEnvelopeFactory.Create(httpContext, ErrorCodes.RateLimited);
            var jsonOptions = httpContext.RequestServices
                .GetRequiredService<IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>()
                .Value.JsonSerializerOptions;
            httpContext.Response.ContentType = "application/json; charset=utf-8";
            await httpContext.Response.WriteAsync(
                JsonSerializer.Serialize(envelope, jsonOptions), cancellationToken);

            if (RateLimitPolicies.Sensitive.Contains(policy))
            {
                rateLimitBurstMonitor.Record(policy, RateLimitPartitionKeys.For(httpContext));
                await rateLimitBurstMonitor.DrainAsync(
                    BurstSecurityEventWriter.For(httpContext.RequestServices), cancellationToken);
            }
        };

        // `RateLimiting:RelaxUnderTesting` (default true) lets the security
        // suite (A2/R1) boot a Testing host whose credential budgets stay at
        // their canonical sizes so a flood can exhaust them over HTTP.
        RateLimitPolicies.AddPolicies(
            options,
            testingOnly: builder.Environment.IsEnvironment("Testing"),
            relaxCredentialBudgets: builder.Configuration.GetValue("RateLimiting:RelaxUnderTesting", true));
    });

    // ---- Persistence (tasks 1.10/1.13/1.14) ------------------------------
    // Connection string: Development uses appsettings.Development.json (LocalDB
    // Stockpile_Dev); other environments supply it via configuration or
    // user-secrets, never a committed file. Auto-migrate is opt-in and refused
    // in Production (architecture §9.2, security-test-matrix H3).
    //
    // Task 1.32 — the Production configuration gate (H1-H3, threat-model §11):
    // in Production a missing or placeholder required secret (today: the
    // connection string) and an enabled AutoMigrate all refuse boot, with a
    // message naming the offending key. No-op in every other environment, so
    // Development and the test host are untouched.
    ProductionConfiguration.Validate(
        builder.Configuration, builder.Environment.EnvironmentName);

    string connectionString = builder.Configuration.GetConnectionString("Stockpile")
        ?? throw new InvalidOperationException(
            "Connection string 'Stockpile' is not configured. " +
            "See .env.example and appsettings.Development.json, or set it with dotnet user-secrets.");
    builder.Services.AddStockpilePersistence(connectionString);

    // ---- 1.12/1.36 Identity + authentication use cases ---------------------
    // Identity core with our stores (StockpileUserStore/RoleStore), the
    // config-driven password policy (threat-model §4) and lockout base
    // window, plus the auth use cases and their options. The cookie scheme
    // itself is wired by AuthenticationConfiguration below (1.34).
    builder.Services.AddStockpileIdentity(builder.Configuration);
    builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));
    builder.Services.Configure<PasswordPolicyOptions>(
        builder.Configuration.GetSection(PasswordPolicyOptions.SectionName));
    builder.Services.AddScoped<AuthenticationService>();

    // Task 1.44 — MFA-ready use cases: enrol mints the pending TOTP secret,
    // verify is the only writer of TwoFactorEnabled (enforcement stays off).
    builder.Services.AddScoped<MfaService>();

    // Task 1.43 — organisation use cases: create (wizard step 3), team
    // invitations (P9), the user's own address book (api.md §2.2).
    builder.Services.AddScoped<OrganizationService>();

    // Task 2E — public catalogue use cases (api.md §2.3–§2.5, §3.1): the
    // relational store pushes filters into SQL; the service owns audience
    // policy and DTO shaping. Read-only for the public surface.
    builder.Services.AddScoped<ICatalogueStore, CatalogueStore>();
    builder.Services.AddScoped<CatalogueService>();

    // ---- Data protection (task 1.33, security-test-matrix H8) -------------
    // Keys never live in the machine-local store: ResolveKeyStore pins them to
    // a file location outside the web root (configured DataProtection:KeyStore:
    // Path, else .storage/dataprotection-keys) and REFUSES to boot on a
    // machine-local or web-root path, and on a missing explicit path in
    // Production. The purpose is scoped per environment and rotation is set
    // explicitly (threat-model §12, §13.14) — required before cookie/antiforgery
    // work in 1D can rely on stable, replayable tokens.
    var keyStore = DataProtectionConfiguration.ResolveKeyStore(
        builder.Configuration, builder.Environment.EnvironmentName, builder.Environment.ContentRootPath);
    builder.Services.AddDataProtection()
        .SetApplicationName(DataProtectionConfiguration.ApplicationName(builder.Environment.EnvironmentName))
        .SetDefaultKeyLifetime(TimeSpan.FromDays(DataProtectionConfiguration.KeyLifetimeDays))
        .PersistKeysToFileSystem(keyStore);

    // ---- Cookie authentication (task 1.34, security-test-matrix A14) ------
    // Single scheme (Cookie), fixed flags: HttpOnly, SameSite=Lax, Secure per
    // Authentication:RequireHttps (refused when false in Production), sliding
    // window from Authentication:SessionMinutes (30). The handler's redirects
    // become 401/403 JSON envelopes (API contract, api.md 1.4/5). The absolute
    // 12h cap + server-side revocation join in 1.36 via LoginSession.
    AuthenticationConfiguration.AddStockpileCookieAuthentication(
        builder.Services, builder.Configuration, builder.Environment.EnvironmentName);

    // ---- Antiforgery (task 1.35, security-test-matrix C1-C5) --------------
    // Double-submit cookie (.Stockpile.Antiforgery) echoed as X-CSRF-TOKEN on
    // every unsafe verb; violations answer 400 ANTIFORGERY_FAILED envelopes.
    // Same Secure knob as the session cookie. The Origin check (C4) is
    // enforced by AntiforgeryEnvelopeFilter, not the framework: net10's
    // DefaultAntiforgery only binds the cookie+header pair. Global filter
    // registered inside the helper.
    AntiforgeryConfiguration.AddStockpileAntiforgery(
        builder.Services, builder.Configuration, builder.Environment.EnvironmentName);

    // ---- Task 1.28 outbox pipeline (architecture §10.1) --------------------
    // The API host registers the pipeline so the whole platform shares one
    // wiring; the Api itself never dispatches (Stockpile.Worker does).
    builder.Services.AddOutbox();

    // ---- Task 1.29 audit + security events (X7/X9/Z4) -----------------------
    // Rows commit atomically with the change they describe; append-only. The
    // burst monitor's RateLimitHit sink is wired inside OnRejected below.
    builder.Services.AddAuditServices();
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<ICurrentRequestContext, ApiRequestContext>();

    // ---- Frameworks added incrementally by Phase 1C/1D --------------------
    // (auth, rate limiting, idempotency, outbox, audit — see
    //  docs/implementation-plan.md §1C/§1D for the ordered checklist.)

    var app = builder.Build();

    // Task 1.36 — the breach-check hook must never silently no-op: when
    // configuration claims breach checking is enabled, the host refuses to boot
    // without an IPasswordBreachChecker registered (threat-model §4 — the
    // platform never claims checking it does not have).
    if (app.Services.GetRequiredService<IOptions<PasswordPolicyOptions>>().Value.BreachCheckEnabled
        && app.Services.GetService<IPasswordBreachChecker>() is null)
    {
        throw new InvalidOperationException(
            $"'{PasswordPolicyOptions.SectionName}:BreachCheckEnabled' is true but no "
            + "IPasswordBreachChecker is registered. Register an implementation or turn the flag off.");
    }

    // The burst monitor's ILogger comes from DI (the monitor is constructed
    // before the host exists); every later request already sees it.
    rateLimitBurstMonitor.AttachLogger(app.Services.GetRequiredService<ILoggerFactory>());

    await app.Services.MigrateDatabaseIfEnabledAsync(
        autoMigrateEnabled: builder.Configuration.GetValue<bool>("Database:AutoMigrate"),
        isProduction: app.Environment.IsProduction());

    // Permissions, transitions, roles, and geography — required platform data
    // in every environment, idempotent (implementation-plan 1.15/1.16).
    await DbSeeder.SeedAsync(app.Services);

    if (OpenApiConfiguration.IsEnabled(builder.Configuration))
    {
        // One document per API version: /openapi/v1.json, /openapi/v2.json…
        // Explicitly anonymous under the deny-by-default fallback (T6.1): the
        // document is a dev/CI artifact, never gated on a caller's ticket.
        app.MapOpenApi().AllowAnonymous().WithDocumentPerVersion();
        app.MapScalarApiReference().AllowAnonymous();
    }

    // ----1.18 Correlation id ---------------------------------------------
    // Outermost: every log line below and every error envelope share one id.
    app.UseMiddleware<CorrelationIdMiddleware>();

    // Request completion logging — deliberately ABOVE the exception handler so
    // a handled 4xx/5xx still produces the "request finished" line (the handler
    // writes the response inside this middleware's next(); had it sat above,
    // the exception would skip that line), and an exception escaping the
    // handler itself is still logged before it propagates.
    app.UseSerilogRequestLogging(o =>
    {
        o.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
            diagnosticContext.Set("RequestPath", httpContext.Request.Path);
    });

    // ---- 1.25 CORS -------------------------------------------------------
    // Above the exception handler so preflights are answered before anything
    // else and even error envelopes carry Access-Control-Allow-Origin for
    // allowlisted origins; unlisted origins get no Access-Control-* header at
    // all (C6). Fail-closed for everyone else by construction.
    app.UseCors(CorsConfiguration.PolicyName);

    // ----1.19 Error envelope ----------------------------------------------
    // Catches everything below (routing, endpoints, rate limiting, and the
    // middleware still to come: idempotency) and answers with the api.md §1.4
    // envelope — status from the code registry, never a stack trace.
    //
    // The lambda is a fallback for exceptions no IExceptionHandler claims;
    // ApiExceptionHandler runs first and claims every exception today, so this
    // exists to keep the envelope shape as the invariant even if a future
    // handler declines one (handlers run before the configured fallback).
    app.UseExceptionHandler(exceptionHandlerApp => exceptionHandlerApp.Run(async httpContext =>
    {
        var envelope = ErrorEnvelopeFactory.Create(httpContext, ErrorCodes.InternalError);
        var jsonOptions = httpContext.RequestServices
            .GetRequiredService<IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>()
            .Value.JsonSerializerOptions;
        httpContext.Response.Headers[CorrelationIdMiddleware.HeaderName] =
            CorrelationIdMiddleware.CorrelationIdOf(httpContext);
        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        httpContext.Response.ContentType = "application/json; charset=utf-8";
        await httpContext.Response.WriteAsync(
            JsonSerializer.Serialize(envelope, jsonOptions), httpContext.RequestAborted);
    }));

    // ---- 1.26 Rate limiting ----------------------------------------------
    // After CORS/exception handler so rejected requests (and their envelopes)
    // still carry CORS and correlation headers; before the endpoints so the
    // on-rejected response is written instead of the action running. Endpoints
    // without an [EnableRateLimiting] declaration are not throttled — health
    // probes stay cheap, and X13 requires every mutation action to declare one.
    app.UseRateLimiter();

    // ---- 1.34 Authentication / base Authorization --------------------------
    // After rate limiting (429s short-circuit before any ticket is read) and
    // before endpoints (the authorization middleware runs the [Authorize] /
    // policy metadata; the health MapGet handlers below are explicitly
    // anonymous by design). Task 1.41/1.42 completed the pipeline: the
    // PermissionPolicyProvider builds capability policies from permission
    // constants, the permission handler resolves grants server-side per
    // request, and a fallback policy denies any endpoint that declares no
    // metadata — the F10/X2 architecture test forbids shipping one.
    app.UseAuthentication();
    app.UseAuthorization();

    app.MapControllers();
    // Liveness = process is up (no checks run). Readiness = "ready"-tagged
    // dependencies reachable — database today (task 1.24); search provider
    // identity and outbox depth join when those subsystems exist (architecture §14).
    // Both answer on the versioned /api/v1 paths (task 1.31) with
    // api-supported-versions: 1.0 and appear in the versioned OpenAPI
    // document. They are MapGet handlers, not MapHealthChecks: the API
    // explorer only describes endpoints carrying MethodInfo metadata, so
    // RequestDelegate-backed MapHealthChecks would silently never enter the
    // spec (EndpointMetadataApiDescriptionProvider). The handler runs the
    // HealthCheckService with the same predicate + JSON writer as before.
    // A version set is required: in the 10.x model, HasApiVersion on a plain
    // minimal endpoint without NewVersionedApi/NewApiVersionSet throws.
    var healthVersionSet = app.NewApiVersionSet().HasApiVersion(new ApiVersion(1, 0)).Build();

    app.MapGet("/api/v{version:apiVersion}/health/live", async (HealthCheckService healthChecks, HttpContext context) =>
    {
        var report = await healthChecks.CheckHealthAsync(_ => false, context.RequestAborted);
        await WriteHealthJson(context, report);
    }).AllowAnonymous().WithApiVersionSet(healthVersionSet).MapToApiVersion(new ApiVersion(1, 0));

    app.MapGet("/api/v{version:apiVersion}/health/ready", async (HealthCheckService healthChecks, HttpContext context) =>
    {
        var report = await healthChecks.CheckHealthAsync(
            registration => registration.Tags.Contains("ready"), context.RequestAborted);
        await WriteHealthJson(context, report);
    }).AllowAnonymous().WithApiVersionSet(healthVersionSet).MapToApiVersion(new ApiVersion(1, 0));

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Stockpile API terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

/// <summary>
/// Exposed for <c>WebApplicationFactory&lt;Program&gt;</c> in integration and
/// security tests. Never referenced from application code.
/// </summary>
public partial class Program
{
    /// <summary>
    /// Minimal health payload. Deliberately does not include exception detail or
    /// dependency names — health endpoints are unauthenticated and must not leak
    /// infrastructure information (security-threat-model T4.5).
    /// </summary>
    internal static Task WriteHealthJson(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        var payload = JsonSerializer.Serialize(new
        {
            status = report.Status.ToString().ToLowerInvariant(),
            uptimeSeconds = (long)(DateTime.UtcNow - ProcessStart).TotalSeconds
        });
        return context.Response.WriteAsync(payload);
    }

    private static readonly DateTime ProcessStart = DateTime.UtcNow;
}

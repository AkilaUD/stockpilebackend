using Serilog;
using Stockpile.Application.Audit;
using Stockpile.Infrastructure.Audit;
using Stockpile.Infrastructure.Configuration;
using Stockpile.Infrastructure.Outbox;
using Stockpile.Infrastructure.Persistence;
using Stockpile.Worker;

var builder = Host.CreateApplicationBuilder(args);

// Bootstrap logger: if configuration itself is broken we still get output.
Serilog.Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var logger = new LoggerConfiguration()
        .ReadFrom.Configuration(builder.Configuration)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("service", "stockpile-worker");

    // Sink chosen here in code, never in config alone: a config-only sink
    // silently drops every event when the "Serilog" section is absent (the
    // API hit this before task 1.23; the Worker inherited the shape without
    // the destination). architecture §14 — JSON in prod/QA, text in dev.
    if (builder.Environment.IsProduction()
        || builder.Environment.IsEnvironment("Qa"))
    {
        logger.WriteTo.Console(new Serilog.Formatting.Json.JsonFormatter());
    }
    else
    {
        logger.WriteTo.Console(
            outputTemplate:
            "{Timestamp:HH:mm:ss.fff} {Level:u3} {Message:lj} {Properties:j}{NewLine}{Exception}");
    }

    builder.Services.AddSerilog(logger.CreateLogger(), dispose: true);

    builder.Services.AddHealthChecks();

    // Task 1.32 — Production configuration gate (H1-H3): the Worker shares the
    // manifest with the API host, so a Production Worker will not boot on a
    // missing/placeholder connection string either. No-op outside Production.
    ProductionConfiguration.Validate(
        builder.Configuration, builder.Environment.EnvironmentName);

    // Task 1.28 — the outbox (architecture §10.1). Same persistence +
    // interceptor wiring as the API; never auto-migrates: migrations are the
    // API host's job (it is the only host that may apply them in Development).
    string connectionString = builder.Configuration.GetConnectionString("Stockpile")
        ?? throw new InvalidOperationException(
            "Connection string 'Stockpile' is not configured. Set ConnectionStrings__Stockpile, or "
            + "provide appsettings with the shared Stockpile_Dev database (.env.example).");
    builder.Services.AddStockpilePersistence(connectionString);
    builder.Services.AddOutbox();

    // Task 1.29 — audit services with the host's ambient request context.
    // Worker has no HTTP context: consumers run as the system actor.
    builder.Services.AddAuditServices();
    builder.Services.AddScoped<ICurrentRequestContext, SystemRequestContext>();

    // Outbox dispatch loop; scheduled jobs and notification delivery register
    // alongside it as their phases land.
    builder.Services.AddHostedService<OutboxDispatcher>();

    var host = builder.Build();
    await host.RunAsync();
}
catch (Exception ex)
{
    Serilog.Log.Fatal(ex, "Stockpile Worker terminated unexpectedly");
    throw;
}

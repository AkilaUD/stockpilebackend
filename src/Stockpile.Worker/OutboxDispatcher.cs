using Stockpile.Application.Outbox;

namespace Stockpile.Worker;

/// <summary>
/// The outbox claim loop (architecture §10.1). Thin by design: every cycle
/// opens a fresh scope and hands <see cref="IOutboxProcessor.ProcessBatchAsync"/>
/// one batch — claim with UPDLOCK/ROWLOCK/READPAST, dispatch to registered
/// consumers, mark Processed or back off (2^n seconds + jitter, 8 attempts,
/// then DeadLetter). Polls at 5 s when idle and 1 s while work is being
/// processed; a failed cycle is logged and simply retried on the next tick.
/// </summary>
public sealed partial class OutboxDispatcher(
    IServiceScopeFactory scopeFactory,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WorkingInterval = TimeSpan.FromSeconds(1);
    private const int BatchSize = 20;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        WorkerLog.Starting(logger);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using IServiceScope scope = scopeFactory.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<IOutboxProcessor>();
                int processed = await processor.ProcessBatchAsync(BatchSize, stoppingToken);

                await Task.Delay(processed == 0 ? IdleInterval : WorkingInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                WorkerLog.CycleFailed(logger, ex);
                await Task.Delay(IdleInterval, stoppingToken);
            }
        }

        WorkerLog.Stopping(logger);
    }
}

/// <summary>
/// Source-generated structured logging. Keeps CA1848 satisfied without paying
/// source-generator complexity per call site in the hot path.
/// </summary>
internal static partial class WorkerLog
{
    [LoggerMessage(EventId = 1000, Level = LogLevel.Information,
        Message = "Outbox dispatcher started")]
    public static partial void Starting(ILogger logger);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information,
        Message = "Outbox dispatcher stopping")]
    public static partial void Stopping(ILogger logger);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Error,
        Message = "Outbox dispatch cycle failed; retrying on next tick")]
    public static partial void CycleFailed(ILogger logger, Exception exception);
}

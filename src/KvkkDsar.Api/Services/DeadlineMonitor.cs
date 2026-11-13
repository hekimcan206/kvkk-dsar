using KvkkDsar.Api.Persistence;
using KvkkDsar.Deadlines;
using Microsoft.Extensions.Options;

namespace KvkkDsar.Api.Services;

/// <summary>
/// Watches the 30-day clock of every open request. When a request enters "at risk" (7 days or fewer left) or becomes
/// overdue, the stage is stored and an audit event written once; the open-by-stage gauge feeds alerting.
/// </summary>
internal sealed partial class DeadlineMonitor(WorkflowStore store, DsarMetrics metrics, TimeProvider time, IOptions<DsarOptions> options, ILogger<DeadlineMonitor> logger)
    : BackgroundService
{
    public async Task<IReadOnlyDictionary<DeadlineStage, int>> ScanAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var counts = Enum.GetValues<DeadlineStage>().ToDictionary(s => s, _ => 0);
        foreach (var row in await store.ListAsync(openOnly: true, limit: 10_000, cancellationToken))
        {
            var stage = DeadlineCalculator.Stage(row.Deadline, now);
            counts[stage]++;
            if (stage != row.Stage && stage != DeadlineStage.OnTrack)
            {
                await store.SetStageAsync(row.Id, stage, cancellationToken);
                if (stage == DeadlineStage.Overdue)
                {
                    LogOverdue(row.Reference, row.DueOn);
                }
            }
        }

        metrics.OpenByStage(counts);
        return counts;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, options.Value.DeadlineScanSeconds)), time);
        do
        {
            try
            {
                await ScanAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogScanFailed(ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Request {Reference} is overdue (due {DueOn})")]
    private partial void LogOverdue(string reference, DateOnly dueOn);

    [LoggerMessage(Level = LogLevel.Error, Message = "Deadline scan failed")]
    private partial void LogScanFailed(Exception exception);
}

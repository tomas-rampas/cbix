namespace Cbix.Worker;

/// <summary>
/// Placeholder background service. Later stories replace the loop body with the
/// document-extraction workflow run.
/// </summary>
/// <remarks>
/// Internal on purpose: nothing outside this assembly constructs it (the host resolves
/// it through <c>AddHostedService</c>), so it is not public API and owes no XML docs
/// to consumers.
/// </remarks>
internal sealed class Worker(ILogger<Worker> logger, TimeProvider timeProvider) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (logger.IsEnabled(LogLevel.Information))
                {
                    // UTC via the injected clock, never DateTime.Now: CBIX is an
                    // audit-grade pipeline, so every timestamp it records must be
                    // unambiguous across machines, and every time-dependent code path
                    // must be testable with a fake clock. This template is copied.
                    logger.LogInformation("Worker running at: {Time:o}", timeProvider.GetUtcNow());
                }

                await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown: the host cancels the stopping token. Swallowing it
            // here keeps the process exit code at zero.
        }
    }
}

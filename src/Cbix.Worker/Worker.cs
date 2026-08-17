namespace Cbix.Worker;

/// <summary>
/// Placeholder background service.
/// </summary>
/// <remarks>
/// S02-01 (or its Sprint-02 sibling) owns replacing this loop with the SQL-table work-queue drain
/// driving <c>CbixWorkflowFactory</c> runs - recorded in <c>plan/sprint-02</c> and the roadmap
/// coverage table. Naming the owner rather than "later stories" is the difference between a
/// placeholder somebody is accountable for and one that survives by nobody noticing it.
/// </remarks>
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
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected on shutdown: the host cancels the stopping token. Swallowing it
            // here keeps the process exit code at zero.
            //
            // The filter is load-bearing. An unfiltered catch also swallows cancellation the
            // host did NOT ask for - a future per-operation timeout token, or a library that
            // cancels internally - and the loop would exit silently, leaving a running process
            // that has stopped doing any work. Anything other than our own shutdown must
            // propagate and fail the host loudly.
        }
    }
}

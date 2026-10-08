namespace Kiberone.Infrastructure;

// Presence must not wait for file copies, package downloads or the workspace lock.
// The shared Student HttpClient carries X-Client-Id and X-Client-Secret.
internal sealed class StudentPresenceLease : IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime;
    private readonly Task loop;

    public StudentPresenceLease(HttpClient http, CancellationToken cancellationToken)
    {
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        loop = RunAsync(http, lifetime.Token);
    }

    private static async Task RunAsync(HttpClient http, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(4));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(3));
                try
                {
                    using var response = await http.PostAsync("/presence", null, deadline.Token);
                    // Older Tutors may not expose this endpoint. The regular heartbeat remains active.
                }
                catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
                {
                    if (cancellationToken.IsCancellationRequested) return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        await loop;
        lifetime.Dispose();
    }
}

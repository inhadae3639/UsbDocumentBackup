using System.Diagnostics;

namespace DocumentBackup.Backup;

/// <summary>
/// Paces reads so a backup running during a presentation does not saturate the USB bus.
/// Every path that reads a source document goes through one of these; there is deliberately no
/// unthrottled read path (a separate hashing pass, for example) that could bypass the limit.
/// </summary>
public sealed class RateLimiter
{
    private readonly long _bytesPerSecond;
    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan _nextSlot = TimeSpan.Zero;

    /// <param name="bytesPerSecond">Zero or less disables throttling.</param>
    public RateLimiter(long bytesPerSecond) => _bytesPerSecond = bytesPerSecond;

    public static RateLimiter Unlimited { get; } = new(0);

    public bool IsEnabled => _bytesPerSecond > 0;

    public async ValueTask ConsumeAsync(int bytes, CancellationToken cancellationToken)
    {
        if (_bytesPerSecond <= 0 || bytes <= 0)
        {
            return;
        }

        TimeSpan delay;
        lock (_gate)
        {
            var now = _clock.Elapsed;
            if (_nextSlot < now)
            {
                _nextSlot = now;
            }

            delay = _nextSlot - now;
            _nextSlot += TimeSpan.FromSeconds((double)bytes / _bytesPerSecond);
        }

        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }
}

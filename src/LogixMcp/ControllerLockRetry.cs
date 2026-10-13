// ── ControllerLockRetry — one bounded retry for the controller lock mnemonic ──
//
// RxCsE_ASA_BLOCKING_HIGHER_EXCL_LOCK means another client (or Studio) holds an exclusive lock for a few
// seconds. The call is retried at most MaxTries times in all, waiting RetryDelay between tries
// (2 waits x 5 s = about 10 s), and only for that mnemonic. Any other failure is thrown at once.
// Core assembly: no Rockwell types, so the policy is testable with a fake.

/// <summary>How many retries a <see cref="ControllerLockRetry"/> run used.</summary>
public sealed class ControllerLockRetryState
{
    /// <summary>Number of retries made (tries after the first). 0 when the first try decided the outcome.</summary>
    public int Retries { get; set; }
}

/// <summary>Bounded retry for a controller-lock failure.</summary>
public static class ControllerLockRetry
{
    /// <summary>Most tries in all, the first included.</summary>
    public const int MaxTries = 3;

    /// <summary>Default wait between tries: 2 waits of 5 s are about 10 s.</summary>
    public static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Runs <paramref name="operation"/>, retrying while it throws an exception for which
    /// <paramref name="isControllerLocked"/> is true, up to <see cref="MaxTries"/> tries. The retries made are
    /// written to <paramref name="state"/>. Any other exception, and the last locked one, is rethrown.
    /// </summary>
    /// <param name="operation">The call to make; receives the zero-based try number.</param>
    /// <param name="isControllerLocked">True when the failure is the controller-lock mnemonic.</param>
    /// <param name="retryDelay">Wait between tries.</param>
    /// <param name="state">Receives the retry count.</param>
    /// <param name="ct">Cancellation token for the waits.</param>
    /// <param name="delay">Test seam replacing <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
    public static async Task RunAsync(
        Func<int, Task> operation,
        Func<Exception, bool> isControllerLocked,
        TimeSpan retryDelay,
        ControllerLockRetryState state,
        CancellationToken ct,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await operation(attempt).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException
                                       && attempt + 1 < MaxTries
                                       && isControllerLocked(ex))
            {
                state.Retries = attempt + 1;
            }
            await (delay ?? Task.Delay)(retryDelay, ct).ConfigureAwait(false);
        }
    }
}

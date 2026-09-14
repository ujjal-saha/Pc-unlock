using System.Collections.Concurrent;

namespace PhoneUnlockService.Security;

/// <summary>
/// Defense-in-depth against a signature being replayed: even though the
/// phone signs the nonce and the nonce is single-use by construction (the
/// Credential Provider generates a fresh random one per unlock attempt),
/// the service independently tracks which nonces it has already accepted
/// an APPROVED result for, and refuses to honor the same nonce twice.
///
/// This matters because the relay server or the WebSocket transport is
/// not fully trusted here - if either replayed an old (nonce, signature)
/// pair, this guard is what stops it from producing a second unlock.
/// </summary>
public sealed class ReplayGuard
{
    // nonce -> UTC time it was consumed. Nonces are single-use for the
    // life of the service process; a lightweight time-based eviction keeps
    // this from growing unbounded on a long-running service.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _consumed = new();
    private static readonly TimeSpan RetentionWindow = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Returns true and marks the nonce consumed if it hasn't been seen
    /// before; returns false if it has (i.e., this is a replay - reject).
    /// </summary>
    public bool TryConsume(string nonce)
    {
        Prune();
        return _consumed.TryAdd(nonce, DateTimeOffset.UtcNow);
    }

    private void Prune()
    {
        var cutoff = DateTimeOffset.UtcNow - RetentionWindow;
        foreach (var kvp in _consumed)
        {
            if (kvp.Value < cutoff)
                _consumed.TryRemove(kvp.Key, out _);
        }
    }
}

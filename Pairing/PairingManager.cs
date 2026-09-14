using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PhoneUnlockService.Relay;
using PhoneUnlockService.Security;

namespace PhoneUnlockService.Pairing;

/// <summary>
/// Handles pairing: you (or a small CLI/UI wrapper not built yet - see
/// handoff note section 4, "Pairing/registration flow on the PC side")
/// call BeginPairing() to get a short code, show it (and/or encode it in
/// a QR payload) to the phone, and the phone submits it back through the
/// relay as a PairingOfferMessage (android_agent_prompt.md, "Pairing
/// flow" section). Whichever offer arrives with a matching, unexpired
/// code gets its public key trusted.
///
/// Deliberately generous with logging here since pairing is the one place
/// where a mistake (trusting the wrong key) silently weakens the whole
/// system for every future unlock.
/// </summary>
public sealed class PairingManager : IHostedService
{
    private sealed record PendingPairing(string Code, DateTimeOffset ExpiresAtUtc);

    private static readonly TimeSpan PairingWindow = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, PendingPairing> _pendingByCode = new();
    private readonly RelayClient _relayClient;
    private readonly CredentialStore _credentialStore;
    private readonly ILogger<PairingManager> _log;
    private TaskCompletionSource<PairedDevice>? _pairingCompletion;

    public PairingManager(RelayClient relayClient, CredentialStore credentialStore, ILogger<PairingManager> log)
    {
        _relayClient = relayClient;
        _credentialStore = credentialStore;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _relayClient.PairingOfferReceived += OnPairingOfferReceived;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _relayClient.PairingOfferReceived -= OnPairingOfferReceived;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Generates a new 6-digit pairing code valid for 5 minutes. Show this
    /// (plus a QR payload containing pc_id + code + relay URL, per
    /// android_agent_prompt.md's "Add a PC" screen) to the phone being
    /// paired. Not yet wired to any UI - call this from whatever pairing
    /// CLI/UI you build next.
    /// </summary>
    public string BeginPairing()
    {
        if (!_relayClient.IsConnected)
            throw new InvalidOperationException("Relay is not connected; no pairing code was opened.");

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        _pairingCompletion = new TaskCompletionSource<PairedDevice>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingByCode[code] = new PendingPairing(code, DateTimeOffset.UtcNow + PairingWindow);

        try
        {
            var raw = $"{{\"type\":\"pairing_open\",\"code\":\"{code}\"}}";
            _relayClient.SendRawAsync(raw, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _pendingByCode.TryRemove(code, out _);
            throw new InvalidOperationException("Could not open the pairing code on the relay.", ex);
        }

        _log.LogInformation("Pairing window opened, code expires at {Expiry}", _pendingByCode[code].ExpiresAtUtc);
        return code;
    }

    public async Task<PairedDevice?> WaitForPairingAsync(TimeSpan timeout)
    {
        var completion = _pairingCompletion;
        if (completion is null)
            return null;

        var completed = await Task.WhenAny(completion.Task, Task.Delay(timeout));
        return completed == completion.Task ? await completion.Task : null;
    }

    private void OnPairingOfferReceived(PairingOfferMessage offer)
    {
        if (!_pendingByCode.TryGetValue(offer.PairingCode, out var pending))
        {
            _log.LogWarning("Ignoring pairing offer with unknown/inactive code");
            return;
        }

        if (DateTimeOffset.UtcNow > pending.ExpiresAtUtc)
        {
            _log.LogWarning("Ignoring pairing offer with expired code");
            _pendingByCode.TryRemove(offer.PairingCode, out _);
            return;
        }

        // Sanity-check the key actually parses as an EC public key before
        // trusting it - fail loudly rather than store garbage that would
        // only be discovered the next time someone is locked out.
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(offer.PublicKeyBase64), out _);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Pairing offer's public key failed to parse, rejecting");
            return;
        }

        _pendingByCode.TryRemove(offer.PairingCode, out _);

        var device = new PairedDevice(
            offer.DeviceId,
            offer.DeviceDisplayName,
            offer.PublicKeyBase64,
            DateTimeOffset.UtcNow);

        try
        {
            _credentialStore.AddDeviceAsync(device).GetAwaiter().GetResult();
            _pairingCompletion?.TrySetResult(device);
            _log.LogInformation("Paired new device {DeviceId} ({Name})", device.DeviceId, device.DisplayName);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to persist paired device {DeviceId}", device.DeviceId);
        }
    }
}

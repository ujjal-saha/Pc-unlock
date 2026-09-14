using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PhoneUnlockService.Security;

namespace PhoneUnlockService.Relay;

/// <summary>
/// Owns the single outbound WebSocket connection to the relay server and
/// correlates outgoing unlock requests with the eventual phone response.
///
/// The relay server described in the handoff note doesn't exist yet, so
/// this class can't be fully exercised end to end - but the contract in
/// RelayMessages.cs is deliberately simple (a dumb authenticated
/// forwarder) so that whichever of you writes the relay next has a fixed
/// target to build against.
/// </summary>
public sealed class RelayClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly RelayOptions _options;
    private readonly CredentialStore _credentialStore;
    private readonly ILogger<RelayClient> _log;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<UnlockResponseMessage>> _pending = new();

    private ClientWebSocket? _socket;
    private string? _connectedPcId;
    private int _isReady;

    public event Action<PairingOfferMessage>? PairingOfferReceived;

    public RelayClient(IOptions<RelayOptions> options, CredentialStore credentialStore, ILogger<RelayClient> log)
    {
        _options = options.Value;
        _credentialStore = credentialStore;
        _log = log;

        if (!_options.Url.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
        {
            // Hard fail rather than silently downgrading security - matches
            // the same requirement called out for the phone app.
            throw new InvalidOperationException(
                $"Relay URL must use wss:// (TLS). Configured URL was: {_options.Url}");
        }
    }

    public bool IsConnected => Volatile.Read(ref _isReady) == 1 && _socket is { State: WebSocketState.Open };

    /// <summary>
    /// Connects, sends the identifying "hello", then pumps incoming
    /// messages until the connection drops or cancellation is requested.
    /// Intended to be run in a loop by RelayClientHost, which handles
    /// reconnect/backoff.
    /// </summary>
    public async Task ConnectAndPumpAsync(CancellationToken ct)
    {
        Volatile.Write(ref _isReady, 0);
        var pcId = await _credentialStore.GetPcIdAsync();
        Volatile.Write(ref _connectedPcId, null);
        var relayUri = BuildRelayUri(_options.Url, pcId);

        using var socket = new ClientWebSocket();
        _socket = socket;

        if (!string.IsNullOrWhiteSpace(_options.AuthToken))
        {
            socket.Options.SetRequestHeader("Authorization", $"Bearer {_options.AuthToken}");
        }

        _log.LogInformation("Connecting to relay {Url} as PC {PcId}", relayUri, pcId);
        await socket.ConnectAsync(relayUri, ct);

        await SendAsync(new HelloMessage { PcId = pcId, AuthToken = _options.AuthToken }, ct);
        Volatile.Write(ref _connectedPcId, pcId);
        Volatile.Write(ref _isReady, 1);
        _log.LogInformation("Relay connection established as PC {PcId}", pcId);

        var buffer = new byte[16 * 1024];
        var messageBuilder = new StringBuilder();

        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            messageBuilder.Clear();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _log.LogWarning("Relay closed the connection: {Status} {Description}",
                        result.CloseStatus, result.CloseStatusDescription);
                    return;
                }
                messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            } while (!result.EndOfMessage);

            Dispatch(messageBuilder.ToString());
        }

        Volatile.Write(ref _isReady, 0);
        Volatile.Write(ref _connectedPcId, null);
    }

    private void Dispatch(string json)
    {
        RelayMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<RelayMessage>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "Ignoring malformed relay message");
            return;
        }

        switch (message)
        {
            case UnlockResponseMessage resp:
                if (_pending.TryRemove(resp.Nonce, out var tcs))
                {
                    tcs.TrySetResult(resp);
                }
                else
                {
                    // Either a duplicate/late response for a request we
                    // already timed out, or (more concerning) a response
                    // to a nonce we never issued - either way, dropping it
                    // is the safe move. Never fabricate a pending entry.
                    _log.LogWarning("Received unlock_response for unknown/expired nonce, discarding");
                }
                break;

            case PairingOfferMessage offer:
                PairingOfferReceived?.Invoke(offer);
                break;

            case RelayAckMessage:
                break;

            default:
                _log.LogWarning("Unhandled relay message type: {Json}", json);
                break;
        }
    }

    /// <summary>
    /// Sends an unlock_request for the given nonce and awaits the phone's
    /// response (relayed back as an unlock_response), or returns null on
    /// timeout. Does NOT verify the signature - that's the caller's job
    /// (PipeServerHost), since this class shouldn't need to know about
    /// paired-device key material.
    /// </summary>
    public async Task<UnlockResponseMessage?> RequestApprovalAsync(
        string pcName, string nonce, TimeSpan timeout, CancellationToken ct)
    {
        if (!IsConnected)
        {
            _log.LogWarning("RequestApprovalAsync called while not connected to relay");
            return null;
        }

        var pcId = await _credentialStore.GetPcIdAsync();
        var connectedPcId = Volatile.Read(ref _connectedPcId);
        if (!string.Equals(pcId, connectedPcId, StringComparison.Ordinal))
        {
            _log.LogError("PC ID changed while connected; refusing unlock request until relay reconnects");
            return null;
        }

        var tcs = new TaskCompletionSource<UnlockResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(nonce, tcs))
        {
            // A nonce should never collide across in-flight requests - the
            // Credential Provider generates a fresh random one per attempt.
            // Treat a collision as a hard failure rather than risk mixing
            // up two unlock attempts.
            _log.LogError("Nonce collision on in-flight request, refusing to proceed");
            return null;
        }

        try
        {
            await SendAsync(new UnlockRequestMessage { PcId = pcId, PcName = pcName, Nonce = nonce }, ct);

            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            using (linked.Token.Register(() => tcs.TrySetCanceled()))
            {
                try
                {
                    return await tcs.Task;
                }
                catch (TaskCanceledException)
                {
                    return null; // timeout or shutdown
                }
            }
        }
        finally
        {
            _pending.TryRemove(nonce, out _);
        }
    }

    public async Task SendRawAsync(string json, CancellationToken ct)
    {
        if (_socket is null) throw new InvalidOperationException("Not connected");
        var bytes = Encoding.UTF8.GetBytes(json);
        await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    private async Task SendAsync(RelayMessage message, CancellationToken ct)
    {
        if (_socket is null) throw new InvalidOperationException("Not connected");
        var json = JsonSerializer.Serialize<RelayMessage>(message, JsonOptions);
        Console.WriteLine($"OUTGOING JSON: {json}");
        var bytes = Encoding.UTF8.GetBytes(json);
        await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    private static Uri BuildRelayUri(string configuredUrl, string pcId)
    {
        var builder = new UriBuilder(configuredUrl);
        var queryParts = builder.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(part =>
            {
                var separator = part.IndexOf('=');
                var key = separator >= 0 ? part[..separator] : part;
                return !Uri.UnescapeDataString(key).Equals("pc_id", StringComparison.OrdinalIgnoreCase);
            })
            .ToList();

        queryParts.Add($"pc_id={Uri.EscapeDataString(pcId)}");
        builder.Query = string.Join('&', queryParts);
        return builder.Uri;
    }
}

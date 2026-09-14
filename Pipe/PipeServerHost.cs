using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PhoneUnlockService.Relay;
using PhoneUnlockService.Security;

namespace PhoneUnlockService.Pipe;

/// <summary>
/// Hosts the "\\.\pipe\PhoneUnlockPipe" named pipe that the Windows
/// Credential Provider's PipeClient.cpp connects to on every unlock
/// attempt. One connection is handled at a time by design - only one
/// active unlock attempt makes sense on a single-session lock screen.
/// </summary>
public sealed class PipeServerHost : BackgroundService
{
    public const string PipeName = "PhoneUnlockPipe"; // full name: \\.\pipe\PhoneUnlockPipe
    private static readonly TimeSpan ApprovalTimeout = TimeSpan.FromSeconds(40); // a hair under the CredProv's 45s cap

    private readonly RelayClient _relayClient;
    private readonly CredentialStore _credentialStore;
    private readonly ReplayGuard _replayGuard;
    private readonly ILogger<PipeServerHost> _log;

    public PipeServerHost(
        RelayClient relayClient,
        CredentialStore credentialStore,
        ReplayGuard replayGuard,
        ILogger<PipeServerHost> log)
    {
        _relayClient = relayClient;
        _credentialStore = credentialStore;
        _replayGuard = replayGuard;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var pipeSecurity = BuildPipeSecurity();
            _log.LogInformation("Starting named pipe server on {PipeName}", PipeName);
            Console.WriteLine($"Starting named pipe server on {PipeName}");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var server = NamedPipeServerStreamAcl.Create(
                        PipeName,
                        PipeDirection.InOut,
                        maxNumberOfServerInstances: 1,
                        PipeTransmissionMode.Message,
                        PipeOptions.Asynchronous,
                        inBufferSize: 4096,
                        outBufferSize: 4096,
                        pipeSecurity);

                    _log.LogInformation("Pipe server is now listening on {PipeName}", PipeName);
                    Console.WriteLine($"Pipe server is now listening on {PipeName}");
                    await server.WaitForConnectionAsync(stoppingToken);
                    _log.LogInformation("Named pipe client connected on {PipeName}", PipeName);
                    await HandleConnectionAsync(server, stoppingToken);

                    if (server.IsConnected)
                        server.Disconnect();
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Named pipe creation/listening failed on {PipeName}", PipeName);
                    Console.WriteLine($"Named pipe creation/listening failed on {PipeName}: {ex}");
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _log.LogCritical(ex, "Named pipe server stopped unexpectedly");
            Console.WriteLine($"Named pipe server stopped unexpectedly: {ex}");
        }
    }

    /// <summary>
    /// Grant the Credential Provider access even when LogonUI's token does
    /// not resolve the expected SYSTEM SID, while retaining the explicit
    /// SYSTEM and local Administrators rules.
    /// </summary>
    private static PipeSecurity BuildPipeSecurity()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return security;
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        var first = await PipeProtocol.ReadMessageAsync(server, ct);
        if (!string.Equals(first, "UNLOCK", StringComparison.Ordinal))
        {
            _log.LogWarning("Unexpected first pipe message: {Message}", first);
            return;
        }

        var nonce = await PipeProtocol.ReadMessageAsync(server, ct);
        if (string.IsNullOrWhiteSpace(nonce))
        {
            _log.LogWarning("Empty nonce received, rejecting");
            await TryWriteResponseAsync(server, PipeProtocol.BuildDenied("empty nonce"), ct);
            return;
        }

        _log.LogInformation("Unlock attempt started, nonce prefix {Prefix}", nonce.Length >= 8 ? nonce[..8] : nonce);

        var response = await _relayClient.RequestApprovalAsync(
            Environment.MachineName, nonce, ApprovalTimeout, ct);

        if (response is null)
        {
            await TryWriteResponseAsync(server, PipeProtocol.Timeout, ct);
            return;
        }

        if (!string.Equals(response.Decision, "approved", StringComparison.OrdinalIgnoreCase))
        {
            await TryWriteResponseAsync(server, PipeProtocol.BuildDenied("denied on phone"), ct);
            return;
        }

        if (!_replayGuard.TryConsume(nonce))
        {
            _log.LogWarning("Rejecting response for already-consumed nonce (possible replay)");
            await TryWriteResponseAsync(server, PipeProtocol.BuildDenied("replay detected"), ct);
            return;
        }

        var devices = await _credentialStore.GetDevicesAsync();
        var device = devices.FirstOrDefault(d => d.DeviceId == response.DeviceId);
        if (device is null || string.IsNullOrEmpty(response.SignatureBase64))
        {
            _log.LogWarning("Approval from unknown device id {DeviceId}, rejecting", response.DeviceId);
            await TryWriteResponseAsync(server, PipeProtocol.BuildDenied("unknown device"), ct);
            return;
        }

        var signatureBytes = Convert.FromBase64String(response.SignatureBase64);
        var publicKeyBytes = Convert.FromBase64String(device.PublicKeyBase64);
        var nonceBytes = Encoding.UTF8.GetBytes(nonce);

        var valid = NonceSignatureVerifier.Verify(nonceBytes, signatureBytes, publicKeyBytes);
        if (!valid)
        {
            _log.LogWarning("Signature verification FAILED for device {DeviceId}", device.DeviceId);
            await TryWriteResponseAsync(server, PipeProtocol.BuildDenied("signature verification failed"), ct);
            return;
        }

        var creds = await _credentialStore.GetWindowsCredentialAsync();
        if (creds is null)
        {
            _log.LogError("Signature verified but no Windows credential is stored - was pairing/setup completed?");
            await TryWriteResponseAsync(server, PipeProtocol.BuildDenied("no credential configured"), ct);
            return;
        }

        _log.LogInformation("Unlock approved and verified for device {DeviceId}", device.DeviceId);
        await TryWriteResponseAsync(server, PipeProtocol.BuildApproved(creds.Value.username, creds.Value.password), ct);
    }

    private Task<bool> TryWriteResponseAsync(
        NamedPipeServerStream server,
        string response,
        CancellationToken ct) =>
        PipeProtocol.TryWriteResponseAsync(server, response, ct, _log);
}

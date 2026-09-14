using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using System.Security.Cryptography;
using PhoneUnlockService.Pipe;
using PhoneUnlockService.Relay;
using PhoneUnlockService.Security;
using PhoneUnlockService.Pairing;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

// Run as a real Windows Service when launched by SCM; falls back to a
// console app when run interactively (handy for `dotnet run` during dev).
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "PhoneUnlockService";
});

builder.Configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);

// --- Core singletons -------------------------------------------------
// CredentialStore owns the on-disk pairing table + DPAPI-encrypted
// Windows password. It's a singleton because it serializes all reads/
// writes to that file internally (see Security/CredentialStore.cs).
builder.Services.AddSingleton<CredentialStore>();
builder.Services.AddSingleton<ReplayGuard>();
builder.Services.AddSingleton<PairingManager>();
// PairingManager is also an IHostedService (it needs Start/Stop to
// subscribe to RelayClient events) - register the same instance as a
// hosted service rather than letting the container create a second one.
builder.Services.AddHostedService(sp => sp.GetRequiredService<PairingManager>());

builder.Services.Configure<RelayOptions>(builder.Configuration.GetSection("Relay"));
builder.Services.AddSingleton<RelayClient>();

// The pipe server is the thing LogonUI's Credential Provider actually
// talks to. It depends on RelayClient (to forward nonces to the phone)
// and CredentialStore (to hand back the real password on approval).
// Skip it for the one-off `--pair` console path so pairing can be
// exercised without trying to create the SYSTEM-owned named pipe from a
// non-elevated interactive session.
var isPairingMode = args.Contains("--pair", StringComparer.OrdinalIgnoreCase);
var isUnlockTestMode = args.Contains("--test-unlock", StringComparer.OrdinalIgnoreCase);

if (!isPairingMode && !isUnlockTestMode)
{
    builder.Services.AddHostedService<PipeServerHost>();
}

builder.Services.AddHostedService<RelayClientHost>();

var host = builder.Build();

if (args.Contains("--pair", StringComparer.OrdinalIgnoreCase))
{
    await host.StartAsync();
    try
    {
        var pairingManager = host.Services.GetRequiredService<PairingManager>();
        var relayClient = host.Services.GetRequiredService<RelayClient>();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!relayClient.IsConnected && DateTime.UtcNow < deadline)
        {
            await Task.Delay(250);
        }

        string code;
        try
        {
            code = pairingManager.BeginPairing();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"PAIRING FAILED: {ex.Message}");
            return;
        }
        Console.WriteLine($"Pairing code: {code}");
        Console.WriteLine("Use this code in the Android app to complete pairing.");
        var pairedDevice = await pairingManager.WaitForPairingAsync(TimeSpan.FromMinutes(5));
        if (pairedDevice is null)
        {
            Console.WriteLine("Pairing window expired without a successful pairing.");
        }
        else
        {
            Console.WriteLine($"Pairing saved for device '{pairedDevice.DeviceId}'.");
        }
    }
    finally
    {
        await host.StopAsync();
    }

    return;
}

if (isUnlockTestMode)
{
    await host.StartAsync();
    try
    {
        var relayClient = host.Services.GetRequiredService<RelayClient>();
        var credentialStore = host.Services.GetRequiredService<CredentialStore>();

        var connectionDeadline = DateTime.UtcNow.AddSeconds(10);
        while (!relayClient.IsConnected && DateTime.UtcNow < connectionDeadline)
        {
            await Task.Delay(250);
        }

        if (!relayClient.IsConnected)
        {
            Console.WriteLine("FAILED: Could not connect to the relay.");
            return;
        }

        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        Console.WriteLine($"Sending unlock test request with nonce {nonce}...");

        var response = await relayClient.RequestApprovalAsync(
            Environment.MachineName,
            nonce,
            TimeSpan.FromSeconds(45),
            CancellationToken.None);

        if (response is null)
        {
            Console.WriteLine("FAILED: No response received before the 45-second timeout.");
            return;
        }

        if (!string.Equals(response.Decision, "approved", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"FAILED: Phone returned decision '{response.Decision}'.");
            return;
        }

        if (!string.Equals(response.Nonce, nonce, StringComparison.Ordinal))
        {
            Console.WriteLine("FAILED: Response nonce did not match the test request.");
            return;
        }

        var device = (await credentialStore.GetDevicesAsync())
            .FirstOrDefault(candidate => candidate.DeviceId == response.DeviceId);
        if (device is null)
        {
            Console.WriteLine($"FAILED: Device '{response.DeviceId}' is not paired on this PC.");
            return;
        }

        if (string.IsNullOrWhiteSpace(response.SignatureBase64))
        {
            Console.WriteLine("FAILED: Approved response did not contain a signature.");
            return;
        }

        try
        {
            var signatureBytes = Convert.FromBase64String(response.SignatureBase64);
            var publicKeyBytes = Convert.FromBase64String(device.PublicKeyBase64);
            var nonceBytes = System.Text.Encoding.UTF8.GetBytes(nonce);

            if (NonceSignatureVerifier.Verify(nonceBytes, signatureBytes, publicKeyBytes))
            {
                Console.WriteLine($"SUCCESS: Signature Verified for device '{device.DeviceId}'.");
            }
            else
            {
                Console.WriteLine("FAILED: Signature verification failed.");
            }
        }
        catch (FormatException)
        {
            Console.WriteLine("FAILED: Signature or public key was not valid Base64.");
        }
    }
    finally
    {
        await host.StopAsync();
    }

    return;
}

await host.RunAsync();

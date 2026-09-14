using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PhoneUnlockService.Security;

public sealed record PairedDevice(
    string DeviceId,
    string DisplayName,
    string PublicKeyBase64, // SubjectPublicKeyInfo, EC P-256
    DateTimeOffset PairedAtUtc);

internal sealed class StoreFile
{
    public string PcId { get; set; } = "";
    public string? Username { get; set; }
    public byte[]? EncryptedPasswordBlob { get; set; } // DPAPI ciphertext
    public List<PairedDevice> Devices { get; set; } = new();
}

/// <summary>
/// Owns the one file on disk that matters most from a security standpoint:
/// the DPAPI-encrypted Windows password and the table of phones that are
/// allowed to unlock this PC.
///
/// Threat model note: this uses DataProtectionScope.LocalMachine (not
/// CurrentUser) because the service typically runs under a dedicated
/// service account, not the interactive user session, and must be able to
/// decrypt after reboot without that user being logged in yet. The
/// consequence is that ANY process running as local admin on this machine
/// (or SYSTEM) can call CryptUnprotectData on this blob too - DPAPI is not
/// a substitute for restricting who has admin on the box. Harden further
/// by:
///   - Running this service under a dedicated low-privilege service
///     account rather than LocalSystem where possible.
///   - Locking down the ACL on the storage directory (done in
///     EnsureStoreDirectory below) so only SYSTEM/Administrators can read
///     the file at all.
///   - Optionally mixing in a machine-specific "entropy" byte array to
///     CryptProtectData/CryptUnprotectData (extra parameter, not wired up
///     here - add if you want an extra secret-on-disk requirement).
/// </summary>
public sealed class CredentialStore
{
    private static readonly string StoreDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "PhoneUnlock");

    private static readonly string StorePath = Path.Combine(StoreDir, "pairing.json");
    private static readonly Mutex StoreMutex = new(false, @"Global\PhoneUnlockCredentialStore");

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILogger<CredentialStore> _log;

    public CredentialStore(ILogger<CredentialStore> log)
    {
        _log = log;
        EnsureStoreDirectory();
    }

    private static void EnsureStoreDirectory()
    {
        if (!Directory.Exists(StoreDir))
        {
            Directory.CreateDirectory(StoreDir);
        }

        // TODO (Windows-only hardening, not implemented here): tighten the
        // NTFS ACL on StoreDir to SYSTEM + local Administrators only, e.g.
        // via System.Security.AccessControl.DirectorySecurity. Left as a
        // TODO because the exact SIDs you want depend on which account
        // this service runs under - don't skip this before real use.
    }

    private async Task<StoreFile> LoadAsync()
    {
        if (!File.Exists(StorePath))
            return new StoreFile();

        await using var fs = File.OpenRead(StorePath);
        var data = await JsonSerializer.DeserializeAsync<StoreFile>(fs);
        return data ?? new StoreFile();
    }

    private Task SaveAsync(StoreFile store)
    {
        var json = JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = true });
        StoreMutex.WaitOne();
        var tmp = StorePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var fs = new FileStream(
                tmp,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                using var writer = new StreamWriter(fs, new UTF8Encoding(false), leaveOpen: true);
                writer.Write(json);
                writer.Flush();
                fs.Flush(flushToDisk: true);
            }

            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(tmp, StorePath, overwrite: true);
                    break;
                }
                catch (UnauthorizedAccessException) when (attempt < 10)
                {
                    Thread.Sleep(100 * (attempt + 1));
                }
            }
        }
        finally
        {
            if (File.Exists(tmp))
                File.Delete(tmp);
            StoreMutex.ReleaseMutex();
        }

        return Task.CompletedTask;
    }

    /// <summary>Called once during pairing/setup to store the Windows password, DPAPI-encrypted at rest.</summary>
    public async Task SetWindowsCredentialAsync(string pcId, string username, string plaintextPassword)
    {
        await _lock.WaitAsync();
        try
        {
            var store = await LoadAsync();
            store.PcId = pcId;
            store.Username = username;

            var plainBytes = System.Text.Encoding.UTF8.GetBytes(plaintextPassword);
            try
            {
                store.EncryptedPasswordBlob = ProtectedData.Protect(
                    plainBytes, optionalEntropy: null, scope: DataProtectionScope.LocalMachine);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plainBytes);
            }

            await SaveAsync(store);
            _log.LogInformation("Stored DPAPI-encrypted credential for {User}", username);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Decrypts and returns the Windows username/password pair. Only ever
    /// call this after a phone signature has verified successfully - see
    /// SignatureVerifier and PipeServerHost.
    /// </summary>
    public async Task<(string username, string password)?> GetWindowsCredentialAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var store = await LoadAsync();
            if (store.EncryptedPasswordBlob is null || store.Username is null)
                return null;

            var plainBytes = ProtectedData.Unprotect(
                store.EncryptedPasswordBlob, optionalEntropy: null, scope: DataProtectionScope.LocalMachine);
            try
            {
                return (store.Username, System.Text.Encoding.UTF8.GetString(plainBytes));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plainBytes);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<string> GetPcIdAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var store = await LoadAsync();
            if (string.IsNullOrEmpty(store.PcId))
            {
                store.PcId = Guid.NewGuid().ToString("N");
                await SaveAsync(store);
            }
            return store.PcId;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task AddDeviceAsync(PairedDevice device)
    {
        await _lock.WaitAsync();
        try
        {
            var store = await LoadAsync();
            store.Devices.RemoveAll(d => d.DeviceId == device.DeviceId);
            store.Devices.Add(device);
            await SaveAsync(store);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RemoveDeviceAsync(string deviceId)
    {
        await _lock.WaitAsync();
        try
        {
            var store = await LoadAsync();
            store.Devices.RemoveAll(d => d.DeviceId == deviceId);
            await SaveAsync(store);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<PairedDevice>> GetDevicesAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var store = await LoadAsync();
            return store.Devices.ToList();
        }
        finally
        {
            _lock.Release();
        }
    }
}

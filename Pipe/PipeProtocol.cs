using System.IO.Pipes;
using System.Text;
using Microsoft.Extensions.Logging;

namespace PhoneUnlockService.Pipe;

/// <summary>
/// Mirrors the toy line protocol documented in PipeClient.cpp exactly:
///
///   Client -> Service:  "UNLOCK\n" then "&lt;nonce&gt;\n"  (two separate WriteFile calls == two pipe messages)
///   Service -> Client:  "APPROVED\n&lt;username&gt;\n&lt;password&gt;\n"
///                     or "DENIED\n&lt;reason&gt;\n"
///                     or "TIMEOUT\n"
///                     (sent as a single WriteFile call, since the client's ReadAll
///                      just concatenates whatever arrives before the pipe empties)
///
/// Encoding is UTF-16LE with no BOM, because the client speaks std::wstring
/// (wchar_t) directly onto the wire. The same TODO from PipeClient.cpp
/// applies here: this should eventually become length-prefixed binary
/// framing with a real schema instead of raw text.
/// </summary>
public static class PipeProtocol
{
    /// <summary>Reads exactly one pipe message and decodes it as UTF-16LE, stripping a single trailing '\n' if present.</summary>
    public static async Task<string> ReadMessageAsync(NamedPipeServerStream stream, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        do
        {
            read = await stream.ReadAsync(buffer, ct);
            if (read > 0)
                ms.Write(buffer, 0, read);
        } while (read > 0 && !stream.IsMessageComplete);

        var text = Encoding.Unicode.GetString(ms.ToArray());
        return text.TrimEnd('\n');
    }

    public static async Task WriteResponseAsync(NamedPipeServerStream stream, string response, CancellationToken ct)
    {
        var bytes = Encoding.Unicode.GetBytes(response);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task<bool> TryWriteResponseAsync(
        NamedPipeServerStream stream,
        string response,
        CancellationToken ct,
        ILogger log)
    {
        try
        {
            await WriteResponseAsync(stream, response, ct);
            return true;
        }
        catch (IOException ex)
        {
            log.LogWarning(ex, "Named pipe client disconnected before the response could be written");
            return false;
        }
        catch (ObjectDisposedException ex)
        {
            log.LogWarning(ex, "Named pipe was disposed before the response could be written");
            return false;
        }
        catch (InvalidOperationException ex)
        {
            log.LogWarning(ex, "Named pipe was no longer writable");
            return false;
        }
    }

    public static string BuildApproved(string username, string password) =>
        $"APPROVED\n{username}\n{password}\n";

    public static string BuildDenied(string reason) =>
        $"DENIED\n{reason}\n";

    public const string Timeout = "TIMEOUT\n";
}

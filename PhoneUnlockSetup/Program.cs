using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using PhoneUnlockService.Security;

namespace PhoneUnlockSetup;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new SetupForm());
    }
}

internal sealed class SetupForm : Form
{
    private readonly TextBox usernameBox = new();
    private readonly TextBox passwordBox = new();
    private readonly Label codeLabel = new();
    private readonly Label statusLabel = new();
    private readonly Button saveButton = new();
    private readonly Button pairButton = new();
    private Process? pairingProcess;
    private readonly ILoggerFactory loggerFactory = LoggerFactory.Create(builder => { });

    public SetupForm()
    {
        Text = "PhoneUnlock PC Setup";
        ClientSize = new Size(520, 300);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        var title = new Label { Text = "PhoneUnlock PC Setup", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Location = new Point(24, 20) };
        var usernameLabel = new Label { Text = "Windows username", AutoSize = true, Location = new Point(24, 66) };
        usernameBox.SetBounds(180, 62, 300, 24);
        usernameBox.Text = Environment.UserName;

        var passwordLabel = new Label { Text = "Windows password", AutoSize = true, Location = new Point(24, 106) };
        passwordBox.SetBounds(180, 102, 300, 24);
        passwordBox.UseSystemPasswordChar = true;

        saveButton.Text = "Save password";
        saveButton.SetBounds(24, 145, 145, 32);
        saveButton.Click += async (_, _) => await SaveCredentialAsync();

        pairButton.Text = "Generate pairing code";
        pairButton.SetBounds(180, 145, 180, 32);
        pairButton.Click += (_, _) => StartPairing();

        codeLabel.Text = "Pairing code: not started";
        codeLabel.AutoSize = true;
        codeLabel.Font = new Font(Font, FontStyle.Bold);
        codeLabel.Location = new Point(24, 205);

        statusLabel.Text = "Enter the Windows password, save it, then generate a pairing code.";
        statusLabel.AutoSize = true;
        statusLabel.Location = new Point(24, 245);

        Controls.AddRange([title, usernameLabel, usernameBox, passwordLabel, passwordBox, saveButton, pairButton, codeLabel, statusLabel]);
        FormClosed += (_, _) => StopPairing();
    }

    private async Task SaveCredentialAsync()
    {
        if (string.IsNullOrWhiteSpace(usernameBox.Text) || string.IsNullOrEmpty(passwordBox.Text))
        {
            statusLabel.Text = "Enter both username and password.";
            return;
        }

        saveButton.Enabled = false;
        try
        {
            var store = new CredentialStore(loggerFactory.CreateLogger<CredentialStore>());
            var pcId = await store.GetPcIdAsync();
            await store.SetWindowsCredentialAsync(pcId, usernameBox.Text.Trim(), passwordBox.Text);
            passwordBox.Clear();
            statusLabel.Text = "Password saved securely with DPAPI.";
        }
        catch (Exception ex)
        {
            statusLabel.Text = $"Save failed: {ex.Message}";
        }
        finally
        {
            saveButton.Enabled = true;
        }
    }

    private void StartPairing()
    {
        StopPairing();
        codeLabel.Text = "Pairing code: starting...";
        pairButton.Enabled = false;

        pairingProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "run -- --pair",
                WorkingDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "PhoneUnlockService")),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            },
            EnableRaisingEvents = true
        };
        pairingProcess.OutputDataReceived += PairingOutputReceived;
        pairingProcess.ErrorDataReceived += PairingOutputReceived;
        pairingProcess.Exited += (_, _) => BeginInvoke(() => pairButton.Enabled = true);
        pairingProcess.Start();
        pairingProcess.BeginOutputReadLine();
        pairingProcess.BeginErrorReadLine();
    }

    private void PairingOutputReceived(object sender, DataReceivedEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.Data)) return;
        var match = Regex.Match(args.Data, @"Pairing code:\s*(\d{6})");
        if (match.Success)
        {
            BeginInvoke(() =>
            {
                codeLabel.Text = $"Pairing code: {match.Groups[1].Value}";
                statusLabel.Text = "Enter this code in the Android app.";
            });
            return;
        }

        var savedMatch = Regex.Match(args.Data, @"Pairing saved for device\s+'([^']+)'\.");
        if (savedMatch.Success)
        {
            BeginInvoke(() =>
            {
                statusLabel.Text = $"Pairing successful for device {savedMatch.Groups[1].Value}.";
                pairButton.Enabled = true;
            });
            return;
        }

        if (args.Data.Contains("Pairing window expired", StringComparison.OrdinalIgnoreCase))
        {
            BeginInvoke(() =>
            {
                statusLabel.Text = "Pairing expired before the phone completed pairing.";
                pairButton.Enabled = true;
            });
            return;
        }

        if (args.Data.Contains("PAIRING FAILED:", StringComparison.OrdinalIgnoreCase))
        {
            BeginInvoke(() =>
            {
                codeLabel.Text = "Pairing code: unavailable";
                statusLabel.Text = args.Data.Trim();
                pairButton.Enabled = true;
            });
        }
    }

    private void StopPairing()
    {
        if (pairingProcess is { HasExited: false })
            pairingProcess.Kill(entireProcessTree: true);
        pairingProcess?.Dispose();
        pairingProcess = null;
    }
}

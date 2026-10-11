using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace CloudflareTunnelManager.Services;

public sealed class CloudflaredManager
{
    private const string DownloadUrl = "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe";
    private static readonly string InstallDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CloudflareTunnelManager", "bin");
    private static readonly string ExecutablePath = Path.Combine(InstallDirectory, "cloudflared.exe");
    public string InstalledExecutablePath => ExecutablePath;
    public event EventHandler<string>? MessageLogged;

    public async Task InstallAsync(string token, Action<string> progress)
    {
        Directory.CreateDirectory(InstallDirectory);
        var temporaryPath = Path.Combine(InstallDirectory, $"cloudflared-{Guid.NewGuid():N}.download");
        try
        {
            progress("Downloading cloudflared from Cloudflare…");
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("CloudflareTunnelManager/1.0");
            await using (var source = await client.GetStreamAsync(DownloadUrl))
            await using (var destination = File.Create(temporaryPath))
                await source.CopyToAsync(destination);

            progress("Verifying the downloaded executable…");
            VerifySignedExecutable(temporaryPath);
            File.Move(temporaryPath, ExecutablePath, true);

            progress("Installing the automatic Windows service…");
            var result = await RunAsync(ExecutablePath, new[] { "service", "install", token }, redactOutput: true);
            if (result.ExitCode != 0 && !result.Output.Contains("already exists", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Cloudflared could not install its Windows service. " + TokenParser.Redact(result.Output));

            progress("Starting the Cloudflare connector…");
            // The service may not be immediately visible to sc.exe after the installer returns.
            // Poll for the service to appear and then start it. If it never appears, attempt to start
            // once to capture the installer output and surface a helpful error.
            var serviceAppeared = false;
            for (var i = 0; i < 10; i++)
            {
                var query = await RunScAsync("query", "cloudflared", throwOnFailure: false);
                if (query.ExitCode == 0)
                {
                    serviceAppeared = true;
                    break;
                }
                await Task.Delay(500);
            }
            if (!serviceAppeared)
            {
                var startResult = await RunScAsync("start", "cloudflared", throwOnFailure: false);
                if (startResult.ExitCode != 0)
                    throw new InvalidOperationException("Cloudflared service did not appear after install. " + TokenParser.Redact(startResult.Output));
            }
            else
            {
                await RunScAsync("start", "cloudflared");
            }
            await Task.Delay(1500);
            MessageLogged?.Invoke(this, "Cloudflare Tunnel installation completed.");
        }
        finally
        {
            token = string.Empty;
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public async Task<TunnelState> GetStateAsync()
    {
        var query = await RunScAsync("query", "cloudflared", throwOnFailure: false);
        var installed = query.ExitCode == 0;
        var running = installed && query.Output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
        var config = installed
            ? await RunScAsync("qc", "cloudflared", throwOnFailure: false)
            : new ProcessResult(-1, string.Empty);
        var startup = !installed ? "Not installed" : config.Output.Contains("AUTO_START", StringComparison.OrdinalIgnoreCase) ? "Automatic" : "Manual";
        var version = "—";
        var binary = File.Exists(ExecutablePath) ? ExecutablePath : FindServiceExecutable(config.Output);
        if (!string.IsNullOrWhiteSpace(binary) && File.Exists(binary))
        {
            var result = await RunAsync(binary, new[] { "--version" });
            version = ParseVersion(result.Output);
        }
        return new TunnelState(installed, running, running ? "Running" : installed ? "Stopped" : "Not installed", startup, version);
    }

    public async Task ControlServiceAsync(string action)
    {
        if (action == "restart")
        {
            await RunScAsync("stop", "cloudflared", throwOnFailure: false);
            await Task.Delay(1000);
            await RunScAsync("start", "cloudflared");
        }
        else await RunScAsync(action, "cloudflared");
        MessageLogged?.Invoke(this, $"Service {action} request completed.");
    }

    public async Task UninstallAsync()
    {
        // Stop the service if running, then delete the service and remove installed files
        MessageLogged?.Invoke(this, "Uninstalling cloudflared service and files…");
        try
        {
            // Attempt to stop the service (ignore failures)
            await RunScAsync("stop", "cloudflared", throwOnFailure: false);
            await Task.Delay(500);

            // Delete the service (ignore failures)
            await RunScAsync("delete", "cloudflared", throwOnFailure: false);

            // Remove the executable if present
            if (File.Exists(ExecutablePath))
            {
                try { File.Delete(ExecutablePath); }
                catch (Exception ex) { MessageLogged?.Invoke(this, $"Could not delete executable: {ex.Message}"); }
            }

            // Remove the install directory if empty
            try
            {
                if (Directory.Exists(InstallDirectory) && !Directory.EnumerateFileSystemEntries(InstallDirectory).Any())
                    Directory.Delete(InstallDirectory, false);
            }
            catch (Exception ex)
            {
                MessageLogged?.Invoke(this, $"Could not remove install directory: {ex.Message}");
            }

            // Remove leftover EventLog registry entry for the service (HKLM\SYSTEM\CurrentControlSet\Services\EventLog\Application\Cloudflared)
            try
            {
                // Use the 64-bit view -- cloudflared installs as a 64-bit service on modern Windows.
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var eventLogApp = baseKey.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\EventLog\Application", writable: true);
                if (eventLogApp != null)
                {
                    var names = eventLogApp.GetSubKeyNames();
                    if (names.Contains("Cloudflared", StringComparer.OrdinalIgnoreCase))
                    {
                        try
                        {
                            eventLogApp.DeleteSubKeyTree("Cloudflared");
                            MessageLogged?.Invoke(this, "Removed EventLog registry key for Cloudflared.");
                        }
                        catch (Exception ex)
                        {
                            // likely permission issue; attempt an elevated removal by spawning PowerShell with Verb=runas
                            MessageLogged?.Invoke(this, $"Could not remove EventLog registry key: {ex.Message}. Attempting elevated removal...");
                            try
                            {
                                var psCommand = "Remove-Item -Path 'HKLM:\\SYSTEM\\CurrentControlSet\\Services\\EventLog\\Application\\Cloudflared' -Recurse -Force";
                                var psi = new ProcessStartInfo
                                {
                                    FileName = "powershell.exe",
                                    Arguments = $"-NoProfile -WindowStyle Hidden -Command \"{psCommand}\"",
                                    UseShellExecute = true,
                                    Verb = "runas",
                                    CreateNoWindow = true
                                };
                                using var proc = Process.Start(psi);
                                if (proc is null)
                                {
                                    MessageLogged?.Invoke(this, "Could not start elevated process for registry removal.");
                                }
                                else
                                {
                                    // wait up to 30s for the elevated process to complete
                                    var completed = proc.WaitForExit(30000);
                                    if (completed && proc.ExitCode == 0)
                                        MessageLogged?.Invoke(this, "Elevated removal of EventLog registry key succeeded.");
                                    else if (!completed)
                                        MessageLogged?.Invoke(this, "Elevated removal did not finish within timeout.");
                                    else
                                        MessageLogged?.Invoke(this, $"Elevated removal process exited with code {proc.ExitCode}.");
                                }
                            }
                            catch (Exception ex2)
                            {
                                MessageLogged?.Invoke(this, $"Elevated registry removal failed: {ex2.Message}");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageLogged?.Invoke(this, $"Could not remove EventLog registry key: {ex.Message}");
            }

            MessageLogged?.Invoke(this, "Uninstall completed.");
        }
        catch (Exception ex)
        {
            MessageLogged?.Invoke(this, $"Uninstall failed: {ex.Message}");
            throw;
        }
    }

    private static void VerifySignedExecutable(string path)
    {
        try
        {
            var certificate = X509Certificate.CreateFromSignedFile(path);
            using var certificate2 = new X509Certificate2(certificate);
            var subject = certificate2.Subject;
            if (!subject.Contains("Cloudflare", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded executable is not signed by Cloudflare.");
        }
        catch (CryptographicException)
        {
            throw new InvalidDataException("The downloaded connector does not contain a valid Cloudflare code-signing certificate.");
        }
    }

    private async Task<ProcessResult> RunScAsync(string command, string service, bool throwOnFailure = true)
    {
        var result = await RunAsync("sc.exe", new[] { command, service });
        if (throwOnFailure && result.ExitCode != 0 && !result.Output.Contains("already running", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(TokenParser.Redact(result.Output.Trim()));
        return result;
    }

    private async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, bool redactOutput = false)
    {
        var start = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = (await stdout) + Environment.NewLine + (await stderr);
        MessageLogged?.Invoke(this, redactOutput ? TokenParser.Redact(output.Trim()) : output.Trim());
        return new ProcessResult(process.ExitCode, output);
    }

    private static string ParseVersion(string output)
    {
        var words = output.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 2 ? words[2] : output.Trim();
    }

    private static string FindServiceExecutable(string output)
    {
        var marker = "BINARY_PATH_NAME";
        var line = output.Split('\n').FirstOrDefault(x => x.Contains(marker, StringComparison.OrdinalIgnoreCase));
        if (line is null) return string.Empty;
        var value = line[(line.IndexOf(':') + 1)..].Trim();
        if (value.StartsWith('"')) return value.Split('"', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        return value.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
    }

    private readonly record struct ProcessResult(int ExitCode, string Output);
}

public sealed record TunnelState(bool IsInstalled, bool IsRunning, string DisplayStatus, string StartupType, string Version);

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace CloudflareTunnelManager.Services;

public sealed class MultiTunnelRunner : IDisposable
{
    private readonly ConcurrentDictionary<string, Process> _processes = new();
    public event EventHandler<string>? MessageLogged;

    public bool IsRunning(string tunnelId) => _processes.TryGetValue(tunnelId, out var process) && !process.HasExited;

    public Task StartAsync(string tunnelId, string tunnelName, string token, string executablePath)
    {
        if (!File.Exists(executablePath)) throw new FileNotFoundException("Install cloudflared from the Setup tunnel page first.", executablePath);
        if (IsRunning(tunnelId)) return Task.CompletedTask;

        var start = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add("tunnel");
        start.ArgumentList.Add("--no-autoupdate");
        start.ArgumentList.Add("run");
        start.ArgumentList.Add("--token");
        start.ArgumentList.Add(token);
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) MessageLogged?.Invoke(this, $"[{tunnelName}] {e.Data}"); };
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) MessageLogged?.Invoke(this, $"[{tunnelName}] {e.Data}"); };
        process.Exited += (_, _) => { _processes.TryRemove(tunnelId, out _); MessageLogged?.Invoke(this, $"[{tunnelName}] Connector stopped."); };
        if (!process.Start()) throw new InvalidOperationException("The connector process could not be started.");
        _processes[tunnelId] = process;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        MessageLogged?.Invoke(this, $"[{tunnelName}] Connector started.");
        return Task.CompletedTask;
    }

    public void Stop(string tunnelId)
    {
        if (!_processes.TryRemove(tunnelId, out var process)) return;
        try { if (!process.HasExited) process.Kill(true); }
        finally { process.Dispose(); }
    }

    public void Dispose()
    {
        foreach (var id in _processes.Keys.ToList()) Stop(id);
    }
}

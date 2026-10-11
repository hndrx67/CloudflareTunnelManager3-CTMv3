using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CloudflareTunnelManager.Models;

public sealed class CloudflareTunnel
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Status { get; init; } = "inactive";
    public string ConfigSource { get; init; } = string.Empty;
    public string StatusLabel => string.IsNullOrWhiteSpace(Status)
        ? "Unknown"
        : char.ToUpperInvariant(Status[0]) + Status[1..];
    public override string ToString() => Name;
}

public sealed class TunnelRoute : INotifyPropertyChanged
{
    private string _hostname = string.Empty;
    private string _service = "http://localhost:3000";
    private string _path = string.Empty;
    private bool _noTlsVerify;

    public string Hostname { get => _hostname; set { _hostname = value; Changed(); } }
    public string Service
    {
        get => _service;
        set
        {
            _service = value;
            Changed();
            Changed(nameof(TunnelType));
            Changed(nameof(Origin));
        }
    }
    public string TunnelType
    {
        get
        {
            var separator = _service.IndexOf("://", StringComparison.Ordinal);
            return separator > 0 ? _service[..separator] : "http";
        }
        set
        {
            var type = string.IsNullOrWhiteSpace(value) ? "http" : value.Trim().ToLowerInvariant();
            _service = $"{type}://{Origin}";
            Changed();
            Changed(nameof(Service));
        }
    }
    public string Origin
    {
        get
        {
            var separator = _service.IndexOf("://", StringComparison.Ordinal);
            return separator >= 0 ? _service[(separator + 3)..] : _service;
        }
        set
        {
            _service = $"{TunnelType}://{value.Trim()}";
            Changed();
            Changed(nameof(Service));
        }
    }
    public string Path { get => _path; set { _path = value; Changed(); } }
    public bool NoTlsVerify { get => _noTlsVerify; set { _noTlsVerify = value; Changed(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class CloudflareZone
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public override string ToString() => Name;
}

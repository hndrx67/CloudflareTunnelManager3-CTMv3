using System;
using System.Text.RegularExpressions;

namespace CloudflareTunnelManager.Services;

public static partial class TokenParser
{
    [GeneratedRegex(@"(?:--token\s+)?(?<token>eyJ[a-zA-Z0-9_\-\.]{40,})", RegexOptions.IgnoreCase)]
    private static partial Regex TokenRegex();

    public static bool TryExtract(string? input, out string token)
    {
        token = string.Empty;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var match = TokenRegex().Match(input.Trim());
        if (!match.Success) return false;
        token = match.Groups["token"].Value.Trim('"', '\'', ';');
        return token.Length >= 40 && token.StartsWith("eyJ", StringComparison.Ordinal);
    }

    public static string Redact(string input) => TokenRegex().Replace(input, "[TUNNEL TOKEN REDACTED]");
}

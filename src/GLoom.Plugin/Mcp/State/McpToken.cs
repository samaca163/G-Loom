using System;
using System.IO;
using System.Security.Cryptography;

namespace GLoom.Mcp.State;

/// <summary>
/// A per-user bearer token that lives across Rhino restarts, so a client configured once
/// keeps working through the rebuild-and-restart loop. Loopback-only binding and the Origin
/// check are the real defences; the token stops another local user or a stray process.
/// </summary>
public static class McpToken
{
    public static string LoadOrCreate()
    {
        McpPaths.Ensure();
        if (File.Exists(McpPaths.TokenFile))
        {
            var existing = File.ReadAllText(McpPaths.TokenFile).Trim();
            if (existing.Length >= 32) return existing;
        }

        var bytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        File.WriteAllText(McpPaths.TokenFile, token);
        try
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(McpPaths.TokenFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch { /* best effort; the directory is already per-user */ }
        return token;
    }

    public static bool Matches(string? authorizationHeader, string token)
    {
        if (string.IsNullOrEmpty(authorizationHeader)) return false;
        const string prefix = "Bearer ";
        if (!authorizationHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var presented = authorizationHeader[prefix.Length..].Trim();
        var a = System.Text.Encoding.UTF8.GetBytes(presented);
        var b = System.Text.Encoding.UTF8.GetBytes(token);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}

using System.Security.Cryptography;

namespace GitSrv.Api.Auth;

public sealed record ParsedSshKey(string KeyType, string NormalisedLine, string Fingerprint);

/// <summary>
/// Parses an OpenSSH <c>authorized_keys</c>-style public key line and computes its SHA-256
/// fingerprint (<c>SHA256:base64</c>, unpadded — the same string <c>ssh-keygen -lf</c> prints).
/// </summary>
public static class SshPublicKey
{
    private static readonly HashSet<string> Supported = new(StringComparer.Ordinal)
    {
        "ssh-ed25519",
        "ssh-rsa",
        "ecdsa-sha2-nistp256",
        "ecdsa-sha2-nistp384",
        "ecdsa-sha2-nistp521",
        "sk-ssh-ed25519@openssh.com",
        "sk-ecdsa-sha2-nistp256@openssh.com",
    };

    public static bool TryParse(string input, out ParsedSshKey key, out string error)
    {
        key = null!;
        error = "";

        var parts = input.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            error = "Expected a key of the form '<type> <base64> [comment]'.";
            return false;
        }

        var type = parts[0];
        if (!Supported.Contains(type))
        {
            error = $"Unsupported key type '{type}'.";
            return false;
        }

        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(parts[1]);
        }
        catch (FormatException)
        {
            error = "Key body is not valid base64.";
            return false;
        }

        // The blob's first length-prefixed string must repeat the key type.
        if (!TryReadString(blob, 0, out var embeddedType, out _) || embeddedType != type)
        {
            error = "Key body does not match its declared type.";
            return false;
        }

        var fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');
        var comment = parts.Length > 2 ? string.Join(' ', parts[2..]) : "";
        var normalised = comment.Length > 0 ? $"{type} {parts[1]} {comment}" : $"{type} {parts[1]}";

        key = new ParsedSshKey(type, normalised, fingerprint);
        return true;
    }

    private static bool TryReadString(byte[] buf, int offset, out string value, out int next)
    {
        value = "";
        next = offset;
        if (offset + 4 > buf.Length)
            return false;
        int len = (buf[offset] << 24) | (buf[offset + 1] << 16) | (buf[offset + 2] << 8) | buf[offset + 3];
        if (len < 0 || offset + 4 + len > buf.Length)
            return false;
        value = System.Text.Encoding.ASCII.GetString(buf, offset + 4, len);
        next = offset + 4 + len;
        return true;
    }
}

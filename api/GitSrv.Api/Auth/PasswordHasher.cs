using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace GitSrv.Api.Auth;

/// <summary>
/// Argon2id password hashing. Self-describing string format so parameters can change without a
/// migration: <c>argon2id$v=19$m=19456,t=2,p=1$salt_b64$hash_b64</c>.
/// </summary>
public sealed class PasswordHasher
{
    private const int MemoryKiB = 19456; // 19 MiB — OWASP minimum for Argon2id
    private const int Iterations = 2;
    private const int Parallelism = 1;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(password, salt, MemoryKiB, Iterations, Parallelism, HashBytes);
        return $"argon2id$v=19$m={MemoryKiB},t={Iterations},p={Parallelism}$" +
               $"{Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool Verify(string password, string encoded)
    {
        try
        {
            var parts = encoded.Split('$');
            if (parts.Length != 5 || parts[0] != "argon2id")
                return false;

            var paramMap = parts[2].Split(',')
                .Select(kv => kv.Split('='))
                .ToDictionary(kv => kv[0], kv => int.Parse(kv[1]));

            var salt = Convert.FromBase64String(parts[3]);
            var expected = Convert.FromBase64String(parts[4]);
            var actual = Derive(password, salt, paramMap["m"], paramMap["t"], paramMap["p"], expected.Length);

            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }

    private static byte[] Derive(string password, byte[] salt, int memoryKiB, int iterations, int parallelism, int outputBytes)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memoryKiB,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };
        return argon2.GetBytes(outputBytes);
    }
}

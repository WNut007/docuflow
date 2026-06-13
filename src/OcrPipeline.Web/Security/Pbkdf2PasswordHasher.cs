using System.Security.Cryptography;

namespace OcrPipeline.Web.Security;

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string stored);
}

/// <summary>
/// PBKDF2 (HMAC-SHA256). Stored format: {iterations}.{base64 salt}.{base64 hash}
/// </summary>
public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    private const int SaltSize = 16;      // 128-bit
    private const int KeySize = 32;       // 256-bit
    private const int Iterations = 100_000;
    private static readonly HashAlgorithmName Algo = HashAlgorithmName.SHA256;

    public string Hash(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, Algo, KeySize);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(key)}";
    }

    public bool Verify(string password, string stored)
    {
        var parts = stored.Split('.', 3);
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], out int iterations)) return false;

        byte[] salt = Convert.FromBase64String(parts[1]);
        byte[] expected = Convert.FromBase64String(parts[2]);
        byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, Algo, expected.Length);

        // constant-time comparison
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

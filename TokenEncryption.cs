using System.Security.Cryptography;
using System.Text;

/// <summary>Versioned authenticated encryption for exchange credentials.</summary>
public static class TokenEncryption
{
    private const string CurrentVersion = "v2";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public static string Encrypt(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        byte[] key = LoadKeyBytes();
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] input = Encoding.UTF8.GetBytes(plaintext);
        byte[] ciphertext = new byte[input.Length];
        byte[] tag = new byte[TagSize];

        using (var aes = new AesGcm(key, TagSize))
            aes.Encrypt(nonce, input, ciphertext, tag, Encoding.UTF8.GetBytes(CurrentVersion));

        CryptographicOperations.ZeroMemory(input);
        return string.Join(':', CurrentVersion,
            Convert.ToBase64String(nonce), Convert.ToBase64String(tag), Convert.ToBase64String(ciphertext));
    }

    public static string Decrypt(string encrypted)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encrypted);
        byte[] key = LoadKeyBytes();
        string[] parts = encrypted.Split(':');

        if (parts.Length == 4 && parts[0] == CurrentVersion)
        {
            byte[] nonce = Convert.FromBase64String(parts[1]);
            byte[] tag = Convert.FromBase64String(parts[2]);
            byte[] ciphertext = Convert.FromBase64String(parts[3]);
            if (nonce.Length != NonceSize || tag.Length != TagSize)
                throw new CryptographicException("Invalid encrypted credential payload.");

            byte[] plaintext = new byte[ciphertext.Length];
            using (var aes = new AesGcm(key, TagSize))
                aes.Decrypt(nonce, ciphertext, tag, plaintext, Encoding.UTF8.GetBytes(CurrentVersion));
            try { return Encoding.UTF8.GetString(plaintext); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }

        // Read-only compatibility for credentials encrypted by the old AES-CBC format.
        // New writes always use authenticated AES-GCM (v2).
        if (parts.Length == 2)
        {
            byte[] iv = Convert.FromBase64String(parts[0]);
            byte[] ciphertext = Convert.FromBase64String(parts[1]);
            if (iv.Length != 16) throw new CryptographicException("Invalid legacy credential payload.");
            using var aes = Aes.Create();
            aes.Key = key;
            aes.IV = iv;
            using var decryptor = aes.CreateDecryptor();
            byte[] plaintext = decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
            try { return Encoding.UTF8.GetString(plaintext); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }

        throw new FormatException("Unsupported encrypted credential format.");
    }

    private static byte[] LoadKeyBytes()
    {
        string? configured = Environment.GetEnvironmentVariable("DATA_PROTECTION_KEY");
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException("DATA_PROTECTION_KEY must be configured; it must be a dedicated 32-byte key, not an exchange API secret.");

        byte[] key;
        if (configured.Length == 64 && configured.All(Uri.IsHexDigit))
            key = Convert.FromHexString(configured);
        else
        {
            try { key = Convert.FromBase64String(configured); }
            catch (FormatException ex) { throw new InvalidOperationException("DATA_PROTECTION_KEY must be Base64 or 64 hexadecimal characters.", ex); }
        }

        if (key.Length != 32)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new InvalidOperationException("DATA_PROTECTION_KEY must decode to exactly 32 bytes (256 bits).");
        }
        return key;
    }
}

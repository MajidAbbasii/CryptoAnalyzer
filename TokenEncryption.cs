using System;
using System.Text;
using System.Security.Cryptography;

public static class TokenEncryption
{
    // Key must be 32 bytes (256-bit) Base64 string, loaded from env
    private static readonly byte[] KeyBytes = LoadKeyBytes();

    private static byte[] LoadKeyBytes()
    {
        var b64 = Environment.GetEnvironmentVariable("DATA_PROTECTION_KEY")
                  ?? Environment.GetEnvironmentVariable("NOBITEX_DATA_KEY");
        if (string.IsNullOrWhiteSpace(b64))
            throw new InvalidOperationException("Encryption key not set (DATA_PROTECTION_KEY or NOBITEX_DATA_KEY).");
        if (b64.Length == 64) // 32 bytes = 64 hex chars
        {
            var bytes = new byte[32];
            for (int i = 0; i < 32; i++)
                bytes[i] = Convert.ToByte(b64.Substring(i * 2, 2), 16);
            return bytes;
        }
        return Convert.FromBase64String(b64);
    }

    public static string GetKeyBase64()
    {
        return Convert.ToBase64String(KeyBytes);
    }

    public static string Encrypt(string token)
    {
        var plaintext = Encoding.UTF8.GetBytes(token);
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key = KeyBytes;
        aes.GenerateIV();
        using var encryptor = aes.CreateEncryptor();
        byte[] iv = aes.IV;
        byte[] ciphertext = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);
        return Convert.ToBase64String(iv) + ":" + Convert.ToBase64String(ciphertext);
    }

    public static string Decrypt(string encrypted)
    {
        var parts = encrypted.Split(':');
        if (parts.Length != 2) throw new FormatException("Invalid token format.");
        byte[] iv = Convert.FromBase64String(parts[0]);
        byte[] ciphertext = Convert.FromBase64String(parts[1]);

        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key = KeyBytes;
        aes.IV = iv;
        using var decryptor = aes.CreateDecryptor();
        byte[] plaintext = decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
        return Encoding.UTF8.GetString(plaintext);
    }
}

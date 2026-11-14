using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NSec.Cryptography;

namespace CryptoAnalyzer.Exchanges;

/// <summary>Builds Nobitex API-key headers for the exact HTTP method, path and body.</summary>
internal static class NobitexApiKeyAuthentication
{
    public static void Apply(HttpRequestMessage request, ExchangeCredentials credentials, byte[] rawBody)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(credentials);
        if (string.IsNullOrWhiteSpace(credentials.ApiKey) || string.IsNullOrWhiteSpace(credentials.ApiSecret))
            throw new UnauthorizedAccessException("Nobitex API public key and private Ed25519 signing key are both required.");

        byte[] publicKey = DecodeKey(credentials.ApiKey, "public");
        byte[] privateSeed = DecodeKey(credentials.ApiSecret, "private");
        try
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Nobitex request URI is missing.");
            string pathAndQuery = uri.IsAbsoluteUri ? uri.PathAndQuery : uri.OriginalString;
            if (!pathAndQuery.StartsWith("/", StringComparison.Ordinal))
                pathAndQuery = "/" + pathAndQuery;

            string timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            byte[] prefix = Encoding.UTF8.GetBytes(timestamp + request.Method.Method.ToUpperInvariant() + pathAndQuery);
            byte[] message = new byte[prefix.Length + rawBody.Length];
            Buffer.BlockCopy(prefix, 0, message, 0, prefix.Length);
            Buffer.BlockCopy(rawBody, 0, message, prefix.Length, rawBody.Length);

            using Key key = Key.Import(SignatureAlgorithm.Ed25519, privateSeed, KeyBlobFormat.RawPrivateKey);
            byte[] derivedPublicKey = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
            bool keyPairMatches = CryptographicOperations.FixedTimeEquals(publicKey, derivedPublicKey);
            CryptographicOperations.ZeroMemory(derivedPublicKey);
            if (!keyPairMatches)
                throw new UnauthorizedAccessException("Nobitex API public key does not match the private Ed25519 signing key; request was not sent.");
            byte[] signature = SignatureAlgorithm.Ed25519.Sign(key, message);

            request.Headers.Remove("Authorization");
            // Nobitex-Key is the API-key identifier issued by the panel. Preserve its
            // original Base64/Base64URL representation (including padding); only the
            // Ed25519 signature is encoded as unpadded Base64URL.
            request.Headers.Add("Nobitex-Key", credentials.ApiKey.Trim());
            request.Headers.Add("Nobitex-Timestamp", timestamp);
            request.Headers.Add("Nobitex-Signature", EncodeUrlSafe(signature));

            CryptographicOperations.ZeroMemory(message);
            CryptographicOperations.ZeroMemory(signature);
        }
        catch (CryptographicException ex)
        {
            throw new UnauthorizedAccessException("Nobitex Ed25519 credentials are invalid; request was not sent.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateSeed);
        }
    }

    private static byte[] DecodeKey(string value, string keyType)
    {
        try
        {
            string normalized = value.Trim().Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight(normalized.Length + (4 - normalized.Length % 4) % 4, '=');
            byte[] decoded = Convert.FromBase64String(normalized);
            if (decoded.Length != 32)
            {
                CryptographicOperations.ZeroMemory(decoded);
                throw new FormatException();
            }
            return decoded;
        }
        catch (FormatException ex)
        {
            throw new UnauthorizedAccessException($"Nobitex API {keyType} key must be a URL-safe Base64 encoded 32-byte Ed25519 {(keyType == "private" ? "seed" : "public key")}.", ex);
        }
    }

    private static string EncodeUrlSafe(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

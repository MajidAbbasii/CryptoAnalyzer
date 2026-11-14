using CryptoAnalyzer.Exchanges;
using Microsoft.Data.Sqlite;

namespace CryptoAnalyzer.Persistence;

public sealed record UserExchangeAccountSummary(
    long Id, string ExchangeCode, string AccountLabel, string PermissionsJson,
    bool IsActive, DateTime CreatedAt, DateTime UpdatedAt);

/// <summary>All account lookups are scoped by Telegram ChatId; secrets are never returned in summaries.</summary>
public sealed class UserExchangeAccountRepository(string connectionString)
{
    public long Add(long chatId, string exchangeCode, string accountLabel,
        string apiKey, string? apiSecret = null, string? passphrase = null, string permissionsJson = "{}")
    {
        if (chatId <= 0) throw new ArgumentOutOfRangeException(nameof(chatId));
        exchangeCode = NormalizeRequired(exchangeCode, nameof(exchangeCode), 32).ToUpperInvariant();
        accountLabel = NormalizeRequired(accountLabel, nameof(accountLabel), 80);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        apiSecret ??= ""; // API-key exchanges store the private signing key here, encrypted per account.
        permissionsJson = string.IsNullOrWhiteSpace(permissionsJson) ? "{}" : permissionsJson;
        using var json = System.Text.Json.JsonDocument.Parse(permissionsJson);
        if (json.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            throw new ArgumentException("Permissions must be a JSON object.", nameof(permissionsJson));

        string encryptedKey = TokenEncryption.Encrypt(apiKey);
        string encryptedSecret = TokenEncryption.Encrypt(apiSecret);
        string? encryptedPassphrase = string.IsNullOrEmpty(passphrase) ? null : TokenEncryption.Encrypt(passphrase);

        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO UserExchangeAccounts
                (ChatId, ExchangeCode, AccountLabel, ApiKeyEncrypted, ApiSecretEncrypted,
                 PassphraseEncrypted, PermissionsJson)
            SELECT $chatId, $exchange, $label, $apiKey, $apiSecret, $passphrase, $permissions
            WHERE EXISTS (SELECT 1 FROM Users WHERE ChatId=$chatId);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$chatId", chatId);
        command.Parameters.AddWithValue("$exchange", exchangeCode);
        command.Parameters.AddWithValue("$label", accountLabel);
        command.Parameters.AddWithValue("$apiKey", encryptedKey);
        command.Parameters.AddWithValue("$apiSecret", encryptedSecret);
        command.Parameters.AddWithValue("$passphrase", (object?)encryptedPassphrase ?? DBNull.Value);
        command.Parameters.AddWithValue("$permissions", permissionsJson);
        var result = command.ExecuteScalar();
        if (result is null || result == DBNull.Value || Convert.ToInt64(result) == 0)
            throw new InvalidOperationException("User must be registered before adding an exchange account.");
        return Convert.ToInt64(result);
    }

    public IReadOnlyList<UserExchangeAccountSummary> List(long chatId)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, ExchangeCode, AccountLabel, PermissionsJson, IsActive, CreatedAt, UpdatedAt
            FROM UserExchangeAccounts WHERE ChatId=$chatId ORDER BY Id;
            """;
        command.Parameters.AddWithValue("$chatId", chatId);
        using var reader = command.ExecuteReader();
        var result = new List<UserExchangeAccountSummary>();
        while (reader.Read())
            result.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetInt32(4) == 1,
                DateTime.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind),
                DateTime.Parse(reader.GetString(6), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        return result;
    }

    public ExchangeCredentials GetCredentials(long chatId, long accountId)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ApiKeyEncrypted, ApiSecretEncrypted, PassphraseEncrypted
            FROM UserExchangeAccounts
            WHERE Id=$id AND ChatId=$chatId AND IsActive=1;
            """;
        command.Parameters.AddWithValue("$id", accountId);
        command.Parameters.AddWithValue("$chatId", chatId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException("Active exchange account not found for this user.");
        return new ExchangeCredentials(TokenEncryption.Decrypt(reader.GetString(0)),
            TokenEncryption.Decrypt(reader.GetString(1)), reader.IsDBNull(2) ? null : TokenEncryption.Decrypt(reader.GetString(2)));
    }

    public bool UpdateCredentials(long chatId, long accountId, string apiKey, string apiSecret)
    {
        if (chatId <= 0) throw new ArgumentOutOfRangeException(nameof(chatId));
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiSecret);
        string encryptedKey = TokenEncryption.Encrypt(apiKey);
        string encryptedSecret = TokenEncryption.Encrypt(apiSecret);

        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE UserExchangeAccounts
            SET ApiKeyEncrypted=$apiKey, ApiSecretEncrypted=$apiSecret, UpdatedAt=CURRENT_TIMESTAMP
            WHERE Id=$id AND ChatId=$chatId AND ExchangeCode='NOBITEX' AND IsActive=1;
            """;
        command.Parameters.AddWithValue("$apiKey", encryptedKey);
        command.Parameters.AddWithValue("$apiSecret", encryptedSecret);
        command.Parameters.AddWithValue("$id", accountId);
        command.Parameters.AddWithValue("$chatId", chatId);
        return command.ExecuteNonQuery() == 1;
    }

    public bool Deactivate(long chatId, long accountId)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE UserExchangeAccounts SET IsActive=0, UpdatedAt=CURRENT_TIMESTAMP WHERE Id=$id AND ChatId=$chatId AND IsActive=1;";
        command.Parameters.AddWithValue("$id", accountId);
        command.Parameters.AddWithValue("$chatId", chatId);
        return command.ExecuteNonQuery() == 1;
    }

    private static string NormalizeRequired(string value, string name, int maxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        value = value.Trim();
        if (value.Length > maxLength) throw new ArgumentOutOfRangeException(name);
        return value;
    }
}

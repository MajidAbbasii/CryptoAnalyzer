using Microsoft.Data.Sqlite;

namespace CryptoAnalyzer.Persistence;

public static class SignalSourcePreference
{
    public const string Nobitex = "NOBITEX";
    public const string Binance = "BINANCE";
    public const string Both = "BOTH";

    public static bool IsValid(string? value) => value is Nobitex or Binance or Both;
}

public sealed class UserSignalPreferenceRepository(string connectionString)
{
    public string GetSource(long chatId)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Source FROM UserSignalPreferences WHERE ChatId=$chatId";
        command.Parameters.AddWithValue("$chatId", chatId);
        return command.ExecuteScalar() as string ?? SignalSourcePreference.Nobitex;
    }

    public bool SetSource(long chatId, string source)
    {
        if (!SignalSourcePreference.IsValid(source)) return false;
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO UserSignalPreferences(ChatId, Source, UpdatedAt)
            SELECT ChatId, $source, CURRENT_TIMESTAMP FROM Users WHERE ChatId=$chatId
            ON CONFLICT(ChatId) DO UPDATE SET Source=excluded.Source, UpdatedAt=CURRENT_TIMESTAMP;
            """;
        command.Parameters.AddWithValue("$chatId", chatId);
        command.Parameters.AddWithValue("$source", source);
        return command.ExecuteNonQuery() == 1;
    }
}

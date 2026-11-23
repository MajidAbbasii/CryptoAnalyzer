using Microsoft.Data.Sqlite;

namespace CryptoAnalyzer.Persistence;

public static class DatabaseInitializer
{
    public static void Initialize(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        using (var foreignKeys = connection.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_keys = ON;";
            foreignKeys.ExecuteNonQuery();
        }

        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, "CREATE TABLE IF NOT EXISTS SchemaMigrations (Version INTEGER PRIMARY KEY, AppliedAt TEXT NOT NULL);");

        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS Users (
                ChatId INTEGER PRIMARY KEY,
                Username TEXT,
                FirstName TEXT,
                IsActive INTEGER NOT NULL DEFAULT 1,
                CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            CREATE TABLE IF NOT EXISTS UserSignalPreferences (
                ChatId INTEGER PRIMARY KEY,
                Source TEXT NOT NULL DEFAULT 'NOBITEX',
                UpdatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                FOREIGN KEY (ChatId) REFERENCES Users(ChatId) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS SignalTracks (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Symbol TEXT NOT NULL,
                Side TEXT NOT NULL DEFAULT 'LONG',
                TradeMode TEXT NOT NULL DEFAULT 'MARGIN',
                ExchangeCode TEXT NOT NULL DEFAULT 'NOBITEX',
                EntryPrice REAL NOT NULL,
                StopLoss REAL NOT NULL,
                TakeProfit REAL NOT NULL,
                Status TEXT NOT NULL DEFAULT 'Pending',
                SignalAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                ResolvedAt TEXT,
                ResolvePrice REAL
            );
            CREATE TABLE IF NOT EXISTS SignalEvaluationLogs (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SignalTrackId INTEGER NOT NULL,
                CheckedAt TEXT NOT NULL,
                MarketPrice REAL,
                Outcome TEXT NOT NULL,
                Details TEXT,
                FOREIGN KEY (SignalTrackId) REFERENCES SignalTracks(Id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS AnalysisResults (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Symbol TEXT,
                ExchangeCode TEXT NOT NULL DEFAULT 'NOBITEX',
                Timeframe TEXT DEFAULT '15m+1h',
                Price REAL,
                RSI REAL,
                BuyScore INTEGER,
                SellScore INTEGER,
                Decision TEXT,
                CreatedAt TEXT DEFAULT CURRENT_TIMESTAMP
            );
            CREATE TABLE IF NOT EXISTS OpenTrades (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ChatId INTEGER NOT NULL,
                Symbol TEXT NOT NULL,
                EntryPrice REAL NOT NULL,
                StopLoss REAL NOT NULL,
                TakeProfit REAL NOT NULL,
                Status TEXT NOT NULL DEFAULT 'Open',
                ExitPrice REAL,
                ExitReason TEXT,
                OpenedAt TEXT DEFAULT CURRENT_TIMESTAMP,
                ClosedAt TEXT,
                Side TEXT NOT NULL DEFAULT 'LONG',
                Leverage INTEGER NOT NULL DEFAULT 1,
                TradeMode TEXT NOT NULL DEFAULT 'MARGIN',
                ExchangeCode TEXT NOT NULL DEFAULT 'NOBITEX',
                ExchangeAccountId INTEGER,
                ExternalPositionId TEXT,
                Quantity TEXT NOT NULL DEFAULT '0',
                OcoStatus TEXT NOT NULL DEFAULT 'NotSubmitted',
                OcoOrderIds TEXT,
                OcoMessage TEXT
            );
            CREATE TABLE IF NOT EXISTS UserExchangeAccounts (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ChatId INTEGER NOT NULL,
                ExchangeCode TEXT NOT NULL,
                AccountLabel TEXT NOT NULL,
                ApiKeyEncrypted TEXT NOT NULL,
                ApiSecretEncrypted TEXT NOT NULL,
                PassphraseEncrypted TEXT,
                PermissionsJson TEXT NOT NULL DEFAULT '{}',
                IsActive INTEGER NOT NULL DEFAULT 1,
                CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                UpdatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                FOREIGN KEY (ChatId) REFERENCES Users(ChatId) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS ManualOrderApprovals (
                Id TEXT PRIMARY KEY,
                ChatId INTEGER NOT NULL,
                ExchangeAccountId INTEGER NOT NULL,
                ExchangeCode TEXT NOT NULL,
                Symbol TEXT NOT NULL,
                TradeMode TEXT NOT NULL,
                Side TEXT NOT NULL,
                OrderType TEXT NOT NULL,
                Quantity TEXT NOT NULL,
                LimitPrice TEXT,
                StopLoss TEXT,
                TakeProfit TEXT,
                Leverage INTEGER NOT NULL DEFAULT 1,
                PriceGuard TEXT,
                Status TEXT NOT NULL DEFAULT 'AwaitingApproval',
                ExpiresAt TEXT NOT NULL,
                CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                ApprovedAt TEXT,
                RejectedAt TEXT,
                TradeRegisteredAt TEXT,
                ExternalOrderId TEXT,
                FOREIGN KEY (ChatId) REFERENCES Users(ChatId),
                FOREIGN KEY (ExchangeAccountId) REFERENCES UserExchangeAccounts(Id)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS UX_UserExchangeAccounts_Owner_Exchange_Label
                ON UserExchangeAccounts(ChatId, ExchangeCode, AccountLabel);
            CREATE INDEX IF NOT EXISTS IX_OpenTrades_User_Status ON OpenTrades(ChatId, Status);
            CREATE INDEX IF NOT EXISTS IX_SignalTracks_Status_Symbol ON SignalTracks(Status, Symbol);
            CREATE INDEX IF NOT EXISTS IX_SignalEvaluationLogs_Signal_CheckedAt
                ON SignalEvaluationLogs(SignalTrackId, CheckedAt);
            CREATE INDEX IF NOT EXISTS IX_ManualOrderApprovals_User_Status ON ManualOrderApprovals(ChatId, Status);
            """);

        // Upgrade databases created by earlier versions without dropping user data.
        EnsureColumn(connection, transaction, "SignalTracks", "Side", "TEXT NOT NULL DEFAULT 'LONG'");
        EnsureColumn(connection, transaction, "SignalTracks", "TradeMode", "TEXT NOT NULL DEFAULT 'MARGIN'");
        EnsureColumn(connection, transaction, "SignalTracks", "ExchangeCode", "TEXT NOT NULL DEFAULT 'NOBITEX'");
        EnsureColumn(connection, transaction, "AnalysisResults", "ExchangeCode", "TEXT NOT NULL DEFAULT 'NOBITEX'");
        EnsureColumn(connection, transaction, "OpenTrades", "Side", "TEXT NOT NULL DEFAULT 'LONG'");
        EnsureColumn(connection, transaction, "OpenTrades", "Leverage", "INTEGER NOT NULL DEFAULT 1");
        EnsureColumn(connection, transaction, "OpenTrades", "TradeMode", "TEXT NOT NULL DEFAULT 'MARGIN'");
        EnsureColumn(connection, transaction, "OpenTrades", "ExchangeCode", "TEXT NOT NULL DEFAULT 'NOBITEX'");
        EnsureColumn(connection, transaction, "OpenTrades", "ExchangeAccountId", "INTEGER");
        EnsureColumn(connection, transaction, "OpenTrades", "ExternalPositionId", "TEXT");
        EnsureColumn(connection, transaction, "OpenTrades", "Quantity", "TEXT NOT NULL DEFAULT '0'");
        EnsureColumn(connection, transaction, "OpenTrades", "OcoStatus", "TEXT NOT NULL DEFAULT 'NotSubmitted'");
        EnsureColumn(connection, transaction, "OpenTrades", "OcoOrderIds", "TEXT");
        EnsureColumn(connection, transaction, "OpenTrades", "OcoMessage", "TEXT");
        EnsureColumn(connection, transaction, "ManualOrderApprovals", "Leverage", "INTEGER NOT NULL DEFAULT 1");
        EnsureColumn(connection, transaction, "ManualOrderApprovals", "PriceGuard", "TEXT");
        EnsureColumn(connection, transaction, "ManualOrderApprovals", "TradeRegisteredAt", "TEXT");

        Execute(connection, transaction,
            "INSERT OR IGNORE INTO SchemaMigrations(Version, AppliedAt) VALUES (1, CURRENT_TIMESTAMP);");
        transaction.Commit();
    }

    private static void EnsureColumn(SqliteConnection connection, SqliteTransaction transaction,
        string table, string column, string definition)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info({table});";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return;
        }
        reader.Close();
        Execute(connection, transaction, $"ALTER TABLE {table} ADD COLUMN {column} {definition};");
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

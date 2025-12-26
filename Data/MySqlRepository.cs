using System.Data;
using MySqlConnector;
using BanCheckPlugin.Steam;

namespace BanCheckPlugin.Data;

public sealed class MySqlRepository
{
    private readonly string _connectionString;

    public MySqlRepository(string connectionString)
    {
        _connectionString = connectionString;
    }

    private MySqlConnection CreateConnection() => new(_connectionString);

    public async Task EnsureTablesAsync(bool createUserInfo, bool createServerInfo, CancellationToken ct)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct).ConfigureAwait(false);

        if (createUserInfo)
        {
            var sql = @"
CREATE TABLE IF NOT EXISTS user_info (
  steamid64 VARCHAR(64) NOT NULL,
  name VARCHAR(255) NOT NULL,
  avatar VARCHAR(512) NULL,
  avatarmedium VARCHAR(512) NULL,
  avatarfull VARCHAR(512) NULL,
  profileurl VARCHAR(512) NULL,
  facebook VARCHAR(512) NULL,
  spotify VARCHAR(512) NULL,
  twitter VARCHAR(512) NULL,
  instagram VARCHAR(512) NULL,
  github VARCHAR(512) NULL,
  google_id VARCHAR(255) NULL,
  discord_id VARCHAR(255) NULL,
  github_oauth_id VARCHAR(255) NULL,
  role VARCHAR(20) NOT NULL DEFAULT 'user',
  banned TINYINT NOT NULL DEFAULT 0,
  last_updated TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (steamid64),
  KEY idx_last_updated (last_updated),
  KEY idx_google_id (google_id),
  KEY idx_discord_id (discord_id),
  KEY idx_github_oauth_id (github_oauth_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;";
            await using var cmd = new MySqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (createServerInfo)
        {
            var sql = @"
CREATE TABLE IF NOT EXISTS server_info (
  server_key VARCHAR(128) NOT NULL,
  server_name VARCHAR(255) NOT NULL,
  ip VARCHAR(64) NULL,
  port INT NULL,
  registered_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
  last_heartbeat TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (server_key)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;";
            await using var cmd = new MySqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task<(bool Found, bool Banned, DateTime? LastUpdatedUtc)> GetBanStatusAsync(string steamId64, CancellationToken ct)
    {
        const string sql = @"SELECT banned, last_updated FROM user_info WHERE steamid64 = @id LIMIT 1;";
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", steamId64);

        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SingleRow, ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return (false, false, null);

        var banned = reader.GetInt32(0) != 0;
        DateTime? lastUpdated = reader.IsDBNull(1) ? null : reader.GetDateTime(1);
        return (true, banned, lastUpdated?.ToUniversalTime());
    }

    public async Task UpsertUserInfoAsync(SteamPlayerSummary summary, CancellationToken ct)
    {
        // DO NOT overwrite role/banned; only update profile fields + last_updated via ON DUPLICATE KEY UPDATE
        const string sql = @"
INSERT INTO user_info (steamid64, name, avatar, avatarmedium, avatarfull, profileurl)
VALUES (@steamid64, @name, @avatar, @avatarmedium, @avatarfull, @profileurl)
ON DUPLICATE KEY UPDATE
  name = VALUES(name),
  avatar = VALUES(avatar),
  avatarmedium = VALUES(avatarmedium),
  avatarfull = VALUES(avatarfull),
  profileurl = VALUES(profileurl);";

        await using var conn = CreateConnection();
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@steamid64", summary.SteamId);
        cmd.Parameters.AddWithValue("@name", Truncate(summary.PersonaName ?? "Unknown", 255));
        cmd.Parameters.AddWithValue("@avatar", Truncate(summary.Avatar, 512));
        cmd.Parameters.AddWithValue("@avatarmedium", Truncate(summary.AvatarMedium, 512));
        cmd.Parameters.AddWithValue("@avatarfull", Truncate(summary.AvatarFull, 512));
        cmd.Parameters.AddWithValue("@profileurl", Truncate(summary.ProfileUrl, 512));

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task InsertUserIfMissingAsync(string steamId64, string nameFallback, CancellationToken ct)
    {
        const string sql = @"
INSERT INTO user_info (steamid64, name)
VALUES (@steamid64, @name)
ON DUPLICATE KEY UPDATE name = name;";
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@steamid64", steamId64);
        cmd.Parameters.AddWithValue("@name", Truncate(string.IsNullOrWhiteSpace(nameFallback) ? "Unknown" : nameFallback, 255));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task RegisterOrHeartbeatServerAsync(string serverKey, string serverName, string? ip, int? port, CancellationToken ct)
    {
        const string sql = @"
INSERT INTO server_info (server_key, server_name, ip, port)
VALUES (@k, @n, @ip, @p)
ON DUPLICATE KEY UPDATE
  server_name = VALUES(server_name),
  ip = VALUES(ip),
  port = VALUES(port),
  last_heartbeat = CURRENT_TIMESTAMP;";
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@k", serverKey);
        cmd.Parameters.AddWithValue("@n", Truncate(serverName, 255));
        cmd.Parameters.AddWithValue("@ip", Truncate(ip, 64));
        cmd.Parameters.AddWithValue("@p", (object?)port ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static string? Truncate(string? value, int maxLen)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= maxLen ? value : value[..maxLen];
    }
}

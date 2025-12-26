using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;

namespace BanCheckPlugin.Config;

public sealed class BanCheckConfig : BasePluginConfig
{
    [JsonPropertyName("Database")]
    public DatabaseSection Database { get; set; } = new();

    [JsonPropertyName("Registration")]
    public RegistrationSection Registration { get; set; } = new();

    [JsonPropertyName("Steam")]
    public SteamSection Steam { get; set; } = new();

    [JsonPropertyName("BanCheck")]
    public BanCheckSection BanCheck { get; set; } = new();

    [JsonPropertyName("ConfigVersion")]
    public override int Version { get; set; } = 1;
}

public sealed class DatabaseSection
{
    [JsonPropertyName("ConnectionString")]
    public string ConnectionString { get; set; } = "";
}

public sealed class RegistrationSection
{
    [JsonPropertyName("Enabled")]
    public bool Enabled { get; set; } = true;

    // Either set this in config OR provide env var SERVER_KEY
    [JsonPropertyName("ServerKey")]
    public string ServerKey { get; set; } = "";

    [JsonPropertyName("ServerName")]
    public string ServerName { get; set; } = "CS2 Server";

    [JsonPropertyName("ServerIp")]
    public string? ServerIp { get; set; } = null;

    [JsonPropertyName("ServerPort")]
    public int? ServerPort { get; set; } = null;

    [JsonPropertyName("HeartbeatSeconds")]
    public int HeartbeatSeconds { get; set; } = 60;

    // If true, plugin will CREATE TABLE IF NOT EXISTS (user_info + server_info)
    [JsonPropertyName("AutoCreateTables")]
    public bool AutoCreateTables { get; set; } = false;
}

public sealed class SteamSection
{
    [JsonPropertyName("UseSteamWebApi")]
    public bool UseSteamWebApi { get; set; } = true;

    // MUST be "API_KEY" per your request (but configurable just in case)
    [JsonPropertyName("ApiKeyEnvVar")]
    public string ApiKeyEnvVar { get; set; } = "API_KEY";

    // Refresh Steam profile into DB if last_updated older than this
    [JsonPropertyName("RefreshMinutes")]
    public int RefreshMinutes { get; set; } = 1440;

    [JsonPropertyName("TimeoutSeconds")]
    public int TimeoutSeconds { get; set; } = 5;
}

public sealed class BanCheckSection
{
    [JsonPropertyName("KickReason")]
    public string KickReason { get; set; } = "You are banned from this server.";

    // If true: allow player when DB/Steam API fails (recommended to avoid false kicks)
    [JsonPropertyName("FailOpen")]
    public bool FailOpen { get; set; } = true;

    // Cache ban decisions (steamid64 -> banned?) to reduce DB load
    [JsonPropertyName("CacheSeconds")]
    public int CacheSeconds { get; set; } = 60;

    // If true: if Steam profile fetch fails for new users, use in-game name and still insert row.
    [JsonPropertyName("InsertIfMissing")]
    public bool InsertIfMissing { get; set; } = true;
}

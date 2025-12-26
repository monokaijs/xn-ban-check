using System.Text.Json.Serialization;

namespace BanCheckPlugin.Steam;

public sealed class PlayerSummariesResponse
{
    [JsonPropertyName("response")]
    public PlayerSummariesInner? Response { get; set; }
}

public sealed class PlayerSummariesInner
{
    [JsonPropertyName("players")]
    public SteamPlayerSummary[] Players { get; set; } = [];
}

public sealed class SteamPlayerSummary
{
    [JsonPropertyName("steamid")]
    public string SteamId { get; set; } = "";

    [JsonPropertyName("personaname")]
    public string PersonaName { get; set; } = "Unknown";

    [JsonPropertyName("profileurl")]
    public string? ProfileUrl { get; set; }

    [JsonPropertyName("avatar")]
    public string? Avatar { get; set; }

    [JsonPropertyName("avatarmedium")]
    public string? AvatarMedium { get; set; }

    [JsonPropertyName("avatarfull")]
    public string? AvatarFull { get; set; }
}

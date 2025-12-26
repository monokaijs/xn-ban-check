using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Timers;
using BanCheckPlugin.Config;
using BanCheckPlugin.Data;
using BanCheckPlugin.Steam;
using BanCheckPlugin.Utils;
using CounterStrikeSharp.API.Modules.Commands;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace BanCheckPlugin;

[MinimumApiVersion(80)]
public sealed class BanCheckPlugin : BasePlugin
{
    public override string ModuleName => "BanCheckPlugin";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "MonokaiJs";
    public override string ModuleDescription => "Kick banned users based on MySQL user_info.banned; optionally registers server in DB.";

    private readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private BanCheckConfig _config = new();
    private MySqlRepository? _repo;

    private HttpClient? _http;
    private SteamWebApiClient? _steam;

    private TimedCache<string, bool>? _banCache;
    private readonly HashSet<int> _inFlightSlots = new();
    private readonly object _inFlightLock = new();

    private CancellationTokenSource? _cts;
    private Timer? _heartbeatTimer;

    public override void Load(bool hotReload)
    {
        _cts = new CancellationTokenSource();

        LoadOrCreateConfig();

        _banCache = new TimedCache<string, bool>(TimeSpan.FromSeconds(Math.Max(1, _config.BanCheck.CacheSeconds)));

        _repo = new MySqlRepository(ResolveConnectionString(_config));

        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(1, _config.Steam.TimeoutSeconds))
        };
        _steam = new SteamWebApiClient(_http);

        // Listeners
        RegisterListener<Listeners.OnClientPutInServer>(OnClientPutInServer);
        RegisterListener<Listeners.OnClientDisconnect>(OnClientDisconnect);

        // Commands (server console)
        AddCommand("css_bancheck_reload", "Reload BanCheckPlugin config", CmdReload);
        AddCommand("css_bancheck_register", "Register server key into DB: css_bancheck_register <server_key>", CmdRegister);

        // Startup tasks
        _ = Task.Run(() => StartupAsync(_cts.Token));

        Logger.LogInformation("[BanCheckPlugin] Loaded. Config: {Path}", GetConfigPath());
    }

    public override void Unload(bool hotReload)
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }

        try { _heartbeatTimer?.Kill(); } catch { /* ignore */ }
        _heartbeatTimer = null;

        _http?.Dispose();
        _http = null;
        _steam = null;

        _repo = null;

        _banCache?.Clear();
        _banCache = null;

        Logger.LogInformation("[BanCheckPlugin] Unloaded.");
    }

    private void OnClientDisconnect(int playerSlot)
    {
        lock (_inFlightLock) _inFlightSlots.Remove(playerSlot);
    }

    private void OnClientPutInServer(int playerSlot)
    {
        // Avoid duplicate checks for the same slot while async work is running
        lock (_inFlightLock)
        {
            if (_inFlightSlots.Contains(playerSlot)) return;
            _inFlightSlots.Add(playerSlot);
        }

        var player = Utilities.GetPlayerFromSlot(playerSlot);
        if (player == null || !player.IsValid)
        {
            lock (_inFlightLock) _inFlightSlots.Remove(playerSlot);
            return;
        }

        // Skip bots / HLTV
        if (player.IsBot || player.IsHLTV)
        {
            lock (_inFlightLock) _inFlightSlots.Remove(playerSlot);
            return;
        }

        var steamId64 = player.AuthorizedSteamID?.SteamId64.ToString();
        if (string.IsNullOrWhiteSpace(steamId64))
        {
            lock (_inFlightLock) _inFlightSlots.Remove(playerSlot);
            return;
        }

        var nameFallback = player.PlayerName ?? "Unknown";

        _ = Task.Run(() => HandleJoinAsync(playerSlot, steamId64!, nameFallback, _cts?.Token ?? CancellationToken.None));
    }

    private async Task StartupAsync(CancellationToken ct)
    {
        if (_repo == null) return;

        try
        {
            if (_config.Registration.AutoCreateTables)
            {
                await _repo.EnsureTablesAsync(createUserInfo: true, createServerInfo: true, ct).ConfigureAwait(false);
                Logger.LogInformation("[BanCheckPlugin] Ensured tables (AutoCreateTables=true).");
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[BanCheckPlugin] Failed to ensure tables.");
        }

        await TryRegisterServerAsync(ct).ConfigureAwait(false);
        SetupHeartbeatTimer();
    }

    private async Task HandleJoinAsync(int playerSlot, string steamId64, string nameFallback, CancellationToken ct)
    {
        try
        {
            if (_repo == null || _banCache == null)
                return;

            // Quick cache hit
            if (_banCache.TryGet(steamId64, out var cachedBanned))
            {
                if (cachedBanned) KickOnMainThread(playerSlot, _config.BanCheck.KickReason);
                return;
            }

            var (found, banned, lastUpdatedUtc) = await _repo.GetBanStatusAsync(steamId64, ct).ConfigureAwait(false);

            if (found)
            {
                _banCache.Set(steamId64, banned);

                if (banned)
                {
                    KickOnMainThread(playerSlot, _config.BanCheck.KickReason);
                    return;
                }

                // Optionally refresh Steam profile if stale
                if (_config.Steam.UseSteamWebApi && ShouldRefreshSteam(lastUpdatedUtc, _config.Steam.RefreshMinutes))
                    await TryFetchAndUpsertSteamAsync(steamId64, ct).ConfigureAwait(false);

                return;
            }

            // Not found
            if (_config.BanCheck.InsertIfMissing)
            {
                // Insert minimal record first (name required NOT NULL)
                await _repo.InsertUserIfMissingAsync(steamId64, nameFallback, ct).ConfigureAwait(false);
            }

            if (_config.Steam.UseSteamWebApi)
                await TryFetchAndUpsertSteamAsync(steamId64, ct).ConfigureAwait(false);

            // New users are not banned by default
            _banCache.Set(steamId64, false);
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[BanCheckPlugin] Error checking ban status for {SteamId64}", steamId64);

            if (!_config.BanCheck.FailOpen)
                KickOnMainThread(playerSlot, "Ban check unavailable. Please try again later.");
        }
        finally
        {
            lock (_inFlightLock) _inFlightSlots.Remove(playerSlot);
        }
    }

    private async Task TryFetchAndUpsertSteamAsync(string steamId64, CancellationToken ct)
    {
        if (_repo == null || _steam == null) return;

        var apiKey = Environment.GetEnvironmentVariable(_config.Steam.ApiKeyEnvVar) ?? "";
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Logger.LogWarning("[BanCheckPlugin] Steam API key env var '{Env}' is missing; skipping Steam profile fetch.", _config.Steam.ApiKeyEnvVar);
            return;
        }

        var summary = await _steam.GetPlayerSummaryAsync(apiKey, steamId64, ct).ConfigureAwait(false);
        if (summary == null) return;

        // ensure steamid matches
        summary.SteamId = steamId64;

        await _repo.UpsertUserInfoAsync(summary, ct).ConfigureAwait(false);
    }

    private async Task TryRegisterServerAsync(CancellationToken ct)
    {
        if (_repo == null) return;
        if (!_config.Registration.Enabled) return;

        var key = ResolveServerKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            Logger.LogWarning("[BanCheckPlugin] Registration enabled, but no server key found. Set env SERVER_KEY or config Registration.ServerKey.");
            return;
        }

        try
        {
            // If AutoCreateTables is false, server_info may not exist; we still try and log errors.
            await _repo.RegisterOrHeartbeatServerAsync(
                serverKey: key,
                serverName: _config.Registration.ServerName,
                ip: _config.Registration.ServerIp,
                port: _config.Registration.ServerPort,
                ct: ct
            ).ConfigureAwait(false);

            Logger.LogInformation("[BanCheckPlugin] Server registered/heartbeat ok. key={Key}", key);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[BanCheckPlugin] Server registration/heartbeat failed.");
        }
    }

    private void SetupHeartbeatTimer()
    {
        if (!_config.Registration.Enabled) return;

        var interval = Math.Max(10, _config.Registration.HeartbeatSeconds);

        _heartbeatTimer?.Kill();
        _heartbeatTimer = AddTimer(interval, () =>
        {
            // Fire and forget; never block tick thread
            _ = Task.Run(() => TryRegisterServerAsync(_cts?.Token ?? CancellationToken.None));
        }, TimerFlags.REPEAT);
    }

    private void KickOnMainThread(int playerSlot, string reason)
    {
        var safeReason = (reason ?? "Banned").Replace('"', '\'').Trim();
        if (safeReason.Length > 120) safeReason = safeReason[..120];

        // Back to game thread
        Server.NextFrame(() =>
        {
            var player = Utilities.GetPlayerFromSlot(playerSlot);
            if (player == null || !player.IsValid || player.IsBot || player.IsHLTV) return;

            var userId = player.UserId;
            if (!userId.HasValue) return;

            Server.ExecuteCommand($"kickid {userId.Value} \"{safeReason}\"");
        });
    }

    private void CmdReload(CCSPlayerController? caller, CommandInfo command)
    {
        if (caller != null)
        {
            caller.PrintToChat("[BanCheckPlugin] Run this from server console.");
            return;
        }

        LoadOrCreateConfig();
        Logger.LogInformation("[BanCheckPlugin] Config reloaded.");

        // re-init cache & timers with new config
        _banCache = new TimedCache<string, bool>(TimeSpan.FromSeconds(Math.Max(1, _config.BanCheck.CacheSeconds)));
        SetupHeartbeatTimer();
    }

    private void CmdRegister(CCSPlayerController? caller, CommandInfo command)
    {
        if (caller != null)
        {
            caller.PrintToChat("[BanCheckPlugin] Run this from server console.");
            return;
        }

        if (command.ArgCount < 2)
        {
            Logger.LogInformation("[BanCheckPlugin] Usage: css_bancheck_register <server_key>");
            return;
        }

        var serverKey = command.ArgByIndex(1);
        if (string.IsNullOrWhiteSpace(serverKey))
            return;

        _config.Registration.ServerKey = serverKey.Trim();
        SaveConfig();

        _ = Task.Run(() => TryRegisterServerAsync(_cts?.Token ?? CancellationToken.None));

        Logger.LogInformation("[BanCheckPlugin] Saved server key and triggered registration.");
    }

    private void LoadOrCreateConfig()
    {
        var path = GetConfigPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (!File.Exists(path))
        {
            _config = new BanCheckConfig();
            File.WriteAllText(path, JsonSerializer.Serialize(_config, _json));
            return;
        }

        try
        {
            var txt = File.ReadAllText(path);
            _config = JsonSerializer.Deserialize<BanCheckConfig>(txt, _json) ?? new BanCheckConfig();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[BanCheckPlugin] Failed to read config. Using defaults.");
            _config = new BanCheckConfig();
        }
    }

    private void SaveConfig()
    {
        var path = GetConfigPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(_config, _json));
    }

    private string GetConfigPath()
    {
        // Typical CSSharp config layout is:
        // /addons/counterstrikesharp/configs/plugins/<PluginName>/<PluginName>.json
        var cssRoot = Path.GetFullPath(Path.Combine(ModuleDirectory, "..", ".."));
        return Path.Combine(cssRoot, "configs", "plugins", ModuleName, $"{ModuleName}.json");
    }

    private static string ResolveConnectionString(BanCheckConfig cfg)
    {
        if (!string.IsNullOrWhiteSpace(cfg.Database.ConnectionString))
            return cfg.Database.ConnectionString;

        // Env fallback
        var host = Environment.GetEnvironmentVariable("DB_HOST") ?? "127.0.0.1";
        var port = Environment.GetEnvironmentVariable("DB_PORT") ?? "3306";
        var user = Environment.GetEnvironmentVariable("DB_USER") ?? "root";
        var pass = Environment.GetEnvironmentVariable("DB_PASSWORD") ?? "";
        var db = Environment.GetEnvironmentVariable("DB_NAME") ?? "cs2";

        return $"Server={host};Port={port};User ID={user};Password={pass};Database={db};TreatTinyAsBoolean=true;SslMode=None;";
    }

    private string ResolveServerKey()
    {
        return (Environment.GetEnvironmentVariable("SERVER_KEY") ?? _config.Registration.ServerKey ?? "").Trim();
    }

    private static bool ShouldRefreshSteam(DateTime? lastUpdatedUtc, int refreshMinutes)
    {
        if (refreshMinutes <= 0) return false;
        if (lastUpdatedUtc == null) return true;

        return (DateTime.UtcNow - lastUpdatedUtc.Value) > TimeSpan.FromMinutes(refreshMinutes);
    }
}

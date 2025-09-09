using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Timers;
using System.Text.Json;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Net.Http.Json;
using SlotTracker.Config;

namespace SlotTracker;

[MinimumApiVersion(100)]
public class SlotTracker : BasePlugin
{
    public override string ModuleName => "SlotTracker";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "agasking1337";

    private Config.ServerConfig _config = null!;
    private int _serverSlots;
    private string _currentMap = string.Empty;
    private string _sessionId = Guid.NewGuid().ToString(); // Unique session ID for this server instance
    private HttpClient _httpClient = null!;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _apiSyncTimer;
    
    // In-memory state tracking
    private int _tRounds = 0;
    private int _ctRounds = 0;
    private int _playerCount = 0;
    private List<PlayerInfo> _tPlayers = new List<PlayerInfo>();
    private List<PlayerInfo> _ctPlayers = new List<PlayerInfo>();

    public class PlayerInfo
    {
        public string Name { get; set; } = string.Empty;
        public string SteamId { get; set; } = string.Empty;
        public string Team { get; set; } = string.Empty;
        public int Kills { get; set; } = 0;
        public int Deaths { get; set; } = 0;
        public int Assists { get; set; } = 0;
        public int Score { get; set; } = 0;
        public int HeadshotKills { get; set; } = 0;
        public int MVPs { get; set; } = 0;
        public string Ping { get; set; } = string.Empty;
    }

    public override void Load(bool hotReload)
    {
        base.Load(hotReload);
        
        Console.WriteLine("[SlotTracker] Plugin loading...");
        
        _config = LoadConfig();
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("X-API-Key", _config.ApiKey);
        _httpClient.DefaultRequestHeaders.Add("X-Server-ID", _config.ServerId);
        
        // Initialize with default value - don't try to get MaxPlayers yet
        _serverSlots = 10; // Default value
        Console.WriteLine($"[SlotTracker] Server initialized with default slots: {_serverSlots}");
        
        // Register event handlers
        RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnect, HookMode.Post);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect, HookMode.Pre);
        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        
        // Register team change events
        RegisterEventHandler<EventPlayerTeam>(OnPlayerTeamChange, HookMode.Post);
        
        // Register round end event for tracking round wins
        RegisterEventHandler<EventRoundEnd>(OnRoundEnd, HookMode.Post);
        
        // Register command to view team stats
        AddCommand("css_teamstats", "Shows current team statistics", CommandTeamStats);
        
        // Add a timer to initialize when server is ready
        AddTimer(5.0f, () => OnServerReady());
        
        Console.WriteLine("[SlotTracker] Plugin loaded successfully!");
        
        if (_config.EnableApiSync)
        {
            Console.WriteLine("[SlotTracker] API Sync enabled");
            Console.WriteLine($"API Endpoint: {_config.ApiEndpoint}");
            Console.WriteLine($"Server ID: {_config.ServerId}");
            Console.WriteLine($"Server Name: {_config.ServerName}");
            Console.WriteLine($"Sync Interval: {_config.ApiSyncIntervalSeconds} seconds");
        }
        else
        {
            Console.WriteLine("[SlotTracker] API Sync disabled");
        }
    }
    
    public override void Unload(bool hotReload)
    {
        try
        {
            // Make sure to dispose of HttpClient when plugin unloads
            _httpClient?.Dispose();
            _apiSyncTimer?.Kill();
            
            Console.WriteLine("[SlotTracker] Plugin unloaded successfully");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SlotTracker] Error during unload: {ex.Message}");
        }
        finally
        {
            base.Unload(hotReload);
        }
    }
    
    private void OnServerReady()
    {
        try
        {
            Console.WriteLine("[SlotTracker] Server is ready, initializing...");
            
            // Now it's safe to access Server.MaxPlayers and get the current map
            try 
            {
                _serverSlots = Server.MaxPlayers;
                if (string.IsNullOrEmpty(_currentMap))
                {
                    _currentMap = Server.MapName;
                    Console.WriteLine($"[SlotTracker] Initial map set to: {_currentMap}");
                }
                Console.WriteLine($"[SlotTracker] Updated server slots to: {_serverSlots}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SlotTracker] Error getting server slots: {ex.Message}");
                Console.WriteLine("[SlotTracker] Using default value of 10 slots");
            }
            
            // Reset stats for new session
            ResetStats();
            
            // Start API sync timer after server is ready
            if (_config.EnableApiSync)
            {
                _apiSyncTimer = AddTimer(_config.ApiSyncIntervalSeconds, () => SyncDataWithApi(), TimerFlags.REPEAT);
                Console.WriteLine($"[SlotTracker] API sync timer started, interval: {_config.ApiSyncIntervalSeconds}s");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SlotTracker] Error in OnServerReady: {ex.Message}");
        }
    }

    private void CommandTeamStats(CCSPlayerController? player, CommandInfo command)
    {
        try
        {
            // Get current stats from memory
            int tCount = _tPlayers.Count;
            int ctCount = _ctPlayers.Count;
            
            // Display stats
            var message = $"Team Stats - T: {tCount} players, CT: {ctCount} players, Map: {_currentMap}";
            
            if (player != null)
            {
                player.PrintToChat($" \x04[SlotTracker]\x01 {message}");
                
                // Show T side players
                if (_tPlayers.Any())
                {
                    player.PrintToChat($" \x04[SlotTracker]\x01 T Side: {string.Join(", ", _tPlayers.Select(p => p.Name))}");
                }
                
                // Show CT side players
                if (_ctPlayers.Any())
                {
                    player.PrintToChat($" \x04[SlotTracker]\x01 CT Side: {string.Join(", ", _ctPlayers.Select(p => p.Name))}");
                }
                
                // Show round stats
                var roundsMessage = $"Round Wins - T: \x07{_tRounds}\x01, CT: \x0B{_ctRounds}\x01";
                player.PrintToChat($" \x04[SlotTracker]\x01 {roundsMessage}");
            }
            else
            {
                Console.WriteLine($"[SlotTracker] {message}");
                
                if (_tPlayers.Any())
                {
                    Console.WriteLine($"[SlotTracker] T Side: {string.Join(", ", _tPlayers.Select(p => p.Name))}");
                }
                
                if (_ctPlayers.Any())
                {
                    Console.WriteLine($"[SlotTracker] CT Side: {string.Join(", ", _ctPlayers.Select(p => p.Name))}");
                }
                
                Console.WriteLine($"[SlotTracker] Round Wins - T: {_tRounds}, CT: {_ctRounds}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SlotTracker] Error retrieving team stats: {ex.Message}");
            if (player != null)
            {
                player.PrintToChat(" \x02[SlotTracker]\x01 Error retrieving team stats.");
            }
        }
    }
    
    private void OnMapStart(string mapName)
    {
        // Update server slots after map change
        try 
        {
            _serverSlots = Server.MaxPlayers;
            _currentMap = mapName;
            Console.WriteLine($"[SlotTracker] Server slots updated on map start: {_serverSlots}");
            Console.WriteLine($"[SlotTracker] Current map: {_currentMap}");
            
            // Only reset stats if this isn't the initial map load
            if (_apiSyncTimer != null)
            {
                Console.WriteLine($"[SlotTracker] Map changed, resetting stats for new map: {_currentMap}");
                ResetStats();
            }
            else
            {
                Console.WriteLine($"[SlotTracker] Initial map load detected, skipping reset");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SlotTracker] Error in OnMapStart: {ex.Message}");
        }
    }

    private void ResetStats()
    {
        _tRounds = 0;
        _ctRounds = 0;
        _playerCount = 0;
        _tPlayers.Clear();
        _ctPlayers.Clear();
        
        // Scan for current players
        UpdatePlayerLists();
        
        Console.WriteLine($"[SlotTracker] Stats reset. Current players: {_playerCount}");
    }
    
    private void UpdatePlayerLists()
    {
        try
        {
            _tPlayers.Clear();
            _ctPlayers.Clear();
            
            var players = Utilities.GetPlayers();
            if (players == null)
            {
                Console.WriteLine("[SlotTracker] Warning: GetPlayers() returned null");
                return;
            }
            
            foreach (var player in players)
            {
                try
                {
                    if (player == null || !player.IsValid || player.IsBot || player.IsHLTV || 
                        player.Connected != PlayerConnectedState.PlayerConnected)
                    {
                        continue;
                    }
                    
                    var playerInfo = new PlayerInfo
                    {
                        Name = player.PlayerName ?? "Unknown",
                        SteamId = player.SteamID.ToString()
                    };
                    
                    // Collect player stats safely
                    try
                    {
                        if (player.PlayerPawn?.Value?.Controller?.Value != null)
                        {
                            var playerController = player.PlayerPawn.Value.Controller.Value;
                            playerInfo.Kills = player.ActionTrackingServices?.MatchStats?.Kills ?? 0;
                            playerInfo.Deaths = player.ActionTrackingServices?.MatchStats?.Deaths ?? 0;
                            playerInfo.Assists = player.ActionTrackingServices?.MatchStats?.Assists ?? 0;
                            playerInfo.Score = player.Score;
                            playerInfo.HeadshotKills = player.ActionTrackingServices?.MatchStats?.HeadShotKills ?? 0;
                            playerInfo.MVPs = player.MVPs;
                            playerInfo.Ping = player.Ping.ToString();
                        }
                    }
                    catch (Exception statEx)
                    {
                        Console.WriteLine($"[SlotTracker] Warning: Could not get stats for player {player.PlayerName}: {statEx.Message}");
                        // Continue with default values
                    }
                    
                    if (player.TeamNum == (int)CounterStrikeSharp.API.Modules.Utils.CsTeam.Terrorist)
                    {
                        playerInfo.Team = "T";
                        _tPlayers.Add(playerInfo);
                    }
                    else if (player.TeamNum == (int)CounterStrikeSharp.API.Modules.Utils.CsTeam.CounterTerrorist)
                    {
                        playerInfo.Team = "CT";
                        _ctPlayers.Add(playerInfo);
                    }
                }
                catch (Exception playerEx)
                {
                    Console.WriteLine($"[SlotTracker] Error processing player: {playerEx.Message}");
                    // Continue with next player
                }
            }
            
            _playerCount = _tPlayers.Count + _ctPlayers.Count;
            Console.WriteLine($"[SlotTracker] Updated player lists: {_tPlayers.Count} T, {_ctPlayers.Count} CT, Total: {_playerCount}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SlotTracker] Error updating player lists: {ex.Message}");
        }
    }

    private Config.ServerConfig LoadConfig()
    {
        var configPath = Path.Join(ModuleDirectory, "config.json");
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException("Config file not found!");
        }

        var jsonString = File.ReadAllText(configPath);
        var config = JsonSerializer.Deserialize<Config.ServerConfig>(jsonString) 
            ?? throw new Exception("Failed to deserialize config!");
            
        Console.WriteLine($"[SlotTracker] Loaded config from {configPath}");
        Console.WriteLine($"[SlotTracker] ServerName from config: '{config.ServerName}'");
        
        return config;
    }

    private HookResult OnPlayerConnect(EventPlayerConnectFull @event, GameEventInfo info)
    {
        Console.WriteLine("[SlotTracker] OnPlayerConnect event triggered");
        
        var player = @event.Userid;
        if (player == null || player.IsBot || player.IsHLTV)
        {
            return HookResult.Continue;
        }

        Console.WriteLine($"[SlotTracker] Player connecting: {player.PlayerName} (SteamID: {player.SteamID})");
        
        // Update player count
        UpdatePlayerLists();
        SyncDataWithApi();
        
        return HookResult.Continue;
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        Console.WriteLine("[SlotTracker] OnPlayerDisconnect event triggered");
        
        var player = @event.Userid;
        if (player == null || player.IsBot || player.IsHLTV)
        {
            return HookResult.Continue;
        }

        Console.WriteLine($"[SlotTracker] Disconnect event - Player: {player.PlayerName}, SteamID: {player.SteamID}, Reason: {@event.Reason}");
        
        // Remove player from team lists
        RemovePlayerFromTeams(player.SteamID.ToString());
        
        // Update counts and sync
        _playerCount = _tPlayers.Count + _ctPlayers.Count;
        SyncDataWithApi();
        
        return HookResult.Continue;
    }
    
    private void RemovePlayerFromTeams(string steamId)
    {
        _tPlayers.RemoveAll(p => p.SteamId == steamId);
        _ctPlayers.RemoveAll(p => p.SteamId == steamId);
    }

    private HookResult OnPlayerTeamChange(EventPlayerTeam @event, GameEventInfo info)
    {
        try
        {
            var player = @event.Userid;
            if (player == null || player.IsBot || player.IsHLTV)
            {
                return HookResult.Continue;
            }
            
            // Get team information
            int oldTeam = @event.Oldteam;
            int newTeam = @event.Team;
            
            string oldTeamName = GetTeamName(oldTeam);
            string newTeamName = GetTeamName(newTeam);
            
            Console.WriteLine($"[SlotTracker] Player {player.PlayerName} changing team: {oldTeamName} -> {newTeamName}");
            
            // Update our player lists
            string steamId = player.SteamID.ToString();
            RemovePlayerFromTeams(steamId);
            
            var playerInfo = new PlayerInfo
            {
                Name = player.PlayerName,
                SteamId = steamId
            };
            
            if (newTeam == (int)CounterStrikeSharp.API.Modules.Utils.CsTeam.Terrorist)
            {
                playerInfo.Team = "T";
                _tPlayers.Add(playerInfo);
            }
            else if (newTeam == (int)CounterStrikeSharp.API.Modules.Utils.CsTeam.CounterTerrorist)
            {
                playerInfo.Team = "CT";
                _ctPlayers.Add(playerInfo);
            }
            
            // Sync updated data
            SyncDataWithApi();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SlotTracker] Error in OnPlayerTeamChange: {ex.Message}");
        }
        
        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        try
        {
            int winnerTeam = @event.Winner;
            string winnerTeamName = GetTeamName(winnerTeam);
            
            Console.WriteLine($"[SlotTracker] Round ended. Winner: {winnerTeamName} (Team {winnerTeam})");
            
            // Update the appropriate team's round count
            if (winnerTeam == (int)CounterStrikeSharp.API.Modules.Utils.CsTeam.Terrorist)
            {
                // T team won
                _tRounds++;
                Console.WriteLine($"[SlotTracker] Updated T rounds: {_tRounds}");
            }
            else if (winnerTeam == (int)CounterStrikeSharp.API.Modules.Utils.CsTeam.CounterTerrorist)
            {
                // CT team won
                _ctRounds++;
                Console.WriteLine($"[SlotTracker] Updated CT rounds: {_ctRounds}");
            }
            else
            {
                Console.WriteLine($"[SlotTracker] Unhandled winner team: {winnerTeam}");
            }
            
            // Sync updated data
            SyncDataWithApi();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SlotTracker] Error in OnRoundEnd: {ex.Message}");
        }
        
        return HookResult.Continue;
    }
    
    private string GetTeamName(int teamNum)
    {
        return teamNum switch
        {
            0 => "None",
            1 => "Spectator",
            2 => "Terrorist",
            3 => "CT",
            _ => $"Unknown({teamNum})"
        };
    }

    private void SyncDataWithApi(int retryCount = 0)
    {
        try
        {
            if (!_config.EnableApiSync)
            {
                return;
            }
            
            Console.WriteLine($"[SlotTracker] Starting API sync (attempt {retryCount + 1})...");
            Console.WriteLine($"[SlotTracker] Current map for API sync: '{_currentMap}'");
            
            // Ensure player lists are up to date
            UpdatePlayerLists();
            
            // Validate data before sending
            if (string.IsNullOrEmpty(_config.ServerId) || string.IsNullOrEmpty(_config.ApiEndpoint))
            {
                Console.WriteLine("[SlotTracker] Invalid configuration: ServerId or ApiEndpoint is empty");
                return;
            }
            
            // Convert to list of player details for API
            var playerDetails = new List<object>();
            
            // Add T players
            foreach (var player in _tPlayers)
            {
                playerDetails.Add(new {
                    name = player.Name ?? "Unknown",
                    steam_id = player.SteamId ?? "0",
                    team = "T",
                    kills = player.Kills,
                    deaths = player.Deaths,
                    assists = player.Assists,
                    score = player.Score,
                    headshot_kills = player.HeadshotKills,
                    mvps = player.MVPs,
                    ping = player.Ping ?? "0"
                });
            }
            
            // Add CT players
            foreach (var player in _ctPlayers)
            {
                playerDetails.Add(new {
                    name = player.Name ?? "Unknown",
                    steam_id = player.SteamId ?? "0",
                    team = "CT",
                    kills = player.Kills,
                    deaths = player.Deaths,
                    assists = player.Assists,
                    score = player.Score,
                    headshot_kills = player.HeadshotKills,
                    mvps = player.MVPs,
                    ping = player.Ping ?? "0"
                });
            }
            
            // Prepare data for API
            var apiData = new
            {
                server_id = _config.ServerId,
                server_name = _config.ServerName ?? "CS2 Server",
                session_id = _sessionId,
                timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                map_name = _currentMap ?? "unknown",
                player_count = _playerCount,
                server_slots = _serverSlots,
                t_rounds = _tRounds,
                ct_rounds = _ctRounds,
                t_players = _tPlayers.Count,
                ct_players = _ctPlayers.Count,
                players = playerDetails
            };
            
            Console.WriteLine($"[SlotTracker] API data server_name: '{apiData.server_name}'");
            Console.WriteLine($"[SlotTracker] API data map_name: '{apiData.map_name}'");
            
            // Send data to API with timeout
            var content = new StringContent(JsonSerializer.Serialize(apiData), Encoding.UTF8, "application/json");
            
            Task.Run(async () => 
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)); // 30 second timeout
                    var response = await _httpClient.PostAsync(_config.ApiEndpoint, content, cts.Token);
                    var responseString = await response.Content.ReadAsStringAsync();
                    
                    if (response.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"[SlotTracker] API sync successful: {responseString}");
                    }
                    else
                    {
                        Console.WriteLine($"[SlotTracker] API sync failed: {response.StatusCode}, {responseString}");
                        
                        // Retry on certain HTTP errors
                        if (retryCount < 3 && (
                            response.StatusCode == System.Net.HttpStatusCode.InternalServerError ||
                            response.StatusCode == System.Net.HttpStatusCode.BadGateway ||
                            response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable ||
                            response.StatusCode == System.Net.HttpStatusCode.GatewayTimeout
                        ))
                        {
                            Console.WriteLine($"[SlotTracker] Retrying API sync in {1000 * (retryCount + 1)}ms...");
                            await Task.Delay(1000 * (retryCount + 1));
                            SyncDataWithApi(retryCount + 1);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("[SlotTracker] API request timed out");
                    
                    // Retry on timeout
                    if (retryCount < 3)
                    {
                        Console.WriteLine($"[SlotTracker] Retrying API sync after timeout in {1000 * (retryCount + 1)}ms...");
                        await Task.Delay(1000 * (retryCount + 1));
                        SyncDataWithApi(retryCount + 1);
                    }
                }
                catch (HttpRequestException ex)
                {
                    Console.WriteLine($"[SlotTracker] HTTP request error: {ex.Message}");
                    
                    // Retry on network errors
                    if (retryCount < 3)
                    {
                        Console.WriteLine($"[SlotTracker] Retrying API sync after network error in {1000 * (retryCount + 1)}ms...");
                        await Task.Delay(1000 * (retryCount + 1));
                        SyncDataWithApi(retryCount + 1);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SlotTracker] API request error: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SlotTracker] Error in API sync: {ex.Message}");
        }
    }
}

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
    private CounterStrikeSharp.API.Modules.Timers.Timer? _statsUpdateTimer;
    
    // In-memory state tracking
    private int _tRounds = 0;
    private int _ctRounds = 0;
    private int _playerCount = 0;
    private List<PlayerInfo> _tPlayers = new List<PlayerInfo>();
    private List<PlayerInfo> _ctPlayers = new List<PlayerInfo>();
    
    // Side switch tracking
    private bool _sidesSwapped = false;
    private int _lastTotalRounds = 0;
    
    // Round timer tracking
    private DateTime _roundStartTime = DateTime.UtcNow;
    private int _roundTimeRemaining = 0;
    private bool _roundInProgress = false;
    private int _roundDuration = 115; // Will be updated from server CVar
    
    // Hibernation tracking
    private bool _isHibernating = false;
    private DateTime _hibernationStartTime = DateTime.UtcNow;
    private int _hibernationCount = 0;
    private int _lastPlayerCount = 0;
    private string _serverPassword = string.Empty;

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
        
        //Console.WriteLine("[SlotTracker] Plugin loading...");
        
        _config = LoadConfig();
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("X-API-Key", _config.ApiKey);
        _httpClient.DefaultRequestHeaders.Add("X-Server-ID", _config.ServerId);
        
        // Initialize with default value - don't try to get MaxPlayers yet
        _serverSlots = 10; // Default value
        //Console.WriteLine($"[SlotTracker] Server initialized with default slots: {_serverSlots}");
        
        // Register event handlers
        RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnect, HookMode.Post);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect, HookMode.Pre);
        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        
        // Register team change events
        RegisterEventHandler<EventPlayerTeam>(OnPlayerTeamChange, HookMode.Post);
        
        // Register round events for tracking round wins and timer
        RegisterEventHandler<EventRoundStart>(OnRoundStart, HookMode.Post);
        RegisterEventHandler<EventRoundEnd>(OnRoundEnd, HookMode.Post);
        
        // Register events for real-time stats tracking
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath, HookMode.Post);
        RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt, HookMode.Post);
        RegisterEventHandler<EventBombDefused>(OnBombDefused, HookMode.Post);
        RegisterEventHandler<EventBombPlanted>(OnBombPlanted, HookMode.Post);
        RegisterEventHandler<EventHostageRescued>(OnHostageRescued, HookMode.Post);
        RegisterEventHandler<EventRoundMvp>(OnRoundMvp, HookMode.Post);
        
        // Register command to view team stats
        //AddCommand("css_teamstats", "Shows current team statistics", CommandTeamStats);
        
        // Register command to debug round timer
        //AddCommand("css_roundtimer", "Shows round timer debug information", CommandRoundTimer);
        
        // Register command to manually trigger hibernation reset for testing
        //AddCommand("css_hibernation", "Manually trigger hibernation reset for testing", CommandHibernation);
        
        // Register command to check hibernation status
        //AddCommand("css_hibernationcheck", "Check hibernation status and trigger if needed", CommandHibernationCheck);
        
        // Register command to check halftime status
        //AddCommand("css_halftime", "Check halftime and side switch status", CommandHalftime);
        
        // Register command to check server password
        //AddCommand("css_serverpassword", "Check server password status", CommandServerPassword);
        
        // Add a timer to initialize when server is ready
        AddTimer(5.0f, () => OnServerReady());
        
        //Console.WriteLine("[SlotTracker] Plugin loaded successfully!");
        
        if (_config.EnableApiSync)
        {
            //Console.WriteLine("[SlotTracker] API Sync enabled");
            //Console.WriteLine($"API Endpoint: {_config.ApiEndpoint}");
            //Console.WriteLine($"Server ID: {_config.ServerId}");
            //Console.WriteLine($"Server Name: {_config.ServerName}");
            //Console.WriteLine($"Sync Interval: {_config.ApiSyncIntervalSeconds} seconds");
        }
        else
        {
            //Console.WriteLine("[SlotTracker] API Sync disabled");
        }
    }
    
    public override void Unload(bool hotReload)
    {
        try
        {
            // Make sure to dispose of HttpClient when plugin unloads
            _httpClient?.Dispose();
            _apiSyncTimer?.Kill();
            _statsUpdateTimer?.Kill();
            
            //Console.WriteLine("[SlotTracker] Plugin unloaded successfully");
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
            //Console.WriteLine("[SlotTracker] Server is ready, initializing...");
            
            // Now it's safe to access Server.MaxPlayers and get the current map
            try 
            {
                _serverSlots = Server.MaxPlayers;
                if (string.IsNullOrEmpty(_currentMap))
                {
                    _currentMap = Server.MapName;
                    //Console.WriteLine($"[SlotTracker] Initial map set to: {_currentMap}");
                }
                //Console.WriteLine($"[SlotTracker] Updated server slots to: {_serverSlots}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SlotTracker] Error getting server slots: {ex.Message}");
                //Console.WriteLine("[SlotTracker] Using default value of 10 slots");
            }
            
            // Reset stats for new session
            ResetStats();
            
            // Update server password
            UpdateServerPassword();
            
            // Start API sync timer after server is ready
            if (_config.EnableApiSync)
            {
                // Send data every second for real-time updates
                _apiSyncTimer = AddTimer(1.0f, () => SyncDataWithApi(), TimerFlags.REPEAT);
                //Console.WriteLine("[SlotTracker] API sync timer started, interval: 1s (real-time mode)");
                
                // Start periodic stats update timer (every 5 seconds)
                _statsUpdateTimer = AddTimer(5.0f, () => UpdateAllPlayerStats(), TimerFlags.REPEAT);
                //Console.WriteLine("[SlotTracker] Stats update timer started, interval: 5s");
                
                // Start round timer update (every second)
                AddTimer(1.0f, () => UpdateRoundTimer(), TimerFlags.REPEAT);
                //Console.WriteLine("[SlotTracker] Round timer update started, interval: 1s");
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
                //Console.WriteLine($"[SlotTracker] {message}");
                
                if (_tPlayers.Any())
                {
                    //Console.WriteLine($"[SlotTracker] T Side: {string.Join(", ", _tPlayers.Select(p => p.Name))}");
                }
                
                if (_ctPlayers.Any())
                {
                    //Console.WriteLine($"[SlotTracker] CT Side: {string.Join(", ", _ctPlayers.Select(p => p.Name))}");
                }
                
                //Console.WriteLine($"[SlotTracker] Round Wins - T: {_tRounds}, CT: {_ctRounds}");
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

    private void CommandRoundTimer(CCSPlayerController? player, CommandInfo command)
    {
        try
        {
            UpdateRoundDurationFromServer();
            var currentTime = Server.CurrentTime;
            var roundStartTime = GetRoundStartTime();
            var elapsed = currentTime - roundStartTime;
            
            var message = $"Round Timer Debug - In Progress: {_roundInProgress}, Duration: {_roundDuration}s, Remaining: {_roundTimeRemaining}s, Server Time: {currentTime:F2}, Round Start: {roundStartTime:F2}, Elapsed: {elapsed:F2}s";
            
            if (player != null)
            {
                player.PrintToChat($" \x04[SlotTracker]\x01 {message}");
            }
            else
            {
                //Console.WriteLine($"[SlotTracker] {message}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SlotTracker] Error retrieving round timer debug: {ex.Message}");
            if (player != null)
            {
                player.PrintToChat(" \x02[SlotTracker]\x01 Error retrieving round timer debug.");
            }
        }
    }

    private void CommandHibernation(CCSPlayerController? player, CommandInfo command)
    {
        try
        {
            //Console.WriteLine($"[SlotTracker] Manual hibernation reset triggered - Before: T={_tRounds}, CT={_ctRounds}");
            
            // Manually trigger hibernation reset
            ResetGameDataOnHibernation();
            
            // Send API call with reset data
            SyncDataWithApi();
            
            var message = $"Hibernation reset triggered - Rounds: T={_tRounds}, CT={_ctRounds}, Hibernation Count: {_hibernationCount}";
            
            if (player != null)
            {
                player.PrintToChat($" \x04[SlotTracker]\x01 {message}");
            }
            else
            {
                //Console.WriteLine($"[SlotTracker] {message}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SlotTracker] Error triggering hibernation reset: {ex.Message}");
            if (player != null)
            {
                player.PrintToChat(" \x02[SlotTracker]\x01 Error triggering hibernation reset.");
            }
        }
    }

    private void CommandHibernationCheck(CCSPlayerController? player, CommandInfo command)
    {
        try
        {
            //Console.WriteLine("[SlotTracker] Manual hibernation check triggered");
            
            // Force check hibernation status
            CheckForHibernation();
            
            var players = Utilities.GetPlayers();
            var currentPlayerCount = players?.Count(p => p != null && p.IsValid && !p.IsBot && p.Connected == PlayerConnectedState.PlayerConnected) ?? 0;
            
            var message = $"Hibernation Check - Players: {currentPlayerCount}, T Rounds: {_tRounds}, CT Rounds: {_ctRounds}, Hibernating: {_isHibernating}";
            
            if (player != null)
            {
                player.PrintToChat($" \x04[SlotTracker]\x01 {message}");
            }
            else
            {
                //Console.WriteLine($"[SlotTracker] {message}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SlotTracker] Error checking hibernation status: {ex.Message}");
            if (player != null)
            {
                player.PrintToChat(" \x02[SlotTracker]\x01 Error checking hibernation status.");
            }
        }
    }

    private void CommandHalftime(CCSPlayerController? player, CommandInfo command)
    {
        try
        {
            int totalRounds = _tRounds + _ctRounds;
            var message = $"Halftime Status - T Rounds: {_tRounds}, CT Rounds: {_ctRounds}, Total: {totalRounds}, Sides Swapped: {_sidesSwapped}, Last Total: {_lastTotalRounds}";
            
            if (player != null)
            {
                player.PrintToChat($" \x04[SlotTracker]\x01 {message}");
            }
            else
            {
                //Console.WriteLine($"[SlotTracker] {message}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SlotTracker] Error checking halftime status: {ex.Message}");
            if (player != null)
            {
                player.PrintToChat(" \x02[SlotTracker]\x01 Error checking halftime status.");
            }
        }
    }

    private void CommandServerPassword(CCSPlayerController? player, CommandInfo command)
    {
        try
        {
            // Update server password before checking
            UpdateServerPassword();
            
            var message = $"Server Password Status - Password: {(string.IsNullOrEmpty(_serverPassword) ? "Not Set" : "Set")}, Source: {(!string.IsNullOrEmpty(_config.ServerPassword) ? "Config" : "CVar")}";
            
            if (player != null)
            {
                player.PrintToChat($" \x04[SlotTracker]\x01 {message}");
                
                // Show password if set (for debugging - be careful in production)
                if (!string.IsNullOrEmpty(_serverPassword))
                {
                    player.PrintToChat($" \x04[SlotTracker]\x01 Password: {_serverPassword}");
                }
            }
            else
            {
                Console.WriteLine($"[SlotTracker] {message}");
                
                if (!string.IsNullOrEmpty(_serverPassword))
                {
                    Console.WriteLine($"[SlotTracker] Password: {_serverPassword}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SlotTracker] Error checking server password: {ex.Message}");
            if (player != null)
            {
                player.PrintToChat(" \x02[SlotTracker]\x01 Error checking server password.");
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
            //Console.WriteLine($"[SlotTracker] Server slots updated on map start: {_serverSlots}");
            //Console.WriteLine($"[SlotTracker] Current map: {_currentMap}");
            
            // Only reset stats if this isn't the initial map load
            if (_apiSyncTimer != null)
            {
                //Console.WriteLine($"[SlotTracker] Map changed, resetting stats for new map: {_currentMap}");
                ResetStats();
            }
            else
            {
                //Console.WriteLine($"[SlotTracker] Initial map load detected, skipping reset");
            }
            
            // Update server password on map change
            UpdateServerPassword();
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
        
        // Reset side switch tracking
        _sidesSwapped = false;
        _lastTotalRounds = 0;
        
        // Reset round timer
        _roundInProgress = false;
        _roundTimeRemaining = 0;
        _roundStartTime = DateTime.UtcNow;
        _roundDuration = 115; // Reset to default
        
        // Scan for current players
        UpdatePlayerLists();
        
        //Console.WriteLine($"[SlotTracker] Stats reset. Current players: {_playerCount}");
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
            //Console.WriteLine($"[SlotTracker] Updated player lists: {_tPlayers.Count} T, {_ctPlayers.Count} CT, Total: {_playerCount}");
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
            
        //Console.WriteLine($"[SlotTracker] Loaded config from {configPath}");
        //Console.WriteLine($"[SlotTracker] ServerName from config: '{config.ServerName}'");
        
        return config;
    }

    private HookResult OnPlayerConnect(EventPlayerConnectFull @event, GameEventInfo info)
    {
        //Console.WriteLine("[SlotTracker] OnPlayerConnect event triggered");
        
        var player = @event.Userid;
        if (player == null || player.IsBot || player.IsHLTV)
        {
            return HookResult.Continue;
        }

        //Console.WriteLine($"[SlotTracker] Player connecting: {player.PlayerName} (SteamID: {player.SteamID})");
        
        // If we were hibernating, exit hibernation mode
        if (_isHibernating)
        {
            _isHibernating = false;
            var hibernationDuration = DateTime.UtcNow - _hibernationStartTime;
            //Console.WriteLine($"[SlotTracker] Exiting hibernation mode after {hibernationDuration.TotalSeconds:F1}s - Player reconnected");
        }
        
        // Update player count
        UpdatePlayerLists();
        RequestApiSync();
        
        return HookResult.Continue;
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        //Console.WriteLine("[SlotTracker] OnPlayerDisconnect event triggered");
        
        var player = @event.Userid;
        if (player == null || player.IsBot || player.IsHLTV)
        {
            return HookResult.Continue;
        }

        //Console.WriteLine($"[SlotTracker] Disconnect event - Player: {player.PlayerName}, SteamID: {player.SteamID}, Reason: {@event.Reason}");
        
        // Remove player from team lists
        RemovePlayerFromTeams(player.SteamID.ToString());
        
        // Update counts
        _playerCount = _tPlayers.Count + _ctPlayers.Count;
        
        // Check for hibernation immediately after disconnect
        //Console.WriteLine("[SlotTracker] Checking for hibernation after disconnect...");
        CheckForHibernationAfterDisconnect();
        
        // Sync data
        RequestApiSync();
        
        return HookResult.Continue;
    }
    
    private void RemovePlayerFromTeams(string steamId)
    {
        _tPlayers.RemoveAll(p => p.SteamId == steamId);
        _ctPlayers.RemoveAll(p => p.SteamId == steamId);
    }

    private void CheckForHibernationAfterDisconnect()
    {
        try
        {
            // Use our internal player count which is updated immediately
            //Console.WriteLine($"[SlotTracker] Post-disconnect check - Internal players: {_playerCount}, T Rounds: {_tRounds}, CT Rounds: {_ctRounds}");
            
            // If no players left and we have rounds won, trigger hibernation reset
            if (_playerCount == 0 && (_tRounds > 0 || _ctRounds > 0))
            {
                //Console.WriteLine($"[SlotTracker] Last player disconnected with rounds won - triggering hibernation reset");
                
                if (!_isHibernating)
                {
                    _isHibernating = true;
                    _hibernationStartTime = DateTime.UtcNow;
                    _hibernationCount++;
                }
                
                // Reset game data immediately
                ResetGameDataOnHibernation();
                
                // Send final API call with reset data (0-0 score)
                //Console.WriteLine("[SlotTracker] Sending final API call with hibernation reset data after disconnect");
                SyncDataWithApi();
            }
            else
            {
                // If internal count shows 0 but we still have rounds, also trigger reset
                if (_playerCount == 0 && (_tRounds > 0 || _ctRounds > 0))
                {
                    //Console.WriteLine($"[SlotTracker] Internal count shows 0 players with rounds - triggering hibernation reset");
                    
                    if (!_isHibernating)
                    {
                        _isHibernating = true;
                        _hibernationStartTime = DateTime.UtcNow;
                        _hibernationCount++;
                    }
                    
                    ResetGameDataOnHibernation();
                    //Console.WriteLine("[SlotTracker] Sending final API call with internal count reset");
                    SyncDataWithApi();
                }
            }
            
            // Also schedule a delayed check in case the server hasn't fully processed the disconnect
            AddTimer(2.0f, () => DelayedHibernationCheck());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SlotTracker] Error checking hibernation after disconnect: {ex.Message}");
        }
    }

    private void DelayedHibernationCheck()
    {
        try
        {
            var players = Utilities.GetPlayers();
            var currentPlayerCount = players?.Count(p => p != null && p.IsValid && !p.IsBot && p.Connected == PlayerConnectedState.PlayerConnected) ?? 0;
            
            //Console.WriteLine($"[SlotTracker] Delayed hibernation check - Server players: {currentPlayerCount}, Internal players: {_playerCount}, T Rounds: {_tRounds}, CT Rounds: {_ctRounds}");
            
            // If server shows 0 players and we have rounds, trigger hibernation reset
            if (currentPlayerCount == 0 && (_tRounds > 0 || _ctRounds > 0))
            {
                //Console.WriteLine($"[SlotTracker] Delayed check: Server shows 0 players with rounds - triggering hibernation reset");
                
                if (!_isHibernating)
                {
                    _isHibernating = true;
                    _hibernationStartTime = DateTime.UtcNow;
                    _hibernationCount++;
                }
                
                ResetGameDataOnHibernation();
                //Console.WriteLine("[SlotTracker] Sending final API call with delayed reset");
                SyncDataWithApi();
            }
            
            _lastPlayerCount = currentPlayerCount;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SlotTracker] Error in delayed hibernation check: {ex.Message}");
        }
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
            
            //Console.WriteLine($"[SlotTracker] Player {player.PlayerName} changing team: {oldTeamName} -> {newTeamName}");
            
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
            RequestApiSync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SlotTracker] Error in OnPlayerTeamChange: {ex.Message}");
        }
        
        return HookResult.Continue;
    }

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        try
        {
            _roundStartTime = DateTime.UtcNow;
            _roundInProgress = true;
            
            // Get the actual round duration from server
            UpdateRoundDurationFromServer();
            _roundTimeRemaining = _roundDuration;
            
            //Console.WriteLine($"[SlotTracker] Round started at {_roundStartTime:HH:mm:ss}, duration: {_roundDuration}s");
            
            // Sync updated data
            RequestApiSync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SlotTracker] Error in OnRoundStart: {ex.Message}");
        }
        
        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        try
        {
            int winnerTeam = @event.Winner;
            string winnerTeamName = GetTeamName(winnerTeam);
            
            _roundInProgress = false;
            _roundTimeRemaining = 0;
            
            //Console.WriteLine($"[SlotTracker] Round ended. Winner: {winnerTeamName} (Team {winnerTeam})");
            
            // Update the appropriate team's round count
            if (winnerTeam == (int)CounterStrikeSharp.API.Modules.Utils.CsTeam.Terrorist)
            {
                // T team won
                _tRounds++;
                //Console.WriteLine($"[SlotTracker] Updated T rounds: {_tRounds}");
            }
            else if (winnerTeam == (int)CounterStrikeSharp.API.Modules.Utils.CsTeam.CounterTerrorist)
            {
                // CT team won
                _ctRounds++;
                //Console.WriteLine($"[SlotTracker] Updated CT rounds: {_ctRounds}");
            }
            else
            {
                //Console.WriteLine($"[SlotTracker] Unhandled winner team: {winnerTeam}");
            }
            
            // Check for halftime/side switch
            CheckForHalftime();
            
            // Sync updated data
            RequestApiSync();
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

    private void CheckForHalftime()
    {
        try
        {
            int totalRounds = _tRounds + _ctRounds;
            
            // Detect halftime when we reach 12 rounds and haven't swapped sides yet
            // In CS2 competitive matches, teams switch sides at halftime (after 12 rounds)
            if (totalRounds == 12 && !_sidesSwapped && _lastTotalRounds < 12)
            {
                //Console.WriteLine($"[SlotTracker] Halftime detected! Swapping sides. Before: T={_tRounds}, CT={_ctRounds}");
                
                // Swap the round counts since teams are switching sides
                int tempRounds = _tRounds;
                _tRounds = _ctRounds;
                _ctRounds = tempRounds;
                
                _sidesSwapped = true;
                
                //Console.WriteLine($"[SlotTracker] Sides swapped! After: T={_tRounds}, CT={_ctRounds}");
                
                // Update player lists to reflect the side switch
                UpdatePlayerLists();
            }
            
            _lastTotalRounds = totalRounds;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SlotTracker] Error checking for halftime: {ex.Message}");
        }
    }

    private void RequestApiSync()
    {
        // Since we're sending data every second, no need for debouncing
        // Just call SyncDataWithApi directly
        SyncDataWithApi();
    }

    // Event handlers for real-time stats tracking
    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        try
        {
            var victim = @event.Userid;
            var attacker = @event.Attacker;
            
            if (victim != null && attacker != null && !attacker.IsBot && !attacker.IsHLTV)
            {
                //Console.WriteLine($"[SlotTracker] Player death: {attacker.PlayerName} killed {victim.PlayerName}");
                UpdatePlayerStats(attacker.SteamID.ToString());
                RequestApiSync();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SlotTracker] Error in OnPlayerDeath: {ex.Message}");
        }
        
        return HookResult.Continue;
    }

    private HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo info)
    {
        try
        {
            var attacker = @event.Attacker;
            if (attacker != null && !attacker.IsBot && !attacker.IsHLTV)
            {
                // Update stats for damage dealt
                UpdatePlayerStats(attacker.SteamID.ToString());
                RequestApiSync();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SlotTracker] Error in OnPlayerHurt: {ex.Message}");
        }
        
        return HookResult.Continue;
    }


    private HookResult OnBombDefused(EventBombDefused @event, GameEventInfo info)
    {
        try
        {
            var defuser = @event.Userid;
            if (defuser != null && !defuser.IsBot && !defuser.IsHLTV)
            {
                //Console.WriteLine($"[SlotTracker] Bomb defused by: {defuser.PlayerName}");
                UpdatePlayerStats(defuser.SteamID.ToString());
                RequestApiSync();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SlotTracker] Error in OnBombDefused: {ex.Message}");
        }
        
        return HookResult.Continue;
    }

    private HookResult OnBombPlanted(EventBombPlanted @event, GameEventInfo info)
    {
        try
        {
            var planter = @event.Userid;
            if (planter != null && !planter.IsBot && !planter.IsHLTV)
            {
                //Console.WriteLine($"[SlotTracker] Bomb planted by: {planter.PlayerName}");
                UpdatePlayerStats(planter.SteamID.ToString());
                RequestApiSync();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SlotTracker] Error in OnBombPlanted: {ex.Message}");
        }
        
        return HookResult.Continue;
    }

    private HookResult OnHostageRescued(EventHostageRescued @event, GameEventInfo info)
    {
        try
        {
            var rescuer = @event.Userid;
            if (rescuer != null && !rescuer.IsBot && !rescuer.IsHLTV)
            {
                //Console.WriteLine($"[SlotTracker] Hostage rescued by: {rescuer.PlayerName}");
                UpdatePlayerStats(rescuer.SteamID.ToString());
                RequestApiSync();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SlotTracker] Error in OnHostageRescued: {ex.Message}");
        }
        
        return HookResult.Continue;
    }

    private HookResult OnRoundMvp(EventRoundMvp @event, GameEventInfo info)
    {
        try
        {
            var mvp = @event.Userid;
            if (mvp != null && !mvp.IsBot && !mvp.IsHLTV)
            {
                //Console.WriteLine($"[SlotTracker] Round MVP: {mvp.PlayerName}");
                UpdatePlayerStats(mvp.SteamID.ToString());
                RequestApiSync();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SlotTracker] Error in OnRoundMvp: {ex.Message}");
        }
        
        return HookResult.Continue;
    }

    private void UpdatePlayerStats(string steamId)
    {
        try
        {
            var player = Utilities.GetPlayers().FirstOrDefault(p => p?.SteamID.ToString() == steamId);
            if (player == null || !player.IsValid || player.IsBot || player.IsHLTV)
            {
                return;
            }

            // Find the player in our lists and update their stats
            var tPlayer = _tPlayers.FirstOrDefault(p => p.SteamId == steamId);
            var ctPlayer = _ctPlayers.FirstOrDefault(p => p.SteamId == steamId);
            var targetPlayer = tPlayer ?? ctPlayer;

            if (targetPlayer != null)
            {
                try
                {
                    if (player.PlayerPawn?.Value?.Controller?.Value != null)
                    {
                        var playerController = player.PlayerPawn.Value.Controller.Value;
                        targetPlayer.Kills = player.ActionTrackingServices?.MatchStats?.Kills ?? 0;
                        targetPlayer.Deaths = player.ActionTrackingServices?.MatchStats?.Deaths ?? 0;
                        targetPlayer.Assists = player.ActionTrackingServices?.MatchStats?.Assists ?? 0;
                        targetPlayer.Score = player.Score;
                        targetPlayer.HeadshotKills = player.ActionTrackingServices?.MatchStats?.HeadShotKills ?? 0;
                        targetPlayer.MVPs = player.MVPs;
                        targetPlayer.Ping = player.Ping.ToString();
                    }
                }
                catch (Exception statEx)
                {
                    Console.WriteLine($"[SlotTracker] Warning: Could not update stats for player {player.PlayerName}: {statEx.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SlotTracker] Error updating player stats: {ex.Message}");
        }
    }

    private void UpdateAllPlayerStats()
    {
        try
        {
            var players = Utilities.GetPlayers();
            if (players == null)
            {
                return;
            }

            bool statsChanged = false;
            
            foreach (var player in players)
            {
                if (player == null || !player.IsValid || player.IsBot || player.IsHLTV || 
                    player.Connected != PlayerConnectedState.PlayerConnected)
                {
                    continue;
                }

                string steamId = player.SteamID.ToString();
                var tPlayer = _tPlayers.FirstOrDefault(p => p.SteamId == steamId);
                var ctPlayer = _ctPlayers.FirstOrDefault(p => p.SteamId == steamId);
                var targetPlayer = tPlayer ?? ctPlayer;

                if (targetPlayer != null)
                {
                    try
                    {
                        if (player.PlayerPawn?.Value?.Controller?.Value != null)
                        {
                            var playerController = player.PlayerPawn.Value.Controller.Value;
                            var newKills = player.ActionTrackingServices?.MatchStats?.Kills ?? 0;
                            var newDeaths = player.ActionTrackingServices?.MatchStats?.Deaths ?? 0;
                            var newAssists = player.ActionTrackingServices?.MatchStats?.Assists ?? 0;
                            var newScore = player.Score;
                            var newHeadshotKills = player.ActionTrackingServices?.MatchStats?.HeadShotKills ?? 0;
                            var newMVPs = player.MVPs;
                            var newPing = player.Ping.ToString();

                            // Check if any stats have changed
                            if (targetPlayer.Kills != newKills || targetPlayer.Deaths != newDeaths || 
                                targetPlayer.Assists != newAssists || targetPlayer.Score != newScore ||
                                targetPlayer.HeadshotKills != newHeadshotKills || targetPlayer.MVPs != newMVPs ||
                                targetPlayer.Ping != newPing)
                            {
                                targetPlayer.Kills = newKills;
                                targetPlayer.Deaths = newDeaths;
                                targetPlayer.Assists = newAssists;
                                targetPlayer.Score = newScore;
                                targetPlayer.HeadshotKills = newHeadshotKills;
                                targetPlayer.MVPs = newMVPs;
                                targetPlayer.Ping = newPing;
                                statsChanged = true;
                            }
                        }
                    }
                    catch (Exception statEx)
                    {
                        Console.WriteLine($"[SlotTracker] Warning: Could not update stats for player {player.PlayerName}: {statEx.Message}");
                    }
                }
            }

            // Only sync if stats actually changed
            if (statsChanged)
            {
                RequestApiSync();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SlotTracker] Error updating all player stats: {ex.Message}");
        }
    }

    private void UpdateRoundTimer()
    {
        try
        {
            // Check for hibernation
            CheckForHibernation();
            
            if (_roundInProgress)
            {
                // Get the actual round time from server CVar
                UpdateRoundDurationFromServer();
                
                // Calculate remaining time based on server time
                var currentTime = Server.CurrentTime;
                var roundStartTime = GetRoundStartTime();
                var elapsed = currentTime - roundStartTime;
                _roundTimeRemaining = Math.Max(0, _roundDuration - (int)elapsed);
                
                // If round time is up, mark round as ended
                if (_roundTimeRemaining <= 0)
                {
                    _roundInProgress = false;
                    //Console.WriteLine("[SlotTracker] Round time expired");
                }
            }
            else
            {
                // Try to detect if a round is in progress by checking game state
                TryDetectRoundInProgress();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SlotTracker] Error updating round timer: {ex.Message}");
        }
    }

    private void UpdateRoundDurationFromServer()
    {
        try
        {
            // Get round time from mp_roundtime CVar (in minutes, convert to seconds)
            var roundTimeCvar = ConVar.Find("mp_roundtime");
            if (roundTimeCvar != null && roundTimeCvar.GetPrimitiveValue<float>() > 0)
            {
                _roundDuration = (int)(roundTimeCvar.GetPrimitiveValue<float>() * 60); // Convert minutes to seconds
            }
            else
            {
                // Fallback: try to get from game rules
                var gameRules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault();
                if (gameRules?.GameRules != null)
                {
                    _roundDuration = gameRules.GameRules.RoundTime;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SlotTracker] Error getting round duration: {ex.Message}");
            // Keep current value if we can't get it from server
        }
    }

    private void UpdateServerPassword()
    {
        try
        {
            // First try to get from config
            if (!string.IsNullOrEmpty(_config.ServerPassword))
            {
                _serverPassword = _config.ServerPassword;
                return;
            }

            // Try to get from server CVar sv_password
            var passwordCvar = ConVar.Find("sv_password");
            if (passwordCvar != null)
            {
                var passwordValue = passwordCvar.StringValue;
                if (!string.IsNullOrEmpty(passwordValue))
                {
                    _serverPassword = passwordValue;
                    //Console.WriteLine($"[SlotTracker] Retrieved server password from CVar: {_serverPassword}");
                }
                else
                {
                    _serverPassword = ""; // No password set
                    //Console.WriteLine("[SlotTracker] No server password set (sv_password is empty)");
                }
            }
            else
            {
                //Console.WriteLine("[SlotTracker] Could not find sv_password CVar");
                _serverPassword = "";
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SlotTracker] Error getting server password: {ex.Message}");
            _serverPassword = "";
        }
    }

    private void CheckForHibernation()
    {
        try
        {
            var players = Utilities.GetPlayers();
            var currentPlayerCount = players?.Count(p => p != null && p.IsValid && !p.IsBot && p.Connected == PlayerConnectedState.PlayerConnected) ?? 0;
            
            // Debug logging every 5 seconds
            if (DateTime.UtcNow.Second % 5 == 0)
            {
                //Console.WriteLine($"[SlotTracker] Hibernation Check - Last: {_lastPlayerCount}, Current: {currentPlayerCount}, Hibernating: {_isHibernating}, T Rounds: {_tRounds}, CT Rounds: {_ctRounds}");
            }
            
            // Check if server went from having players to having no players (hibernation)
            if (_lastPlayerCount > 0 && currentPlayerCount == 0)
            {
                if (!_isHibernating)
                {
                    _isHibernating = true;
                    _hibernationStartTime = DateTime.UtcNow;
                    _hibernationCount++;
                    
                    //Console.WriteLine($"[SlotTracker] Server hibernation detected (count: {_hibernationCount}) - Last: {_lastPlayerCount}, Current: {currentPlayerCount}");
                    
                    // Reset game data on hibernation
                    ResetGameDataOnHibernation();
                    
                    // Send final API call with reset data (0-0 score)
                    //Console.WriteLine("[SlotTracker] Sending final API call with hibernation reset data");
                    SyncDataWithApi();
                }
            }
            // Check if server came back from hibernation (players reconnected)
            else if (_isHibernating && currentPlayerCount > 0)
            {
                _isHibernating = false;
                var hibernationDuration = DateTime.UtcNow - _hibernationStartTime;
                
                //Console.WriteLine($"[SlotTracker] Server woke up from hibernation after {hibernationDuration.TotalMinutes:F1} minutes");
                
                // Reset stats for new session
                ResetStats();
            }
            // If already hibernating, ensure player data is cleared
            else if (_isHibernating)
            {
                // Force clear player data when hibernating
                _tPlayers.Clear();
                _ctPlayers.Clear();
                _playerCount = 0;
            }
            // Also check if we have 0 players and rounds > 0 (should trigger hibernation reset)
            else if (currentPlayerCount == 0 && (_tRounds > 0 || _ctRounds > 0) && !_isHibernating)
            {
                //Console.WriteLine($"[SlotTracker] Detected stale data with 0 players but rounds > 0 - triggering hibernation reset");
                _isHibernating = true;
                _hibernationStartTime = DateTime.UtcNow;
                _hibernationCount++;
                ResetGameDataOnHibernation();
                
                // Send final API call with reset data
                //Console.WriteLine("[SlotTracker] Sending final API call with stale data reset");
                SyncDataWithApi();
            }
            // Additional check: if we have 0 players and any rounds, always reset
            else if (currentPlayerCount == 0 && (_tRounds > 0 || _ctRounds > 0))
            {
                //Console.WriteLine($"[SlotTracker] Fallback check: 0 players with rounds > 0 - forcing hibernation reset");
                if (!_isHibernating)
                {
                    _isHibernating = true;
                    _hibernationStartTime = DateTime.UtcNow;
                    _hibernationCount++;
                }
                ResetGameDataOnHibernation();
                //Console.WriteLine("[SlotTracker] Sending final API call with fallback reset");
                SyncDataWithApi();
            }
            
            _lastPlayerCount = currentPlayerCount;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SlotTracker] Error checking for hibernation: {ex.Message}");
        }
    }

    private void ResetGameDataOnHibernation()
    {
        try
        {
            //Console.WriteLine($"[SlotTracker] Before hibernation reset - T Rounds: {_tRounds}, CT Rounds: {_ctRounds}, Players: {_playerCount}");
            
            // Reset rounds won
            _tRounds = 0;
            _ctRounds = 0;
            
            // Reset side switch tracking
            _sidesSwapped = false;
            _lastTotalRounds = 0;
            
            // Reset round timer
            _roundInProgress = false;
            _roundTimeRemaining = 0;
            
            // Clear player lists completely
            _tPlayers.Clear();
            _ctPlayers.Clear();
            _playerCount = 0;
            
            // Force update player lists to ensure they're empty
            UpdatePlayerLists();
            
            //Console.WriteLine($"[SlotTracker] After hibernation reset - T Rounds: {_tRounds}, CT Rounds: {_ctRounds}, Players: {_playerCount}");
            //Console.WriteLine($"[SlotTracker] T Players: {_tPlayers.Count}, CT Players: {_ctPlayers.Count}");
            //Console.WriteLine("[SlotTracker] Game data reset due to hibernation");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SlotTracker] Error resetting game data on hibernation: {ex.Message}");
        }
    }

    private void TryDetectRoundInProgress()
    {
        try
        {
            // Check if there are players in the game
            var players = Utilities.GetPlayers();
            if (players != null && players.Any(p => p != null && p.IsValid && !p.IsBot && p.Connected == PlayerConnectedState.PlayerConnected))
            {
                // If we have players but no round in progress, we can't reliably detect round state
                // from the CounterStrikeSharp API, so we'll rely on the EventRoundStart event instead
                // This method is kept for future API improvements
                //Console.WriteLine("[SlotTracker] Players detected but no round in progress - waiting for EventRoundStart");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SlotTracker] Error detecting round in progress: {ex.Message}");
        }
    }

    private float GetRoundStartTime()
    {
        try
        {
            // Try to get round start time from game rules
            var gameRules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault();
            if (gameRules?.GameRules != null)
            {
                return gameRules.GameRules.RoundStartTime;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SlotTracker] Error getting round start time: {ex.Message}");
        }
        
        // Fallback: use our tracked start time converted to server time
        return (float)(_roundStartTime - DateTime.UnixEpoch).TotalSeconds;
    }

    private void SyncDataWithApi(int retryCount = 0)
    {
        try
        {
            if (!_config.EnableApiSync)
            {
                return;
            }
            
            // Console.WriteLine($"[SlotTracker] Starting API sync (attempt {retryCount + 1})...");
            // Console.WriteLine($"[SlotTracker] Current map for API sync: '{_currentMap}'");
            
            // Update server password before each sync to ensure it's current
            UpdateServerPassword();
            
            // Ensure player lists are up to date (unless hibernating)
            if (!_isHibernating)
            {
                UpdatePlayerLists();
            }
            else
            {
                // When hibernating, ensure player lists are empty
                _tPlayers.Clear();
                _ctPlayers.Clear();
                _playerCount = 0;
                //Console.WriteLine("[SlotTracker] Hibernating - cleared player data for API");
            }
            
            // Validate data before sending
            if (string.IsNullOrEmpty(_config.ServerId) || string.IsNullOrEmpty(_config.ApiEndpoint))
            {
                //Console.WriteLine("[SlotTracker] Invalid configuration: ServerId or ApiEndpoint is empty");
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
                server_ip = _config.ServerIp ?? "127.0.0.1",
                server_port = _config.ServerPort,
                server_password = _serverPassword,
                session_id = _sessionId,
                timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                map_name = _currentMap ?? "unknown",
                player_count = _playerCount,
                server_slots = _serverSlots,
                t_rounds = _tRounds,
                ct_rounds = _ctRounds,
                t_players = _tPlayers.Count,
                ct_players = _ctPlayers.Count,
                round_in_progress = _roundInProgress,
                round_time_remaining = _roundTimeRemaining,
                round_start_time = _roundStartTime.ToString("yyyy-MM-dd HH:mm:ss"),
                is_hibernating = _isHibernating,
                hibernation_count = _hibernationCount,
                hibernation_start_time = _isHibernating ? _hibernationStartTime.ToString("yyyy-MM-dd HH:mm:ss") : null,
                players = playerDetails
            };
            
            //Console.WriteLine($"[SlotTracker] API data server_name: '{apiData.server_name}'");
            //Console.WriteLine($"[SlotTracker] API data map_name: '{apiData.map_name}'");
            
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
                        //Console.WriteLine($"[SlotTracker] API sync successful: {responseString}");
                    }
                    else
                    {
                        //Console.WriteLine($"[SlotTracker] API sync failed: {response.StatusCode}, {responseString}");
                        
                        // Retry on certain HTTP errors
                        if (retryCount < 3 && (
                            response.StatusCode == System.Net.HttpStatusCode.InternalServerError ||
                            response.StatusCode == System.Net.HttpStatusCode.BadGateway ||
                            response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable ||
                            response.StatusCode == System.Net.HttpStatusCode.GatewayTimeout
                        ))
                        {
                            //Console.WriteLine($"[SlotTracker] Retrying API sync in {1000 * (retryCount + 1)}ms...");
                            await Task.Delay(1000 * (retryCount + 1));
                            SyncDataWithApi(retryCount + 1);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    //Console.WriteLine("[SlotTracker] API request timed out");
                    
                    // Retry on timeout
                    if (retryCount < 3)
                    {
                        //Console.WriteLine($"[SlotTracker] Retrying API sync after timeout in {1000 * (retryCount + 1)}ms...");
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

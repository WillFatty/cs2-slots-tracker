namespace SlotTracker.Models;

public class TeamPlayer
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; }
    public string SteamId { get; set; } = string.Empty;
    public string PlayerName { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string MapName { get; set; } = string.Empty;
}

public class TeamSwitch
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; }
    public string SteamId { get; set; } = string.Empty;
    public string PlayerName { get; set; } = string.Empty;
    public string PreviousTeam { get; set; } = string.Empty;
    public string NewTeam { get; set; } = string.Empty;
    public string MapName { get; set; } = string.Empty;
}

public enum CsTeam
{
    None = 0,
    Spectator = 1,
    Terrorist = 2,
    CounterTerrorist = 3
} 
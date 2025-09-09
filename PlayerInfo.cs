namespace SlotTracker;

public class PlayerInfo
{
    public string Name { get; set; } = string.Empty;
    public string SteamId { get; set; } = string.Empty;
    public string Team { get; set; } = string.Empty;
    
    // Player stats
    public int Kills { get; set; } = 0;
    public int Deaths { get; set; } = 0;
    public int Assists { get; set; } = 0;
    public int Score { get; set; } = 0;
    public int HeadshotKills { get; set; } = 0;
    public int MVPs { get; set; } = 0;
    public string Ping { get; set; } = "0";
    
    // Helper method to safely get string values
    public string GetSafeString(string? value, string defaultValue = "")
    {
        return string.IsNullOrEmpty(value) ? defaultValue : value;
    }
    
    // Helper method to safely get int values
    public int GetSafeInt(int? value, int defaultValue = 0)
    {
        return value ?? defaultValue;
    }
} 
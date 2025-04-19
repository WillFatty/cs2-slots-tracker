namespace SlotTracker.Config;

public class ServerConfig
{
    // API Configuration
    public bool EnableApiSync { get; init; } = true;
    public string ApiEndpoint { get; init; } = "https://admin.affinitycs2.com/api/stats";
    public string ApiKey { get; init; } = "YOUR_API_KEY";
    public string ServerId { get; init; } = "server1"; // Unique identifier for this server
    public string ServerName { get; init; } = "CS2 Server"; // Friendly name for the server
    public int ApiSyncIntervalSeconds { get; init; } = 60; // How often to sync data with API
} 
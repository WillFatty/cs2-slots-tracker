namespace SlotTracker.Config;

public class ServerConfig
{
    // API Configuration
    public bool EnableApiSync { get; init; } = true;
    public string ApiEndpoint { get; init; } = "https://servers.affinitycs2.com/api/cs2";
    public string ApiKey { get; init; } = "YOUR_API_KEY";
    public string ServerId { get; init; } = "server1"; // Unique identifier for this server
    public string ServerName { get; init; } = "CS2 Server"; // Friendly name for the server
    public string ServerIp { get; init; } = "24.101.101.161"; // Server IP address
    public int ServerPort { get; init; } = 27015; // Server port
    public int ApiSyncIntervalSeconds { get; init; } = 60; // How often to sync data with API
} 
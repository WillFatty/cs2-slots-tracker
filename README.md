# CS2 Slots Tracker Plugin

A Counter-Strike 2 plugin built with CounterStrikeSharp that tracks server player counts and sends the data to a central API. This plugin helps server administrators monitor server population across multiple servers.

## Features

- Real-time tracking of player connections and disconnections
- Automatic server slot detection
- API synchronization for centralized data storage
- Excludes bots and HLTV from player counts
- Detailed logging for troubleshooting
- Team-based player tracking (T side and CT side)
- In-game team statistics command
- Server password status command for debugging
- Map tracking in server statistics
- Round win tracking for T and CT teams
- Team round stats displayed in stats command
- Stats automatically reset on map change and server start
- Configurable API endpoints and server identifiers
- Server password tracking and transmission to API

## Prerequisites

- Counter-Strike 2 Server
- [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) installed on your server
- .NET 7.0 Runtime

## Installation

1. Download the latest release from the releases page
2. Extract the contents to your CS2 server's plugin directory:
   ```
   addons/counterstrikesharp/plugins/cs2-slots-tracker/
   ```
3. Ensure the following files are present in your plugin directory:
   - `cs2-slots-tracker.dll`
   - `config.json`

## Configuration

Edit the `config.json` file with your API settings:
```json
{
    "EnableApiSync": true,
    "ApiEndpoint": "https://admin.affinitycs2.com/api/stats",
    "ApiKey": "YOUR_API_KEY",
    "ServerId": "server1",
    "ServerName": "CS2 Server #1",
    "ServerIp": "24.101.101.161",
    "ServerPort": 27015,
    "ServerPassword": "",
    "ApiSyncIntervalSeconds": 60
}
```

**API Configuration:**
- `EnableApiSync`: Set to `true` to enable API synchronization, `false` to disable
- `ApiEndpoint`: The URL endpoint where stats will be sent (admin.affinitycs2.com/api/stats)
- `ApiKey`: Your authentication key for the API
- `ServerId`: A unique identifier for this server instance
- `ServerName`: A friendly name for the server that appears in dashboards
- `ServerIp`: The IP address of the server
- `ServerPort`: The port number of the server
- `ServerPassword`: Server password (optional - if empty, will try to get from sv_password CVar)
- `ApiSyncIntervalSeconds`: How often to sync data with the API (in seconds)

## Available Commands

The plugin provides several in-game commands for debugging and monitoring:

- `css_teamstats` - Shows current team statistics and player counts
- `css_roundtimer` - Shows round timer debug information
- `css_hibernation` - Manually trigger hibernation reset for testing
- `css_hibernationcheck` - Check hibernation status and trigger if needed
- `css_halftime` - Check halftime and side switch status
- `css_serverpassword` - Check server password status and source

## Building from Source

1. Clone the repository
2. Make sure you have .NET 7.0 SDK installed
3. Run the following commands:
   ```bash
   dotnet build -c Release
   ```
   Or use the included `publish.bat` script.

## API Integration

The plugin synchronizes data with a central API for multi-server tracking. Every `ApiSyncIntervalSeconds` seconds, it will send current server stats to the specified API endpoint.

### API Data Format

The data is sent as JSON in the following format:

```json
{
  "server_id": "server1",
  "server_name": "AffinityCS2 Prac",
  "session_id": "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx",
  "timestamp": "2023-08-10 15:30:45",
  "map_name": "de_dust2",
  "player_count": 16,
  "server_slots": 24,
  "t_rounds": 5,
  "ct_rounds": 7,
  "t_players": 8,
  "ct_players": 8,
  "players": [
    {
      "name": "Player1",
      "steam_id": "76561198012345678",
      "team": "T",
      "kills": 12,
      "deaths": 8,
      "assists": 3,
      "score": 24,
      "headshot_kills": 5,
      "mvps": 2,
      "ping": "45"
    },
    {
      "name": "Player2",
      "steam_id": "76561198087654321",
      "team": "CT",
      "kills": 10,
      "deaths": 9,
      "assists": 1,
      "score": 20,
      "headshot_kills": 3,
      "mvps": 1,
      "ping": "55"
    },
    {
      "name": "Player3",
      "steam_id": "76561198011223344",
      "team": "Spectator",
      "kills": 0,
      "deaths": 0,
      "assists": 0,
      "score": 0,
      "headshot_kills": 0,
      "mvps": 0,
      "ping": "30"
    }
  ]
}
```

### Authentication

The plugin sends the following HTTP headers with each request:
- `X-API-Key`: Your API key (from config.json)
- `X-Server-ID`: Your server ID (from config.json)

### Setting Up Multiple Servers

To track multiple servers:
1. Install the plugin on each server
2. Give each server a unique `ServerId` in its config.json
3. Use the same API endpoint and key for all servers

## Troubleshooting

### Common Issues

1. **Plugin Not Loading**
   - Verify all required DLLs are present in the plugin directory
   - Check the server console for error messages
   - Ensure CounterStrikeSharp is properly installed

2. **API Connection Issues**
   - Verify your API endpoint is accessible from your server
   - Check that your API key is valid and properly configured
   - Ensure network connectivity between your server and the API endpoint

## Logs

The plugin logs important events to the server console with the `[SlotTracker]` prefix. Monitor these logs for:
- Plugin initialization
- API connection status
- Player connect/disconnect events
- Team changes
- Any errors that occur

## License

This project is licensed under the MIT License - see the LICENSE file for details.

## Contributing

Contributions are welcome! Please feel free to submit pull requests.

## Support

If you encounter any issues or need help, please:
1. Check the troubleshooting section
2. Look through existing issues
3. Create a new issue with detailed information about your problem

## Credits

Built with:
- [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp)

## In-Game Commands

- `css_teamstats` - Shows current team statistics including player counts and names for each team

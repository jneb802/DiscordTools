# DiscordTools

A BepInEx client/server mod for collecting full client `LogOutput.log` files on a dedicated Valheim server.

## Features

- Server command:
  ```text
  client-logs {playerNameOrSteamID}
  ```
- Client uploads on logout.
- Client attempts upload on normal quit.
- In-game Discord link command:
  ```text
  !link CODE
  ```
- Full log file is gzip-compressed before transfer.
- Server stores logs by player name and stable player ID for later lookup.
- Server writes JSON metadata and lookup indexes.
- Server uploads received logs as `.log` files to any compatible Discord bot API when configured.

## How to use

- Requires a Discord bot with a compatible upload API if you want logs posted to Discord.
- Install DiscordTools on the dedicated server and on every client that should be able to send logs.
- Set `DISCORDTOOLS_BOT_API_URL` and `DISCORDTOOLS_BOT_API_KEY` on the dedicated server.
- Set `DISCORDTOOLS_LINK_API_URL` on the dedicated server if you want in-game Discord account linking.
- Start the server and have the player join.
- Run `client-logs {playerNameOrSteamID}` on the server to request that player's log.
- Logs are saved on the server disk and, when configured, sent to Discord as a `.log` attachment.
- A player can type `!link CODE` after receiving a code from Discord. DiscordTools consumes the text before it is sent as chat, sends the code privately to the server, and the server posts the code with the player's Steam/platform ID to the configured link API.

## Server Storage

Logs are stored under `BepInEx/client-logs` by default:

```text
client-logs/
  players/{playerName}_{playerId}/
    player.json
    latest.json
    logs/{yyyy-MM}/
      {timestamp}_{reason}_{playerName}_LogOutput.log.gz
      {timestamp}_{reason}_{playerName}_LogOutput.json
  index/
    players.json
    recent.json
  incoming/
  bot-upload-failed/
```

## Build

```bash
dotnet build DiscordTools.csproj
```

The built DLL is written to `bin/Debug/DiscordTools.dll`.

## Configuration

The plugin GUID is `warpalicious.DiscordTools`. Configure the generated file:

```text
BepInEx/config/warpalicious.DiscordTools.cfg
```

Clients do not need the bot URL or API key. Keep bot credentials server-only.

Preferred dedicated-server setup:

```bash
export DISCORDTOOLS_BOT_API_URL="https://your-bot-host.example.com/api/client-log"
export DISCORDTOOLS_LINK_API_URL="https://your-bot-host.example.com/api/valheim-link"
export DISCORDTOOLS_BOT_API_KEY="shared-secret"
```

The BepInEx config can also be used for private dedicated-server installs, but do not ship a config containing `ApiKey` to clients:

```ini
[BotApi]
PostToBotApi = true
ApiUrl = https://your-bot-host.example.com/api/client-log
LinkApiUrl = https://your-bot-host.example.com/api/valheim-link
ApiKey = shared-secret
```

Fresh installs default `ApiUrl` and `ApiKey` to empty values.

Archived logs are retained for 30 days by default. To keep logs forever, set `RetentionDays` to `0`.

## Link API

When a player enters `!link CODE`, the dedicated server posts JSON to `LinkApiUrl`:

```json
{
  "requestId": "6b7b8d9c0f2a4d7ca5f8c37e87b6fd13",
  "code": "PRAE-482913",
  "playerId": "76561198000000000",
  "playerName": "Player",
  "endpoint": "76561198000000000",
  "platformDisplayName": "SteamName",
  "receivedAtUtc": "2026-05-28T18:42:00.0000000Z"
}
```

The endpoint should return `2xx` when the code is accepted. A plain-text response body is shown to the player in chat.

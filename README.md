# Recconect

Recconect is an early R.E.P.O. BepInEx/Harmony mod project focused on reconnect support for unstable networks.

Current status: project initialized, builds cleanly, logs Photon and Steam lobby state from `NetworkConnect` and `NetworkManager`, and includes opt-in experimental reconnect attempts. Reconnect behavior is disabled by default.

## Build

```powershell
dotnet tool restore
dotnet build .\Recconect.sln
```

The debug DLL is produced at:

```text
bin\Debug\netstandard2.1\Recconect.dll
```

## Development Notes

- R.E.P.O. install discovered locally: `D:\SteamLibrary\steamapps\common\REPO`.
- Main game assembly: `D:\SteamLibrary\steamapps\common\REPO\REPO_Data\Managed\Assembly-CSharp.dll`.
- Photon PUN assembly: `D:\SteamLibrary\steamapps\common\REPO\REPO_Data\Managed\PhotonUnityNetworking.dll`.
- Photon Realtime assembly: `D:\SteamLibrary\steamapps\common\REPO\REPO_Data\Managed\PhotonRealtime.dll`.

See `docs/local-game-map.md` and `docs/local-game-map.json` for the current API map.

## Local Deploy

```powershell
.\scripts\deploy-local.ps1 -BepInExPluginsPath "<profile>\BepInEx\plugins" -Configuration Debug
```

The deploy script builds the project and copies `Recconect.dll` to:

```text
<profile>\BepInEx\plugins\YoomarXD-Recconect\Recconect.dll
```

Use a disposable mod-manager profile for reconnect testing.

## Runtime Config

The first launch creates a BepInEx config file for:

- `Diagnostics.Enabled`
- `Diagnostics.LogJoinState`
- `Reconnect.ExperimentalReconnectEnabled`
- `Reconnect.ConfigureRoomTtlOnCreate`
- `Reconnect.AllowHostReconnect`
- `Reconnect.PlayerTtlMilliseconds`
- `Reconnect.EmptyRoomTtlMilliseconds`
- `Reconnect.MaxReconnectAttempts`
- `Reconnect.ReconnectAttemptDelaySeconds`
- `Reconnect.ReconnectAttemptTimeoutSeconds`
- `Reconnect.EligibleDisconnectCauses`

Reconnect defaults to no behavior change. When enabled, room creators also set nonzero Photon room TTL values so rejoin APIs have a chance to work.

## Packaging

```powershell
.\scripts\package-thunderstore.ps1 -Configuration Release
```

This creates `dist\Recconect-0.1.0.zip`. Publishing still needs a valid `icon.png`; the script warns when one is missing.

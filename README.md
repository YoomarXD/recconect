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

`manifest.json` is a Thunderstore skeleton for later packaging. Validate it before publishing and include only release artifacts in a package zip.

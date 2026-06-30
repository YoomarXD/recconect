# Recconect

Recconect is an early R.E.P.O. BepInEx/Harmony mod project focused on reconnect support for unstable networks.

Current status: project initialized, builds cleanly, and includes disconnect probe patches that log Photon and Steam lobby state from `NetworkConnect` and `NetworkManager`. Actual reconnect behavior is intentionally not enabled yet.

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
- `Reconnect.MaxReconnectAttempts`
- `Reconnect.ReconnectAttemptDelaySeconds`

Reconnect options are reserved and default to no behavior change.

## Packaging

`manifest.json` is a Thunderstore skeleton for later packaging. Validate it before publishing and include only release artifacts in a package zip.

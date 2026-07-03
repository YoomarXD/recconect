# Local R.E.P.O. Game Map

Generated on 2026-06-30 from the local Steam install.

## Steam Install

| Field | Value |
| --- | --- |
| Steam root | `C:\Program Files (x86)\Steam` |
| Library | `D:\SteamLibrary` |
| App ID | `3241660` |
| App name | `R.E.P.O.` |
| Install dir | `D:\SteamLibrary\steamapps\common\REPO` |
| Steam build ID | `23363152` |
| Executable | `D:\SteamLibrary\steamapps\common\REPO\REPO.exe` |
| Unity data dir | `D:\SteamLibrary\steamapps\common\REPO\REPO_Data` |
| Managed assemblies | `D:\SteamLibrary\steamapps\common\REPO\REPO_Data\Managed` |
| In-place BepInEx folder | Not present in the Steam game directory |

## Key Assemblies

| Assembly | Purpose | Observed size |
| --- | --- | --- |
| `Assembly-CSharp.dll` | Game code and R.E.P.O. MonoBehaviours | 2,963,968 bytes |
| `PhotonUnityNetworking.dll` | Photon PUN API used by game code | 114,688 bytes |
| `PhotonRealtime.dll` | Photon Realtime client/state APIs | 113,152 bytes |
| `Photon3Unity3D.dll` | Photon transport/runtime support | 240,640 bytes |
| `Facepunch.Steamworks.Win64.dll` | Steam lobby/auth integration | 574,464 bytes |
| `websocket-sharp.dll` | Websocket transport dependency | 244,736 bytes |

## Reconnect-Relevant Game Types

These are symbol-level findings only. Do not commit decompiled game source.

| Type | Why it matters |
| --- | --- |
| `NetworkConnect` | Owns startup connection flow, Steam auth ticket setup, room create/join, and disconnect UI handling. |
| `NetworkManager` | Spawns player avatars, tracks loading completion, handles `OnDisconnected`, `OnPlayerLeftRoom`, and forced leave flow. |
| `RunManager` | Drives scene changes, lobby/menu transitions, and `LeaveToMainMenu`. |
| `RunManagerPUN` | Syncs open Steam lobby data from host to other clients and sends lobby IDs by RPC. |
| `SteamManager` | Owns Steam lobby create/join/leave, lobby metadata, auth ticket generation, and auto-join. |
| `MenuPageServerList` | Uses Photon lobby listing, then disconnects after gathering public rooms. |
| `MenuElementServer` | Stores selected room name and triggers lobby join scene flow. |
| `MenuPageLobby` | Displays lobby players and locks lobby before start. |
| `LobbyMenuOpen` | Opens `MenuPageIndex.Lobby` after a timer and can reopen the lobby UI if it survives into a run scene. |
| `PlayerAvatar` | Local avatar `Start()` links `PlayerController.playerAvatarScript`, sets static `PlayerAvatar.instance`, and requests host spawn when level generation is complete. |
| `LevelGenerator` | Host calls `PlayerSpawn()` for `GameDirector.PlayerList`; player avatars set `spawned=true` through `SpawnRPC`. |
| `GameManager` | Stores lobby type, random matchmaking mode, max players, and public/private join intent. |

## Observed Call Flow

Initial connection appears to use this path:

1. `NetworkConnect.Start()` sets `PhotonNetwork.NickName`, disables automatic scene sync, disconnects any existing Photon state, then starts `CreateLobby()`.
2. `NetworkConnect.CreateLobby()` sends Steam auth, picks Photon region, handles Steam lobby metadata, then calls `PhotonNetwork.ConnectUsingSettings()`.
3. `NetworkConnect.OnConnectedToMaster()` decides between creating, joining, joining random, or joining/creating a room.
4. Private/direct lobby flow uses `NetworkConnect.TryJoiningRoom()` and `PhotonNetwork.JoinOrCreateRoom(RoomName, ...)`.
5. `NetworkConnect.OnJoinedRoom()` marks joined state and enables `PhotonNetwork.AutomaticallySyncScene`.
6. Host-side scene advance uses `PhotonNetwork.LoadLevel("Reload")` or later `PhotonNetwork.LoadLevel("Main")`.

Disconnect handling currently appears intentionally terminal:

- `NetworkConnect.OnDisconnected(DisconnectCause)` shows disconnected UI for non-client/server logic disconnects, then calls `PhotonNetwork.Disconnect()` and `SteamManager.instance.LeaveLobby()`.
- `NetworkManager.OnDisconnected(DisconnectCause)` sets `leavePhotonRoom = true`, which feeds the leave-to-menu path.
- `NetworkManager.LeavePhotonRoom()` calls `PhotonNetwork.Disconnect()`, leaves the Steam lobby, and starts `RunManager.instance.LeaveToMainMenu()`.

## Photon APIs Worth Testing

Photon PUN 2.52 exposes:

- `PhotonNetwork.Reconnect()`
- `PhotonNetwork.RejoinRoom(string roomName)`
- `PhotonNetwork.ReconnectAndRejoin()`
- `PhotonNetwork.JoinRoom(string roomName, string[] expectedUsers = null)`
- `PhotonNetwork.JoinOrCreateRoom(string roomName, RoomOptions roomOptions, TypedLobby typedLobby, string[] expectedUsers = null)`
- `PhotonNetwork.LeaveRoom(bool becomeInactive = true)`

Photon Realtime exposes the underlying state:

- `PhotonNetwork.NetworkingClient.State`
- `PhotonNetwork.NetworkingClient.DisconnectedCause`
- `LoadBalancingClient.ReconnectToMaster()`
- `LoadBalancingClient.ReconnectAndRejoin()`
- `LoadBalancingClient.OpRejoinRoom(string roomName, object ticket = null)`

## Current Probe Patch

The mod currently logs Photon and Steam lobby state from:

- `NetworkConnect.OnDisconnected(DisconnectCause _cause)`
- `NetworkConnect.OnJoinedRoom()`
- `NetworkManager.OnDisconnected(DisconnectCause cause)`

This is intentionally diagnostic by default. Opt-in reconnect attempts are available through config, but should be treated as experimental until logs confirm which callback fires first, what state survives after a timeout, and whether Steam lobby metadata is still valid.

## Current Reconnect Prototype

The reconnect prototype is disabled by default through `Reconnect.ExperimentalReconnectEnabled=false`.

When enabled:

1. `NetworkConnect.OnJoinedRoom()` remembers the last Photon room name, region, local actor, local user id, and whether the local client was master.
2. `NetworkConnect.OnDisconnected` and `NetworkManager.OnDisconnected` prefixes check the disconnect cause.
3. Explicit terminal causes such as client/server logic disconnects, authentication failure, region failure, CCU limits, and operation limits are excluded.
4. Host reconnect is blocked unless `Reconnect.AllowHostReconnect=true`.
5. If this client creates a room while reconnect is enabled, `PhotonNetwork.CreateRoom`, `JoinOrCreateRoom`, and `JoinRandomOrCreateRoom` get configured `PlayerTtl`, `EmptyRoomTtl`, and player-object cache preservation values.
6. The reconnect coroutine tries `PhotonNetwork.ReconnectAndRejoin()`.
7. If the client reconnects to master but is not in a room, it tries `PhotonNetwork.RejoinRoom(roomName)` once per attempt.
8. If attempts fail, the coordinator calls `PhotonNetwork.Disconnect()`, `SteamManager.LeaveLobby()`, and `RunManager.LeaveToMainMenu()` as a fallback terminal path.

## Active Reconnect Findings

- Photon room rejoin can succeed after `ClientTimeout` when room `PlayerTtl` is nonzero and `CleanupCacheOnLeave=false`.
- The rejoined client can still have the wrong menu stack: snapshots showed `menuPage=Lobby` and a live `LobbyMenuOpen` timer while `level=Level - Museum` and `director=Main`.
- The local avatar can be present and spawned in runtime state while direct static access is unreliable during reconnect. Prefer resolving the local avatar from runtime `PlayerAvatar.instance`, then `GameDirector.PlayerList` by owned `PhotonView`.
- Forced local respawn made the ghost state worse by destroying the cached player object path. Keep `Reconnect.ForcePlayerRespawnAfterReconnect=false` unless specifically testing respawn repair.
- `NetworkManager.Start()` is the normal scene-start path that instantiates `playerAvatarPrefab`, instantiates `Voice`, and sends `NetworkManager.PlayerSpawnedRPC`; Photon rejoin does not rerun this method.
- `PlayerAvatar.Awake()` destroys duplicate avatars with the same Photon owner already in `GameDirector.PlayerList`, so client-only replacement is rejected if the host still has the stale old actor avatar.
- A viable reconnect repair must be host-coordinated: host removes stale avatar/voice objects for the returning actor, then the returning client instantiates the normal avatar and voice prefabs so `PlayerAvatar.Start()` and host `LevelGenerator.PlayerSpawn()` can rebuild the gameplay links.
- Do not subscribe custom Photon event handlers from plugin `Awake()`. Defer Photon event subscriptions until `NetworkConnect.OnConnectedToMaster`, matching the room TTL and disconnect guard patches, or the game can stall at the region-selection screen.

## Useful Inspection Commands

```powershell
dotnet tool restore
dotnet tool run ilspycmd -- -l c --disable-updatecheck "D:\SteamLibrary\steamapps\common\REPO\REPO_Data\Managed\Assembly-CSharp.dll"
dotnet tool run ilspycmd -- --disable-updatecheck -r "D:\SteamLibrary\steamapps\common\REPO\REPO_Data\Managed" -t NetworkConnect "D:\SteamLibrary\steamapps\common\REPO\REPO_Data\Managed\Assembly-CSharp.dll"
```

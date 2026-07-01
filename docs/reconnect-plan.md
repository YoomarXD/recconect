# Reconnect Plan

## Goal

Let a client recover from transient network loss without immediately being pushed to the main menu, when Photon and Steam state make that safe.

## Phase 1: Diagnostics

- Log every disconnect callback from `NetworkConnect` and `NetworkManager`.
- Log `PhotonNetwork.NetworkingClient.State`, `DisconnectedCause`, `CurrentRoom?.Name`, `CloudRegion`, `IsMasterClient`, message queue state, and Steam lobby validity around disconnects.
- Log successful `NetworkConnect.OnJoinedRoom` state so disconnect logs can be compared against the last known good room/lobby state.
- Keep all reconnect config present but disabled until these diagnostics have been validated in-game.
- Test client timeout, host timeout, Steam overlay join, public server join, private invite join, and host migration.

## Phase 2: Passive Recovery Prototype

- Add BepInEx config for enable/disable, max attempts, attempt delay, attempt timeout, host reconnect policy, and allowed `DisconnectCause` values.
- For client-side timeouts, try `PhotonNetwork.ReconnectAndRejoin()` only while cached room data is still coherent.
- Never run reconnect attempts for explicit leave, kick, ban, version mismatch, closed lobby, application quit, or host-controlled scene transition.
- Keep default behavior as vanilla until the user opts in.

Current implementation:

- `Reconnect.ExperimentalReconnectEnabled=false` preserves vanilla behavior.
- When enabled by a room creator, `PhotonNetwork.CreateRoom`, `JoinOrCreateRoom`, and `JoinRandomOrCreateRoom` receive configured `PlayerTtl`, `EmptyRoomTtl`, and player-object cache preservation values.
- `NetworkConnect.OnJoinedRoom()` stores the last successful Photon room and whether the local client was master.
- Eligible disconnect prefixes suppress vanilla disconnect handling only while an opt-in reconnect coroutine is active.
- The coroutine tries `PhotonNetwork.ReconnectAndRejoin()`, then falls back to `Reconnect()` plus `RejoinRoom(roomName)` if connected to master but not in room.
- If all attempts fail, it explicitly falls back to `PhotonNetwork.Disconnect()`, `SteamManager.LeaveLobby()`, and `RunManager.LeaveToMainMenu()`.
- Host reconnect is blocked unless `Reconnect.AllowHostReconnect=true`.

## Phase 3: State Restoration

- Verify in-game whether configured Photon actor inactivity is accepted by R.E.P.O. public/private room flows.
- If `ReconnectAndRejoin()` fails despite nonzero `PlayerTtl`, test fallback to `Reconnect()` then `RejoinRoom(roomName)`.
- Preserve Steam lobby membership when possible; avoid `SteamManager.LeaveLobby()` during retry windows.
- If recovery fails, allow vanilla disconnect UI and leave-to-menu flow.

## Known Risks

- `NetworkConnect.OnDisconnected` and `NetworkManager.OnDisconnected` both currently push toward terminal disconnect behavior.
- Harmony prefixes now suppress those terminal paths only during opt-in reconnect attempts; this needs in-game validation across callback order.
- `NetworkManager.OnMasterClientSwitched` forces leave, so host loss may be unrecoverable without deeper host migration work.
- Scene loading uses `PhotonNetwork.AutomaticallySyncScene` and `PhotonNetwork.LoadLevel`; reconnecting mid-load may require special handling.
- Steam lobby metadata stores Photon room name, region, build name, and password flag; stale metadata can send clients into the wrong recovery path.

## Tooling

- `scripts/install-recconect.ps1` discovers profiles, builds or uses a bundled DLL, installs the mod, writes diagnostics/experimental config, and can create a friend zip.
- If no BepInEx profile exists, the installer can extract `BepInEx_win_x64_5.4.23.5.zip` into the R.E.P.O. game folder and then install the mod.
- `scripts/deploy-local.ps1` builds and copies the DLL into a supplied BepInEx plugins directory.
- `scripts/package-thunderstore.ps1` builds a release zip under `dist/`.
- Thunderstore publishing still requires a valid `icon.png`.

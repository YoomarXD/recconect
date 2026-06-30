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

- Add BepInEx config for enable/disable, max attempts, attempt delay, and allowed `DisconnectCause` values.
- For client-side timeouts, try `PhotonNetwork.ReconnectAndRejoin()` only while cached room/lobby data is still coherent.
- Never run reconnect attempts for explicit leave, kick, ban, version mismatch, closed lobby, application quit, or host-controlled scene transition.
- Keep default behavior as vanilla until the user opts in.

## Phase 3: State Restoration

- Verify whether Photon actor inactivity is enabled by the game's room options.
- If `ReconnectAndRejoin()` fails, test fallback to `Reconnect()` then `RejoinRoom(roomName)`.
- Preserve Steam lobby membership when possible; avoid `SteamManager.LeaveLobby()` during retry windows.
- If recovery fails, allow vanilla disconnect UI and leave-to-menu flow.

## Known Risks

- `NetworkConnect.OnDisconnected` and `NetworkManager.OnDisconnected` both currently push toward terminal disconnect behavior.
- `NetworkManager.OnMasterClientSwitched` forces leave, so host loss may be unrecoverable without deeper host migration work.
- Scene loading uses `PhotonNetwork.AutomaticallySyncScene` and `PhotonNetwork.LoadLevel`; reconnecting mid-load may require special handling.
- Steam lobby metadata stores Photon room name, region, build name, and password flag; stale metadata can send clients into the wrong recovery path.

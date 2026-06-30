# In-Game Test Plan

Use a separate r2modman/Gale profile or a disposable BepInEx install. Do not test experimental reconnect in a normal save/profile first.

## Setup

1. Build the mod with `dotnet build .\Recconect.sln`.
2. Deploy with `.\scripts\deploy-local.ps1 -BepInExPluginsPath "<profile>\BepInEx\plugins" -Configuration Debug`.
3. Launch once with default config and verify the mod loads.
4. Close the game and edit the generated config:
   - `Diagnostics.Enabled=true`
   - `Diagnostics.LogJoinState=true`
   - `Reconnect.ExperimentalReconnectEnabled=false` for diagnostics-only runs.

## Diagnostics-Only Pass

1. Host a private lobby.
2. Join from a second client.
3. Start a run and confirm logs include `NetworkConnect.OnJoinedRoom:postfix`.
4. Trigger a normal leave and confirm vanilla leave behavior still works.
5. Trigger a client network interruption and capture:
   - `NetworkConnect.OnDisconnected:prefix`
   - `NetworkManager.OnDisconnected:prefix`
   - Photon client state
   - room name
   - Steam lobby validity

## Experimental Reconnect Pass

Run only after diagnostics match expectations.

1. Enable reconnect on the host/room creator:
   - `Reconnect.ExperimentalReconnectEnabled=true`
   - `Reconnect.ConfigureRoomTtlOnCreate=true`
   - `Reconnect.PlayerTtlMilliseconds=30000`
   - `Reconnect.EmptyRoomTtlMilliseconds=60000`
2. Enable reconnect on the client with the same values.
3. Host a fresh lobby after changing config so room TTL patches apply during room creation.
4. Join, start a run, then interrupt only the client network briefly.
5. Expected success path:
   - reconnect coroutine starts
   - `PhotonNetwork.ReconnectAndRejoin()` starts
   - room is rejoined before timeout
   - no fallback leave-to-menu starts
6. Expected failure path:
   - attempts time out
   - fallback calls Photon disconnect, Steam lobby leave, and `RunManager.LeaveToMainMenu()`
   - player is not stranded in a half-disconnected state

## Cases To Avoid Until Later

- Host disconnects.
- Master-client switch during active run.
- Scene loading while reconnecting.
- Public matchmaking with unknown room creator config.
- Kicks, bans, version mismatch, closed lobby, or explicit leave.

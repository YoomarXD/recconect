# In-Game Test Plan

Use a separate r2modman/Gale profile or a disposable BepInEx install. Do not test experimental reconnect in a normal save/profile first.

## Setup

1. If installing from a cloned repo and .NET is missing, either install it manually:
   ```powershell
   winget install --id Microsoft.DotNet.SDK.8 --exact --source winget --accept-package-agreements --accept-source-agreements
   ```
   Or add `-InstallDotNetSdk` to the installer command.
2. List detected profiles:
   ```powershell
   .\scripts\install-recconect.ps1 -ListProfiles
   ```
3. Install diagnostics mode:
   ```powershell
   .\scripts\install-recconect.ps1 -ProfileName "ReconnectTest" -ConfigMode Diagnostics
   ```
4. If profile discovery fails, pass the exact path:
   ```powershell
   .\scripts\install-recconect.ps1 -BepInExPluginsPath "<profile>\BepInEx\plugins" -ConfigMode Diagnostics
   ```
5. If BepInEx is not installed, install into the game folder so the installer can extract the bundled BepInEx archive:
   ```powershell
   .\scripts\install-recconect.ps1 -ListGameInstalls
   .\scripts\install-recconect.ps1 -GamePath "D:\SteamLibrary\steamapps\common\REPO" -ConfigMode Diagnostics
   ```
6. To make a friend zip:
   ```powershell
   .\scripts\install-recconect.ps1 -CreateFriendZip -ConfigMode Experimental -FriendZipPath .\dist\Recconect-friend-test.zip
   ```
7. Your friend extracts the zip and runs one of these:
   ```powershell
   .\Install-Recconect.ps1 -ListProfiles
   .\Install-Recconect.ps1 -ProfileName "ReconnectTest" -ConfigMode Experimental
   ```
   If they do not have BepInEx installed:
   ```powershell
   .\Install-Recconect.ps1 -GamePath "<their R.E.P.O. folder>" -ConfigMode Experimental
   ```
8. Launch once with default config and verify the mod loads.
9. Close the game and verify the generated config:
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
   - `Reconnect.PreservePlayerObjectsDuringReconnect=true`
   - `Reconnect.PlayerTtlMilliseconds=120000`
   - `Reconnect.EmptyRoomTtlMilliseconds=180000`
   - `Reconnect.MaxReconnectAttempts=5`
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

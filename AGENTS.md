# Project Agent Instructions

## Project

Recconect is a BepInEx/HarmonyX code mod for the Steam game R.E.P.O. The goal is to improve the experience for unstable networks by researching and eventually implementing safe reconnect/rejoin behavior around the game's Photon PUN and Steam lobby flow.

## Local Workflow

- This repository lives at `C:\Users\YoomarXD\repomods\recconect`; use native PowerShell for local file and Git work.
- Build with `dotnet build .\Recconect.sln`.
- Inspect game assemblies with the local tool manifest: `dotnet tool restore`, then `dotnet tool run ilspycmd -- ...`.
- Do not commit or publish decompiled R.E.P.O. source code or game assets. Store only symbol names, signatures, observations, and small call-flow notes.
- Prefer read-only inspection of the Steam install. Do not modify the Steam game directory unless explicitly asked.
- Keep the first implementation conservative: log/probe disconnect causes, map state transitions, then add reconnect logic behind BepInEx config toggles.

## Reference Maps

- Human-readable install/API notes: `docs/local-game-map.md`.
- Machine-readable lookup index: `docs/local-game-map.json`.
- Implementation plan and risks: `docs/reconnect-plan.md`.

## Modding Stack

- Loader: BepInEx 5 for Mono Unity.
- Patching: HarmonyX/Harmony through the existing template.
- R.E.P.O. content APIs: REPOLib is documented for future content/network prefab work, but it is not a dependency for the current code-only reconnect probe.
- Multiplayer transport: Photon PUN 2.52 plus Steam lobby metadata.

## Safety Rules

- Treat reconnect logic as multiplayer-sensitive. Avoid changing lobby visibility, room ownership, save state, or scene loading until the exact host/client state machine is verified.
- Any behavior that calls `PhotonNetwork.ReconnectAndRejoin`, `RejoinRoom`, `JoinOrCreateRoom`, `SteamManager.JoinLobby`, or scene-loading APIs must be guarded by config and tested in a separate mod-manager profile.
- Do not use local model output as source of truth. Verify with direct file reads, builds, logs, ILSpy inspection, or in-game tests.

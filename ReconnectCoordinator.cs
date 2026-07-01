using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Recconect;

internal static class ReconnectCoordinator
{
    private static readonly HashSet<DisconnectCause> NeverReconnectCauses = new()
    {
        DisconnectCause.DisconnectByClientLogic,
        DisconnectCause.DisconnectByServerLogic,
        DisconnectCause.InvalidAuthentication,
        DisconnectCause.CustomAuthenticationFailed,
        DisconnectCause.AuthenticationTicketExpired,
        DisconnectCause.MaxCcuReached,
        DisconnectCause.InvalidRegion,
        DisconnectCause.DisconnectByOperationLimit
    };

    private static RoomMemory? lastJoinedRoom;
    private static bool reconnecting;
    private static bool allowingTerminalDisconnect;

    internal static bool IsReconnecting => reconnecting;
    internal static bool ShouldBlockPhotonDisconnect => reconnecting && !allowingTerminalDisconnect;

    internal static void ScheduleHostRepairForEnteredPlayer(Player player)
    {
        if (!Recconect.ModConfig.ExperimentalReconnectEnabled.Value ||
            !Recconect.ModConfig.ForcePlayerRespawnAfterReconnect.Value ||
            !PhotonNetwork.IsMasterClient ||
            player.IsLocal ||
            LevelGenerator.Instance == null ||
            !LevelGenerator.Instance.Generated)
        {
            return;
        }

        Recconect.Logger.LogInfo($"Scheduling host-side spawn repair for entered player actor={player.ActorNumber} nick={player.NickName}.");
        Recconect.Instance.StartCoroutine(HostRepairEnteredPlayerRoutine(player.ActorNumber, player.NickName));
    }

    internal static void RecordJoinedRoom()
    {
        Room? room = PhotonNetwork.CurrentRoom;
        if (room == null)
        {
            return;
        }

        lastJoinedRoom = new RoomMemory(
            room.Name,
            PhotonNetwork.CloudRegion,
            PhotonNetwork.IsMasterClient,
            PhotonNetwork.LocalPlayer?.UserId,
            PhotonNetwork.LocalPlayer?.ActorNumber ?? -1);

        Recconect.Logger.LogInfo($"Remembered room for reconnect: {lastJoinedRoom}");
    }

    internal static bool TryHandleDisconnect(DisconnectCause cause, string source)
    {
        if (reconnecting)
        {
            Recconect.Logger.LogInfo($"Suppressing vanilla disconnect during reconnect flow from {source}: {cause}");
            return true;
        }

        if (!Recconect.ModConfig.ExperimentalReconnectEnabled.Value)
        {
            return false;
        }

        if (!CanAttemptReconnect(cause, out string reason))
        {
            Recconect.Logger.LogInfo($"Reconnect not attempted from {source}: {reason}");
            return false;
        }

        reconnecting = true;
        Recconect.Instance.StartCoroutine(ReconnectRoutine(cause, source, lastJoinedRoom!.Value));
        return true;
    }

    private static bool CanAttemptReconnect(DisconnectCause cause, out string reason)
    {
        if (lastJoinedRoom == null || string.IsNullOrWhiteSpace(lastJoinedRoom.Value.RoomName))
        {
            reason = "no remembered room";
            return false;
        }

        if (lastJoinedRoom.Value.WasMasterClient && !Recconect.ModConfig.AllowHostReconnect.Value)
        {
            reason = "last joined room was as master client and host reconnect is disabled";
            return false;
        }

        if (Recconect.ModConfig.MaxReconnectAttempts.Value <= 0)
        {
            reason = "max reconnect attempts is zero";
            return false;
        }

        if (NeverReconnectCauses.Contains(cause))
        {
            reason = $"cause {cause} is explicitly excluded";
            return false;
        }

        if (!EligibleCauses().Contains(cause))
        {
            reason = $"cause {cause} is not in eligible config list";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static IEnumerator ReconnectRoutine(DisconnectCause cause, string source, RoomMemory room)
    {
        Recconect.Logger.LogWarning($"Experimental reconnect started from {source}: cause={cause}, {room}");

        for (int attempt = 1; attempt <= Recconect.ModConfig.MaxReconnectAttempts.Value; attempt++)
        {
            yield return new WaitForSeconds(Recconect.ModConfig.ReconnectAttemptDelaySeconds.Value);

            Recconect.Logger.LogInfo($"Reconnect attempt {attempt}/{Recconect.ModConfig.MaxReconnectAttempts.Value}: ReconnectAndRejoin room={room.RoomName}");
            PhotonNetwork.IsMessageQueueRunning = true;

            bool started = SafeCall("PhotonNetwork.ReconnectAndRejoin", PhotonNetwork.ReconnectAndRejoin);
            if (!started)
            {
                started = SafeCall("PhotonNetwork.Reconnect", PhotonNetwork.Reconnect);
            }

            if (!started)
            {
                Recconect.Logger.LogWarning($"Reconnect attempt {attempt} did not start.");
                continue;
            }

            float deadline = Time.realtimeSinceStartup + Recconect.ModConfig.ReconnectAttemptTimeoutSeconds.Value;
            bool rejoinRequested = false;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (PhotonNetwork.InRoom && PhotonNetwork.CurrentRoom?.Name == room.RoomName)
                {
                    Recconect.Logger.LogWarning($"Photon rejoin succeeded on attempt {attempt}: room={room.RoomName}; stabilizing game state.");
                    NetworkStateSnapshot.Log("ReconnectCoordinator:photon-success");
                    yield return StabilizeAfterPhotonRejoin(room);

                    reconnecting = false;
                    Recconect.Logger.LogWarning($"Experimental reconnect stabilized on attempt {attempt}: room={room.RoomName}");
                    NetworkStateSnapshot.Log("ReconnectCoordinator:success");
                    yield break;
                }

                if (!rejoinRequested && PhotonNetwork.IsConnectedAndReady && !PhotonNetwork.InRoom)
                {
                    rejoinRequested = true;
                    Recconect.Logger.LogInfo($"Reconnect attempt {attempt}: connected to master, trying RejoinRoom({room.RoomName})");
                    SafeCall("PhotonNetwork.RejoinRoom", () => PhotonNetwork.RejoinRoom(room.RoomName));
                }

                yield return null;
            }

            NetworkStateSnapshot.Log($"ReconnectCoordinator:attempt-timeout:{attempt}", cause);
        }

        reconnecting = false;
        Recconect.Logger.LogWarning("Experimental reconnect failed; future disconnect callbacks will use vanilla behavior.");
        NetworkStateSnapshot.Log("ReconnectCoordinator:failed", cause);
        StartVanillaFallbackAfterFailedReconnect();
    }

    internal static void ClearAfterVanillaDisconnect()
    {
        if (!reconnecting)
        {
            lastJoinedRoom = null;
        }
    }

    private static bool SafeCall(string label, Func<bool> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            Recconect.Logger.LogWarning($"{label} threw: {ex}");
            return false;
        }
    }

    private static HashSet<DisconnectCause> EligibleCauses()
    {
        return Recconect.ModConfig.EligibleDisconnectCauses.Value
            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(static value => value.Trim())
            .Where(static value => Enum.TryParse(value, ignoreCase: true, out DisconnectCause _))
            .Select(static value => Enum.Parse<DisconnectCause>(value, ignoreCase: true))
            .ToHashSet();
    }

    private static void StartVanillaFallbackAfterFailedReconnect()
    {
        allowingTerminalDisconnect = true;
        try
        {
            if (SafeAction("NetworkManager.LeavePhotonRoom", InvokeNetworkManagerLeavePhotonRoom))
            {
                return;
            }

            SafeAction("PhotonNetwork.Disconnect", PhotonNetwork.Disconnect);
            SafeAction("SteamManager.LeaveLobby", InvokeSteamLeaveLobby);
            SafeAction("GameManager.SetGameMode(0)", InvokeGameManagerSetGameModeMainMenu);
            SafeAction("RunManager.LeaveToMainMenu", InvokeRunManagerLeaveToMainMenu);
        }
        finally
        {
            allowingTerminalDisconnect = false;
        }
    }

    private static IEnumerator StabilizeAfterPhotonRejoin(RoomMemory room)
    {
        float deadline = Time.realtimeSinceStartup + Recconect.ModConfig.ReconnectStabilizeSeconds.Value;
        float nextSceneSync = 0f;
        bool respawnStarted = false;
        PhotonNetwork.AutomaticallySyncScene = true;
        TryLoadLevelIfSynced();
        NetworkStateSnapshot.Log("ReconnectCoordinator:stabilize-start");

        while (Time.realtimeSinceStartup < deadline)
        {
            if (Time.realtimeSinceStartup >= nextSceneSync)
            {
                TryLoadLevelIfSynced();
                nextSceneSync = Time.realtimeSinceStartup + 1f;
            }

            if (!IsPhotonLoadingLevel())
            {
                PhotonNetwork.IsMessageQueueRunning = true;
            }

            if (!respawnStarted && Recconect.ModConfig.ForcePlayerRespawnAfterReconnect.Value && IsSyncedSceneLoaded(out _))
            {
                respawnStarted = true;
                yield return ForceLocalPlayerNetworkRespawn();
            }

            if (IsGameStateReadyAfterRejoin(out string reason))
            {
                Recconect.Logger.LogInfo($"Reconnect stabilization ready: {reason}");
                break;
            }

            yield return null;
        }

        PhotonNetwork.IsMessageQueueRunning = true;
        NetworkStateSnapshot.Log("ReconnectCoordinator:stabilize-end");
    }

    private static void TryLoadLevelIfSynced()
    {
        try
        {
            AccessTools.Method(typeof(PhotonNetwork), "LoadLevelIfSynced")?.Invoke(null, null);
        }
        catch (Exception ex)
        {
            Recconect.Logger.LogWarning($"PhotonNetwork.LoadLevelIfSynced threw during reconnect stabilization: {ex}");
        }
    }

    private static bool IsGameStateReadyAfterRejoin(out string reason)
    {
        if (!PhotonNetwork.InRoom)
        {
            reason = "not in Photon room";
            return false;
        }

        if (IsPhotonLoadingLevel())
        {
            reason = "Photon is loading synced scene";
            return false;
        }

        if (!IsSyncedSceneLoaded(out reason))
        {
            return false;
        }

        object? networkManager = AccessTools.Field(AccessTools.TypeByName("NetworkManager"), "instance")?.GetValue(null);
        if (networkManager == null)
        {
            reason = "NetworkManager.instance is not available";
            return false;
        }

        if (!IsLevelGenerated(out reason))
        {
            return false;
        }

        if (!IsGameDirectorMain(out reason))
        {
            return false;
        }

        object? playerAvatar = AccessTools.Field(AccessTools.TypeByName("PlayerAvatar"), "instance")?.GetValue(null);
        if (playerAvatar == null)
        {
            reason = "PlayerAvatar.instance is not available";
            return false;
        }

        object? playerController = AccessTools.Field(AccessTools.TypeByName("PlayerController"), "instance")?.GetValue(null);
        object? playerAvatarScript = playerController == null ? null : AccessTools.Field(playerController.GetType(), "playerAvatarScript")?.GetValue(playerController);
        if (playerAvatarScript == null)
        {
            reason = "PlayerController.playerAvatarScript is not available";
            return false;
        }

        reason = $"scene={SceneManager.GetActiveScene().name} local player objects are available";
        return true;
    }

    private static IEnumerator ForceLocalPlayerNetworkRespawn()
    {
        Recconect.Logger.LogWarning("Refreshing local player network objects after Photon rejoin.");
        NetworkStateSnapshot.Log("ReconnectCoordinator:respawn-start");

        DestroyLocalPlayerAvatar();
        DestroyLocalPlayerVoice();

        yield return null;
        yield return null;
        PhotonNetwork.SendAllOutgoingCommands();
        yield return null;

        NetworkManager? networkManager = NetworkManager.instance;
        if (networkManager == null)
        {
            Recconect.Logger.LogWarning("Cannot respawn local player because NetworkManager.instance is null.");
            yield break;
        }

        PhotonNetwork.IsMessageQueueRunning = true;
        string? avatarPrefabName = networkManager.playerAvatarPrefab?.name;
        if (string.IsNullOrWhiteSpace(avatarPrefabName))
        {
            Recconect.Logger.LogWarning("Cannot respawn local player because NetworkManager.playerAvatarPrefab is missing.");
            yield break;
        }

        PhotonNetwork.Instantiate(avatarPrefabName, Vector3.zero, Quaternion.identity, 0);
        PhotonNetwork.Instantiate("Voice", Vector3.zero, Quaternion.identity, 0);
        PhotonNetwork.SendAllOutgoingCommands();
        networkManager.photonView.RPC("PlayerSpawnedRPC", RpcTarget.All);

        yield return null;
        NetworkStateSnapshot.Log("ReconnectCoordinator:respawn-end");
    }

    private static void DestroyLocalPlayerAvatar()
    {
        PlayerAvatar? avatar = PlayerAvatar.instance;
        if (avatar == null)
        {
            return;
        }

        PhotonView? photonView = avatar.photonView != null ? avatar.photonView : avatar.GetComponent<PhotonView>();
        if (photonView != null && !photonView.IsMine)
        {
            return;
        }

        PlayerAvatar.instance = null;
        DestroyOwnedNetworkObject("stale local player avatar", avatar.gameObject);
    }

    private static void DestroyLocalPlayerVoice()
    {
        PlayerVoiceChat? voice = PlayerVoiceChat.instance;
        if (voice == null)
        {
            return;
        }

        PhotonView? photonView = voice.photonView != null ? voice.photonView : voice.GetComponent<PhotonView>();
        if (photonView != null && !photonView.IsMine)
        {
            return;
        }

        PlayerVoiceChat.instance = null;
        DestroyOwnedNetworkObject("stale local player voice", voice.gameObject);
    }

    private static void DestroyOwnedNetworkObject(string label, GameObject gameObject)
    {
        try
        {
            if (PhotonNetwork.InRoom)
            {
                PhotonNetwork.Destroy(gameObject);
                Recconect.Logger.LogInfo($"Destroyed {label} through PhotonNetwork.Destroy.");
                return;
            }
        }
        catch (Exception ex)
        {
            Recconect.Logger.LogWarning($"PhotonNetwork.Destroy failed for {label}: {ex}");
        }

        UnityEngine.Object.Destroy(gameObject);
    }

    private static IEnumerator HostRepairEnteredPlayerRoutine(int actorNumber, string nickName)
    {
        float deadline = Time.realtimeSinceStartup + 10f;
        while (Time.realtimeSinceStartup < deadline)
        {
            PlayerAvatar? avatar = FindPlayerAvatarByActor(actorNumber);
            if (avatar != null)
            {
                if (!avatar.spawned)
                {
                    SpawnSinglePlayerAvatar(avatar, actorNumber, nickName);
                }
                else
                {
                    Recconect.Logger.LogInfo($"Host-side spawn repair skipped for actor={actorNumber}; avatar is already spawned.");
                }

                yield break;
            }

            yield return new WaitForSeconds(0.25f);
        }

        Recconect.Logger.LogWarning($"Host-side spawn repair could not find avatar for actor={actorNumber} nick={nickName}.");
    }

    private static PlayerAvatar? FindPlayerAvatarByActor(int actorNumber)
    {
        if (GameDirector.instance == null)
        {
            return null;
        }

        foreach (PlayerAvatar avatar in GameDirector.instance.PlayerList)
        {
            if (avatar?.photonView != null && avatar.photonView.OwnerActorNr == actorNumber)
            {
                return avatar;
            }
        }

        return null;
    }

    private static void SpawnSinglePlayerAvatar(PlayerAvatar avatar, int actorNumber, string nickName)
    {
        (Vector3 position, Quaternion rotation) = PickSpawnPoint(actorNumber);
        Recconect.Logger.LogWarning($"Host-side spawning repaired avatar for actor={actorNumber} nick={nickName} at {position}.");
        avatar.Spawn(position, rotation);
        EnsurePlayerSupportObjects(avatar);
        NetworkStateSnapshot.Log("ReconnectCoordinator:host-repair-spawn");
    }

    private static (Vector3 Position, Quaternion Rotation) PickSpawnPoint(int actorNumber)
    {
        List<SpawnPoint> spawnPoints = UnityEngine.Object.FindObjectsOfType<SpawnPoint>().ToList();
        if (spawnPoints.Count == 0)
        {
            Transform? fallback = TruckSafetySpawnPoint.instance?.transform;
            return fallback != null
                ? (fallback.position, fallback.rotation)
                : (Vector3.zero + Vector3.up * 2f, Quaternion.identity);
        }

        List<SpawnPoint> debugSpawnPoints = spawnPoints.Where(static point => point.debug).ToList();
        List<SpawnPoint> candidates = debugSpawnPoints.Count > 0 ? debugSpawnPoints : spawnPoints;
        SpawnPoint selected = candidates[Math.Abs(actorNumber) % candidates.Count];
        return (selected.transform.position, selected.transform.rotation);
    }

    private static void EnsurePlayerSupportObjects(PlayerAvatar avatar)
    {
        LevelGenerator? levelGenerator = LevelGenerator.Instance;
        if (levelGenerator == null || SemiFunc.MenuLevel())
        {
            return;
        }

        if (avatar.playerDeathHead == null && levelGenerator.PlayerDeathHeadPrefab != null)
        {
            GameObject deathHead = PhotonNetwork.Instantiate(levelGenerator.PlayerDeathHeadPrefab.name, AssetManager.instance.physDisabledPosition, Quaternion.identity, 0);
            PlayerDeathHead component = deathHead.GetComponent<PlayerDeathHead>();
            component.playerAvatar = avatar;
            avatar.playerDeathHead = component;
        }

        if (avatar.tumble == null && levelGenerator.PlayerTumblePrefab != null)
        {
            GameObject tumble = PhotonNetwork.Instantiate(levelGenerator.PlayerTumblePrefab.name, AssetManager.instance.physDisabledPosition, Quaternion.identity, 0);
            PlayerTumble component = tumble.GetComponent<PlayerTumble>();
            component.playerAvatar = avatar;
            avatar.tumble = component;
        }

        PhotonNetwork.SendAllOutgoingCommands();
    }

    private static bool IsPhotonLoadingLevel()
    {
        try
        {
            return (bool)(AccessTools.Field(typeof(PhotonNetwork), "loadingLevelAndPausedNetwork")?.GetValue(null) ?? false);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSyncedSceneLoaded(out string reason)
    {
        object? scene = PhotonNetwork.CurrentRoom?.CustomProperties["curScn"];
        Scene activeScene = SceneManager.GetActiveScene();

        if (scene == null)
        {
            reason = "room has no synced scene property";
            return true;
        }

        if (scene is int sceneIndex)
        {
            bool matches = activeScene.buildIndex == sceneIndex;
            reason = matches ? $"synced scene index {sceneIndex} is loaded" : $"waiting for synced scene index {sceneIndex}, active={activeScene.buildIndex}:{activeScene.name}";
            return matches;
        }

        if (scene is string sceneName)
        {
            bool matches = activeScene.name == sceneName;
            reason = matches ? $"synced scene {sceneName} is loaded" : $"waiting for synced scene {sceneName}, active={activeScene.name}";
            return matches;
        }

        reason = $"unknown synced scene property type {scene.GetType().FullName}";
        return true;
    }

    private static bool IsLevelGenerated(out string reason)
    {
        LevelGenerator? levelGenerator = LevelGenerator.Instance;
        if (levelGenerator == null)
        {
            reason = "LevelGenerator.Instance is not available";
            return false;
        }

        if (!levelGenerator.Generated)
        {
            reason = "LevelGenerator has not finished";
            return false;
        }

        reason = "level is generated";
        return true;
    }

    private static bool IsGameDirectorMain(out string reason)
    {
        GameDirector? gameDirector = GameDirector.instance;
        if (gameDirector == null)
        {
            reason = "GameDirector.instance is not available";
            return false;
        }

        if (gameDirector.currentState != GameDirector.gameState.Main)
        {
            reason = $"GameDirector is {gameDirector.currentState}";
            return false;
        }

        reason = "GameDirector is Main";
        return true;
    }

    private static void InvokeNetworkManagerLeavePhotonRoom()
    {
        object? networkManager = AccessTools.Field(AccessTools.TypeByName("NetworkManager"), "instance")?.GetValue(null);
        System.Reflection.MethodInfo? leavePhotonRoom = AccessTools.Method(networkManager?.GetType(), "LeavePhotonRoom");

        if (networkManager == null || leavePhotonRoom == null)
        {
            throw new InvalidOperationException("NetworkManager.instance or LeavePhotonRoom was not available.");
        }

        leavePhotonRoom.Invoke(networkManager, null);
    }

    private static void InvokeSteamLeaveLobby()
    {
        object? steamManager = AccessTools.Field(AccessTools.TypeByName("SteamManager"), "instance")?.GetValue(null);
        AccessTools.Method(steamManager?.GetType(), "LeaveLobby")?.Invoke(steamManager, null);
    }

    private static void InvokeGameManagerSetGameModeMainMenu()
    {
        object? gameManager = AccessTools.Field(AccessTools.TypeByName("GameManager"), "instance")?.GetValue(null);
        System.Reflection.MethodInfo? setGameMode = AccessTools.Method(gameManager?.GetType(), "SetGameMode", new[] { typeof(int) });

        if (gameManager == null || setGameMode == null)
        {
            throw new InvalidOperationException("GameManager.instance or SetGameMode(int) was not available.");
        }

        setGameMode.Invoke(gameManager, new object[] { 0 });
    }

    private static void InvokeRunManagerLeaveToMainMenu()
    {
        object? runManager = AccessTools.Field(AccessTools.TypeByName("RunManager"), "instance")?.GetValue(null);
        object? coroutine = AccessTools.Method(runManager?.GetType(), "LeaveToMainMenu")?.Invoke(runManager, null);
        if (coroutine is IEnumerator routine)
        {
            Recconect.Instance.StartCoroutine(routine);
        }
    }

    private static bool SafeAction(string label, Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            Recconect.Logger.LogWarning($"{label} fallback threw: {ex}");
            return false;
        }
    }

    private readonly struct RoomMemory
    {
        internal RoomMemory(string roomName, string region, bool wasMasterClient, string? userId, int actorNumber)
        {
            RoomName = roomName;
            Region = region;
            WasMasterClient = wasMasterClient;
            UserId = userId;
            ActorNumber = actorNumber;
        }

        internal string RoomName { get; }
        internal string Region { get; }
        internal bool WasMasterClient { get; }
        internal string? UserId { get; }
        internal int ActorNumber { get; }

        public override string ToString()
        {
            return $"room={RoomName} region={Region} wasMaster={WasMasterClient} user={UserId ?? "<null>"} actor={ActorNumber}";
        }
    }
}

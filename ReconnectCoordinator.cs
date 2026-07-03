using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using ExitGames.Client.Photon;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Recconect;

internal static class ReconnectCoordinator
{
    private const byte ReplaceActorObjectsRequestEventCode = 171;
    private const byte ReplaceActorObjectsReadyEventCode = 172;

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
    private static bool eventHandlerInstalled;
    private static readonly Dictionary<int, float> actorReplacementDeadlines = new();
    private static readonly HashSet<int> hostReplacementReadyActors = new();

    internal static bool IsReconnecting => reconnecting;
    internal static bool ShouldBlockPhotonDisconnect => reconnecting && !allowingTerminalDisconnect;

    internal static void PreparePlayerAvatarAwake(PlayerAvatar avatar)
    {
        if (!Recconect.ModConfig.ExperimentalReconnectEnabled.Value || avatar == null)
        {
            return;
        }

        PhotonView? photonView = avatar.GetComponent<PhotonView>();
        int actorNumber = photonView?.OwnerActorNr ?? -1;
        if (!IsActorReplacementActive(actorNumber))
        {
            return;
        }

        int removed = RemoveDuplicateActorAvatarsBeforeAwake(actorNumber, photonView?.ViewID ?? -1, destroyLocalObjects: true);
        if (removed > 0)
        {
            Recconect.Logger.LogWarning($"Prepared PlayerAvatar.Awake for replacement actor={actorNumber}, view={photonView?.ViewID ?? -1}; removed {removed} stale same-owner avatar entries before duplicate guard.");
        }
    }

    internal static void LogPlayerAvatarLifecycle(string stage, PlayerAvatar avatar)
    {
        if (!Recconect.ModConfig.DiagnosticsEnabled.Value || avatar == null)
        {
            return;
        }

        PhotonView? photonView = avatar.photonView != null ? avatar.photonView : avatar.GetComponent<PhotonView>();
        bool isMine = photonView != null && photonView.IsMine;
        bool replacementActive = IsActorReplacementActive(photonView?.OwnerActorNr ?? -1);
        if (!ShouldLogRuntimeDiagnostics(photonView, replacementActive))
        {
            return;
        }

        Transform avatarTransform = avatar.transform;
        GameObject? parentObject = avatarTransform.parent == null ? null : avatarTransform.parent.gameObject;
        Recconect.Logger.LogInfo(
            $"PlayerAvatar.{stage}: view={photonView?.ViewID ?? -1} owner={photonView?.OwnerActorNr ?? -1} isMine={isMine} replacementActive={replacementActive} isLocal={avatar.isLocal} spawned={avatar.spawned} disabled={avatar.isDisabled} unityNull={(avatar == null)} transform={FormatTransform(avatarTransform)} parent={FormatGameObject(parentObject)} staticInstance={FormatStaticPlayerAvatar()} controller={FormatPlayerController()} playerList={PlayerListSummary()}");

        if (ShouldLogAvatarDestroyStack(stage, photonView, replacementActive))
        {
            Recconect.Logger.LogWarning($"PlayerAvatar.{stage}: destroy-stack\n{CompactStackTrace()}");
            LogDeepDiagnosticState($"PlayerAvatar.{stage}");
        }
    }

    internal static void LogPlayerAvatarSpawn(string stage, PlayerAvatar avatar, Vector3 position, Quaternion rotation, PhotonMessageInfo? info = null)
    {
        if (!Recconect.ModConfig.DiagnosticsEnabled.Value || avatar == null)
        {
            return;
        }

        PhotonView? photonView = avatar.photonView != null ? avatar.photonView : avatar.GetComponent<PhotonView>();
        if (!ShouldLogRuntimeDiagnostics(photonView, IsActorReplacementActive(photonView?.OwnerActorNr ?? -1)))
        {
            return;
        }

        string sender = info.HasValue
            ? $"{info.Value.Sender?.ActorNumber ?? -1}:{info.Value.Sender?.NickName ?? "<null>"}"
            : "<none>";
        Recconect.Logger.LogWarning(
            $"PlayerAvatar.{stage}: view={photonView?.ViewID ?? -1} owner={photonView?.OwnerActorNr ?? -1} isMine={photonView?.IsMine ?? false} sender={sender} pos={FormatVector(position)} rot={FormatQuaternion(rotation)} before spawned={avatar.spawned} transform={FormatTransform(avatar.transform)} staticInstance={FormatStaticPlayerAvatar()} controller={FormatPlayerController()}");
        LogDeepDiagnosticState($"PlayerAvatar.{stage}");
    }

    internal static void LogPlayerAvatarSpawnResult(string stage, PlayerAvatar avatar)
    {
        if (!Recconect.ModConfig.DiagnosticsEnabled.Value || avatar == null)
        {
            return;
        }

        PhotonView? photonView = avatar.photonView != null ? avatar.photonView : avatar.GetComponent<PhotonView>();
        if (!ShouldLogRuntimeDiagnostics(photonView, IsActorReplacementActive(photonView?.OwnerActorNr ?? -1)))
        {
            return;
        }

        Recconect.Logger.LogWarning(
            $"PlayerAvatar.{stage}: view={photonView?.ViewID ?? -1} owner={photonView?.OwnerActorNr ?? -1} isMine={photonView?.IsMine ?? false} after spawned={avatar.spawned} transform={FormatTransform(avatar.transform)} staticInstance={FormatStaticPlayerAvatar()} controller={FormatPlayerController()}");
    }

    internal static void LogNetworkSpawnMethod(string label, PhotonMessageInfo? info = null)
    {
        if (!ShouldLogDeepDiagnostics())
        {
            return;
        }

        string sender = info.HasValue
            ? $"{info.Value.Sender?.ActorNumber ?? -1}:{info.Value.Sender?.NickName ?? "<null>"}"
            : "<none>";
        Recconect.Logger.LogWarning($"{label}: sender={sender}");
        LogDeepDiagnosticState(label);
        StartDiagnosticBurst(label, 3f, 0.5f);
    }

    internal static void StartDiagnosticBurst(string label, float seconds, float interval)
    {
        if (!ShouldLogDeepDiagnostics() || Recconect.Instance == null)
        {
            return;
        }

        Recconect.Instance.StartCoroutine(DiagnosticBurstRoutine(label, seconds, interval));
    }

    internal static void LogDeepDiagnosticState(string label)
    {
        if (!ShouldLogDeepDiagnostics())
        {
            return;
        }

        try
        {
            Recconect.Logger.LogInfo(BuildDeepDiagnosticState(label));
        }
        catch (Exception ex)
        {
            Recconect.Logger.LogWarning($"Deep diagnostic snapshot failed for {label}: {ex}");
        }
    }

    private static IEnumerator DiagnosticBurstRoutine(string label, float seconds, float interval)
    {
        int tick = 0;
        float deadline = Time.realtimeSinceStartup + seconds;
        while (Time.realtimeSinceStartup <= deadline)
        {
            LogDeepDiagnosticState($"{label}:burst:{tick++}");
            yield return new WaitForSeconds(interval);
        }
    }

    private static bool ShouldLogAvatarDestroyStack(string stage, PhotonView? photonView, bool replacementActive)
    {
        if (!stage.StartsWith("OnDestroy", StringComparison.Ordinal))
        {
            return false;
        }

        if (photonView == null || photonView.ViewID <= 0)
        {
            return false;
        }

        int localActor = PhotonNetwork.LocalPlayer?.ActorNumber ?? -1;
        return replacementActive || reconnecting || photonView.OwnerActorNr == localActor || photonView.IsMine;
    }

    private static bool ShouldLogRuntimeDiagnostics(PhotonView? photonView, bool replacementActive)
    {
        if (!Recconect.ModConfig.DiagnosticsEnabled.Value)
        {
            return false;
        }

        if (Recconect.ModConfig.VerboseRuntimeDiagnostics.Value)
        {
            return true;
        }

        if (reconnecting || replacementActive)
        {
            return true;
        }

        return false;
    }

    private static bool ShouldLogDeepDiagnostics()
    {
        if (!Recconect.ModConfig.DiagnosticsEnabled.Value)
        {
            return false;
        }

        return Recconect.ModConfig.VerboseRuntimeDiagnostics.Value || reconnecting || actorReplacementDeadlines.Count > 0;
    }

    private static string CompactStackTrace()
    {
        string[] lines = Environment.StackTrace
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Skip(2)
            .Take(28)
            .Select(static line => line.Trim())
            .ToArray();

        return string.Join("\n", lines);
    }

    private static string BuildDeepDiagnosticState(string label)
    {
        StringBuilder builder = new();
        builder.AppendLine($"[Recconect deep] label={label} realtime={Time.realtimeSinceStartup:F2}");
        builder.AppendLine($"photon connected={PhotonNetwork.IsConnected} ready={PhotonNetwork.IsConnectedAndReady} inRoom={PhotonNetwork.InRoom} state={PhotonNetwork.NetworkingClient?.State} server={PhotonNetwork.NetworkingClient?.Server} queue={PhotonNetwork.IsMessageQueueRunning} localActor={PhotonNetwork.LocalPlayer?.ActorNumber ?? -1} localUser={PhotonNetwork.LocalPlayer?.UserId ?? "<null>"} isMaster={PhotonNetwork.IsMasterClient}");

        Room? room = PhotonNetwork.CurrentRoom;
        builder.AppendLine(room == null
            ? "room=<null>"
            : $"room name={room.Name} players={room.PlayerCount}/{room.MaxPlayers} open={room.IsOpen} visible={room.IsVisible} custom={FormatHashtable(room.CustomProperties)}");

        Scene activeScene = SceneManager.GetActiveScene();
        builder.AppendLine($"scene index={activeScene.buildIndex} name={activeScene.name} loaded={activeScene.isLoaded} path={activeScene.path}");
        builder.AppendLine($"game mode={GameManager.instance?.gameMode.ToString() ?? "<null>"} director={GameDirector.instance?.currentState.ToString() ?? "<null>"} networkLoadingDone={NetworkManager.instance?.LoadingDone.ToString() ?? "<null>"} level={RunManager.instance?.levelCurrent?.name ?? "<null>"} levelGenerated={LevelGenerator.Instance?.Generated.ToString() ?? "<null>"} levelState={LevelGenerator.Instance?.State.ToString() ?? "<null>"} levelPlayerSpawned={LevelGenerator.Instance?.playerSpawned.ToString() ?? "<null>"} allPlayersReady={LevelGenerator.Instance?.AllPlayersReady.ToString() ?? "<null>"} leavePhotonRoom={NetworkManager.instance?.leavePhotonRoom.ToString() ?? "<null>"}");
        builder.AppendLine($"staticAvatar={FormatStaticPlayerAvatar()}");
        builder.AppendLine($"controller={FormatPlayerController()}");
        builder.AppendLine($"camera={FormatCamera()}");
        builder.AppendLine($"menu={FormatMenuState()}");
        builder.AppendLine($"players={FormatAllPlayerAvatars()}");
        builder.AppendLine($"voices={FormatAllVoiceChats()}");
        builder.AppendLine($"photonViews={FormatRelevantPhotonViews()}");
        return builder.ToString().TrimEnd();
    }

    private static string FormatHashtable(ExitGames.Client.Photon.Hashtable? hashtable)
    {
        if (hashtable == null || hashtable.Count == 0)
        {
            return "<empty>";
        }

        try
        {
            List<string> entries = new();
            foreach (object key in hashtable.Keys)
            {
                entries.Add($"{key}={hashtable[key]}");
            }

            return string.Join(",", entries);
        }
        catch (Exception ex)
        {
            return $"<error:{ex.Message}>";
        }
    }

    private static string FormatStaticPlayerAvatar()
    {
        PlayerAvatar? avatar = PlayerAvatar.instance;
        return FormatAvatar("static", avatar);
    }

    private static string FormatAllPlayerAvatars()
    {
        try
        {
            return string.Join(" ; ", UnityEngine.Object.FindObjectsOfType<PlayerAvatar>().Select((avatar, index) => FormatAvatar(index.ToString(), avatar)));
        }
        catch (Exception ex)
        {
            return $"<error:{ex.Message}>";
        }
    }

    private static string FormatAllVoiceChats()
    {
        try
        {
            return string.Join(" ; ", UnityEngine.Object.FindObjectsOfType<PlayerVoiceChat>().Select(static (voice, index) =>
            {
                if (voice == null)
                {
                    return $"{index}:<unity-null>";
                }

                PhotonView? photonView = voice.photonView != null ? voice.photonView : voice.GetComponent<PhotonView>();
                return $"{index}:name={voice.name} view={photonView?.ViewID ?? -1} owner={photonView?.OwnerActorNr ?? -1} isMine={photonView?.IsMine ?? false} active={voice.gameObject.activeSelf} transform={FormatTransform(voice.transform)} playerAvatar={FormatAvatar("voiceAvatar", voice.playerAvatar)}";
            }));
        }
        catch (Exception ex)
        {
            return $"<error:{ex.Message}>";
        }
    }

    private static string FormatRelevantPhotonViews()
    {
        try
        {
            int localActor = PhotonNetwork.LocalPlayer?.ActorNumber ?? -1;
            HashSet<int> roomActors = PhotonNetwork.CurrentRoom?.Players?.Keys.ToHashSet() ?? new HashSet<int>();
            return string.Join(" ; ", UnityEngine.Object.FindObjectsOfType<PhotonView>()
                .Where(view => view != null && (view.OwnerActorNr == localActor || roomActors.Contains(view.OwnerActorNr) || view.ViewID < 3000))
                .OrderBy(static view => view.ViewID)
                .Take(80)
                .Select(static view => $"{view.ViewID}:owner={view.OwnerActorNr} ctrl={view.ControllerActorNr} mine={view.IsMine} name={view.name} active={view.gameObject.activeSelf} pos={FormatTransform(view.transform)}"));
        }
        catch (Exception ex)
        {
            return $"<error:{ex.Message}>";
        }
    }

    private static string FormatAvatar(string label, PlayerAvatar? avatar)
    {
        if (avatar == null)
        {
            return $"{label}:<null-or-destroyed>";
        }

        PhotonView? photonView = avatar.photonView != null ? avatar.photonView : avatar.GetComponent<PhotonView>();
        return $"{label}:name={avatar.name} view={photonView?.ViewID ?? -1} owner={photonView?.OwnerActorNr ?? -1} ctrl={photonView?.ControllerActorNr ?? -1} isMine={photonView?.IsMine ?? false} isLocal={avatar.isLocal} spawned={avatar.spawned} disabled={avatar.isDisabled} active={avatar.gameObject.activeSelf} transform={FormatTransform(avatar.transform)} parent={FormatGameObject(avatar.transform.parent?.gameObject)}";
    }

    private static string FormatPlayerController()
    {
        try
        {
            PlayerController? controller = PlayerController.instance;
            if (controller == null)
            {
                return "<null-or-destroyed>";
            }

            object? rbObject = GetRuntimeInstanceField(controller, "rb");
            string rbState = rbObject is Rigidbody rb
                ? $"rbPos={FormatVector(rb.position)} rbVel={FormatVector(rb.velocity)}"
                : "rb=<null>";
            return $"active={controller.gameObject.activeSelf} transform={FormatTransform(controller.transform)} {rbState} inputDisable={GetRuntimeInstanceField(controller, "InputDisableTimer") ?? "<null>"} avatarGo={FormatGameObject(controller.playerAvatar)} avatarScript={FormatAvatar("controllerAvatar", controller.playerAvatarScript)}";
        }
        catch (Exception ex)
        {
            return $"<error:{ex.Message}>";
        }
    }

    private static string FormatCamera()
    {
        try
        {
            Camera? camera = Camera.main;
            if (camera == null)
            {
                return "<null>";
            }

            return $"name={camera.name} active={camera.gameObject.activeSelf} enabled={camera.enabled} transform={FormatTransform(camera.transform)}";
        }
        catch (Exception ex)
        {
            return $"<error:{ex.Message}>";
        }
    }

    private static string FormatMenuState()
    {
        try
        {
            object? menuPage = MenuManager.instance == null ? null : GetRuntimeInstanceField(MenuManager.instance, "currentMenuPage");
            object? lobbyMenuOpen = GetRuntimeStaticField("LobbyMenuOpen", "instance");
            return $"menuState={MenuManager.instance?.currentMenuState.ToString() ?? "<null>"} currentPage={GetRuntimeInstanceField(menuPage, "menuPageIndex") ?? "<null>"} hudHidden={HUD.instance?.hidden.ToString() ?? "<null>"} loadingActive={LoadingUI.instance?.gameObject.activeSelf.ToString() ?? "<null>"} loadingStuck={GetRuntimeInstanceField(LoadingUI.instance, "stuckActive") ?? "<null>"} lobbyOpen={GetRuntimeInstanceField(lobbyMenuOpen, "opened") ?? "<null>"}/{GetRuntimeInstanceField(lobbyMenuOpen, "timer") ?? "<null>"}";
        }
        catch (Exception ex)
        {
            return $"<error:{ex.Message}>";
        }
    }

    private static string FormatGameObject(GameObject? gameObject)
    {
        if (gameObject == null)
        {
            return "<null-or-destroyed>";
        }

        PhotonView? photonView = gameObject.GetComponent<PhotonView>();
        return $"{gameObject.name} active={gameObject.activeSelf} view={photonView?.ViewID ?? -1} owner={photonView?.OwnerActorNr ?? -1} transform={FormatTransform(gameObject.transform)}";
    }

    private static string FormatTransform(Transform? transform)
    {
        if (transform == null)
        {
            return "<null>";
        }

        return $"pos={FormatVector(transform.position)} rot={FormatQuaternion(transform.rotation)}";
    }

    private static string FormatVector(Vector3 value)
    {
        return $"({value.x:F2},{value.y:F2},{value.z:F2})";
    }

    private static string FormatQuaternion(Quaternion value)
    {
        Vector3 euler = value.eulerAngles;
        return $"({euler.x:F1},{euler.y:F1},{euler.z:F1})";
    }

    internal static void InstallEventHandler()
    {
        if (eventHandlerInstalled)
        {
            return;
        }

        PhotonNetwork.NetworkingClient.EventReceived += OnPhotonEventReceived;
        eventHandlerInstalled = true;
    }

    internal static void UninstallEventHandler()
    {
        if (!eventHandlerInstalled)
        {
            return;
        }

        PhotonNetwork.NetworkingClient.EventReceived -= OnPhotonEventReceived;
        eventHandlerInstalled = false;
    }

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
        bool rejoinedPhotonRoom = false;

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
                    rejoinedPhotonRoom = true;
                    Recconect.Logger.LogWarning($"Photon rejoin succeeded on attempt {attempt}: room={room.RoomName}; stabilizing game state.");
                    NetworkStateSnapshot.Log("ReconnectCoordinator:photon-success");
                    StartDiagnosticBurst("photon-success", 4f, 0.5f);
                    yield return StabilizeAfterPhotonRejoin(room);

                    if (!IsGameStateReadyAfterRejoin(out string stabilizeFailureReason))
                    {
                        Recconect.Logger.LogWarning($"Reconnect attempt {attempt} rejoined Photon but game state did not stabilize: {stabilizeFailureReason}");
                        NetworkStateSnapshot.Log("ReconnectCoordinator:stabilize-failed");
                        break;
                    }

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
        if (rejoinedPhotonRoom)
        {
            Recconect.Logger.LogWarning("Experimental reconnect rejoined Photon but game state did not stabilize; leaving client in-room for diagnostics instead of forcing vanilla disconnect.");
            NetworkStateSnapshot.Log("ReconnectCoordinator:failed-in-room", cause);
            yield break;
        }

        Recconect.Logger.LogWarning("Experimental reconnect failed before Photon room rejoin; future disconnect callbacks will use vanilla behavior.");
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
        float nextUiRepair = 0f;
        bool uiRepairLogged = false;
        bool replacementStarted = false;
        bool respawnStarted = false;
        PhotonNetwork.AutomaticallySyncScene = true;
        TryLoadLevelIfSynced();
        NetworkStateSnapshot.Log("ReconnectCoordinator:stabilize-start");
        LogDeepDiagnosticState("stabilize-start");

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

            if (PhotonNetwork.InRoom &&
                Time.realtimeSinceStartup >= nextUiRepair &&
                IsReconnectRuntimeSceneReadyForMenuCleanup())
            {
                RepairLocalGameplayUiAfterReconnect(!uiRepairLogged);
                uiRepairLogged = true;
                nextUiRepair = Time.realtimeSinceStartup + 0.5f;
            }

            if (!replacementStarted && ShouldReplaceLocalPlayerObjectsAfterReconnect(out string replacementReason))
            {
                replacementStarted = true;
                Recconect.Logger.LogWarning($"Local player replacement is required after reconnect: {replacementReason}");
                yield return ReplaceLocalPlayerObjectsAfterReconnect(room);
            }

            if (!respawnStarted &&
                Recconect.ModConfig.ForcePlayerRespawnAfterReconnect.Value &&
                ShouldForceLocalPlayerRespawn(out string respawnReason))
            {
                respawnStarted = true;
                Recconect.Logger.LogWarning($"Local player respawn is required after reconnect: {respawnReason}");
                yield return ForceLocalPlayerNetworkRespawn();
            }

            if (IsGameStateReadyAfterRejoin(out string reason))
            {
                Recconect.Logger.LogInfo($"Reconnect stabilization ready: {reason}");
                RepairLocalGameplayUiAfterReconnect(!uiRepairLogged);
                break;
            }

            yield return null;
        }

        PhotonNetwork.IsMessageQueueRunning = true;
        NetworkStateSnapshot.Log("ReconnectCoordinator:stabilize-end");
        LogDeepDiagnosticState("stabilize-end");
    }

    private static void RepairLocalGameplayUiAfterReconnect(bool logSnapshot = true)
    {
        SafeAction("LoadingUI.StopLoading", () =>
        {
            if (LoadingUI.instance != null && LoadingUI.instance.gameObject.activeSelf)
            {
                LoadingUI.instance.StopLoading();
            }
        });

        SafeAction("HUD.Show", () =>
        {
            if (HUD.instance != null)
            {
                HUD.instance.Show();
            }
        });

        SafeAction("MenuManager.PageCloseAll", () =>
        {
            if (MenuManager.instance == null)
            {
                return;
            }

            MenuManager.instance.PageCloseAll();
            DestroyRuntimeMenuPages(MenuManager.instance);
            AccessTools.Field(typeof(MenuManager), "currentMenuPage")?.SetValue(MenuManager.instance, null);
            AccessTools.Field(typeof(MenuManager), "currentMenuState")?.SetValue(MenuManager.instance, (int)MenuManager.MenuState.Closed);
        });

        SafeAction("LobbyMenuOpen.Destroy", () =>
        {
            Type? lobbyMenuOpenType = AccessTools.TypeByName("LobbyMenuOpen");
            if (lobbyMenuOpenType == null)
            {
                return;
            }

            foreach (UnityEngine.Object opener in UnityEngine.Object.FindObjectsOfType(lobbyMenuOpenType))
            {
                if (opener is Component component)
                {
                    UnityEngine.Object.Destroy(component.gameObject);
                }
            }

            AccessTools.Field(lobbyMenuOpenType, "instance")?.SetValue(null, null);
        });

        SafeAction("PlayerController.InputDisableTimer", () =>
        {
            object? playerController = GetRuntimeStaticField("PlayerController", "instance");
            if (playerController != null)
            {
                SetRuntimeInstanceField(playerController, "InputDisableTimer", 0f);
            }
        });

        if (logSnapshot)
        {
            NetworkStateSnapshot.Log("ReconnectCoordinator:ui-repair");
        }
    }

    private static bool IsReconnectRuntimeSceneReadyForMenuCleanup()
    {
        if (!IsSyncedSceneLoaded(out _) || !IsLevelGenerated(out _))
        {
            return false;
        }

        try
        {
            return !SemiFunc.MenuLevel();
        }
        catch
        {
            object? runManager = GetRuntimeStaticField("RunManager", "instance");
            object? levelCurrent = runManager == null ? null : GetRuntimeInstanceField(runManager, "levelCurrent");
            string? levelName = GetRuntimeInstanceProperty(levelCurrent, "name")?.ToString();
            return !string.IsNullOrWhiteSpace(levelName) &&
                !levelName.Contains("Lobby Menu") &&
                !levelName.Contains("Main Menu") &&
                !levelName.Contains("Splash");
        }
    }

    private static void DestroyRuntimeMenuPages(MenuManager menuManager)
    {
        foreach (string listFieldName in new[] { "allPages", "inactivePages", "addedPagesOnTop" })
        {
            if (AccessTools.Field(typeof(MenuManager), listFieldName)?.GetValue(menuManager) is not System.Collections.IList pages)
            {
                continue;
            }

            object[] pageSnapshot = pages.Cast<object>().ToArray();
            foreach (object page in pageSnapshot)
            {
                if (IsUnityNull(page))
                {
                    continue;
                }

                try
                {
                    if (page is Component component && !IsUnityNull(component))
                    {
                        UnityEngine.Object.Destroy(component.gameObject);
                    }
                }
                catch (Exception ex)
                {
                    Recconect.Logger.LogInfo($"Skipped stale menu page during reconnect cleanup: {ex.Message}");
                }
            }

            pages.Clear();
        }
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

        if (!IsLocalPlayerAvatarReady(out reason))
        {
            return false;
        }

        reason = $"scene={SceneManager.GetActiveScene().name} local player objects are available";
        return true;
    }

    private static bool ShouldReplaceLocalPlayerObjectsAfterReconnect(out string reason)
    {
        if (!PhotonNetwork.InRoom)
        {
            reason = "not in Photon room";
            return false;
        }

        if (!IsSyncedSceneLoaded(out reason) || !IsLevelGenerated(out reason) || !IsGameDirectorMain(out reason))
        {
            return false;
        }

        if (IsLocalPlayerAvatarReady(out reason))
        {
            return false;
        }

        reason = $"runtime local avatar is not usable: {reason}";
        return true;
    }

    private static IEnumerator ReplaceLocalPlayerObjectsAfterReconnect(RoomMemory room)
    {
        NetworkStateSnapshot.Log("ReconnectCoordinator:replace-start");
        LogDeepDiagnosticState("replace-start");
        StartDiagnosticBurst("replace-start", 4f, 0.5f);

        int actorNumber = PhotonNetwork.LocalPlayer?.ActorNumber ?? room.ActorNumber;
        if (actorNumber <= 0)
        {
            Recconect.Logger.LogWarning("Cannot replace local player objects because the local actor number is unavailable.");
            yield break;
        }

        RequestHostRemoveStaleActorObjects(actorNumber);
        yield return WaitForHostReplacementReady(actorNumber);

        CleanupLocalDestroyedPlayerReferences();
        ClearStaleLocalSingletons();

        NetworkManager? networkManager = NetworkManager.instance;
        if (networkManager == null)
        {
            Recconect.Logger.LogWarning("Cannot replace local player objects because NetworkManager.instance is null.");
            yield break;
        }

        string? avatarPrefabName = networkManager.playerAvatarPrefab?.name;
        if (string.IsNullOrWhiteSpace(avatarPrefabName))
        {
            Recconect.Logger.LogWarning("Cannot replace local player objects because NetworkManager.playerAvatarPrefab is missing.");
            yield break;
        }

        PhotonNetwork.IsMessageQueueRunning = true;
        Recconect.Logger.LogWarning($"Instantiating replacement local avatar and voice for actor={actorNumber}.");
        LogDeepDiagnosticState("replace-before-instantiate");
        PhotonNetwork.Instantiate(avatarPrefabName, Vector3.zero, Quaternion.identity, 0);

        if (!HasUsableOwnedVoiceChat(actorNumber))
        {
            PhotonNetwork.Instantiate("Voice", Vector3.zero, Quaternion.identity, 0);
        }

        PhotonNetwork.SendAllOutgoingCommands();
        networkManager.photonView.RPC("PlayerSpawnedRPC", RpcTarget.All);
        LogDeepDiagnosticState("replace-after-player-spawned-rpc");
        StartDiagnosticBurst("replace-after-instantiate", 8f, 0.25f);

        float deadline = Time.realtimeSinceStartup + 8f;
        while (Time.realtimeSinceStartup < deadline)
        {
            CleanupLocalDestroyedPlayerReferences();
            if (IsLocalPlayerAvatarReady(out string readyReason))
            {
                Recconect.Logger.LogWarning($"Replacement local player objects are ready: {readyReason}");
                NetworkStateSnapshot.Log("ReconnectCoordinator:replace-ready");
                yield break;
            }

            yield return null;
        }

        Recconect.Logger.LogWarning("Replacement local player objects did not become ready before timeout.");
        NetworkStateSnapshot.Log("ReconnectCoordinator:replace-timeout");
        LogDeepDiagnosticState("replace-timeout");
    }

    private static void RequestHostRemoveStaleActorObjects(int actorNumber)
    {
        try
        {
            MarkActorReplacement(actorNumber);
            hostReplacementReadyActors.Remove(actorNumber);
            RemoveDuplicateActorAvatarsBeforeAwake(actorNumber, exceptViewId: -1, destroyLocalObjects: true);

            RaiseEventOptions options = new()
            {
                Receivers = ReceiverGroup.All
            };

            PhotonNetwork.RaiseEvent(ReplaceActorObjectsRequestEventCode, actorNumber, options, SendOptions.SendReliable);
            Recconect.Logger.LogWarning($"Requested host-side stale player object removal for actor={actorNumber}.");
        }
        catch (Exception ex)
        {
            Recconect.Logger.LogWarning($"Failed to request host-side stale player object removal: {ex}");
        }
    }

    private static IEnumerator WaitForHostReplacementReady(int actorNumber)
    {
        float deadline = Time.realtimeSinceStartup + 5f;
        while (Time.realtimeSinceStartup < deadline)
        {
            if (hostReplacementReadyActors.Contains(actorNumber))
            {
                Recconect.Logger.LogWarning($"Host confirmed stale player object cleanup for actor={actorNumber}; instantiating replacement.");
                yield break;
            }

            yield return null;
        }

        Recconect.Logger.LogWarning($"Timed out waiting for host stale player cleanup acknowledgement for actor={actorNumber}; trying replacement anyway.");
    }

    private static void OnPhotonEventReceived(EventData photonEvent)
    {
        if (photonEvent.Code != ReplaceActorObjectsRequestEventCode &&
            photonEvent.Code != ReplaceActorObjectsReadyEventCode)
        {
            return;
        }

        if (!Recconect.ModConfig.ExperimentalReconnectEnabled.Value)
        {
            return;
        }

        int actorNumber = photonEvent.CustomData switch
        {
            int value => value,
            short value => value,
            byte value => value,
            _ => -1
        };

        if (actorNumber <= 0)
        {
            Recconect.Logger.LogWarning($"Ignoring stale player replacement request with invalid actor payload: {photonEvent.CustomData}");
            return;
        }

        if (photonEvent.Code == ReplaceActorObjectsReadyEventCode)
        {
            MarkActorReplacement(actorNumber);
            hostReplacementReadyActors.Add(actorNumber);
            Recconect.Logger.LogWarning($"Received host stale player cleanup acknowledgement for actor={actorNumber}.");
            return;
        }

        MarkActorReplacement(actorNumber);
        int removedLocalDuplicates = RemoveDuplicateActorAvatarsBeforeAwake(actorNumber, exceptViewId: -1, destroyLocalObjects: !PhotonNetwork.IsMasterClient);
        if (removedLocalDuplicates > 0)
        {
            Recconect.Logger.LogWarning($"Marked actor={actorNumber} for replacement and removed {removedLocalDuplicates} local stale duplicate avatar entries.");
        }

        if (PhotonNetwork.IsMasterClient)
        {
            Recconect.Logger.LogWarning($"Host received stale player replacement request for actor={actorNumber}.");
            Recconect.Instance.StartCoroutine(HostRemoveAndRepairActorObjects(actorNumber));
        }
    }

    private static void MarkActorReplacement(int actorNumber)
    {
        if (actorNumber <= 0)
        {
            return;
        }

        actorReplacementDeadlines[actorNumber] = Time.realtimeSinceStartup + 15f;
    }

    private static bool IsActorReplacementActive(int actorNumber)
    {
        if (actorNumber <= 0 || !actorReplacementDeadlines.TryGetValue(actorNumber, out float deadline))
        {
            return false;
        }

        if (Time.realtimeSinceStartup <= deadline)
        {
            return true;
        }

        actorReplacementDeadlines.Remove(actorNumber);
        return false;
    }

    private static int RemoveDuplicateActorAvatarsBeforeAwake(int actorNumber, int exceptViewId, bool destroyLocalObjects)
    {
        if (actorNumber <= 0 || GameDirector.instance == null)
        {
            return 0;
        }

        int removed = 0;
        List<PlayerAvatar> avatars = GameDirector.instance.PlayerList;
        for (int i = avatars.Count - 1; i >= 0; i--)
        {
            PlayerAvatar avatar = avatars[i];
            if (avatar == null)
            {
                avatars.RemoveAt(i);
                removed++;
                continue;
            }

            PhotonView? photonView = avatar.photonView != null ? avatar.photonView : avatar.GetComponent<PhotonView>();
            if (photonView == null || photonView.OwnerActorNr != actorNumber || photonView.ViewID == exceptViewId)
            {
                continue;
            }

            Recconect.Logger.LogWarning($"Removing stale replacement duplicate before PlayerAvatar.Awake: actor={actorNumber}, view={photonView.ViewID}, destroyLocal={destroyLocalObjects}.");
            avatars.RemoveAt(i);
            removed++;

            if (destroyLocalObjects)
            {
                try
                {
                    UnityEngine.Object.Destroy(avatar.gameObject);
                }
                catch (Exception ex)
                {
                    Recconect.Logger.LogInfo($"Failed to locally destroy stale duplicate avatar view={photonView.ViewID}: {ex.Message}");
                }
            }
        }

        return removed;
    }

    private static string PlayerListSummary()
    {
        try
        {
            if (GameDirector.instance?.PlayerList == null)
            {
                return "<null>";
            }

            return string.Join(
                ",",
                GameDirector.instance.PlayerList.Select(static avatar =>
                {
                    if (avatar == null)
                    {
                        return "null";
                    }

                    PhotonView? photonView = avatar.photonView != null ? avatar.photonView : avatar.GetComponent<PhotonView>();
                    return $"{photonView?.OwnerActorNr ?? -1}:{photonView?.ViewID ?? -1}:spawned={avatar.spawned}:local={avatar.isLocal}";
                }));
        }
        catch (Exception ex)
        {
            return $"<error:{ex.Message}>";
        }
    }

    private static IEnumerator HostRemoveAndRepairActorObjects(int actorNumber)
    {
        RemoveHostActorObjects(actorNumber);
        NetworkStateSnapshot.Log("ReconnectCoordinator:host-remove-stale");
        SendHostReplacementReady(actorNumber);

        float deadline = Time.realtimeSinceStartup + 8f;
        while (Time.realtimeSinceStartup < deadline)
        {
            PlayerAvatar? avatar = FindPlayerAvatarByActor(actorNumber);
            if (avatar != null)
            {
                if (!avatar.spawned)
                {
                    SpawnSinglePlayerAvatar(avatar, actorNumber, avatar.photonView?.Owner?.NickName ?? "<unknown>");
                }
                else
                {
                    EnsurePlayerSupportObjects(avatar);
                    Recconect.Logger.LogWarning($"Host accepted replacement avatar for actor={actorNumber}; avatar is spawned.");
                    NetworkStateSnapshot.Log("ReconnectCoordinator:host-accepted-replacement");
                }

                yield break;
            }

            yield return new WaitForSeconds(0.25f);
        }

        Recconect.Logger.LogWarning($"Host did not see replacement avatar for actor={actorNumber} before timeout.");
    }

    private static void SendHostReplacementReady(int actorNumber)
    {
        try
        {
            RaiseEventOptions options = new()
            {
                Receivers = ReceiverGroup.All
            };

            PhotonNetwork.RaiseEvent(ReplaceActorObjectsReadyEventCode, actorNumber, options, SendOptions.SendReliable);
            Recconect.Logger.LogWarning($"Host sent stale player cleanup acknowledgement for actor={actorNumber}.");
        }
        catch (Exception ex)
        {
            Recconect.Logger.LogWarning($"Failed to send host stale player cleanup acknowledgement for actor={actorNumber}: {ex}");
        }
    }

    private static void RemoveHostActorObjects(int actorNumber)
    {
        foreach (PlayerAvatar avatar in UnityEngine.Object.FindObjectsOfType<PlayerAvatar>().ToArray())
        {
            if (avatar == null || avatar.photonView == null || avatar.photonView.OwnerActorNr != actorNumber)
            {
                continue;
            }

            Recconect.Logger.LogWarning($"Host removing stale avatar for actor={actorNumber}, view={avatar.photonView.ViewID}.");
            DestroyLocalObjectOnly("stale host avatar", avatar.gameObject);
        }

        foreach (PlayerVoiceChat voice in UnityEngine.Object.FindObjectsOfType<PlayerVoiceChat>().ToArray())
        {
            if (voice == null || voice.photonView == null || voice.photonView.OwnerActorNr != actorNumber)
            {
                continue;
            }

            Recconect.Logger.LogWarning($"Host removing stale voice for actor={actorNumber}, view={voice.photonView.ViewID}.");
            DestroyLocalObjectOnly("stale host voice", voice.gameObject);
        }

        CleanupLocalDestroyedPlayerReferences();
    }

    private static void DestroyLocalObjectOnly(string label, GameObject gameObject)
    {
        try
        {
            UnityEngine.Object.Destroy(gameObject);
            Recconect.Logger.LogInfo($"Destroyed {label} locally.");
        }
        catch (Exception ex)
        {
            Recconect.Logger.LogWarning($"Local destroy failed for {label}: {ex.Message}");
        }
    }

    private static void CleanupLocalDestroyedPlayerReferences()
    {
        try
        {
            GameDirector.instance?.PlayerList?.RemoveAll(static avatar => avatar == null);
        }
        catch
        {
        }

        try
        {
            RunManager.instance?.voiceChats?.RemoveAll(static voice => voice == null);
        }
        catch
        {
        }
    }

    private static void ClearStaleLocalSingletons()
    {
        if (PlayerAvatar.instance == null)
        {
            PlayerAvatar.instance = null;
        }

        if (PlayerVoiceChat.instance == null)
        {
            PlayerVoiceChat.instance = null;
        }
    }

    private static bool HasUsableOwnedVoiceChat(int actorNumber)
    {
        if (PlayerVoiceChat.instance != null &&
            PlayerVoiceChat.instance.photonView != null &&
            PlayerVoiceChat.instance.photonView.OwnerActorNr == actorNumber)
        {
            return true;
        }

        foreach (PlayerVoiceChat voice in RunManager.instance?.voiceChats ?? new List<PlayerVoiceChat>())
        {
            if (voice != null && voice.photonView != null && voice.photonView.OwnerActorNr == actorNumber)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ShouldForceLocalPlayerRespawn(out string reason)
    {
        if (!IsSyncedSceneLoaded(out reason) || !IsLevelGenerated(out reason) || !IsGameDirectorMain(out reason))
        {
            return false;
        }

        if (IsLocalPlayerAvatarReady(out reason))
        {
            return false;
        }

        return true;
    }

    private static bool IsLocalPlayerAvatarReady(out string reason)
    {
        object? playerAvatar = GetRuntimeLocalPlayerAvatar();
        if (IsUnityNull(playerAvatar))
        {
            reason = "PlayerAvatar.instance is not available";
            return false;
        }

        if (GetRuntimeBoolField(playerAvatar, "isDisabled") == true)
        {
            reason = "PlayerAvatar.instance is disabled";
            return false;
        }

        if (GetRuntimeBoolField(playerAvatar, "spawned") != true)
        {
            reason = "PlayerAvatar.instance is not spawned";
            return false;
        }

        object? playerController = GetRuntimeStaticField("PlayerController", "instance");
        if (IsUnityNull(playerController))
        {
            reason = "PlayerController.instance is not available";
            return false;
        }

        object? playerAvatarScript = GetRuntimeInstanceField(playerController!, "playerAvatarScript");
        if (IsUnityNull(playerAvatarScript))
        {
            SetRuntimeInstanceField(playerController!, "playerAvatarScript", playerAvatar);
            playerAvatarScript = playerAvatar;
        }

        if (!ReferenceEquals(playerAvatarScript, playerAvatar))
        {
            reason = "PlayerController.playerAvatarScript points at a different avatar";
            return false;
        }

        reason = "local player avatar is spawned and active";
        return true;
    }

    private static object? GetRuntimeLocalPlayerAvatar()
    {
        object? playerAvatar = GetRuntimeStaticField("PlayerAvatar", "instance");
        if (!IsUnityNull(playerAvatar))
        {
            return playerAvatar;
        }

        object? gameDirector = GetRuntimeStaticField("GameDirector", "instance");
        if (GetRuntimeInstanceField(gameDirector!, "PlayerList") is not System.Collections.IEnumerable players)
        {
            return null;
        }

        foreach (object candidate in players)
        {
            if (IsUnityNull(candidate))
            {
                continue;
            }

            object? photonViewObject = GetRuntimeInstanceField(candidate, "photonView");
            PhotonView? photonView = photonViewObject as PhotonView;
            if (photonView == null && candidate is Component component)
            {
                photonView = component.GetComponent<PhotonView>();
            }

            if (photonView is { IsMine: true })
            {
                GetRuntimeStaticFieldInfo("PlayerAvatar", "instance")?.SetValue(null, candidate);
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerator ForceLocalPlayerNetworkRespawn()
    {
        float graceDeadline = Time.realtimeSinceStartup + Recconect.ModConfig.ReconnectRespawnGraceSeconds.Value;
        while (Time.realtimeSinceStartup < graceDeadline)
        {
            if (IsLocalPlayerAvatarReady(out string readyReason))
            {
                Recconect.Logger.LogInfo($"Skipping forced player respawn; original avatar recovered during grace window: {readyReason}");
                NetworkStateSnapshot.Log("ReconnectCoordinator:respawn-skipped-ready");
                yield break;
            }

            yield return null;
        }

        if (IsLocalPlayerAvatarReady(out string finalReadyReason))
        {
            Recconect.Logger.LogInfo($"Skipping forced player respawn; original avatar is ready: {finalReadyReason}");
            NetworkStateSnapshot.Log("ReconnectCoordinator:respawn-skipped-ready");
            yield break;
        }

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

    private static FieldInfo? GetRuntimeStaticFieldInfo(string typeName, string fieldName)
    {
        return AccessTools.TypeByName(typeName)?.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
    }

    private static object? GetRuntimeStaticField(string typeName, string fieldName)
    {
        try
        {
            return GetRuntimeStaticFieldInfo(typeName, fieldName)?.GetValue(null);
        }
        catch
        {
            return null;
        }
    }

    private static object? GetRuntimeInstanceField(object? target, string fieldName)
    {
        if (IsUnityNull(target))
        {
            return null;
        }

        try
        {
            return target!.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(target);
        }
        catch
        {
            return null;
        }
    }

    private static object? GetRuntimeInstanceProperty(object? target, string propertyName)
    {
        if (IsUnityNull(target))
        {
            return null;
        }

        try
        {
            return target!.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(target);
        }
        catch
        {
            return null;
        }
    }

    private static bool? GetRuntimeBoolField(object? target, string fieldName)
    {
        return GetRuntimeInstanceField(target, fieldName) as bool?;
    }

    private static void SetRuntimeInstanceField(object target, string fieldName, object? value)
    {
        try
        {
            target.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(target, value);
        }
        catch (Exception ex)
        {
            Recconect.Logger.LogWarning($"Failed to set {target.GetType().Name}.{fieldName}: {ex.Message}");
        }
    }

    private static bool IsUnityNull(object? target)
    {
        return target == null || target is UnityEngine.Object unityObject && unityObject == null;
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

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

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

    internal static bool IsReconnecting => reconnecting;

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
                    reconnecting = false;
                    Recconect.Logger.LogWarning($"Experimental reconnect succeeded on attempt {attempt}: room={room.RoomName}");
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
        if (SafeAction("NetworkManager.LeavePhotonRoom", InvokeNetworkManagerLeavePhotonRoom))
        {
            return;
        }

        SafeAction("PhotonNetwork.Disconnect", PhotonNetwork.Disconnect);
        SafeAction("SteamManager.LeaveLobby", InvokeSteamLeaveLobby);
        SafeAction("GameManager.SetGameMode(0)", InvokeGameManagerSetGameModeMainMenu);
        SafeAction("RunManager.LeaveToMainMenu", InvokeRunManagerLeaveToMainMenu);
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

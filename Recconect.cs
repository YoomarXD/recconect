using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Recconect;

[BepInPlugin(PluginInfo.Guid, PluginInfo.Name, PluginInfo.Version)]
public class Recconect : BaseUnityPlugin
{
    internal static Recconect Instance { get; private set; } = null!;
    internal new static ManualLogSource Logger => Instance._logger;
    private ManualLogSource _logger => base.Logger;
    internal static RecconectConfig ModConfig { get; private set; } = null!;
    internal Harmony? Harmony { get; set; }

    private void Awake()
    {
        Instance = this;
        ModConfig = new RecconectConfig(Config);

        // Prevent the plugin from being deleted
        this.gameObject.transform.parent = null;
        this.gameObject.hideFlags = HideFlags.HideAndDontSave;

        try
        {
            Patch();
        }
        catch (System.Exception ex)
        {
            Logger.LogError(ex);
            throw;
        }

        Logger.LogInfo($"{Info.Metadata.GUID} v{Info.Metadata.Version} has loaded!");
    }

    internal void Patch()
    {
        Harmony ??= new Harmony(Info.Metadata.GUID);
        Harmony.PatchAll();
    }

    internal void Unpatch()
    {
        Harmony?.UnpatchSelf();
    }

    private void OnDestroy()
    {
        ReconnectCoordinator.UninstallEventHandler();
        Unpatch();
    }
}

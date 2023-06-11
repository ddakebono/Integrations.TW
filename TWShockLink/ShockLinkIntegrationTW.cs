﻿using System.Drawing;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using ShockLink.Integrations.TW;
using TotallyWholesome;
using TotallyWholesome.Managers;
using TotallyWholesome.Managers.Lead;
using TotallyWholesome.Objects.ConfigObjects;
using TWNetCommon.Data.ControlPackets;
using Yggdrasil.Extensions;
using Yggdrasil.Logging;

[assembly: MelonInfo(typeof(ShockLinkIntegrationTW), "ShockLink.Integrations.TW", "1.0.0", "ShockLink Team")]

namespace ShockLink.Integrations.TW;

public class ShockLinkIntegrationTW : MelonMod
{

    private static readonly MelonLogger.Instance Logger = new(nameof(ShockLinkIntegrationTW), Color.LawnGreen);
    
    private const string DefaultBaseUri = "https://api.shocklink.net";
    public override void OnInitializeMelon()
    {
        Logger.Msg("Getting config options..");
        var category = MelonPreferences.CreateCategory("ShockLinkIntegrationsTW");

        var tokenSetting = category.CreateEntry("APIToken", "");
        var endPointSetting = category.CreateEntry("APIBaseUri", DefaultBaseUri);

        tokenSetting.OnEntryValueChanged.Subscribe((_, newValue) => ShockLinkAPI.Reload(endPointSetting.Value, newValue));
        endPointSetting.OnEntryValueChanged.Subscribe((_, newValue) => ShockLinkAPI.Reload(newValue, tokenSetting.Value));
        
        ShockLinkAPI.Reload(endPointSetting.Value, tokenSetting.Value);
        
        
        Logger.Msg("Applying Patches");

        ApplyPatch(typeof(PiShockManager), "Execute",
            typeof(ShockLinkIntegrationTW), nameof(PatchExcecute));

        ApplyPatch(typeof(PiShockManager), nameof(PiShockManager.RegisterNewToken),
            typeof(ShockLinkIntegrationTW), nameof(PatchRegisterNewToken));

        ApplyPatch(typeof(PiShockManager), "GetShockerLog",
            typeof(ShockLinkIntegrationTW), nameof(PatchGetShockerLog));

        ApplyPatch(typeof(PiShockManager), "GetShockerInfo",
            typeof(ShockLinkIntegrationTW), nameof(PatchGetShockerInfo));

        Logger.Msg("Finished applying Patches");
    }

    private void ApplyPatch(Type originalClass, string originalMethod, Type patchType, string patchMethod)
    {
        Logger.Msg($"Applying Patch {originalMethod}");
        
        try
        {
            var originalMethodInfo = originalClass.GetMethod(originalMethod, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
            var patchMethodInfo = patchType.GetMethod(patchMethod, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
            HarmonyInstance.Patch(originalMethodInfo, new HarmonyMethod(patchMethodInfo));
            Logger.Msg($"Finished applying Patch {originalMethod}");
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to apply patch for {originalMethod}");
        }
    }

    public static bool PatchRegisterNewToken(string code, Action<string, string> onCompleted, Action onFailed)
    {
        Logger.Msg("PatchRegisterNewToken");
        Configuration.JSONConfig.PiShockShockers.Add(new PiShockShocker(code, code.Substring(0, 13)));

        TotallyWholesome.Main.Instance.MainThreadQueue.Enqueue(() => onCompleted?.Invoke(code, code));

        return false;
    }

    public static bool PatchExcecute(ShockOperation op, int duration, int strength)
    {
        Logger.Msg("PatchExcecute");
        Task.Run(async () =>
        {
            if (op == ShockOperation.NoOp) return;
            if ((op == ShockOperation.Beep && !ConfigManager.Instance.IsActive(AccessType.AllowBeep, LeadManager.Instance.MasterId)) ||
                (op == ShockOperation.Vibrate && !ConfigManager.Instance.IsActive(AccessType.AllowVibrate, LeadManager.Instance.MasterId)) ||
                (op == ShockOperation.Shock && !ConfigManager.Instance.IsActive(AccessType.AllowShock, LeadManager.Instance.MasterId)))

            {
                Logger.Msg("PiShockManager.Execute not allowed");
                return;
            }

            Logger.Msg($"{op} - {duration} - {strength}");

            var shockers = Configuration.JSONConfig.PiShockShockers;

            if (!shockers.Any(x => x.Enabled)) return;

            PiShockShocker? shocker;

            if (ConfigManager.Instance.IsActive(AccessType.PiShockRandomShocker, LeadManager.Instance.MasterId))
            {
                shocker = shockers.Where(x => x.Enabled).Random();
            }
            else
            {
                shocker = shockers.FirstOrDefault(x => x.Prioritized && x.Enabled) ?? shockers.FirstOrDefault(x => x.Enabled);
            }

            if (shocker == null)
            {
                Logger.Warning("No shocker configured");
                return;
            }

            Logger.Msg($"selected shocker {shocker.Name} {shocker.Key}");

            if(!OperationTranslation.TryGetValue(op, out var translated)) translated = ControlType.Vibrate;

            var control = new Control
            {
                Duration = (uint)duration * 1000,
                Intensity = (byte)strength,
                Id = Guid.Parse(shocker.Key),
                Type = translated
            };

            await ShockLinkAPI.Control(control);
        });
            
        return false;
    }

    public static bool PatchGetShockerLog(string code, out Task<PiShockerLog[]> __result)
    {
        Logger.Msg("PatchGetShockerLog");
        __result = Task.FromResult(new PiShockerLog[] { });
        return false;
    }

    public static bool PatchGetShockerInfo(string key, out Task<PiShockerInfo> __result)
    {
        Logger.Msg("PatchGetShockerInfo");
        __result = Task.FromResult(new PiShockerInfo {
            Name = key,
            MaxDuration = 15,
            MaxIntensity = 100
        });
        return false;   
    }

    private static readonly IReadOnlyDictionary<ShockOperation, ControlType> OperationTranslation =
        new Dictionary<ShockOperation, ControlType>
        {
            { ShockOperation.Shock, ControlType.Shock },
            { ShockOperation.Vibrate, ControlType.Vibrate },
            { ShockOperation.Beep, ControlType.Sound }
        };
}
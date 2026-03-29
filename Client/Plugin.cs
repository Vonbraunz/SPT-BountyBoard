using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using SPT.Reflection.Patching;
using SPT.Reflection.Utils;
using System.Reflection;
using System;
using System.IO;
using System.Linq;
using EFT;
using Comfort.Common;
using UnityEngine;

namespace BountyBoard.Client
{
    // ── Plugin entry point ─────────────────────────────────────────────────────
    [BepInPlugin("com.vonbraunz.bountyboard.client", "BountyBoard.Client", "2.0.0")]
    [BepInDependency("com.fika.core", BepInDependency.DependencyFlags.SoftDependency)]
    public class BountyClientPlugin : BaseUnityPlugin
    {
        public static bool IsFikaInstalled { get; private set; }
        public static bool IsFikaHost { get; private set; } = true; // default true for solo play
        // ── Shared config accessible to BountyClientMono ──────────────────────
        public static ConfigEntry<string>? BountyStatePath;
        public static ConfigEntry<KeyboardShortcut>? ScanTargetKey;
        public static ConfigEntry<string>? HunterStatePath;
        public static ConfigEntry<bool>? ForceHunters;

        // ── SPT root resolution ────────────────────────────────────────────────
      
        /// Walks up from the plugin DLL location to find the SPT root.
        /// DLL is always at &lt;SPT_ROOT&gt;\BepInEx\plugins\BountyBoard.Client.dll
        /// so two directories up is the SPT root regardless of working directory.
      
        public static string SptRoot { get; private set; } = string.Empty;

        private static string ResolveSptRoot()
        {
            string dllPath = Assembly.GetExecutingAssembly().Location;
            // Up from plugins\ → BepInEx\ → SPT root
            string? root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(dllPath)));
            return root ?? AppDomain.CurrentDomain.BaseDirectory;
        }
        private static ManualLogSource? _logger;

        public static void Log(LogLevel level, string message) =>
            _logger?.Log(level, message);

        /// Checks if this client is the Fika host (or solo). Must be called at raid time.
        public static void UpdateFikaHostStatus()
        {
            if (!IsFikaInstalled) { IsFikaHost = true; return; }

            try
            {
                // Fika.Core.Networking.FikaBackendUtils.IsServer
                var fikaAsm = System.AppDomain.CurrentDomain.GetAssemblies();
                foreach (var asm in fikaAsm)
                {
                    var backendUtils = asm.GetType("Fika.Core.Networking.FikaBackendUtils");
                    if (backendUtils == null) continue;

                    var isServerProp = backendUtils.GetProperty("IsServer",
                        BindingFlags.Public | BindingFlags.Static);
                    if (isServerProp != null)
                    {
                        IsFikaHost = (bool)isServerProp.GetValue(null);
                        Log(LogLevel.Info, $"Fika host check: {(IsFikaHost ? "HOST" : "CLIENT")}");
                        return;
                    }
                }
                IsFikaHost = true; // fallback if property not found
            }
            catch
            {
                IsFikaHost = true; // fallback on error
            }
        }

        // ── Awake ──────────────────────────────────────────────────────────────
        private void Awake()
        {
            _logger = Logger;

            SptRoot = ResolveSptRoot();
            Log(LogLevel.Info, $"SPT root resolved to: {SptRoot}");

            BountyStatePath = Config.Bind(
                "General",
                "Bounty State Path",
                "./SPT/user/mods/BountyBoard-drb/data/bounty_state.json",
                "Path to bounty_state.json from the BountyBoard server mod. " +
                "Relative paths are resolved from the SPT root automatically. " +
                "Use an absolute path with forward slashes if your mod folder name differs.");

            HunterStatePath = Config.Bind(
                "General",
                "Hunter State Path",
                "./SPT/user/mods/BountyBoard-drb/data/hunter_state.json",
                "Path to hunter_state.json. Tracks raids survived for hunter spawn chance. " +
                "Relative paths are resolved from the SPT root automatically.");

            ScanTargetKey = Config.Bind(
                "General",
                "Scan Target Key",
                new KeyboardShortcut(KeyCode.O),
                "Press in-raid to scan for nearby bounty targets. " +
                "Shows whether any active bounty target is alive in the current raid.");

            ForceHunters = Config.Bind(
                "Debug",
                "Force Hunters",
                false,
                "Force hunter spawn chance to 100% every raid. For testing only.");

            // Detect Fika
            IsFikaInstalled = BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.fika.core");
            if (IsFikaInstalled)
                Log(LogLevel.Info, "Fika detected — hunter strip will only run on host.");

            new NewGamePatch().Enable();
            new HunterStripPatch().Enable();

            Logger.LogInfo("BountyBoard.Client v2.0.0 loaded.");
        }

    }

    // ── Notification helper ────────────────────────────────────────────────────
    // The EFT notification manager and its enums are obfuscated / nested inside an
    // obfuscated type and CANNOT be referenced by name at compile time — doing so
    // emits a TypeRef to the empty-named outer class which causes a TypeLoadException
    // at runtime. Everything is resolved via reflection and cached on first use.
    internal static class NotificationHelper
    {
        // Duration enum values (ENotificationDurationType): Default=0, Long=1, Infinite=2
        public const int DurationDefault = 0;
        public const int DurationLong    = 1;

        // Icon enum values (ENotificationIconType): Default=0, Alert=1, Quest=5
        public const int IconDefault = 0;
        public const int IconAlert   = 1;
        public const int IconQuest   = 5;

        private static MethodInfo? _displayMessage;
        private static Type? _durationType;
        private static Type? _iconType;
        private static bool _initialized;

        private static void EnsureInitialized()
        {
            if (_initialized) return;
            _initialized = true;

            try
            {
                var type = PatchConstants.EftTypes.FirstOrDefault(
                    t => t.GetMethod("DisplayMessageNotification",
                             BindingFlags.Public | BindingFlags.Static) != null);

                if (type == null)
                {
                    BountyClientPlugin.Log(LogLevel.Error,
                        "NotificationHelper: could not find DisplayMessageNotification type.");
                    return;
                }

                _displayMessage = type.GetMethod("DisplayMessageNotification",
                    BindingFlags.Public | BindingFlags.Static);

                // Grab the enum types from the method's parameter types so we never
                // create a compile-time reference to them.
                var parameters = _displayMessage!.GetParameters();
                _durationType = parameters[1].ParameterType; // ENotificationDurationType
                _iconType     = parameters[2].ParameterType; // ENotificationIconType

                BountyClientPlugin.Log(LogLevel.Info,
                    "NotificationHelper: resolved DisplayMessageNotification.");
            }
            catch (Exception ex)
            {
                BountyClientPlugin.Log(LogLevel.Error,
                    $"NotificationHelper init failed: {ex.Message}");
            }
        }

        public static void Display(string message, int duration = DurationDefault, int iconType = IconDefault)
        {
            EnsureInitialized();
            if (_displayMessage == null || _durationType == null || _iconType == null) return;
            try
            {
                _displayMessage.Invoke(null, new object[]
                {
                    message,
                    Enum.ToObject(_durationType, duration),
                    Enum.ToObject(_iconType, iconType),
                    null
                });
            }
            catch (Exception ex)
            {
                BountyClientPlugin.Log(LogLevel.Error,
                    $"NotificationHelper.Display failed: {ex.Message}");
            }
        }
    }

    // ── Patch: hook GameWorld.OnGameStarted ────────────────────────────────────
    internal class NewGamePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            typeof(GameWorld).GetMethod("OnGameStarted");

        [PatchPrefix]
        public static void PatchPrefix()
        {
            BountyClientPlugin.UpdateFikaHostStatus();
            BountyClientMono.Init();
        }
    }

    // ── Patch: strip hunter spawns if roll fails ────────────────────────────
    internal class HunterStripPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            typeof(GameWorld).GetMethod("OnGameStarted");

        [PatchPrefix]
        public static void PatchPrefix()
        {
            // In Fika, only the host controls bot spawning — skip on non-host clients
            if (!BountyClientPlugin.IsFikaHost) return;

            var mono = BountyClientMono.Instance;
            if (mono == null || mono.HuntersActive) return;

            var gameWorld = Singleton<GameWorld>.Instance;
            if (gameWorld == null) return;

            // Walk GameWorld fields and their child properties looking for BossLocationSpawn.
            // Log every type name we touch so dnSpy can be used to find the correct path
            // if this search fails.
            try
            {
                foreach (var field in typeof(GameWorld).GetFields(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    object val;
                    try { val = field.GetValue(gameWorld); }
                    catch { continue; }
                    if (val == null) continue;

                    BountyClientPlugin.Log(LogLevel.Debug,
                        $"[StripPatch] GameWorld field '{field.Name}' type={val.GetType().Name}");

                    if (TryStripHunterSpawns(val, field.Name)) return;

                    foreach (var prop in val.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        object sub;
                        try { sub = prop.GetValue(val); }
                        catch { continue; }
                        if (sub == null) continue;

                        BountyClientPlugin.Log(LogLevel.Debug,
                            $"[StripPatch]   .{prop.Name} type={sub.GetType().Name}");

                        if (TryStripHunterSpawns(sub, $"{field.Name}.{prop.Name}")) return;
                    }
                }
            }
            catch (Exception ex)
            {
                BountyClientPlugin.Log(LogLevel.Error, $"HunterStripPatch error: {ex.Message}");
            }

            BountyClientPlugin.Log(LogLevel.Warning,
                "Could not find BossLocationSpawn list — use dnSpy on GameWorld to find the correct path. " +
                "Check BepInEx log (Debug level) for the field/property tree that was searched.");
        }

        // Hunter spawns are tagged with a fractional Time value: SpawnDelay + 0.1337
        // We detect them by checking if the Time has this .1337 fingerprint.
        private const float HunterFingerprint = 0.1337f;
        private const float Epsilon = 0.001f;

        private static bool TryStripHunterSpawns(object obj, string path = "")
        {
            var bossSpawnProp = obj.GetType().GetProperty("BossLocationSpawn",
                BindingFlags.Public | BindingFlags.Instance);
            if (bossSpawnProp == null || !bossSpawnProp.PropertyType.IsGenericType)
                return false;

            BountyClientPlugin.Log(LogLevel.Info, $"[StripPatch] Found BossLocationSpawn at path: {path}");

            var listObj = bossSpawnProp.GetValue(obj);
            if (listObj == null) return false;

            var listType = listObj.GetType();
            var countProp = listType.GetProperty("Count");
            var indexer = listType.GetProperty("Item");
            var removeAt = listType.GetMethod("RemoveAt");
            if (countProp == null || indexer == null || removeAt == null) return false;

            int count = (int)countProp.GetValue(listObj);
            int removed = 0;

            for (int i = count - 1; i >= 0; i--)
            {
                var item = indexer.GetValue(listObj, new object[] { i });
                if (item == null) continue;

                var timeProp = item.GetType().GetProperty("Time",
                    BindingFlags.Public | BindingFlags.Instance);
                if (timeProp == null) continue;

                var timeVal = timeProp.GetValue(item);
                if (timeVal == null) continue;

                float time = Convert.ToSingle(timeVal);
                float fractional = time - (float)Math.Floor(time);
                if (Math.Abs(fractional - HunterFingerprint) < Epsilon)
                {
                    removeAt.Invoke(listObj, new object[] { i });
                    removed++;
                }
            }

            if (removed > 0)
                BountyClientPlugin.Log(LogLevel.Info, $"Hunter roll failed — stripped {removed} hunter spawn(s).");

            return removed > 0;
        }
    }
}

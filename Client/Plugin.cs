using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using SPT.Reflection.Patching;
using System.Reflection;
using System;
using System.IO;
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

            // Detect Fika
            IsFikaInstalled = BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.fika.core");
            if (IsFikaInstalled)
                Log(LogLevel.Info, "Fika detected — hunter strip will only run on host.");

            new NewGamePatch().Enable();
            new HunterStripPatch().Enable();

            Logger.LogInfo("BountyBoard.Client v2.0.0 loaded.");
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

            // Use reflection to find the location field (obfuscated in EFT)
            // Walk GameWorld fields looking for one that has a BossLocationSpawn property
            try
            {
                foreach (var field in typeof(GameWorld).GetFields(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    object val;
                    try { val = field.GetValue(gameWorld); }
                    catch { continue; }
                    if (val == null) continue;

                    if (TryStripHunterSpawns(val)) return;

                    foreach (var prop in val.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        object sub;
                        try { sub = prop.GetValue(val); }
                        catch { continue; }
                        if (sub != null && TryStripHunterSpawns(sub)) return;
                    }
                }
            }
            catch (Exception ex)
            {
                BountyClientPlugin.Log(LogLevel.Error, $"HunterStripPatch error: {ex.Message}");
            }

            BountyClientPlugin.Log(LogLevel.Warning,
                "Could not find BossLocationSpawn list — hunter spawns may still occur.");
        }

        // Hunter spawns are tagged with a fractional Time value: SpawnDelay + 0.1337
        // We detect them by checking if the Time has this .1337 fingerprint.
        private const float HunterFingerprint = 0.1337f;
        private const float Epsilon = 0.001f;

        private static bool TryStripHunterSpawns(object obj)
        {
            var bossSpawnProp = obj.GetType().GetProperty("BossLocationSpawn",
                BindingFlags.Public | BindingFlags.Instance);
            if (bossSpawnProp == null || !bossSpawnProp.PropertyType.IsGenericType)
                return false;

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

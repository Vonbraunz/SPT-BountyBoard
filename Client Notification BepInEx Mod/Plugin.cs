using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using SPT.Reflection.Patching;
using System.Reflection;
using System;
using System.IO;
using System.Text.RegularExpressions;
using EFT;
using EFT.Communications;
using Comfort.Common;
using UnityEngine;

namespace BountyBoard.Client
{
    // ── Plugin entry point ─────────────────────────────────────────────────────
    [BepInPlugin("com.vonbraunz.bountyboard.client", "BountyBoard.Client", "1.0.0")]
    public class BountyClientPlugin : BaseUnityPlugin
    {
        // ── Shared config accessible to BountyClientMono ──────────────────────
        public static ConfigEntry<string>? BountyStatePath;
        public static ConfigEntry<bool>? DebugEnabled;
        public static ConfigEntry<KeyboardShortcut>? TestNotificationKey;
        public static ConfigEntry<KeyboardShortcut>? TestRealTargetKey;

        // ── SPT root resolution ────────────────────────────────────────────────
        /// <summary>
        /// Walks up from the plugin DLL location to find the SPT root.
        /// DLL is always at &lt;SPT_ROOT&gt;\BepInEx\plugins\BountyBoard.Client.dll
        /// so two directories up is the SPT root regardless of working directory.
        /// </summary>
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

            DebugEnabled = Config.Bind(
                "Debug",
                "Enable Debug Keys",
                false,
                "Enable the F8/F9 test notification keybinds. Disable this during normal play.");

            TestNotificationKey = Config.Bind(
                "Debug",
                "Test Notification Key",
                new KeyboardShortcut(KeyCode.F8),
                "Press in-raid to fire fake Spotted + Killed notifications. " +
                "Used to verify the HUD display without waiting for a real target.");

            TestRealTargetKey = Config.Bind(
                "Debug",
                "Test Real Target Key",
                new KeyboardShortcut(KeyCode.F9),
                "Press in-raid to read bounty_state.json and fire a Spotted notification " +
                "using the actual first active target name. Confirms name matching is working.");

            new NewGamePatch().Enable();

            Logger.LogInfo("BountyBoard.Client v1.0.0 loaded.");
        }

        // ── Update: poll test keybinds ─────────────────────────────────────────
        private void Update()
        {
            if (DebugEnabled == null || !DebugEnabled.Value) return;

            if (IsPressed(TestNotificationKey))
                FireDummyTest();

            if (TestRealTargetKey != null && UnityInput.Current.GetKeyDown(TestRealTargetKey.Value.MainKey))
                FireRealTargetTest();
        }

        private void FireDummyTest()
        {
            if (!Singleton<GameWorld>.Instantiated)
            {
                Log(LogLevel.Warning, "Test notification skipped — not in a raid.");
                return;
            }

            Log(LogLevel.Info, "Firing dummy test notifications.");
            NotificationManagerClass.DisplayMessageNotification(
                "⚠ BOUNTY TARGET SPOTTED\nTestTarget has been detected in this raid.",
                ENotificationDurationType.Long);
            NotificationManagerClass.DisplayMessageNotification(
                "☠ BOUNTY COLLECTED\nTestTarget has been eliminated.",
                ENotificationDurationType.Long);
        }

        private void FireRealTargetTest()
        {
            try
            {
                if (!Singleton<GameWorld>.Instantiated)
                {
                    Log(LogLevel.Warning, "Real target test skipped — not in a raid.");
                    return;
                }

                string rawPath = BountyStatePath?.Value ?? "./SPT/user/mods/BountyBoard-drb/data/bounty_state.json";
                string fullPath = Path.IsPathRooted(rawPath)
                    ? rawPath
                    : Path.Combine(BountyClientPlugin.SptRoot, rawPath.TrimStart('.', '/', '\\'));

                if (!File.Exists(fullPath))
                {
                    Log(LogLevel.Warning, $"Real target test: bounty_state.json not found at '{fullPath}'.");
                    NotificationManagerClass.DisplayMessageNotification(
                        "⚠ BOUNTY TEST FAILED\nbounty_state.json not found — check log for path.",
                        ENotificationDurationType.Long);
                    return;
                }

                string json = File.ReadAllText(fullPath);
                string? firstName = GetFirstActiveTarget(json);

                if (firstName == null)
                {
                    Log(LogLevel.Warning, "Real target test: no active targets found.");
                    NotificationManagerClass.DisplayMessageNotification(
                        "⚠ BOUNTY TEST\nNo active targets found in bounty_state.json.",
                        ENotificationDurationType.Long);
                    return;
                }

                Log(LogLevel.Info, $"Real target test: firing notification for '{firstName}'.");
                NotificationManagerClass.DisplayMessageNotification(
                    $"⚠ BOUNTY TARGET SPOTTED\n{firstName} has been detected in this raid.",
                    ENotificationDurationType.Long);
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, $"FireRealTargetTest exception: {ex}");
            }
        }

        /// <summary>
        /// Pulls the first TargetName where IsCompleted is false directly from the
        /// JSON string — reuses the same regex approach as BountyStateParser so the
        /// test exercises the exact same name extraction logic.
        /// </summary>
        private string? GetFirstActiveTarget(string json)
        {
            var blocks = Regex.Matches(json, @"\{[^{}]*\}");
            var nameRx = new Regex(@"""TargetName""\s*:\s*""([^""]*)""");
            var doneRx = new Regex(@"""IsCompleted""\s*:\s*(true|false)", RegexOptions.IgnoreCase);

            foreach (Match block in blocks)
            {
                var nameMatch = nameRx.Match(block.Value);
                if (!nameMatch.Success) continue;

                var doneMatch = doneRx.Match(block.Value);
                bool completed = doneMatch.Success &&
                                 doneMatch.Groups[1].Value.Equals("true", System.StringComparison.OrdinalIgnoreCase);
                if (!completed)
                    return nameMatch.Groups[1].Value;
            }

            return null;
        }

        // ── Helpers ────────────────────────────────────────────────────────────
        private static bool IsPressed(ConfigEntry<KeyboardShortcut>? entry)
        {
            if (entry == null) return false;
            var shortcut = entry.Value;
            if (!UnityInput.Current.GetKeyDown(shortcut.MainKey)) return false;
            foreach (var modifier in shortcut.Modifiers)
                if (!UnityInput.Current.GetKey(modifier)) return false;
            return true;
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
            BountyClientMono.Init();
        }
    }
}

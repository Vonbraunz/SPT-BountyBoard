using BepInEx.Logging;
using Comfort.Common;
using EFT;
using EFT.Communications;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace BountyBoard.Client
{
    /// <summary>
    /// MonoBehaviour attached to the GameWorld singleton at raid start.
    /// Reads bounty_state.json, then watches for bounty targets spawning in-raid.
    /// </summary>
    public class BountyClientMono : MonoBehaviour
    {
        // ── Singleton ──────────────────────────────────────────────────────────
        public static BountyClientMono? Instance { get; private set; }

        // ── State ──────────────────────────────────────────────────────────────
        /// <summary>Nicknames of all non-completed bounty targets this raid.</summary>
        private HashSet<string> _activeTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Targets we have already notified about, so we fire only once per target per raid.</summary>
        private HashSet<string> _notifiedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Targets confirmed killed this raid, so we don't re-notify on respawn edge cases.</summary>
        private HashSet<string> _killedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Players we are subscribed to for death events, so we can clean up properly.</summary>
        private HashSet<Player> _watchedPlayers = new HashSet<Player>();

        private GameWorld? _gameWorld;

        // ── Init (called from NewGamePatch) ────────────────────────────────────
        /// <summary>
        /// Called from <see cref="NewGamePatch"/> as a prefix on GameWorld.OnGameStarted.
        /// Attaches this component to the GameWorld singleton so Unity drives the lifecycle.
        /// </summary>
        public static void Init()
        {
            if (!Singleton<GameWorld>.Instantiated) return;
            Instance = Singleton<GameWorld>.Instance.GetOrAddComponent<BountyClientMono>();
            BountyClientPlugin.Log(LogLevel.Info, "BountyClientMono attached to GameWorld.");
        }

        // ── Unity lifecycle ────────────────────────────────────────────────────
        private void Start()
        {
            _gameWorld = Singleton<GameWorld>.Instance;

            // 1 – Load active bounty targets from disk
            LoadBounties();

            if (_activeTargets.Count == 0)
            {
                BountyClientPlugin.Log(LogLevel.Info, "No active bounty targets this raid.");
            }
            else
            {
                BountyClientPlugin.Log(LogLevel.Info,
                    $"Tracking {_activeTargets.Count} bounty target(s): {string.Join(", ", _activeTargets)}");
            }

            // 2 – Check bots already present at raid start
            if (_gameWorld?.AllAlivePlayersList != null)
            {
                foreach (var player in _gameWorld.AllAlivePlayersList)
                    CheckPlayer(player as Player);
            }

            // 3 – Hook future spawns (covers all wave timings)
            if (_gameWorld != null)
            {
                _gameWorld.OnPersonAdd += OnPersonAdd;
                BountyClientPlugin.Log(LogLevel.Info, "Registered OnPersonAdd hook.");
            }
        }

        private void OnDestroy()
        {
            if (_gameWorld != null)
                _gameWorld.OnPersonAdd -= OnPersonAdd;

            // Unsubscribe death events for any players we are still watching
            foreach (var player in _watchedPlayers)
            {
                if (player != null)
                    player.OnPlayerDeadOrUnspawn -= OnBountyTargetDeadOrUnspawn;
            }

            _activeTargets.Clear();
            _notifiedTargets.Clear();
            _killedTargets.Clear();
            _watchedPlayers.Clear();
            Instance = null;

            BountyClientPlugin.Log(LogLevel.Info, "BountyClientMono destroyed — state cleared.");
        }

        // ── Bounty loading ─────────────────────────────────────────────────────
        private void LoadBounties()
        {
            _activeTargets.Clear();

            string rawPath = BountyClientPlugin.BountyStatePath?.Value
                             ?? "./SPT/user/mods/BountyBoard-drb/data/bounty_state.json";

            // Resolve relative paths from the SPT root derived from DLL location
            string fullPath = Path.IsPathRooted(rawPath)
                ? rawPath
                : Path.Combine(BountyClientPlugin.SptRoot, rawPath.TrimStart('.', '/', '\\'));

            BountyClientPlugin.Log(LogLevel.Info, $"Reading bounty_state.json from: {fullPath}");

            if (!File.Exists(fullPath))
            {
                BountyClientPlugin.Log(LogLevel.Warning,
                    $"bounty_state.json not found at '{fullPath}'. " +
                    "Make sure the BountyBoard server mod has run at least one session.");
                return;
            }

            try
            {
                string json = File.ReadAllText(fullPath);
                var state = BountyStateParser.Parse(json);

                if (state == null || state.Bounties == null)
                {
                    BountyClientPlugin.Log(LogLevel.Warning, "bounty_state.json parsed but Bounties list is null.");
                    return;
                }

                foreach (var bounty in state.Bounties)
                {
                    if (!bounty.IsCompleted && !string.IsNullOrWhiteSpace(bounty.TargetName))
                        _activeTargets.Add(bounty.TargetName.Trim());
                }

                BountyClientPlugin.Log(LogLevel.Info,
                    $"Loaded {_activeTargets.Count} active target(s) from bounty_state.json " +
                    $"(generated {state.GeneratedAt:u}).");
            }
            catch (Exception ex)
            {
                BountyClientPlugin.Log(LogLevel.Error, $"Failed to load bounty_state.json: {ex.Message}");
            }
        }

        // ── Spawn hook ─────────────────────────────────────────────────────────
        private void OnPersonAdd(IPlayer iPlayer) => CheckPlayer(iPlayer as Player);

        private void CheckPlayer(Player? player)
        {
            if (player == null || player.IsYourPlayer) return;

            string name = player.Profile?.Info?.Nickname ?? string.Empty;
            if (string.IsNullOrEmpty(name)) return;
            if (!_activeTargets.Contains(name)) return;

            // Fire once per target per raid
            if (!_notifiedTargets.Add(name)) return;

            BountyClientPlugin.Log(LogLevel.Info, $"Bounty target spotted in raid: {name}");
            FireSpotNotification(name);

            // Subscribe to death event so we can notify when the bounty is collected
            if (_watchedPlayers.Add(player))
                player.OnPlayerDeadOrUnspawn += OnBountyTargetDeadOrUnspawn;
        }

        private void OnBountyTargetDeadOrUnspawn(Player player)
        {
            // Unsubscribe immediately — we only need this once
            player.OnPlayerDeadOrUnspawn -= OnBountyTargetDeadOrUnspawn;
            _watchedPlayers.Remove(player);

            string name = player.Profile?.Info?.Nickname ?? string.Empty;
            if (string.IsNullOrEmpty(name)) return;

            // Guard against duplicate kills (shouldn't happen, but be safe)
            if (!_killedTargets.Add(name)) return;

            BountyClientPlugin.Log(LogLevel.Info, $"Bounty target eliminated: {name}");
            FireKillNotification(name);
        }

        // ── Notifications ──────────────────────────────────────────────────────
        private static void FireSpotNotification(string targetName)
        {
            NotificationManagerClass.DisplayMessageNotification(
                $"⚠ BOUNTY TARGET SPOTTED\n{targetName} has been detected in this raid.",
                ENotificationDurationType.Long);
        }

        private static void FireKillNotification(string targetName)
        {
            NotificationManagerClass.DisplayMessageNotification(
                $"☠ BOUNTY COLLECTED\n{targetName} has been eliminated.",
                ENotificationDurationType.Long);
        }
    }

    /// <summary>
    /// Minimal parser for bounty_state.json.
    /// Uses only BCL types (no Newtonsoft / System.Text.Json needed) so it works
    /// on net472 with zero extra references.
    /// </summary>
    internal static class BountyStateParser
    {
        // Matches:  "TargetName": "Hooshu"
        private static readonly Regex _stringField =
            new Regex(@"""(\w+)""\s*:\s*""([^""]*)""", RegexOptions.Compiled);

        // Matches:  "IsCompleted": true   or   "IsCompleted": false
        private static readonly Regex _boolField =
            new Regex(@"""(\w+)""\s*:\s*(true|false)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Matches:  "GeneratedAt": "2026-03-13T14:18:08Z"
        private static readonly Regex _generatedAt =
            new Regex(@"""GeneratedAt""\s*:\s*""([^""]*)""", RegexOptions.Compiled);

        // Matches each individual bounty object { ... }
        private static readonly Regex _bountyBlock =
            new Regex(@"\{[^{}]*\}", RegexOptions.Compiled);

        public static BountyState? Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;

            var state = new BountyState();

            // Parse GeneratedAt
            var gaMatch = _generatedAt.Match(json);
            if (gaMatch.Success && DateTime.TryParse(gaMatch.Groups[1].Value, out var dt))
                state.GeneratedAt = dt;

            // Parse each bounty object
            foreach (Match block in _bountyBlock.Matches(json))
            {
                var bounty = new Bounty();

                foreach (Match sf in _stringField.Matches(block.Value))
                {
                    if (sf.Groups[1].Value == "TargetName")
                        bounty.TargetName = sf.Groups[2].Value;
                    // CompletedBySession is informational only; we don't need it
                }

                foreach (Match bf in _boolField.Matches(block.Value))
                {
                    if (bf.Groups[1].Value == "IsCompleted")
                        bounty.IsCompleted = bf.Groups[2].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
                }

                state.Bounties.Add(bounty);
            }

            return state;
        }
    }
}

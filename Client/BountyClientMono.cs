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

    /// MonoBehaviour attached to the GameWorld singleton at raid start.
    /// Reads bounty_state.json, then watches for bounty targets spawning in-raid.
    /// Also manages the Hunter system: escalating PMC hunter spawns based on raids survived.

    public class BountyClientMono : MonoBehaviour
    {
        // ── Singleton ──────────────────────────────────────────────────────────
        public static BountyClientMono? Instance { get; private set; }

        // ── Bounty State ───────────────────────────────────────────────────────
        private HashSet<string> _activeTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _notifiedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _killedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private HashSet<Player> _watchedPlayers = new HashSet<Player>();

        // ── Hunter State ───────────────────────────────────────────────────────
        public bool HuntersActive { get; private set; }
        private int _raidsSurvived;
        private float _hunterSpawnTime;
        private bool _hunterNotificationFired;
        private HashSet<Player> _hunterPlayers = new HashSet<Player>();
        private int _huntersKilled;
        private int _hunterCount;
        private bool _playerDied;

        private GameWorld? _gameWorld;

        // ── Init (called from NewGamePatch) ────────────────────────────────────
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
                BountyClientPlugin.Log(LogLevel.Info, "No active bounty targets this raid.");
            else
                BountyClientPlugin.Log(LogLevel.Info,
                    $"Tracking {_activeTargets.Count} bounty target(s): {string.Join(", ", _activeTargets)}");

            // 2 – Hunter system: load state and roll
            LoadHunterState();
            RollHunterSpawn();

            // 3 – Check bots already present at raid start
            if (_gameWorld?.AllAlivePlayersList != null)
            {
                foreach (var player in _gameWorld.AllAlivePlayersList)
                    CheckPlayer(player as Player);
            }

            // 4 – Hook future spawns (covers all wave timings)
            if (_gameWorld != null)
            {
                _gameWorld.OnPersonAdd += OnPersonAdd;
                BountyClientPlugin.Log(LogLevel.Info, "Registered OnPersonAdd hook.");
            }

            // 5 – Track player death for hunter state
            if (_gameWorld?.MainPlayer != null)
                _gameWorld.MainPlayer.OnPlayerDeadOrUnspawn += OnMainPlayerDeath;
        }

        private void Update()
        {
            // Scan key
            if (BountyClientPlugin.ScanTargetKey != null)
            {
                var shortcut = BountyClientPlugin.ScanTargetKey.Value;
                if (Input.GetKeyDown(shortcut.MainKey))
                {
                    bool modifiersHeld = true;
                    foreach (var modifier in shortcut.Modifiers)
                        if (!Input.GetKey(modifier)) { modifiersHeld = false; break; }
                    if (modifiersHeld) ScanForTargets();
                }
            }

            // Hunter notification timer
            if (HuntersActive && !_hunterNotificationFired && Time.time >= _hunterSpawnTime)
            {
                _hunterNotificationFired = true;
                NotificationManagerClass.DisplayMessageNotification(
                    "⚠ CONTRACT ALERT\nSomeone has put a contract on you. Hunters have been spotted nearby.",
                    ENotificationDurationType.Long);
                BountyClientPlugin.Log(LogLevel.Info, "Hunter notification fired — hunters should be spawning now.");
            }
        }

        private void OnDestroy()
        {
            if (_gameWorld != null)
                _gameWorld.OnPersonAdd -= OnPersonAdd;

            if (_gameWorld?.MainPlayer != null)
                _gameWorld.MainPlayer.OnPlayerDeadOrUnspawn -= OnMainPlayerDeath;

            // Unsubscribe death events for any players we are still watching
            foreach (var player in _watchedPlayers)
                if (player != null)
                    player.OnPlayerDeadOrUnspawn -= OnBountyTargetDeadOrUnspawn;

            foreach (var player in _hunterPlayers)
                if (player != null)
                    player.OnPlayerDeadOrUnspawn -= OnHunterDeadOrUnspawn;

            // Persist hunter state on raid end
            if (!_playerDied)
                RecordSurvival();

            _activeTargets.Clear();
            _notifiedTargets.Clear();
            _killedTargets.Clear();
            _watchedPlayers.Clear();
            _hunterPlayers.Clear();
            Instance = null;

            BountyClientPlugin.Log(LogLevel.Info, "BountyClientMono destroyed — state cleared.");
        }

        // ── Hunter system ──────────────────────────────────────────────────────
        private void LoadHunterState()
        {
            _raidsSurvived = 0;
            string fullPath = ResolveHunterStatePath();

            if (!File.Exists(fullPath))
            {
                BountyClientPlugin.Log(LogLevel.Info, "No hunter_state.json found — starting fresh (0 raids survived).");
                return;
            }

            try
            {
                string json = File.ReadAllText(fullPath);
                var match = Regex.Match(json, @"""RaidsSurvived""\s*:\s*(\d+)");
                if (match.Success && int.TryParse(match.Groups[1].Value, out int survived))
                    _raidsSurvived = survived;

                BountyClientPlugin.Log(LogLevel.Info, $"Hunter state loaded: {_raidsSurvived} raids survived.");
            }
            catch (Exception ex)
            {
                BountyClientPlugin.Log(LogLevel.Error, $"Failed to load hunter_state.json: {ex.Message}");
            }
        }

        private void RollHunterSpawn()
        {
            int chance = Math.Min(25 + (_raidsSurvived * 5), 100);
            int roll = UnityEngine.Random.Range(0, 100);
            HuntersActive = roll < chance;

            // Estimate spawn time (server config SpawnDelay, default 180s)
            _hunterSpawnTime = Time.time + 180f;
            _hunterNotificationFired = false;
            _hunterCount = 2; // 1 leader + 1 escort

            BountyClientPlugin.Log(LogLevel.Info,
                $"Hunter roll: {chance}% chance (survived {_raidsSurvived}), rolled {roll} — " +
                $"{(HuntersActive ? "HUNTERS ACTIVE" : "no hunters this raid")}");
        }

        private void OnMainPlayerDeath(Player player)
        {
            player.OnPlayerDeadOrUnspawn -= OnMainPlayerDeath;
            _playerDied = true;
            RecordDeath();
            BountyClientPlugin.Log(LogLevel.Info, "Player died — hunter state reset.");
        }

        private void RecordSurvival()
        {
            if (!HuntersActive && _raidsSurvived == 0) return; // nothing to track yet

            _raidsSurvived++;
            PersistHunterState();
            BountyClientPlugin.Log(LogLevel.Info, $"Raid survived — raids survived now: {_raidsSurvived}");
        }

        private void RecordDeath()
        {
            _raidsSurvived = 0;
            PersistHunterState();
        }

        private void PersistHunterState()
        {
            try
            {
                string fullPath = ResolveHunterStatePath();
                string? dir = Path.GetDirectoryName(fullPath);
                if (dir != null) Directory.CreateDirectory(dir);
                File.WriteAllText(fullPath, $"{{\n  \"RaidsSurvived\": {_raidsSurvived}\n}}");
            }
            catch (Exception ex)
            {
                BountyClientPlugin.Log(LogLevel.Error, $"Failed to persist hunter_state.json: {ex.Message}");
            }
        }

        private static string ResolveHunterStatePath()
        {
            string rawPath = BountyClientPlugin.HunterStatePath?.Value
                             ?? "./SPT/user/mods/BountyBoard-drb/data/hunter_state.json";
            return Path.IsPathRooted(rawPath)
                ? rawPath
                : Path.Combine(BountyClientPlugin.SptRoot, rawPath.TrimStart('.', '/', '\\'));
        }

        // ── Bounty loading ─────────────────────────────────────────────────────
        private void LoadBounties()
        {
            _activeTargets.Clear();

            string rawPath = BountyClientPlugin.BountyStatePath?.Value
                             ?? "./SPT/user/mods/BountyBoard-drb/data/bounty_state.json";

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
        private void OnPersonAdd(IPlayer iPlayer)
        {
            var player = iPlayer as Player;
            CheckPlayer(player);
            CheckHunterSpawn(player);
        }

        private void CheckPlayer(Player? player)
        {
            if (player == null || player.IsYourPlayer) return;

            string name = player.Profile?.Info?.Nickname ?? string.Empty;
            if (string.IsNullOrEmpty(name)) return;
            if (!_activeTargets.Contains(name)) return;

            if (!_notifiedTargets.Add(name)) return;

            BountyClientPlugin.Log(LogLevel.Info, $"Bounty target spotted in raid: {name}");
            FireSpotNotification(name);

            if (_watchedPlayers.Add(player))
                player.OnPlayerDeadOrUnspawn += OnBountyTargetDeadOrUnspawn;
        }

        private void CheckHunterSpawn(Player? player)
        {
            if (!HuntersActive) return;
            if (player == null || player.IsYourPlayer) return;

            // Detect PMCs spawning in the hunter time window (±60s of expected spawn time)
            var role = player.Profile?.Info?.Settings?.Role;
            if (role == null) return;

            bool isPmc = role == WildSpawnType.pmcBEAR || role == WildSpawnType.pmcUSEC;
            if (!isPmc) return;

            // Only track PMCs that spawn after the hunter delay window
            float raidTime = Time.time;
            float expectedSpawn = _hunterSpawnTime;
            if (raidTime < expectedSpawn - 60f) return; // too early, regular PMC

            if (_hunterPlayers.Add(player))
            {
                player.OnPlayerDeadOrUnspawn += OnHunterDeadOrUnspawn;
                BountyClientPlugin.Log(LogLevel.Info,
                    $"Potential hunter detected: {player.Profile?.Info?.Nickname} ({role})");
            }
        }

        private void OnBountyTargetDeadOrUnspawn(Player player)
        {
            player.OnPlayerDeadOrUnspawn -= OnBountyTargetDeadOrUnspawn;
            _watchedPlayers.Remove(player);

            string name = player.Profile?.Info?.Nickname ?? string.Empty;
            if (string.IsNullOrEmpty(name)) return;

            if (!_killedTargets.Add(name)) return;

            BountyClientPlugin.Log(LogLevel.Info, $"Bounty target eliminated: {name}");
            FireKillNotification(name);
        }

        private void OnHunterDeadOrUnspawn(Player player)
        {
            player.OnPlayerDeadOrUnspawn -= OnHunterDeadOrUnspawn;
            _hunterPlayers.Remove(player);
            _huntersKilled++;

            string name = player.Profile?.Info?.Nickname ?? string.Empty;
            BountyClientPlugin.Log(LogLevel.Info, $"Hunter eliminated: {name}");

            NotificationManagerClass.DisplayMessageNotification(
                $"☠ HUNTER ELIMINATED\n{name} has been taken out. ({_huntersKilled}/{_hunterCount})",
                ENotificationDurationType.Long);
        }

        // ── Scan ───────────────────────────────────────────────────────────────
        private void ScanForTargets()
        {
            if (_activeTargets.Count == 0)
            {
                NotificationManagerClass.DisplayMessageNotification(
                    "No active bounties.",
                    ENotificationDurationType.Default);
                return;
            }

            if (_gameWorld?.AllAlivePlayersList == null)
            {
                NotificationManagerClass.DisplayMessageNotification(
                    "No target found.",
                    ENotificationDurationType.Default);
                return;
            }

            var found = new List<string>();
            foreach (var iPlayer in _gameWorld.AllAlivePlayersList)
            {
                var player = iPlayer as Player;
                if (player == null || player.IsYourPlayer) continue;
                string name = player.Profile?.Info?.Nickname ?? string.Empty;
                if (_activeTargets.Contains(name) && !_killedTargets.Contains(name))
                    found.Add(name);
            }

            string hunterStatus = HuntersActive
                ? $"Hunters in raid. ({_huntersKilled}/{_hunterCount} eliminated)"
                : "No hunters this raid.";

            if (found.Count > 0)
            {
                NotificationManagerClass.DisplayMessageNotification(
                    $"⚠ TARGET SPOTTED\n{string.Join(", ", found)}\n{hunterStatus}",
                    ENotificationDurationType.Long);
            }
            else
            {
                NotificationManagerClass.DisplayMessageNotification(
                    $"No target found.\n{hunterStatus}",
                    ENotificationDurationType.Default);
            }
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


    /// Minimal parser for bounty_state.json.
    /// Uses only BCL types (no Newtonsoft / System.Text.Json needed) so it works
    /// on net472 with zero extra references.

    internal static class BountyStateParser
    {
        private static readonly Regex _stringField =
            new Regex(@"""(\w+)""\s*:\s*""([^""]*)""", RegexOptions.Compiled);

        private static readonly Regex _boolField =
            new Regex(@"""(\w+)""\s*:\s*(true|false)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex _generatedAt =
            new Regex(@"""GeneratedAt""\s*:\s*""([^""]*)""", RegexOptions.Compiled);

        private static readonly Regex _bountyBlock =
            new Regex(@"\{[^{}]*\}", RegexOptions.Compiled);

        public static BountyState? Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;

            var state = new BountyState();

            var gaMatch = _generatedAt.Match(json);
            if (gaMatch.Success && DateTime.TryParse(gaMatch.Groups[1].Value, out var dt))
                state.GeneratedAt = dt;

            foreach (Match block in _bountyBlock.Matches(json))
            {
                var bounty = new Bounty();

                foreach (Match sf in _stringField.Matches(block.Value))
                {
                    if (sf.Groups[1].Value == "TargetName")
                        bounty.TargetName = sf.Groups[2].Value;
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

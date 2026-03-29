using BepInEx.Logging;
using Comfort.Common;
using EFT;
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

        // ── Hunter config (loaded from config.json) ───────────────────────────
        private int _configEscortCount = 1;
        private int _configSpawnDelay = 180;
        private int _configBaseChance = 25;
        private int _configChancePerSurvival = 5;
        private int _configMaxChance = 100;

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

            // 2 – Hunter system: load config, state, and roll
            LoadHunterConfig();
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
                NotificationHelper.Display(
                    "⚠ CONTRACT ALERT\nSomeone has put a contract on you. Hunters have been spotted nearby.",
                    NotificationHelper.DurationLong, NotificationHelper.IconAlert);
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
        private void LoadHunterConfig()
        {
            string rawPath = "./SPT/user/mods/BountyBoard-drb/config.json";
            string fullPath = Path.Combine(BountyClientPlugin.SptRoot, rawPath.TrimStart('.', '/', '\\'));

            if (!File.Exists(fullPath))
            {
                BountyClientPlugin.Log(LogLevel.Info, "No config.json found — using default hunter settings.");
                return;
            }

            try
            {
                string json = File.ReadAllText(fullPath);

                var escortMatch = Regex.Match(json, @"""EscortCount""\s*:\s*(\d+)");
                if (escortMatch.Success && int.TryParse(escortMatch.Groups[1].Value, out int escort))
                    _configEscortCount = escort;

                var delayMatch = Regex.Match(json, @"""SpawnDelay""\s*:\s*(\d+)");
                if (delayMatch.Success && int.TryParse(delayMatch.Groups[1].Value, out int delay))
                    _configSpawnDelay = delay;

                var baseChanceMatch = Regex.Match(json, @"""BaseChance""\s*:\s*(\d+)");
                if (baseChanceMatch.Success && int.TryParse(baseChanceMatch.Groups[1].Value, out int bc))
                    _configBaseChance = bc;

                var perSurvivalMatch = Regex.Match(json, @"""ChancePerSurvival""\s*:\s*(\d+)");
                if (perSurvivalMatch.Success && int.TryParse(perSurvivalMatch.Groups[1].Value, out int cps))
                    _configChancePerSurvival = cps;

                var maxChanceMatch = Regex.Match(json, @"""MaxChance""\s*:\s*(\d+)");
                if (maxChanceMatch.Success && int.TryParse(maxChanceMatch.Groups[1].Value, out int mc))
                    _configMaxChance = mc;

                BountyClientPlugin.Log(LogLevel.Info,
                    $"Hunter config loaded: EscortCount={_configEscortCount}, SpawnDelay={_configSpawnDelay}s, " +
                    $"BaseChance={_configBaseChance}, ChancePerSurvival={_configChancePerSurvival}, MaxChance={_configMaxChance}");
            }
            catch (Exception ex)
            {
                BountyClientPlugin.Log(LogLevel.Error, $"Failed to load config.json: {ex.Message}");
            }
        }

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
            bool forceHunters = BountyClientPlugin.ForceHunters?.Value ?? false;
            int chance = forceHunters ? 100 : Math.Min(_configBaseChance + (_raidsSurvived * _configChancePerSurvival), _configMaxChance);
            int roll = forceHunters ? 0 : UnityEngine.Random.Range(0, 100);
            HuntersActive = roll < chance;

            _hunterSpawnTime = Time.time + _configSpawnDelay;
            _hunterNotificationFired = false;
            _hunterCount = 1 + _configEscortCount; // 1 leader + escorts

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
            if (_hunterPlayers.Count >= _hunterCount) return; // all hunter slots filled

            var role = player.Profile?.Info?.Settings?.Role;
            if (role == null) return;

            bool isPmc = role == WildSpawnType.pmcBEAR || role == WildSpawnType.pmcUSEC;
            if (!isPmc) return;

            // Hunters spawn at SpawnDelay + 0.1337s (server fingerprint).
            // Use a tight window around that time to avoid tagging PMCs from
            // other bot spawning mods (ABPS, DONUTS, SWAG, etc.) whose waves
            // may overlap the general timeframe.
            float raidTime = Time.time;
            float expectedSpawn = _hunterSpawnTime;
            float tolerance = 15f; // tight ±15s window around the fingerprinted spawn time
            if (raidTime < expectedSpawn - tolerance || raidTime > expectedSpawn + tolerance) return;

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
            if (_huntersKilled >= _hunterCount) return; // all hunters already accounted for
            _huntersKilled++;

            string name = player.Profile?.Info?.Nickname ?? string.Empty;
            BountyClientPlugin.Log(LogLevel.Info, $"Hunter eliminated: {name}");

            NotificationHelper.Display(
                $"☠ HUNTER ELIMINATED\n{name} has been taken out. ({_huntersKilled}/{_hunterCount})",
                NotificationHelper.DurationLong, NotificationHelper.IconAlert);
        }

        // ── Scan ───────────────────────────────────────────────────────────────
        private void ScanForTargets()
        {
            if (_activeTargets.Count == 0)
            {
                NotificationHelper.Display("No active bounties.");
                return;
            }

            if (_gameWorld?.AllAlivePlayersList == null)
            {
                NotificationHelper.Display("No target found.");
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

            string hunterStatus = HuntersActive ? "Hunters in raid." : "No hunters this raid.";

            if (found.Count > 0)
            {
                NotificationHelper.Display(
                    $"⚠ TARGET SPOTTED\n{string.Join(", ", found)}\n{hunterStatus}",
                    NotificationHelper.DurationLong, NotificationHelper.IconAlert);
            }
            else
            {
                NotificationHelper.Display($"No target found.\n{hunterStatus}");
            }
        }

        // ── Notifications ──────────────────────────────────────────────────────
        private static void FireSpotNotification(string targetName)
        {
            NotificationHelper.Display(
                $"⚠ BOUNTY TARGET SPOTTED\n{targetName} has been detected in this raid.",
                NotificationHelper.DurationLong, NotificationHelper.IconAlert);
        }

        private static void FireKillNotification(string targetName)
        {
            NotificationHelper.Display(
                $"☠ BOUNTY COLLECTED\n{targetName} has been eliminated.",
                NotificationHelper.DurationLong, NotificationHelper.IconQuest);
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

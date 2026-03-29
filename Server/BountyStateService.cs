using BountyBoard.Models;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BountyBoard.Services;


/// Singleton service responsible for:
///   • Generating three random PMC-name targets on startup
///   • Persisting state to /data/bounty_state.json between restarts
///   • Exposing helpers for the bot commands to query and claim bounties

[Injectable(InjectionType.Singleton)]
public class BountyStateService(
    ISptLogger<BountyStateService> logger,
    DatabaseService databaseService,
    ModHelper modHelper)
{
    // ── State ──────────────────────────────────────────────────────────────────

    private static readonly Random Rng = new();
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private BountyState? _state;
    private string? _stateFilePath;
    private BountyConfig _config = new();

    // ── Public API ─────────────────────────────────────────────────────────────

    public RewardConfig Rewards => _config.Rewards;

   
    /// Call once from <see cref="BountyBoardMod.OnLoad"/> after the DB is ready.
    /// Loads existing state from disk, or generates a fresh cycle if none exists.
  
    public void Initialize(Assembly modAssembly)
    {
        var modPath = modHelper.GetAbsolutePathToModFolder(modAssembly);
        _stateFilePath = Path.Combine(modPath, "data", "bounty_state.json");
        var configPath = Path.Combine(modPath, "config.json");

        Directory.CreateDirectory(Path.GetDirectoryName(_stateFilePath)!);

        // Load config
        if (File.Exists(configPath))
        {
            try
            {
                var raw = File.ReadAllText(configPath);
                _config = JsonSerializer.Deserialize<BountyConfig>(raw) ?? new BountyConfig();
                logger.Info($"[BountyBoard] Config loaded — Targets: {_config.TargetCount}, Reward: {_config.Rewards.CurrencyAmount:N0} (tpl: {_config.Rewards.CurrencyTpl})");
            }
            catch (Exception ex)
            {
                logger.Warning($"[BountyBoard] Could not parse config.json ({ex.Message}). Using defaults.");
            }
        }
        else
        {
            logger.Warning("[BountyBoard] config.json not found — using defaults.");
        }

        _state = LoadOrGenerate();
        Persist();

        logger.Success($"[BountyBoard] Active bounties this cycle:");
        foreach (var b in _state.Bounties)
            logger.Info($"  [{(b.IsCompleted ? "DONE" : "OPEN")}] {b.TargetName}");
    }

    /// Returns all bounties that have not yet been claimed.
    public IReadOnlyList<Bounty> GetActiveBounties() =>
        (_state?.Bounties ?? []).Where(b => !b.IsCompleted).ToList();

    /// Returns all bounties in the current cycle (including completed ones).
    public IReadOnlyList<Bounty> GetAllBounties() =>
        _state?.Bounties ?? [];

    /// Picks a random high-tier medical template ID for the reward package.
    public string GetRandomMedicalTpl()
    {
        var pool = _config.Rewards.MedicalItems;
        return pool.Count > 0
            ? pool[Rng.Next(pool.Count)]
            : "590c661e86f7741e566b646a"; // fallback: Grizzly
    }

   
    /// Attempts to mark the named bounty as complete for the given session.
    /// Returns <c>true</c> if the bounty was found, was still open, and has now been claimed.
   
    public bool TryClaimBounty(string targetName, string sessionId)
    {
        if (_state == null) return false;

        var bounty = _state.Bounties.FirstOrDefault(b =>
            !b.IsCompleted &&
            string.Equals(b.TargetName, targetName, StringComparison.OrdinalIgnoreCase));

        if (bounty == null) return false;

        bounty.IsCompleted = true;
        bounty.CompletedBySession = sessionId;

        // If all bounties are now complete, rotate to a fresh cycle
        if (_state.Bounties.All(b => b.IsCompleted))
        {
            logger.Success("[BountyBoard] All contracts fulfilled — generating new bounty cycle.");
            _state = GenerateNewBounties();
        }

        Persist();

        logger.Success($"[BountyBoard] Contract fulfilled — Target: '{targetName}' | Session: {sessionId}");
        return true;
    }

    // ── Private helpers ────────────────────────────────────────────────────────

   
    /// Loads the state file if it exists and is valid; otherwise generates a fresh cycle.
   
    private BountyState LoadOrGenerate()
    {
        if (File.Exists(_stateFilePath))
        {
            try
            {
                var raw = File.ReadAllText(_stateFilePath!);
                var loaded = JsonSerializer.Deserialize<BountyState>(raw);

                if (loaded?.Bounties is { Count: > 0 })
                {
                    var age = DateTime.UtcNow - loaded.GeneratedAt;
                    if (age.TotalHours >= _config.RefreshHours)
                    {
                        logger.Info($"[BountyBoard] Cycle expired ({age.TotalHours:F1}h old, limit is {_config.RefreshHours}h) — generating fresh bounties.");
                        return GenerateNewBounties();
                    }

                    logger.Info($"[BountyBoard] Loaded existing bounty cycle from disk ({age.TotalHours:F1}h old).");
                    return loaded;
                }
            }
            catch (Exception ex)
            {
                logger.Warning($"[BountyBoard] Could not parse bounty_state.json ({ex.Message}). Generating new cycle.");
            }
        }

        logger.Info("[BountyBoard] No saved cycle found — generating fresh bounties.");
        return GenerateNewBounties();
    }

    
    /// Picks 3 unique, random PMC names from the USEC/BEAR bot name pools.
    /// Names are formatted as "FirstName LastName" to match the dogtag Nickname field.
   
    private BountyState GenerateNewBounties()
    {
        var bots = databaseService.GetBots().Types;
        var firstNames = new List<string>();
        var lastNames  = new List<string>();

        // Pull names from both PMC factions
        if (bots.TryGetValue("usec", out var usec))
        {
            firstNames.AddRange(usec.FirstNames);
            if (usec.LastNames != null) lastNames.AddRange(usec.LastNames);
        }
        if (bots.TryGetValue("bear", out var bear))
        {
            firstNames.AddRange(bear.FirstNames);
            if (bear.LastNames != null) lastNames.AddRange(bear.LastNames);
        }

        // Fallback in case the DB has no names (shouldn't happen in a normal install)
        if (firstNames.Count == 0)
        {
            logger.Warning("[BountyBoard] PMC name pool is empty — using hardcoded fallback names.");
            firstNames = ["Alpha", "Bravo", "Charlie", "Delta", "Echo", "Foxtrot"];
        }

        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bounties  = new List<Bounty>();
        int maxTries  = 200;

        while (bounties.Count < _config.TargetCount && maxTries-- > 0)
        {
            var first = firstNames[Rng.Next(firstNames.Count)];
            var last  = lastNames.Count > 0 ? lastNames[Rng.Next(lastNames.Count)] : string.Empty;
            var full  = string.IsNullOrWhiteSpace(last) ? first : $"{first} {last}";

            if (!usedNames.Add(full)) continue; // deduplicate

            bounties.Add(new Bounty { TargetName = full });
        }

        logger.Success($"[BountyBoard] Generated {bounties.Count} new target(s) for this cycle.");

        return new BountyState
        {
            GeneratedAt = DateTime.UtcNow,
            Bounties = bounties
        };
    }

    /// Writes the current state to <c>bounty_state.json</c>.
    private void Persist()
    {
        if (_stateFilePath == null || _state == null) return;

        var json = JsonSerializer.Serialize(_state, JsonOpts);
        File.WriteAllText(_stateFilePath, json);
    }
}

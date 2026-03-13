using BountyBoard.Models;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BountyBoard.Services;

/// <summary>
/// Singleton service responsible for:
///   • Generating three random PMC-name targets on startup
///   • Persisting state to /data/bounty_state.json between restarts
///   • Exposing helpers for the bot commands to query and claim bounties
/// </summary>
[Injectable(InjectionType.Singleton)]
public class BountyStateService(
    ISptLogger<BountyStateService> logger,
    DatabaseService databaseService,
    ModHelper modHelper)
{
    // ── Reward pool ────────────────────────────────────────────────────────────

    /// <summary>Template IDs for the random high-tier medical reward.</summary>
    private static readonly string[] HighTierMedicals =
    [
        "5d02778e86f774203e7dedbe", // Surv12 field surgery kit
        "590c661e86f7741e566b646a", // Grizzly medical kit
        "5755356824597772cb798962", // IFAK
        "5c0e533786f7747fa1419862", // Propital regenerative stimulant injector
        "5c0e530286f7747fa1419869", // Morphine injector
    ];

    // ── State ──────────────────────────────────────────────────────────────────

    private static readonly Random Rng = new();
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private BountyState? _state;
    private string? _stateFilePath;
    private BountyConfig _config = new();

    // ── Public API ─────────────────────────────────────────────────────────────

    public int RewardRoubles => _config.RewardRoubles;

    /// <summary>
    /// Call once from <see cref="BountyBoardMod.OnLoad"/> after the DB is ready.
    /// Loads existing state from disk, or generates a fresh cycle if none exists.
    /// </summary>
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
                logger.Info($"[BountyBoard] Config loaded — Targets: {_config.TargetCount}, Reward: {_config.RewardRoubles:N0} roubles");
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

    /// <summary>Returns all bounties that have not yet been claimed.</summary>
    public IReadOnlyList<Bounty> GetActiveBounties() =>
        (_state?.Bounties ?? []).Where(b => !b.IsCompleted).ToList();

    /// <summary>Returns all bounties in the current cycle (including completed ones).</summary>
    public IReadOnlyList<Bounty> GetAllBounties() =>
        _state?.Bounties ?? [];

    /// <summary>Picks a random high-tier medical template ID for the reward package.</summary>
    public string GetRandomMedicalTpl() =>
        HighTierMedicals[Rng.Next(HighTierMedicals.Length)];

    /// <summary>
    /// Attempts to mark the named bounty as complete for the given session.
    /// Returns <c>true</c> if the bounty was found, was still open, and has now been claimed.
    /// </summary>
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

    /// <summary>
    /// Loads the state file if it exists and is valid; otherwise generates a fresh cycle.
    /// </summary>
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
                    logger.Info("[BountyBoard] Loaded existing bounty cycle from disk.");
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

    /// <summary>
    /// Picks 3 unique, random PMC names from the USEC/BEAR bot name pools.
    /// Names are formatted as "FirstName LastName" to match the dogtag Nickname field.
    /// </summary>
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

    /// <summary>Writes the current state to <c>bounty_state.json</c>.</summary>
    private void Persist()
    {
        if (_stateFilePath == null || _state == null) return;

        var json = JsonSerializer.Serialize(_state, JsonOpts);
        File.WriteAllText(_stateFilePath, json);
    }
}

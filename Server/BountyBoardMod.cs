using BountyBoard.Models;
using BountyBoard.Services;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;
using System.Reflection;
using System.Text.Json;

namespace BountyBoard;

[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 2)]
public class BountyBoardMod(
    ISptLogger<BountyBoardMod> logger,
    BountyStateService bountyStateService,
    DatabaseService databaseService,
    ModHelper modHelper,
    JsonUtil jsonUtil,
    ConfigServer configServer) : IOnLoad
{
    // Must match BountyBoardBot.GetChatBot().Id exactly
    private const string BotId = "674db14ed849a3727ef24da1";

    private static readonly List<string> HunterMaps =
    [
        "bigmap", "factory4_day", "factory4_night", "interchange", "laboratory",
        "lighthouse", "rezervbase", "sandbox", "sandbox_high", "shoreline",
        "tarkovstreets", "woods", "labyrinth"
    ];

    public async Task OnLoad()
    {
        logger.Info("[BountyBoard] ==========================================");
        logger.Info("[BountyBoard]  Bounty Board v1.2.0 loading...");
        logger.Info("[BountyBoard] ==========================================");

        // Register our bot ID in CoreConfig so DialogueController.GetActiveChatBots()
        // includes it — without this entry the bot is silently excluded from the friends list.
        var coreConfig = configServer.GetConfig<SPTarkov.Server.Core.Models.Spt.Config.CoreConfig>();
        coreConfig.Features.ChatbotFeatures.EnabledBots[BotId] = true;
        logger.Info($"[BountyBoard] Registered bot ID {BotId} in EnabledBots.");

        bountyStateService.Initialize(Assembly.GetExecutingAssembly());

        // ── Hunter spawn injection ────────────────────────────────────────────
        var modPath = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        var configPath = Path.Combine(modPath, "config.json");
        BountyConfig? config = null;

        if (File.Exists(configPath))
        {
            try
            {
                config = await jsonUtil.DeserializeFromFileAsync<BountyConfig>(configPath);
            }
            catch (Exception ex)
            {
                logger.Warning($"[BountyBoard] Could not parse config.json for hunters: {ex.Message}");
            }
        }

        config ??= new BountyConfig();

        if (config.Hunters.Enabled)
        {
            InjectHunterSpawns(config.Hunters);
        }

        logger.Success("[BountyBoard] Ready! Open the messenger and find 'Bounty Board'.");
        logger.Info("[BountyBoard]   Commands:  'bounty list'  |  'bounty claim'");
    }

    private void InjectHunterSpawns(HunterConfig hunters)
    {
        var locations = databaseService.GetLocations();
        var locationDict = locations.GetDictionary();

        foreach (var map in HunterMaps)
        {
            var actualKey = locations.GetMappedKey(map);
            if (!locationDict.TryGetValue(actualKey, out var location))
                continue;

            location.Base.BossLocationSpawn.Add(new BossLocationSpawn
            {
                BossName = hunters.Faction,
                BossChance = 100,
                BossDifficulty = hunters.Difficulty,
                BossEscortType = hunters.Faction,
                BossEscortAmount = hunters.EscortCount.ToString(),
                BossEscortDifficulty = hunters.Difficulty,
                BossZone = "",
                Time = hunters.SpawnDelay + 0.1337f,
                IgnoreMaxBots = true,
                ForceSpawn = false,
                DependKarma = false,
                DependKarmaPVE = false,
                SpawnMode = ["regular", "pve"],
                Supports = null!,
                TriggerId = "",
                TriggerName = "",
                Delay = 0
            });
        }

        logger.Info($"[BountyBoard] Hunter spawns injected: {hunters.Faction} x{hunters.EscortCount + 1}, " +
                     $"{hunters.Difficulty} difficulty, {hunters.SpawnDelay}s delay, across {HunterMaps.Count} maps");
    }
}

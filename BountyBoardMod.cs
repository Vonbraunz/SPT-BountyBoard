using BountyBoard.Services;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;
using System.Reflection;

namespace BountyBoard;

[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 2)]
public class BountyBoardMod(
    ISptLogger<BountyBoardMod> logger,
    BountyStateService bountyStateService,
    ConfigServer configServer) : IOnLoad
{
    // Must match BountyBoardBot.GetChatBot().Id exactly
    private const string BotId = "674db14ed849a3727ef24da1";

    public Task OnLoad()
    {
        logger.Info("[BountyBoard] ==========================================");
        logger.Info("[BountyBoard]  Bounty Board v1.0.0 loading...");
        logger.Info("[BountyBoard] ==========================================");

        // Register our bot ID in CoreConfig so DialogueController.GetActiveChatBots()
        // includes it — without this entry the bot is silently excluded from the friends list.
        var coreConfig = configServer.GetConfig<SPTarkov.Server.Core.Models.Spt.Config.CoreConfig>();
        coreConfig.Features.ChatbotFeatures.EnabledBots[BotId] = true;
        logger.Info($"[BountyBoard] Registered bot ID {BotId} in EnabledBots.");

        bountyStateService.Initialize(Assembly.GetExecutingAssembly());

        logger.Success("[BountyBoard] Ready! Open the messenger and find 'Bounty Board'.");
        logger.Info("[BountyBoard]   Commands:  'bounty list'  |  'bounty claim'");

        return Task.CompletedTask;
    }
}

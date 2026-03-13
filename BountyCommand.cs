using BountyBoard.Services;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers.Dialog.Commando;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Dialog;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Servers;

namespace BountyBoard.Commands;

/// <summary>
/// Handles "bounty list" and "bounty claim" inside the Bounty Board chat bot.
/// Implements IChatCommand so AbstractDialogChatBot can pick it up via DI.
/// </summary>
[Injectable]
public class BountyCommand(
    BountyStateService bountyStateService,
    MailSendService mailSendService,
    SaveServer saveServer,
    ISptLogger<BountyCommand> logger) : IChatCommand
{
    // ── IChatCommand ───────────────────────────────────────────────────────────

    public string CommandPrefix => "bounty";
    public List<string> Commands => ["list", "claim"];

    public string GetCommandHelp(string command) => command switch
    {
        "list"  => "bounty list  — Show the three active kill contracts.",
        "claim" => "bounty claim — Check your stash for bounty dogtags and collect rewards.",
        _       => string.Empty
    };

    public ValueTask<string> Handle(
        string command,
        UserDialogInfo commandHandler,
        MongoId sessionId,
        SendMessageRequest request)
    {
        switch (command)
        {
            case "list":  HandleList(sessionId, commandHandler);  break;
            case "claim": HandleClaim(sessionId, commandHandler); break;
        }
        return ValueTask.FromResult(request.DialogId);
    }

    // ── Template IDs ───────────────────────────────────────────────────────────

    private static readonly MongoId RoubleTpl           = new("5449016a4bdc2d6f028b456f");
    private const int    RewardStorageSeconds = 72 * 3600;

    // ── Sub-command handlers ───────────────────────────────────────────────────

    private void HandleList(MongoId sessionId, UserDialogInfo sender)
    {
        var active = bountyStateService.GetActiveBounties();

        if (active.Count == 0)
        {
            mailSendService.SendUserMessageToPlayer(sessionId, sender,
                "All bounties have been completed. New targets will be assigned on the next server restart.\n\nCheck back soon.");
            return;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("======== ACTIVE BOUNTIES ========\n");
        sb.AppendLine("Eliminate each target and bring their dogtag back to your stash.");
        sb.AppendLine("Type 'bounty claim' to collect payment.\n");
        for (int i = 0; i < active.Count; i++)
            sb.AppendLine($"  [{i + 1}] {active[i].TargetName}");
        sb.AppendLine($"\nReward per contract: {bountyStateService.RewardRoubles:N0} roubles + high-tier medical item");
        sb.AppendLine("=================================");

        mailSendService.SendUserMessageToPlayer(sessionId, sender, sb.ToString());
    }

    private void HandleClaim(MongoId sessionId, UserDialogInfo sender)
    {
        var profiles = saveServer.GetProfiles();
        if (!profiles.TryGetValue(sessionId, out var fullProfile))
        {
            mailSendService.SendUserMessageToPlayer(sessionId, sender,
                "Could not load your profile. Try again after your next raid.");
            return;
        }

        var pmc = fullProfile.CharacterData?.PmcData;
        var inventoryItems = pmc?.Inventory?.Items;
        if (inventoryItems == null)
        {
            mailSendService.SendUserMessageToPlayer(sessionId, sender,
                "Your inventory appears to be empty or unreadable. Try again after a raid.");
            return;
        }

        // Filter by Upd.Dogtag presence — more robust than template ID matching
        var dogtags = inventoryItems
            .Where(item =>
                item is not null &&
                !string.IsNullOrEmpty(item.Upd?.Dogtag?.Nickname))
            .ToList();

        if (dogtags.Count == 0)
        {
            mailSendService.SendUserMessageToPlayer(sessionId, sender,
                "No dogtags found in your stash.\n\n" +
                "Kill a bounty target in raid and bring back their dogtag. " +
                "Type 'bounty list' to see current targets.");
            return;
        }

        var activeBounties = bountyStateService.GetActiveBounties();
        var claimed = new List<string>();

        foreach (var dogtag in dogtags)
        {
            var victimName = dogtag.Upd?.Dogtag?.Nickname;
            if (string.IsNullOrEmpty(victimName)) continue;

            var match = activeBounties.FirstOrDefault(b =>
                string.Equals(b.TargetName, victimName, StringComparison.OrdinalIgnoreCase));

            if (match == null) continue;

            if (!bountyStateService.TryClaimBounty(match.TargetName, sessionId.ToString()))
                continue;

            claimed.Add(match.TargetName);
            SendRewardMail(sessionId, sender, match.TargetName);
        }

        if (claimed.Count == 0)
        {
            var targetList = string.Join(", ", activeBounties.Select(b => b.TargetName));
            mailSendService.SendUserMessageToPlayer(sessionId, sender,
                $"No matching dogtags found for the current bounties.\n\n" +
                $"Current targets: {targetList}\n\n" +
                "Make sure the target's dogtag is in your stash, not inside a container.");
        }
        else
        {
            mailSendService.SendUserMessageToPlayer(sessionId, sender,
                $"Contract(s) confirmed: {string.Join(", ", claimed)}\n\n" +
                "Check your in-game messages for your payment.");
        }
    }

    // ── Reward ─────────────────────────────────────────────────────────────────

    private void SendRewardMail(MongoId sessionId, UserDialogInfo sender, string targetName)
    {
        var rewardItems = new List<Item>
        {
            new()
            {
                Id       = new MongoId(),
                Template = RoubleTpl,
                Upd      = new Upd { StackObjectsCount = (double)bountyStateService.RewardRoubles }
            },
            new()
            {
                Id       = new MongoId(),
                Template = new MongoId(bountyStateService.GetRandomMedicalTpl())
            }
        };

        var message =
            $"CONTRACT COMPLETE\n\n" +
            $"Target eliminated: {targetName}\n\n" +
            $"Enclosed: {bountyStateService.RewardRoubles:N0} roubles + medical supplies.\n\n" +
            "Good work. Stay sharp out there.";

        mailSendService.SendSystemMessageToPlayer(
            sessionId,
            message,
            rewardItems,
            RewardStorageSeconds);

        logger.Success($"[BountyBoard] Reward mail sent for contract: {targetName}");
    }
}

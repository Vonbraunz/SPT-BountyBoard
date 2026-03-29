using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers.Dialog.Commando;
using SPTarkov.Server.Core.Helpers.Dialogue;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services;

namespace BountyBoard;

[Injectable(InjectionType.Scoped, null, int.MaxValue)]
public class BountyBoardBot(
    ISptLogger<AbstractDialogChatBot> logger,
    MailSendService mailSendService,
    ServerLocalisationService localisationService,
    IEnumerable<IChatCommand> chatCommands
) : AbstractDialogChatBot(logger, mailSendService, localisationService, chatCommands)
{
    public override UserDialogInfo GetChatBot() => new()
    {
        Id  = "674db14ed849a3727ef24da1",
        Aid = 8_675_309,
        Info = new UserDialogDetails
        {
            Nickname               = "Bounty Board",
            Side                   = "Usec",
            Level                  = 99,
            MemberCategory         = MemberCategory.Developer,
            SelectedMemberCategory = MemberCategory.Developer
        }
    };

    protected override string GetUnrecognizedCommandMessage() =>
        "Unknown command.\n\nType  bounty list  to see active contracts.\nType  bounty claim  to collect rewards.";
}

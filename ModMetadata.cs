using SPTarkov.Server.Core.Models.Spt.Mod;

namespace BountyBoard;


/// Replaces the old package.json — required for all SPT 4.0 mods.
/// All properties must be overridden; unused ones may be left null.

public record ModMetadata : AbstractModMetadata
{
    public override string ModGuid { get; init; } = "com.VonBraunZ.bountyboard";
    public override string Name { get; init; } = "Bounty Board";
    public override string Author { get; init; } = "DrBraun";
    public override List<string>? Contributors { get; init; }
    public override SemanticVersioning.Version Version { get; init; } = new("1.2.0");
    public override SemanticVersioning.Range SptVersion { get; init; } = new("~4.0.13");
    public override List<string>? Incompatibilities { get; init; }
    public override Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public override string? Url { get; init; }
    public override bool? IsBundleMod { get; init; } = false;
    public override string License { get; init; } = "MIT";
}

namespace BountyBoard.Models;

public class BountyConfig
{
    public int TargetCount { get; set; } = 3;
    public int RefreshHours { get; set; } = 24;
    public RewardConfig Rewards { get; set; } = new();
}

public class RewardConfig
{
    public string CurrencyTpl { get; set; } = "5449016a4bdc2d6f028b456f"; // Roubles
    public int CurrencyAmount { get; set; } = 1_000_000;
    public List<string> MedicalItems { get; set; } =
    [
      "544fb45d4bdc2dee738b4568", // Salewa First Aid Kit
      "590c678286f77426c9660122", // IFAK First Aid Kit
      "590c661e86f7741e566b646a", // Car First Aid Kit
      "5d02778e86f774203e7dedbe", // CMS Kit
      "590c657e86f77412b013051d" // Grizzly First Aid Kit
    ];
}

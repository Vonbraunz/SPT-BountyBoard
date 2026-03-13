namespace BountyBoard.Models;


/// Root object persisted to /data/bounty_state.json.
/// A fresh set is generated each time the server starts (unless one already exists).

public class BountyState
{
    /// <summary>UTC timestamp of when this bounty cycle was generated.</summary>
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The three active kill contracts for this cycle.</summary>
    public List<Bounty> Bounties { get; set; } = [];
}


/// A single PMC kill contract.

public class Bounty
{
    
    /// Full name of the PMC to eliminate (e.g. "Gary Sullivan").
    /// This must match the Upd.Dogtag.Nickname on the dogtag they drop.
    
    public string TargetName { get; set; } = string.Empty;

    /// <summary>Whether a player has already collected the reward for this bounty.</summary>
    public bool IsCompleted { get; set; } = false;

    /// <summary>Session ID of the player who claimed this bounty (null until claimed).</summary>
    public string? CompletedBySession { get; set; }
}

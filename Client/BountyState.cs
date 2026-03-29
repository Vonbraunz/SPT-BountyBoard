using System;
using System.Collections.Generic;

namespace BountyBoard.Client
{

    /// Root object for bounty_state.json produced by the BountyBoard server mod.

    public class BountyState
    {
        public DateTime GeneratedAt { get; set; }
        public List<Bounty> Bounties { get; set; } = new List<Bounty>();
    }

  
    /// Represents a single active or completed bounty target.

    public class Bounty
    {
        ///The exact PMC nickname that appears in-raid.
        public string TargetName { get; set; } = string.Empty;

        /// True once the bounty has been claimed / the target has been killed.
        public bool IsCompleted { get; set; }

        /// Session ID of the player who completed the bounty, or null if unclaimed.
        public string? CompletedBySession { get; set; }
    }

    /// Persisted hunter state — tracks raids survived for escalating spawn chance.
    public class HunterState
    {
        public int RaidsSurvived { get; set; }
    }
}

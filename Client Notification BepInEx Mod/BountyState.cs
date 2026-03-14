using System;
using System.Collections.Generic;

namespace BountyBoard.Client
{
    /// <summary>
    /// Root object for bounty_state.json produced by the BountyBoard server mod.
    /// </summary>
    public class BountyState
    {
        public DateTime GeneratedAt { get; set; }
        public List<Bounty> Bounties { get; set; } = new List<Bounty>();
    }

    /// <summary>
    /// Represents a single active or completed bounty target.
    /// </summary>
    public class Bounty
    {
        /// <summary>The exact PMC nickname that appears in-raid.</summary>
        public string TargetName { get; set; } = string.Empty;

        /// <summary>True once the bounty has been claimed / the target has been killed.</summary>
        public bool IsCompleted { get; set; }

        /// <summary>Session ID of the player who completed the bounty, or null if unclaimed.</summary>
        public string? CompletedBySession { get; set; }
    }
}

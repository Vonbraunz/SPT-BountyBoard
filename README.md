# Bounty Board — SPT 4.0.13

A dynamic bounty contract system for **Single Player Tarkov (SPT) 4.0.13**. A new "Bounty Board" contact appears in your in-game messenger with a rotating list of PMC targets. Hunt them down, bring back their dogtag, and collect your reward.

**New in 2.0.0** — PMC Hunters can now be sent after *you*. Survive enough raids and someone puts a contract on your head.

---

## Features

### Bounty System
- **Bounty Board contact** appears in your messenger alongside Commando and SPT Friend
- **Randomized PMC targets** generated from the real SPT bot name pool each cycle
- **Live HUD notifications** — get an in-raid alert the moment a bounty target spawns, and another when they're eliminated
- **Claim rewards** by typing a command after extracting with a target's dogtag in your stash
- **Auto-rotating cycle** — when all contracts are completed, a fresh set of targets is immediately generated
- **Timed refresh** — bounty cycle resets every 24 hours regardless of completion (configurable)
- **Persistent state** — bounty progress is saved to disk and survives server restarts

### Hunter System (New in 2.0.0)
- **Escalating threat** — hunter PMCs can spawn in your raids, starting at 25% chance and increasing by 5% per survived raid
- **Death resets** — die in raid and the hunter chance resets back to 25%
- **Same-faction pairs** — 2 PMC hunters spawn as a group (same faction so they don't fight each other)
- **Delayed entry** — hunters arrive ~3 minutes into the raid, giving you time to get moving
- **Contract alert** — receive a notification when hunters are dispatched after you
- **Kill confirmation** — notification fires when you eliminate a hunter, with a kill counter
- **Fully configurable** — faction, difficulty, spawn delay, escort count, base chance, and scaling all set via `config.json`

### Intel Scanner (New in 2.0.0)
- **Press O in-raid** to scan for active bounty targets and hunter status
- Shows target names if spotted, and whether hunters are active with a kill counter
- Scan key is configurable via BepInEx config

---

## Compatibility

- Bot Callsigns - Reloaded
- [SAIN] Twitch Players

No known incompatibilities.

---

## Installation

The release zip contains both the server mod and BepInEx client mod — fully drag-and-drop:

1. Download `BountyBoard.zip`
2. Extract and drag the `SPT` and `BepInEx` folders into your SPT install root
3. Start the server — the Bounty Board contact will appear the next time you log in

**What goes where:**
```
<SPT Root>/
├── SPT/user/mods/BountyBoard-drb/
│   ├── BountyBoard.dll       <- server mod
│   └── config.json
└── BepInEx/plugins/
    └── BountyBoard.Client.dll  <- client mod
```

### Server Mod Only

If you don't want in-raid notifications or the hunter system, copy just the `BountyBoard-drb` folder into `SPT/user/mods/`. The bounty board messenger commands work without the client mod. Note: hunter spawns will still be injected but the client-side chance roll and stripping won't occur — disable hunters in `config.json` if running server-only.

---

## HUD Notifications

When the BepInEx client mod is installed, you'll receive native EFT-style notifications during raids:

- **BOUNTY TARGET SPOTTED** — fires when a bounty target PMC spawns in your raid
- **BOUNTY COLLECTED** — fires when a bounty target is eliminated
- **CONTRACT ALERT** — fires when hunter PMCs are dispatched after you (~3 min into raid)
- **HUNTER ELIMINATED** — fires when you take out a hunter, with kill count (e.g. 1/2)

### Intel Scanner (O Key)

Press **O** at any time in-raid to get a status report:

- Names of any active bounty targets currently alive in the raid
- Whether hunters are active and how many you've eliminated

### Client Mod Configuration

Settings are available in the BepInEx configuration manager (F12 in-game) under `BountyBoard.Client`:

| Setting | Default | Description |
|---|---|---|
| Bounty State Path | *(auto)* | Path to `bounty_state.json` — only change if your install is non-standard |
| Hunter State Path | *(auto)* | Path to `hunter_state.json` — tracks raids survived for hunter spawn chance |
| Scan Target Key | `O` | Keybind for the in-raid intel scanner |

---

## Usage

Open the **Bounty Board** contact in your in-game messenger and type:

| Command | Description |
|---|---|
| `bounty list` | Show the current active kill contracts and reward info |
| `bounty claim` | Scan your stash for matching dogtags and collect any earned rewards |

### How it works

1. Check `bounty list` to see your current targets
2. Find and kill a target PMC in raid — you'll get a HUD alert when they spawn
3. Extract with their **dogtag in your stash** (not inside a container)
4. Message the Bounty Board and type `bounty claim`
5. Reward mail arrives with your configured currency and a random bonus item

---

## Configuration

Edit `config.json` in the mod folder (`SPT/user/mods/BountyBoard-drb/`):

```json
{
  "TargetCount": 3,
  "RefreshHours": 24,
  "Rewards": {
    "CurrencyTpl": "5449016a4bdc2d6f028b456f",
    "CurrencyAmount": 1000000,
    "MedicalItems": [
      "544fb45d4bdc2dee738b4568",
      "590c678286f77426c9660122",
      "590c661e86f7741e566b646a",
      "5d02778e86f774203e7dedbe",
      "590c657e86f77412b013051d"
    ]
  },
  "Hunters": {
    "Enabled": true,
    "BaseChance": 25,
    "ChancePerSurvival": 5,
    "MaxChance": 100,
    "EscortCount": 1,
    "Difficulty": "hard",
    "SpawnDelay": 180,
    "Faction": "pmcUSEC"
  }
}
```

### Bounty Settings

| Field | Description | Default |
|---|---|---|
| `TargetCount` | Number of targets per bounty cycle | `3` |
| `RefreshHours` | Hours before the cycle resets regardless of completion (`0` to disable) | `24` |
| `Rewards.CurrencyTpl` | Item template ID of the currency reward | Roubles |
| `Rewards.CurrencyAmount` | Stack size of the currency reward | `1000000` |
| `Rewards.MedicalItems` | Pool of item template IDs for the bonus reward | High-tier medicals |

### Hunter Settings

| Field | Description | Default |
|---|---|---|
| `Hunters.Enabled` | Enable/disable the hunter system entirely | `true` |
| `Hunters.BaseChance` | Starting hunter spawn chance (%) | `25` |
| `Hunters.ChancePerSurvival` | Additional chance per survived raid (%) | `5` |
| `Hunters.MaxChance` | Maximum hunter spawn chance (%) | `100` |
| `Hunters.EscortCount` | Number of escort hunters (total hunters = 1 leader + escorts) | `1` |
| `Hunters.Difficulty` | Bot difficulty: `easy`, `normal`, `hard`, `impossible` | `hard` |
| `Hunters.SpawnDelay` | Seconds into raid before hunters spawn | `180` |
| `Hunters.Faction` | Hunter faction: `pmcUSEC` or `pmcBEAR` | `pmcUSEC` |

### Currency Template IDs
| Currency | Template ID |
|---|---|
| Roubles | `5449016a4bdc2d6f028b456f` |
| Dollars | `5696686a4bdc2da3298b456a` |
| Euros | `569668774bdc2da2298b4568` |

Changes take effect on the next server restart.

---

## Rewards

Each completed bounty contract pays out:
- Configurable currency and amount (default: 1,000,000 roubles)
- One random item from the configurable bonus pool (default: high-tier medicals)

---

## Requirements

- SPT **4.0.13**
- .NET 9 SDK (to build from source)

---

## Building from Source

```bash
git clone https://github.com/Vonbraunz/SPT-BountyBoard
cd SPT-BountyBoard
```

Open `BountyBoard.sln` in Visual Studio or Rider. Build the Server project first, then the Client project. The Client post-build creates a combined release zip containing both DLLs.

---

## Changelog

### 2.0.0 — "The Hunted"
- Added Hunter system: PMC hunters can spawn in your raids with escalating chance based on raids survived
- Added Intel Scanner: press O in-raid to check for bounty targets and hunter status
- Restructured project into proper Server/Client solution with combined release zip
- Removed debug test keys (F8/F9)

### 1.2.0
- Added BepInEx companion client mod for in-raid HUD notifications
- Bounty target spotted and collected notifications
- Dogtag detection improvements

### 1.0.0 — Initial Release
- Bounty Board messenger contact with list/claim commands
- Randomized PMC targets from bot name pool
- Configurable rewards and cycle timing
- Persistent state across server restarts

---

## License

MIT

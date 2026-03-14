# 🎯 Bounty Board — SPT 4.0.13

A dynamic bounty contract system for **Single Player Tarkov (SPT) 4.0.13**. A new "Bounty Board" contact appears in your in-game messenger with a rotating list of PMC targets. Hunt them down, bring back their dogtag, and collect your reward.

---

## Features

- **Bounty Board contact** appears in your messenger alongside Commando and SPT Friend
- **Randomized PMC targets** generated from the real SPT bot name pool each cycle
- **Live HUD notifications** — get an in-raid alert the moment a bounty target spawns, and another when they're eliminated *(requires the companion BepInEx mod, Installed by Default)*
- **Claim rewards** by typing a command after extracting with a target's dogtag in your stash
- **Auto-rotating cycle** — when all contracts are completed, a fresh set of targets is immediately generated
- **Timed refresh** — bounty cycle resets every 24 hours regardless of completion (configurable)
- **Fully configurable** — target count, refresh interval, currency type, currency amount, and bonus item pool all set via `config.json`
- **Persistent state** — bounty progress is saved to disk and survives server restarts

---

## Compatibility

✅ Bot Callsigns - Reloaded  
✅ [SAIN] Twitch Players

No incompatibilities yet...

---

## Installation

### Server Mod + BepInEx Client (Recommended)

The release zip contains both components and is fully drag-and-drop:

1. Download `BountyBoard-1.2.0.zip`
2. Extract and drag the `SPT` and `BepInEx` folders into your SPT install root
3. Start the server — the Bounty Board contact will appear the next time you log in

**What goes where:**
```
<SPT Root>/
├── SPT/user/mods/BountyBoard-drb/
│   ├── BountyBoard.dll       ← server mod
│   └── config.json
└── BepInEx/plugins/
    └── BountyBoard.Client.dll  ← HUD notification mod
```

### Server Mod Only

If you don't want in-raid notifications, copy just the `BountyBoard-drb` folder into `SPT/user/mods/`. Everything works without the client mod.

---

## HUD Notifications

When the BepInEx client mod is installed, you'll receive native EFT-style notifications during raids:

- **⚠ BOUNTY TARGET SPOTTED** — fires when a bounty target PMC spawns in your raid, including late wave spawns
- **☠ BOUNTY COLLECTED** — fires when a bounty target is eliminated

Notifications work automatically with no setup required. The client mod reads `bounty_state.json` at the start of each raid and tracks only active (non-completed) targets.

### Client Mod Configuration

Settings are available in the BepInEx configuration manager (F12 in-game) under `BountyBoard.Client`:

| Setting | Default | Description |
|---|---|---|
| Bounty State Path | *(auto)* | Path to `bounty_state.json`. Auto-resolved from SPT root — only change this if your install is non-standard |
| Enable Debug Keys | `false` | Enables F8/F9 test keybinds for verifying notifications without waiting for a real target |
| Test Notification Key | `F8` | Fires dummy spotted + killed notifications |
| Test Real Target Key | `F9` | Reads `bounty_state.json` and fires a real notification using the first active target name |

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

Edit `config.json` in the mod root folder:

```json
{
  "TargetCount": 3,
  "RefreshHours": 24,
  "Rewards": {
    "CurrencyTpl": "5449016a4bdc2d6f028b456f",
    "CurrencyAmount": 1000000,
    "MedicalItems": [
      "5d02778e86f774203e7dedbe",
      "590c661e86f7741e566b646a",
      "5755356824597772cb798962",
      "5c0e533786f7747fa1419862",
      "5c0e530286f7747fa1419869"
    ]
  }
}
```

| Field | Description | Default |
|---|---|---|
| `TargetCount` | Number of targets per bounty cycle | `3` |
| `RefreshHours` | Hours before the cycle resets regardless of completion (`0` to disable) | `24` |
| `Rewards.CurrencyTpl` | Item template ID of the currency reward | `5449016a4bdc2d6f028b456f` (Roubles) |
| `Rewards.CurrencyAmount` | Stack size of the currency reward | `1000000` |
| `Rewards.MedicalItems` | Pool of item template IDs to pick the bonus reward from | See above |

Changes take effect on the next server restart.

### Currency Template IDs
| Currency | Template ID |
|---|---|
| Roubles | `5449016a4bdc2d6f028b456f` |
| Dollars | `5696686a4bdc2da3298b456a` |
| Euros | `569668774bdc2da2298b4568` |

---

## Rewards

Each completed contract pays out:
- 💰 Configurable currency and amount (default: 1,000,000 ₽)
- 💊 One random item from the configurable bonus pool (default: high-tier medicals)
  - Surv12 Field Surgery Kit
  - Grizzly Medical Kit
  - IFAK Individual First Aid Kit
  - Propital Regenerative Stimulant
  - Morphine Injector

---

## Requirements

- SPT **4.0.13**
- .NET 9 SDK (to build from source)

---

## Building from Source

```bash
git clone https://github.com/Vonbraunz/SPT-BountyBoard
cd BountyBoard
dotnet build
```

The compiled server mod DLL and release zip will be output to the project root.  
The BepInEx client mod is a separate project — build it independently and it will be included in the release zip automatically.

---

## License

MIT

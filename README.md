# 🎯 Bounty Board — SPT 4.0 Server Mod

A server-side mod for **Single Player Tarkov (SPT) 4.0** that adds a dynamic bounty contract system to your game. A new "Bounty Board" contact appears in your in-game messenger with a rotating list of PMC targets. Hunt them down, bring back their dogtag, and collect your reward.

---

## Features

- **Bounty Board contact** appears in your messenger alongside Commando and SPT Friend
- **Randomized PMC targets** generated from the real SPT bot name pool each cycle
- **Claim rewards** by typing a command after extracting with a target's dogtag in your stash
- **Auto-rotating cycle** — when all contracts are completed, a fresh set of targets is immediately generated
- **Configurable** — adjust target count and ruble reward via a simple JSON config file
- **Persistent state** — bounty progress is saved to disk and survives server restarts

---

## Compatibility 

✅Bot Callsigns - Reloaded
✅[SAIN] Twitch Players

No Incompatibilities yet...

---

## Installation

1. Build the project or download the latest release
2. Copy the `BountyBoard` folder into your SPT `user/mods/` directory
3. Start the server — the Bounty Board contact will appear the next time you log in

---

## Usage

Open the **Bounty Board** contact in your in-game messenger and type:

| Command | Description |
|---|---|
| `bounty list` | Show the current active kill contracts and reward info |
| `bounty claim` | Scan your stash for matching dogtags and collect any earned rewards |

### How it works

1. Check `bounty list` to see your current targets
2. Find and kill a target PMC in raid
3. Extract with their **dogtag in your stash** (not inside a container)
4. Message the Bounty Board and type `bounty claim`
5. Reward mail arrives with roubles and a random high-tier medical item

---

## Configuration

Edit `config.json` in the mod root folder:

```json
{
  "TargetCount": 3,
  "RewardRoubles": 1000000
}
```

| Field | Description | Default |
|---|---|---|
| `TargetCount` | Number of targets per bounty cycle | `3` |
| `RewardRoubles` | Ruble payout per completed contract | `1000000` |

Changes take effect on the next server restart.

---

## Rewards

Each completed contract pays out:
- 💰 Configurable ruble amount (default: 1,000,000 ₽)
- 💊 One random high-tier medical item from the pool:
  - Surv12 Field Surgery Kit
  - Grizzly Medical Kit
  - IFAK Individual First Aid Kit
  - Propital Regenerative Stimulant
  - Morphine Injector

---

## Requirements

- SPT **4.0.x**
- .NET 9 SDK (to build from source)

---

## Building from Source

```bash
git clone https://github.com/Vonbraunz/SPT-BountyBoard
cd BountyBoard
dotnet build
```

The compiled DLL will be output to `bin/Release/BountyBoard/`.

---

## License

MIT

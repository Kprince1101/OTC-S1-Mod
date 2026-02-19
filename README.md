# OverTheCounter for Schedule I

**OverTheCounter** is a Comprehensive logistics expansion for Schedule I. Features physical Manager automation, 24/7 market access via "Executive Privilege," high-stakes "Desperation" daytime deals, a tiered OTC customer app SaaS scam, and manual cash-laundering via the "Rinse Cycle" questline.

> **DUAL BUILD:** This mod ships both `OverTheCounter.Il2Cpp.dll` and `OverTheCounter.Mono.dll`.
> If using a mod manager, [SwapperPlugin](https://thunderstore.io/c/schedule-i/p/the_croods/SwapperPlugin/) (included as a dependency) automatically loads the correct DLL for your game branch.
> If installing manually, only copy the DLL that matches your game branch — see instructions below.

# Features

## 1. The Manager Update (Endgame Automation)
*Stop running errands. Start running an empire.*
A complete, simulation-based automation system built on **S1API** for maximum compatibility (works seamlessly alongside **k0Mods**).

* **True Logistics:** Hire physical **Manager NPCs** at your Laundromat, Post Office, Car Wash, or Taco Ticklers.
* **Supply Routes:** Managers automatically physically visit stores to keep your shelves stocked with essential ingredients (fertilizer, chemicals, etc.).
* **Distribution Routes:** Assign up to **3 custom logic routes** per manager. They move product from Container A to Container B, allowing you to chain storage containers across the map.
* **The Cost of Business:** Managers cost **$350/day** (paid from their locker's petty cash) and report their status via a daily summary text.
* **"Executive Privilege" Quest:** A new endgame questline. Prove your worth to the **Night Market Boss** in the downtown high-rises by crafting high-value Weed ($105+), Meth ($200+), and Cocaine ($400+) mixes. Success unlocks 24/7 Night Market access for your automation network.

## 2. Smart Logistics UI (Quality of Life)
*No more mental math. No more clicking back and forth.*

* **The Contract Aggregator:** Merges all active orders into a single "Pending Deliveries" list, grouped by time window. See exactly what you need for the next run at a glance.
* **Live Stash Manifest:** When you open a container, a side panel instantly calculates the total product required for your active contracts versus what you are holding.
* **Smart Fill Button:** One-click transfer that automatically pulls the exact amount of product needed from the container to your inventory, prioritizing jars over baggies to save space. Toggle "Include All Delivery Windows" to limit fills to only your current window's contracts.

## 3. Dynamic World Events
*The city feels alive, and the market is volatile.*

* **The "Desperation" System:** Breaks the "wait for night" meta.
    * **Daytime Rush:** High-addiction "Fiends" trigger urgent orders between 08:00 and Curfew.
    * **High Risk/High Reward:** strict 2-hour deadlines with a **+45% payout bonus**. Missing the deadline boosts the Cartel's reputation, not yours.
* **Drifter Encounters:** Random NPCs spawn throughout the city offering one-time deals via text.
    * **The Risks:** Encounter **Whales** (bulk buyers), **Robbers** (ambushes), or **Narcs** (police stings).
    * **Scaling:** Spawn rates increase as you unlock more regions.

## 4. Progression & Economy
*You have to earn your tools.*

* **"OverTheCounter" (OTC) App:** The customer list is no longer free.
    * **SaaS Model:** Purchase software tiers ($3k - $12k) to unlock features like Region Sorting, Addiction Indicators, and GPS Customer Tracking.
    * **Subscription:** Failure to pay the $1,000/week server rent disables the app.
* **Early Game Laundering:**
    * **Meet Vic:** A corrupt associate who offers manual cash laundering once you hit the $10k weekly ATM limit.
    * **Risk vs. Reward:** Pay a 17-20% fee to clean dirty cash early, bridging the gap between street dealing and owning legitimate businesses.
## 5. Customizable Minimap (UI & Navigation)

* **Opt-In UI:** The minimap is turned **OFF** out of the box. You must enable it first (see the Configuration section below).
* **Total Control:** Choose between a circular or square map, adjust the size, set your screen anchor, and use a custom zoom cycle hotkey (Default: N).
* **Icon Filtering:** Keep your screen clean by toggling exactly which POIs show up, from active customers to your newly hired Managers.

## Multiplayer Support
Built from the ground up for co-op.
* **Host-Authoritative:** Quest progress, Manager routes, and OTC subscriptions sync flawlessly between host and clients.

## Requirements & Installation

### Using a mod manager (recommended)
Install OverTheCounter from Thunderstore using **r2modman**, **Thunderstore Mod Manager**, or **Gale**. When prompted to install dependencies, click **Yes** — the mod manager will download and configure everything for you, including:
* **S1API** — modding API layer
* **[SwapperPlugin](https://thunderstore.io/c/schedule-i/p/the_croods/SwapperPlugin/)** — automatically detects your game branch (IL2CPP or Mono) and loads the correct DLL. No manual steps needed.
* **SteamNetworkLib** — multiplayer sync (single-player works fine without it, but there's no harm in having it installed)

Launch the game. That's it.

### Manual installation
If you prefer not to use a mod manager, you'll need to install each dependency yourself.

1.  Install **MelonLoader v0.7.0**.
2.  Install **S1API** (ifBars fork) — modding API layer. Make sure to pick the version matching your game branch (IL2CPP or Mono).
3.  *(Multiplayer only)* Install **SteamNetworkLib** — required for multiplayer sync. Again, pick the version matching your game branch.
4.  Download the latest OverTheCounter release. It includes two DLLs:
    * `OverTheCounter.Il2Cpp.dll` — for the **IL2CPP** branch
    * `OverTheCounter.Mono.dll` — for the **Mono** branch
5.  Copy **only the DLL that matches your game branch** into your `Mods` folder. **Do not install both** — loading the wrong DLL will crash MelonLoader. SwapperPlugin is not needed for manual installs.
6.  Launch the game.

## Configuration
Settings are stored in MelonLoader's config file and organized into nine categories:

* **Desperation System** — Enable/disable toggle, fiend addiction threshold, trigger chance per hour, max daily events, response/delivery deadlines, bonus multiplier, relationship penalty, cooldown, and active hours.
* **Vic Laundering** — Tier costs, returns, trust unlock threshold, and intro quest requirements.
* **Static Subscription** — Weekly billing cost, cycle length, ATM deposit trigger, and tier upgrade costs/requirements.
* **Contract Notifications** — Enable/disable toggle, consolidation threshold (minimum contracts before grouping kicks in).
* **Manager System** — Daily wage and signing fee.
* **Executive Privilege (Bella)** — Minimum weed, meth, and cocaine mix value thresholds for the quest.
* **Drifter System** — Enable/disable toggle, spawn chance per hour, max active drifters, active hours, offer window, delivery deadline, linger duration, and minimum deal value.
* **Minimap** — Enable/disable toggle (off by default), size, zoom level, toggle key (default: N), screen position, circle/square shape, rotate-with-player, border color/width, and icon scale. Invalid values are automatically corrected.
* **Minimap POIs** — Per-category toggles for potential customers, unlocked customers (off by default), dealers, dead drops, contracts, quests, properties, and managers.

Every setting includes a full description visible in [ModsApp by k0Mods](https://thunderstore.io/c/schedule-i/p/k0Mods/ModsApp/) (recommended, open-source). Also compatible with [Mod Manager & Phone App](https://www.nexusmods.com/schedule1/mods/397) (descriptions not supported). You can always edit the config file directly if you prefer.

In multiplayer, the host's settings are automatically synced to all clients (except local-only preferences like consolidation settings).

## License
**Creative Commons Attribution-NonCommercial-ShareAlike 4.0 International (CC BY-NC-SA 4.0)**

You are free to:
* **Share** — copy and redistribute the material in any medium or format.
* **Adapt** — remix, transform, and build upon the material.

Under the following terms:
* **Attribution** — You must give appropriate credit to the original author (hdlmrell) and indicate if changes were made. You may not suggest the author endorses you or your use.
* **NonCommercial** — You may not use the material for commercial purposes.
* **ShareAlike** — If you remix, transform, or build upon the material, you must distribute your contributions under the same license as the original.

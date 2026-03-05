# OverTheCounter for Schedule I

**OverTheCounter** is a comprehensive logistics expansion for Schedule I, compatible with both **IL2CPP and Mono** branches. Features physical Manager automation, 24/7 market access via "Executive Privilege," high-stakes "Desperation" daytime deals, a tiered OTC customer app SaaS scam, and manual cash-laundering via the "Rinse Cycle" questline.

> **DUAL BUILD:** This mod ships both `OverTheCounter.Il2Cpp.dll` and `OverTheCounter.Mono.dll`.
> The bundled **OTC Loader** plugin automatically detects your game branch and loads the correct DLL — no setup required.
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
* **Smart Fill — Storage:** Open any container to see a live manifest of what you need vs. what you have. One click pulls the exact product into your inventory, prioritizing jars over baggies.
* **Smart Fill — Handover:** During a contract handover, click once to fill the bare minimum (quality-aware, smallest packaging first). Click again to boost until acceptance hits 95%+.

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

* **"OverTheCounter" (OTC) App:** A dedicated phone app for managing your operations, automation network, and customer intel — locked behind a paygate until Static sells you access.
    * **Customers Tab:** Your full customer roster presented as a mugshot grid. Click any card to open a detail panel showing relationship health, addiction level, preferred effects, connections, and weekly purchase history. Regions unlock progressively as you expand territory and reduce cartel influence. Desperate customers are flagged with an urgent alert.
    * **Employees Tab:** Monitor your workforce — view current status, daily wage, locker balance, and carried inventory for each employee. Filter by type (Chemist, Botanist, Handler, Cleaner) or group by property. Click any employee to open a detail view with minimap tracking. If [HireMe](https://thunderstore.io/c/schedule-i/p/UnicornsCanMod/HireMe/) ([Nexus](https://www.nexusmods.com/schedule1/mods/1099)) is installed, a **Hire / Transfer** button appears for managing your roster.
    * **Managers Tab:** Full visibility into your Manager NPC network. View active supply and distribution routes, read daily activity logs, and track each manager's real-time position on the minimap.
    * **SaaS Model:** Purchase software tiers ($1.5k – $12k) to expand customer region access and unlock progressive features like GPS customer tracking.
    * **Subscription:** Failure to pay the $1,000/week server fee disables the app until renewed with Static at the Casino.
* **Early Game Laundering:**
    * **Meet Vic:** A corrupt associate who offers manual cash laundering once you hit the $10k weekly ATM limit.
    * **Risk vs. Reward:** Pay a 17-20% fee to clean dirty cash early, bridging the gap between street dealing and owning legitimate businesses.
## 5. Customizable Minimap (UI & Navigation)

* **Opt-In UI:** The minimap is turned **OFF** out of the box. You must enable it first (see the Configuration section below).
* **Total Control:** Choose between a circular or square map, adjust the size, set your screen anchor, and use a custom zoom cycle hotkey (Default: N).
* **Icon Filtering:** Keep your screen clean by toggling exactly which POIs show up, from active customers to your newly hired Managers.
* **Rank & XP Bar (opt-in):** An optional panel below the minimap shows your current rank and tier (e.g. *Hoodlum IV*) with a live XP progress bar and numeric readout. Every time you earn XP a **+X XP** label floats up from the bar and fades out. On tier advancement the label also shows **+1 Level** in a distinct royal color. Enable via the *Show Rank/XP* config option (off by default).

## Multiplayer Support
Built from the ground up for co-op.
* **Host-Authoritative:** Quest progress, Manager routes, and OTC subscriptions sync flawlessly between host and clients.

## Requirements & Installation

> **IL2CPP vs. Mono:** Schedule I runs on two different Unity backends. Most players are on **IL2CPP** (the Steam default). Mods built for one backend are incompatible with the other and will crash MelonLoader if loaded together. The bundled **OTC Loader** plugin (included in every install method below) automatically detects which version you're running and disables any incompatible mod DLLs across your entire mod list — not just OverTheCounter.

### Using a mod manager (recommended)
Install OverTheCounter from Thunderstore using **r2modman**, **Thunderstore Mod Manager**, or **Gale**. When prompted to install dependencies, click **Yes** — the mod manager will download and configure everything for you, including:
* **S1API** — modding API layer
* **OTC Loader** *(bundled)* — automatically detects your game branch (IL2CPP or Mono) and disables incompatible DLLs. Replaces the need for SwapperPlugin.
* **SteamNetworkLib** — multiplayer sync (single-player works fine without it, but there's no harm in having it installed)

Launch the game. That's it.

### Manual installation
If you prefer not to use a mod manager, you'll need to install each dependency yourself.

1.  Install **MelonLoader v0.7.0**.
2.  Install **S1API** (ifBars fork) — modding API layer. Make sure to pick the version matching your game branch (IL2CPP or Mono).
3.  *(Multiplayer only)* Install **SteamNetworkLib** — required for multiplayer sync. Again, pick the version matching your game branch.
4.  Download the latest OverTheCounter release. It includes three files:
    * `OverTheCounter.Il2Cpp.dll` — for the **IL2CPP** branch
    * `OverTheCounter.Mono.dll` — for the **Mono** branch
    * `OverTheCounter-Loader.dll` — the OTC Loader plugin *(optional, see step 6)*
5.  Copy **only the DLL that matches your game branch** into your `Mods` folder. **Do not install both** — loading the wrong-branch DLL will crash MelonLoader.
6.  *(Optional)* Copy `OverTheCounter-Loader.dll` into your `Plugins` folder. This is the **OTC Loader** — it scans your entire `Mods` folder on startup and automatically disables any DLL that doesn't match your game branch, preventing crashes from other mods shipping both IL2CPP and Mono versions. You don't need it if you're manually managing your mod list yourself.
7.  Launch the game.

## Configuration
Settings are stored in MelonLoader's config file and organized into nine categories:

* **Desperation System** — Enable/disable toggle, fiend addiction threshold, trigger chance per hour, max daily events, response/delivery deadlines, bonus multiplier, relationship penalty, cooldown, and active hours.
* **Vic Laundering** — Tier costs, returns, trust unlock threshold, and intro quest requirements.
* **Static Subscription** — Weekly billing cost, cycle length, ATM deposit trigger, and tier upgrade costs/requirements.
* **Contract Notifications** — Enable/disable toggle, consolidation threshold (minimum contracts before grouping kicks in).
* **Manager System** — Daily wage and signing fee.
* **Executive Privilege (Bella)** — Minimum weed, meth, and cocaine mix value thresholds for the quest.
* **Drifter System** — Enable/disable toggle, spawn chance per hour, max active drifters, active hours, offer window, delivery deadline, linger duration, and minimum deal value.
* **Minimap** — Enable/disable toggle (off by default), size, zoom level, toggle key (default: N), screen position, circle/square shape, rotate-with-player, border color/width, icon scale, and Show Rank/XP bar (off by default). Invalid values are automatically corrected.
* **Minimap POIs** — Per-category toggles for potential customers, unlocked customers (off by default), dealers, dead drops, contracts, quests, properties, and managers.

Every setting includes a full description visible in [ModsApp by k0Mods](https://thunderstore.io/c/schedule-i/p/k0Mods/ModsApp/) (recommended, open-source). Also compatible with [Mod Manager & Phone App](https://www.nexusmods.com/schedule1/mods/397) (descriptions not supported). You can always edit the config file directly if you prefer.

In multiplayer, the host's settings are automatically synced to all clients (except local-only preferences like consolidation settings).

## Mod Compatibility

### HireMe — *built-in soft integration*
[Thunderstore](https://thunderstore.io/c/schedule-i/p/UnicornsCanMod/HireMe/) | [Nexus](https://www.nexusmods.com/schedule1/mods/1099)

Provides a dedicated UI for hiring and transferring employees through Manny's network — hire multiple at once, assign them across properties, transfer between locations, and fire with one click.

When HireMe is installed alongside OverTheCounter, a **Hire / Transfer** button appears in the OTC app's Employees tab for quick access. No configuration needed — the integration is detected automatically at runtime.

## License
**Creative Commons Attribution-NonCommercial-ShareAlike 4.0 International (CC BY-NC-SA 4.0)**

You are free to:
* **Share** — copy and redistribute the material in any medium or format.
* **Adapt** — remix, transform, and build upon the material.

Under the following terms:
* **Attribution** — You must give appropriate credit to the original author (hdlmrell) and indicate if changes were made. You may not suggest the author endorses you or your use.
* **NonCommercial** — You may not use the material for commercial purposes.
* **ShareAlike** — If you remix, transform, or build upon the material, you must distribute your contributions under the same license as the original.

# OverTheCounter for Schedule I

**OverTheCounter** is a Quality of Life, Utility, and Balance overhaul for *Schedule I*. It focuses on streamlining logistics, modernizing the UI, and rebalancing the gameplay loop to make the day phase strategically viable.

> [!WARNING]
> **COMPATIBILITY NOTICE:**
> This mod is built for the **IL2CPP branch** of the game using **MelonLoader 0.7.0**.
> It utilizes Harmony patching that is **NOT compatible with the Mono branch**.
> Do not attempt to use this on Mono builds; it will crash or fail to load.

## Features

### 1. The "Desperation" System (Balance Change)
A new mechanic designed to break the "Night Meta" and make daytime deliveries profitable.
* **Director System:** A smart background system rolls for high-addiction "Fiends" to demand product during the day (08:00 - Curfew).
* **High Risk / High Reward:** These orders come with a **2-hour deadline** but pay out a **+45% "Desperation Premium"**, mathematically beating the standard Night+Curfew bonus.
* **Consequences:** Missing the window results in the customer buying from "The Cartel" (Relationship Penalty & 24h Lockout).

### 2. Contract Aggregator & Manifest (QoL Utility)
Solves the issue of accepted contracts clipping off the screen and streamlines inventory prep.
* **HUD Cleanup:** Condenses multiple active contract entries into a single, clean "Pending Deliveries" list to prevent UI overflow. Contracts are grouped by their **delivery time window** — only contracts sharing the same window are combined.
* **Delivery Manifest:** Automatically calculates and displays the **total product required** for your current run (e.g., *"Total Loadout: 120g Coke, 45g Weed"*). You no longer need to check 5 different contracts and do mental math at your stash.
* *Note: You still travel to each customer individually; this feature simply organizes the data.*

### 3. Smart Stash Overlay (QoL Utility)
A "Delivery Manifest" side-panel that appears automatically when you open any storage container.
* **Live Manifest:** Aggregates all active contract requirements and shows what you still need, accounting for what's already in your inventory. Updates in real-time as you move items.
* **Packaging-Aware:** Correctly accounts for jars (5x) vs baggies (1x) when calculating quantities, so your counts always match what contracts actually need.
* **Smart Fill:** One-click button that automatically transfers the right products from the open container into your inventory. Prioritizes jars over baggies for efficiency.
* **Include All Shifts Toggle:** Choose whether to see only the current delivery window or all upcoming contracts at once (defaults to all).

### 4. "Hustle as a Service" Quest & OTC App (Progression & UI)
The customer list is no longer a default feature. It is now a bootleg app called "OverTheCounter" (OTC), acquired via a new questline.
* **The Quest:** Static sells the software on a predatory subscription model. You must pay an upfront install fee per tier, plus a recurring $1,000/month "Server Rent." Failure to pay disables the app features.
* **The "SaaS" Trap:** Clicking a customer in the app highlights their location in-world, fixing a missing feature from the base game.
* **Software Tiers:**
    * **v0.1 "Early Access" ($3,000):** Unlocks Region Sorting for Northtown and Westville only.
    * **v1.0 "Pro License" ($6,000):** Unlocks Global Region Sorting and adds Addiction Status Indicators next to customer names.
    * **v2.0 "Enterprise" ($12,000):** Unlocks the GPS Locator (click to highlight in-world) and the Desperation Filter.

### 5. Cash Laundering Alternative (Progression)
Addresses the single-player economy bottleneck by introducing a manual laundering option before you own legitimate businesses.
* **"Rinse Cycle" Quest:** Hitting the standard $10k weekly deposit limit triggers an introduction to Vic, a corrupt bank associate looking for a side hustle.
* **Manual Laundering:** A daily interaction that lets you clean extra cash beyond the ATM limit. It comes with a significant "Risk Fee" (15-20%) and requires physical travel, balancing the extra capacity with effort.
* **Trust Progression:** Regular visits increase your standing with Vic, eventually unlocking better rates and raising the daily cap - enough to bridge the gap to the mid-game without breaking the economy.

### Multiplayer Support
All features are fully multiplayer compatible with host-authoritative state sync. Quest progress, NPC interactions, and config settings synchronize across host and clients via Steam lobby data.

## Requirements & Installation

1.  Ensure you are on the **IL2CPP Branch** of *Schedule I*.
2.  Install **MelonLoader v0.7.0**.
3.  Install **S1API** (ifBars fork) — modding API layer.
4.  Install **SteamNetworkLib IL2CPP** — required for multiplayer sync.
5.  Download the latest `OverTheCounter.dll` from Releases.
6.  Drop the `.dll` file into your `Mods` folder.
7.  Launch the game.

> If installing via Thunderstore mod manager, S1API and SteamNetworkLib are installed automatically as dependencies.

## Configuration
All settings are managed through the **MelonLoader Mod Settings** interface in-game. Settings are organized into four categories:

* **Desperation System** — Fiend addiction threshold, trigger chance per hour, max daily events, response/delivery deadlines, bonus multiplier, relationship penalty, cooldown, and active hours.
* **Vic Laundering** — Tier costs, returns, trust unlock threshold, and intro quest requirements.
* **Static Subscription** — Weekly billing cost, cycle length, ATM deposit trigger, and tier upgrade costs/requirements.
* **Contract Notifications** — Consolidation threshold (minimum contracts before grouping kicks in).

In multiplayer, the host's settings are automatically synced to all clients (except local-only preferences like consolidation threshold).

## License
**Creative Commons Attribution-NonCommercial-ShareAlike 4.0 International (CC BY-NC-SA 4.0)**

You are free to:
* **Share** — copy and redistribute the material in any medium or format.
* **Adapt** — remix, transform, and build upon the material.

Under the following terms:
* **Attribution** — You must give appropriate credit to the original author (hdlmrell) and indicate if changes were made. You may not suggest the author endorses you or your use.
* **NonCommercial** — You may not use the material for commercial purposes.
* **ShareAlike** — If you remix, transform, or build upon the material, you must distribute your contributions under the same license as the original.

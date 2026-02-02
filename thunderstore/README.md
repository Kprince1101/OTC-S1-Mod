# OverTheCounter

**OverTheCounter** is a Quality of Life, Utility, and Balance overhaul for *Schedule I*. It focuses on streamlining logistics, modernizing the UI, and rebalancing the gameplay loop to make the day phase strategically viable.

> **IMPORTANT:** This mod is built for the **IL2CPP branch** of the game using **MelonLoader 0.7.0**.
> It is **NOT compatible with the Mono branch**. Do not attempt to use this on Mono builds.

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

## Installation

1. Ensure you are on the **IL2CPP Branch** of *Schedule I*.
2. Install **MelonLoader v0.7.0**.
3. Install **S1API** and **SteamNetworkLib** (listed as dependencies — installed automatically by the mod manager).
4. Drop `OverTheCounter.dll` into your `Mods` folder (or install via Thunderstore mod manager).
5. Launch the game.

## Configuration
A `Config.cfg` is generated upon first launch to tweak the Desperation balance:
* `HourlyChance`: Probability of a Desperation event triggering (Default: 0.12).
* `MaxDailyEvents`: Hard cap on daily stress events (Default: 3).

## License
**Creative Commons Attribution-NonCommercial-ShareAlike 4.0 International (CC BY-NC-SA 4.0)**

You are free to share and adapt this work under the following terms: Attribution, NonCommercial, and ShareAlike. See [CC BY-NC-SA 4.0](https://creativecommons.org/licenses/by-nc-sa/4.0/) for full details.

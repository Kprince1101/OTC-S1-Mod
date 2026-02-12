# OverTheCounter for Schedule I

**OverTheCounter** is a Quality of Life, Utility, and Balance overhaul for *Schedule I*. It focuses on streamlining logistics, modernizing the UI, and rebalancing the gameplay loop to make the day phase strategically viable.

> **COMPATIBILITY NOTICE:**
> This mod is built for the **IL2CPP branch** of the game using **MelonLoader 0.7.0**.
> It utilizes Harmony patching that is **NOT compatible with the Mono branch**.
> Do not attempt to use this on Mono builds; it will crash or fail to load.

# Features

## 1. The Manager Update (Endgame Automation)
*Stop running errands. Start running an empire.*
A complete, simulation-based automation system built on **S1API** for maximum compatibility (works seamlessly alongside **k0Mods**).

* **True Logistics:** Hire physical **Manager NPCs** at your Laundromat, Post Office, Car Wash, or Taco Ticklers.
* **Supply Routes:** Managers automatically physically visit stores to keep your shelves stocked with essential ingredients (fertilizer, chemicals, etc.).
* **Distribution Routes:** Assign up to **3 custom logic routes** per manager. They move product from Container A to Container B, allowing you to chain storage containers across the map.
* **The Cost of Business:** Managers cost **$500/day** (paid from their locker's petty cash) and report their status via a daily summary text.
* **"Executive Privilege" Quest:** A new endgame questline. Prove your worth to the **Night Market Boss** in the downtown high-rises by crafting high-value Weed ($200+), Meth ($400+), and Cocaine ($800+) mixes. Success unlocks 24/7 Night Market access for your automation network.

## 2. Smart Logistics UI (Quality of Life)
*No more mental math. No more clicking back and forth.*

* **The Contract Aggregator:** Merges all active orders into a single "Pending Deliveries" list, grouped by time window. See exactly what you need for the next run at a glance.
* **Live Stash Manifest:** When you open a container, a side panel instantly calculates the total product required for your active contracts versus what you are holding.
* **Smart Fill Button:** One-click transfer that automatically pulls the exact amount of product needed from the container to your inventory, prioritizing jars over baggies to save space.

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
    * **Subscription:** Failure to pay the $1,000/month server rent disables the app.
* **Early Game Laundering:**
    * **Meet Vic:** A corrupt associate who offers manual cash laundering once you hit the $10k weekly ATM limit.
    * **Risk vs. Reward:** Pay a 15-20% fee to clean dirty cash early, bridging the gap between street dealing and owning legitimate businesses.

## Multiplayer Support
Built from the ground up for co-op.
* **Host-Authoritative:** Quest progress, Manager routes, and OTC subscriptions sync flawlessly between host and clients.

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
Settings are stored in MelonLoader's config file and organized into four categories:

* **Desperation System** — Fiend addiction threshold, trigger chance per hour, max daily events, response/delivery deadlines, bonus multiplier, relationship penalty, cooldown, and active hours.
* **Vic Laundering** — Tier costs, returns, trust unlock threshold, and intro quest requirements.
* **Static Subscription** — Weekly billing cost, cycle length, ATM deposit trigger, and tier upgrade costs/requirements.
* **Contract Notifications** — Consolidation threshold (minimum contracts before grouping kicks in).
* **Drifter System** — Spawn chance per hour, max active drifters, active hours, offer window, delivery deadline, linger duration, and minimum deal value.

You can edit the config file directly, or use [ModsApp by k0Mods](https://thunderstore.io/c/schedule-i/p/k0Mods/ModsApp/) for an in-game settings UI (optional, not required).

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

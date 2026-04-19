# ZombieMonsters

**Oxide/uMod Plugin for Rust** — Spawns Scarecrow NPCs in configurable PvP zones, with Purge event buffs and MonumentEvents integration.

![Version](https://img.shields.io/badge/version-2.3.8-blue?style=flat-square)
![Rust](https://img.shields.io/badge/game-Rust-orange?style=flat-square)
![Oxide](https://img.shields.io/badge/framework-Oxide%2FuMod-green?style=flat-square)
[![Discord](https://img.shields.io/badge/Discord-GameZoneOne-5865F2?style=flat-square&logo=discord&logoColor=white)](https://discord.gg/dx2q8wNM9U)

---

## Screenshots

> Replace with actual screenshots — upload to `screenshots/` in the repo and update the paths below.

| NPCs at monument | Purge event |
|---|---|
| ![NPCs at monument](https://placehold.co/480x270/0d1117/4d9375?text=NPCs+at+Monument) | ![Purge event](https://placehold.co/480x270/0d1117/4d9375?text=Purge+Event) |

---

## Features

- Spawns **Scarecrow NPCs** at defined spawn zones on a configurable interval
- **Monument-aware** — ties into MonumentEvents to activate/deactivate zones per event
- **Purge integration** — optionally boosts zombie count/behavior during Purge events
- Smart **exclusion zones** — no spawns near TC (building privilege), sleeping bags, or too close to players
- Automatic **distance-based despawn** when no players are nearby
- Configurable per-monument spawn zones with individual settings

## Dependencies

| Plugin | Required | Link |
|---|---|---|
| [Purge](https://umod.org/plugins/purge) | Optional | Enables Purge-mode buffs |
| [MonumentEvents](https://umod.org/plugins/monument-events) | Optional | Zone activation per event |

## Installation

1. Copy `ZombieMonsters.cs` into your `oxide/plugins/` folder
2. *(Optional)* Install Purge and/or MonumentEvents for full feature support
3. Use `/zm` (admin) to manage spawn zones in-game

## Permissions

| Permission | Description |
|---|---|
| `zombiemonsters.admin` | Access to admin commands |

## Supported Monuments

Airfield, Ferry Terminal, Harbor (Large/Small), Junkyard, Launch Site, Power Plant, Radtown, Satellite Dish, Sewer Branch, Supermarket, The Dome, Train Yard, Water Treatment Plant, and more.

## Author

Made by **[GameZoneOne](https://discord.gg/dx2q8wNM9U)**  
📧 info@gamezoneone.de

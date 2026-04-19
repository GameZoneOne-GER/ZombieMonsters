using System;
using System.Collections.Generic;
using UnityEngine;
using Oxide.Core;
using Oxide.Core.Plugins;
using Newtonsoft.Json;

namespace Oxide.Plugins
{
    [Info("Zombie Monsters", "gamezoneone", "2.3.8")]
    [Description("Spawns Scarecrow NPCs in PvP zones with configurable despawn, lifetime and optional Purge event buffs.")]
    public class ZombieMonsters : RustPlugin
    {
        [PluginReference] Plugin Purge;
        [PluginReference] Plugin MonumentEvents;
        private const string PermAdmin = "zombiemonsters.admin";

        /// <summary>Nur Scarecrow — mehrere Kandidaten, falls sich Prefab-Pfade je Build unterscheiden.</summary>
        private static readonly string[] ScarecrowPrefabCandidates =
        {
            "assets/prefabs/npc/scarecrow/scarecrow.prefab",
            "assets/rust.ai/agents/npcplayer/humannpc/scarecrow/scarecrow.prefab"
        };

        /// <summary>Standard-Nahkampf nach komplettem Strip (kein Vanilla-Granaten-/Kit-Zeug).</summary>
        private const string ZombieMeleeShortname = "bone.club";

        private readonly Dictionary<string, SpawnZone> SpawnPoints = new Dictionary<string, SpawnZone>();
        private readonly List<BaseCombatEntity> ActiveZombies = new List<BaseCombatEntity>();
        private readonly HashSet<string> ActiveMonumentEvents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Vector3> MonumentPositions = new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, string> MonumentDisplayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["radtown_1"] = "Radtown",
            ["gas_station_1"] = "Oxum's Gas Station",
            ["supermarket_1"] = "Abandoned Supermarket",
            ["water_treatment_plant_1"] = "Water Treatment Plant",
            ["powerplant_1"] = "Power Plant",
            ["ferry_terminal_1"] = "Ferry Terminal",
            ["harbor_2"] = "Small Harbor",
            ["harbor_1"] = "Large Harbor",
            ["junkyard_1"] = "Junkyard",
            ["sphere_tank"] = "The Dome",
            ["airfield_1"] = "Airfield",
            ["trainyard_1"] = "Train Yard",
            ["satellite_dish"] = "Satellite Dish",
            ["radtown_small_3"] = "Sewer Branch",
            ["launch_site_1"] = "Launch Site"
        };

        private float SpawnInterval = 75f;
        private int ZombiesPerPoint = 2;

        private float ScarecrowLifetime = 480f;

        private float DespawnDistance = 60f;
        private float MinAliveBeforeDistanceDespawn = 90f;

        /// <summary>Keine Spawns im TC-Umkreis (Bauten / Compound).</summary>
        private const float CupboardExclusionRadius = 48f;

        /// <summary>Keine Spawns auf Schlafsack/Bett (Respawn).</summary>
        private const float SleepingBagExclusionRadius = 20f;

        /// <summary>Mindestabstand zum auslösenden Spieler (verhindert Spawns in Base am Zonenrand).</summary>
        private const float MinSpawnDistanceFromPlayer = 22f;

        private Timer spawnTimer;

        private string T(string key, string userId = null) => lang.GetMessage(key, this, userId);

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NoPermission"] = "You don't have permission to use this command.",
                ["ZombiesKilled"] = "All zombies have been removed.",
                ["WaveTriggered"] = "Zombie wave triggered manually.",
                ["SpawnSuccess"] = "Spawned {0} Scarecrow(s) near you.",
                ["SpawnFailed"] = "Spawn failed — check server log (prefab).",
            }, this);
        }

        void Init()
        {
            permission.RegisterPermission(PermAdmin, this);
        }

        // ---------------------------------------------------------
        // INITIALISIERUNG
        // ---------------------------------------------------------
        void OnServerInitialized()
        {
            ConsoleSystem.Run(ConsoleSystem.Option.Server, "zombie.population 0");

            FindAllSpawnPoints();
            BuildMonumentPositionCache();
            RefreshActiveMonumentEvents();
            StartZombieLoop();
            timer.Once(10f, SpawnWaveNearPlayers);
        }

        void Unload()
        {
            spawnTimer?.Destroy();
            KillAllZombies();
        }

        // ---------------------------------------------------------
        // SPAWNPOINTS FINDEN
        // ---------------------------------------------------------
        private void FindAllSpawnPoints()
        {
            SpawnPoints.Clear();
            LoadPvpZonesFromZoneManager();

            Puts($"[ZombieMonsters] Found {SpawnPoints.Count} spawn points.");
        }

        private void AddSpawnPoint(string key, Vector3 pos, float radius)
        {
            if (!SpawnPoints.ContainsKey(key))
                SpawnPoints[key] = new SpawnZone { Id = key, Position = pos, Radius = Mathf.Max(1f, radius) };
        }

        private void LoadPvpZonesFromZoneManager()
        {
            try
            {
                var data = Interface.Oxide.DataFileSystem.ReadObject<ZoneDataFile>("ZoneManager/zone_data");
                if (data?.Definitions == null || data.Definitions.Count == 0)
                {
                    PrintWarning("[ZombieMonsters] Keine ZoneManager-Definitionen gefunden.");
                    return;
                }

                foreach (var zone in data.Definitions)
                {
                    if (!IsPvpZone(zone)) continue;
                    if (zone.Radius <= 0f) continue;
                    if (!TryParseVector3(zone.Location, out Vector3 pos)) continue;

                    AddSpawnPoint(zone.Id ?? $"zone_{SpawnPoints.Count}", pos, zone.Radius);
                }
            }
            catch (Exception ex)
            {
                PrintWarning($"[ZombieMonsters] Konnte ZoneManager/zone_data nicht laden: {ex.Message}");
            }
        }

        private static bool IsPvpZone(ZoneDefinition zone)
        {
            if (zone == null) return false;
            string id = (zone.Id ?? string.Empty).ToLowerInvariant();
            string name = (zone.Name ?? string.Empty).ToLowerInvariant();
            string enter = (zone.EnterMessage ?? string.Empty).ToLowerInvariant();
            // Id/Name: z. B. airfield_pvp. EnterMessage: früher nur "pvp zone" (Leerzeichen) —
            // deutsche Texte nutzen "PvP-Zone" → lower "pvp-zone", daher allgemein "pvp" in der Nachricht.
            return id.Contains("pvp") || name.Contains("pvp") || enter.Contains("pvp");
        }

        private static bool TryParseVector3(string input, out Vector3 result)
        {
            result = Vector3.zero;
            if (string.IsNullOrWhiteSpace(input)) return false;

            string[] parts = input.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3) return false;

            if (!float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x)) return false;
            if (!float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float y)) return false;
            if (!float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float z)) return false;

            result = new Vector3(x, y, z);
            return true;
        }

        private class SpawnZone
        {
            public string Id;
            public Vector3 Position;
            public float Radius;
        }

        // ---------------------------------------------------------
        // SPAWN LOOP
        // ---------------------------------------------------------
        private void StartZombieLoop()
        {
            spawnTimer = timer.Every(SpawnInterval, () =>
            {
                SpawnWaveNearPlayers();
                CleanupZombies();
            });
        }

        private void SpawnWaveNearPlayers()
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player == null || !player.IsConnected || player.IsDead())
                    continue;

                if (!TryGetPvpZoneForPlayer(player, out var zone))
                    continue;

                if (ShouldBlockSpawnForMomentumEvent(player.transform.position, zone))
                    continue;

                // Spawns an zufaelligen Punkten IN der PvP-Zone (Zentrum + Radius), nicht um die Spielerposition.
                // Sonst landen Zombies in Basen / auf Bags, wenn die Zone die Base nur streift.
                SpawnWaveForPlayerInZone(player, zone);
            }
        }

        private bool ShouldBlockSpawnForMomentumEvent(Vector3 playerPos, SpawnZone zone)
        {
            if (zone == null || ActiveMonumentEvents.Count == 0)
                return false;

            foreach (string monumentId in ActiveMonumentEvents)
            {
                if (ZoneIdMatchesMonument(zone.Id, monumentId))
                    return true;

                if (MonumentPositions.TryGetValue(monumentId, out Vector3 monumentPos))
                {
                    float zoneToMonument = Vector3.Distance(zone.Position, monumentPos);
                    if (zoneToMonument <= zone.Radius + 80f)
                        return true;

                    float playerToMonument = Vector3.Distance(playerPos, monumentPos);
                    if (playerToMonument <= 120f)
                        return true;
                }
            }

            return false;
        }

        private static bool ZoneIdMatchesMonument(string zoneId, string monumentId)
        {
            if (string.IsNullOrWhiteSpace(zoneId) || string.IsNullOrWhiteSpace(monumentId))
                return false;

            if (zoneId.IndexOf(monumentId, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            string baseId = TrimNumericSuffix(monumentId);
            return !string.IsNullOrEmpty(baseId) &&
                   zoneId.IndexOf(baseId, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string TrimNumericSuffix(string monumentId)
        {
            int lastUnderscore = monumentId.LastIndexOf('_');
            if (lastUnderscore <= 0 || lastUnderscore >= monumentId.Length - 1)
                return monumentId;

            string suffix = monumentId.Substring(lastUnderscore + 1);
            if (int.TryParse(suffix, out _))
                return monumentId.Substring(0, lastUnderscore);

            return monumentId;
        }

        private void BuildMonumentPositionCache()
        {
            MonumentPositions.Clear();

            List<MonumentInfo> monuments = TerrainMeta.Path?.Monuments;
            if (monuments == null || monuments.Count == 0)
                return;

            foreach (var pair in MonumentDisplayNames)
            {
                MonumentInfo match = FindMonumentByDisplayName(monuments, pair.Value);
                if (match != null)
                    MonumentPositions[pair.Key] = match.transform.position;
            }
        }

        private static MonumentInfo FindMonumentByDisplayName(List<MonumentInfo> monuments, string targetName)
        {
            if (string.IsNullOrWhiteSpace(targetName))
                return null;

            foreach (MonumentInfo monument in monuments)
            {
                string englishName = monument?.displayPhrase?.english;
                if (string.IsNullOrWhiteSpace(englishName))
                    continue;

                if (englishName.Equals(targetName, StringComparison.OrdinalIgnoreCase))
                    return monument;
            }

            foreach (MonumentInfo monument in monuments)
            {
                string englishName = monument?.displayPhrase?.english;
                if (string.IsNullOrWhiteSpace(englishName))
                    continue;

                if (englishName.IndexOf(targetName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    targetName.IndexOf(englishName, StringComparison.OrdinalIgnoreCase) >= 0)
                    return monument;
            }

            return null;
        }

        private void RefreshActiveMonumentEvents()
        {
            ActiveMonumentEvents.Clear();
            if (MonumentEvents == null)
                return;

            foreach (string monumentId in MonumentDisplayNames.Keys)
            {
                object result = MonumentEvents.Call("IsMonumentEventsRunning", monumentId);
                if (result is bool isRunning && isRunning)
                    ActiveMonumentEvents.Add(monumentId);
            }
        }

        private void OnMEStarted(string monumentId)
        {
            if (!string.IsNullOrWhiteSpace(monumentId))
                ActiveMonumentEvents.Add(monumentId);
        }

        private void OnMEStop(string monumentId)
        {
            if (!string.IsNullOrWhiteSpace(monumentId))
                ActiveMonumentEvents.Remove(monumentId);
        }

        private bool TryGetPvpZoneForPlayer(BasePlayer player, out SpawnZone zone)
        {
            zone = null;
            if (player == null) return false;

            Vector3 pos = player.transform.position;
            float bestDist = float.MaxValue;

            foreach (var entry in SpawnPoints)
            {
                var candidate = entry.Value;
                float dist = Vector3.Distance(pos, candidate.Position);
                if (dist > candidate.Radius + 12f)
                    continue;

                if (dist < bestDist)
                {
                    bestDist = dist;
                    zone = candidate;
                }
            }

            return zone != null;
        }

        /// <summary>Wave-Spawns nur in der Zone, mit Abstand zum Spieler und ohne TC/Bags.</summary>
        private int SpawnWaveForPlayerInZone(BasePlayer player, SpawnZone zone)
        {
            if (GameManager.server == null || TerrainMeta.HeightMap == null)
                return 0;
            if (player == null || zone == null)
                return 0;

            int spawned = 0;
            int count = CountZombiesNear(zone.Position);
            int max = IsPurgeActive() ? ZombiesPerPoint * 2 : ZombiesPerPoint;

            if (count >= max)
                return 0;

            int toSpawn = max - count;
            Vector3 playerPos = player.transform.position;

            for (int i = 0; i < toSpawn; i++)
            {
                if (!TryPickRandomSpawnInPvpZone(zone, playerPos, out Vector3 flatXZ))
                    continue;

                if (TrySpawnOneScarecrow(flatXZ, true, out _, out Vector3 spawnPos))
                {
                    Puts($"[ZombieMonsters] Wave-Spawn @ {spawnPos}");
                    spawned++;
                }
            }

            return spawned;
        }

        /// <summary>Zufaelliger Punkt in der Zone (XZ), mit Mindestabstand zum Spieler und ohne TC/SleepingBag.</summary>
        private bool TryPickRandomSpawnInPvpZone(SpawnZone zone, Vector3 playerPos, out Vector3 flatXZ)
        {
            flatXZ = default;
            float maxRad = Mathf.Max(8f, zone.Radius * 0.9f);
            float minFromPlayer = Mathf.Min(MinSpawnDistanceFromPlayer, zone.Radius * 0.45f);
            minFromPlayer = Mathf.Clamp(minFromPlayer, 10f, MinSpawnDistanceFromPlayer);

            for (int attempt = 0; attempt < 40; attempt++)
            {
                float ang = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                float rad = UnityEngine.Random.Range(6f, maxRad);
                Vector3 p = zone.Position + new Vector3(Mathf.Cos(ang) * rad, 0f, Mathf.Sin(ang) * rad);

                if (Vector3.Distance(p, zone.Position) > zone.Radius + 0.5f)
                    continue;

                Vector3 playerFlat = new Vector3(playerPos.x, 0f, playerPos.z);
                Vector3 pFlat = new Vector3(p.x, 0f, p.z);
                if (Vector3.Distance(pFlat, playerFlat) < minFromPlayer)
                    continue;

                if (IsExcludedZombieSpawnArea(p))
                    continue;

                flatXZ = p;
                return true;
            }

            return false;
        }

        private bool IsExcludedZombieSpawnArea(Vector3 flatXZ)
        {
            if (TerrainMeta.HeightMap == null)
                return true;

            float terrainY = TerrainMeta.HeightMap.GetHeight(flatXZ.x, flatXZ.z);
            float waterY = TerrainMeta.WaterMap != null ? TerrainMeta.WaterMap.GetHeight(flatXZ) : float.MinValue;
            float y = Mathf.Max(terrainY, waterY, flatXZ.y);
            var sample = new Vector3(flatXZ.x, y + 1f, flatXZ.z);

            var privs = new List<BuildingPrivlidge>();
            Vis.Entities(sample, CupboardExclusionRadius, privs);
            if (privs.Count > 0)
                return true;

            var bags = new List<SleepingBag>();
            Vis.Entities(sample, SleepingBagExclusionRadius, bags);
            return bags.Count > 0;
        }

        // ---------------------------------------------------------
        // SPAWNING (mit Bodenhöhen-Fix)
        // ---------------------------------------------------------
        /// <param name="adminForceMinimumOne">Wenn true: mindestens ein Scarecrow direkt vor dem Spieler, unabhängig vom Zähler-Limit (Admin-/Test-Spawn).</param>
        private int SpawnZombiesAt(Vector3 pos, bool adminForceMinimumOne, float spawnSpreadRadius = 20f)
        {
            if (GameManager.server == null || TerrainMeta.HeightMap == null)
                return 0;

            int spawned = 0;

            if (adminForceMinimumOne)
            {
                Vector3 near = pos + new Vector3(
                    UnityEngine.Random.Range(-2f, 2f),
                    0f,
                    UnityEngine.Random.Range(-2f, 2f)
                );
                if (TrySpawnOneScarecrow(near, false, out _, out Vector3 adminSpawnPos))
                {
                    Puts($"[ZombieMonsters] Admin-Spawn erfolgreich @ {adminSpawnPos}");
                    spawned++;
                }
                else
                    PrintWarning("[ZombieMonsters] Admin-Spawn: kein Scarecrow konnte erzeugt werden (Prefab/Server).");
            }

            int count = CountZombiesNear(pos);
            int max = IsPurgeActive() ? ZombiesPerPoint * 2 : ZombiesPerPoint;

            if (count >= max)
                return spawned;

            int toSpawn = max - count;

            for (int i = 0; i < toSpawn; i++)
            {
                Vector3 rawPos = pos + new Vector3(
                    UnityEngine.Random.Range(-spawnSpreadRadius, spawnSpreadRadius),
                    0,
                    UnityEngine.Random.Range(-spawnSpreadRadius, spawnSpreadRadius)
                );

                if (TrySpawnOneScarecrow(rawPos, true, out _, out Vector3 spawnPos))
                {
                    Puts($"[ZombieMonsters] Wave-Spawn @ {spawnPos}");
                    spawned++;
                }
            }

            return spawned;
        }

        /// <summary>Entspricht Murderers.cs: CreateEntity mit Quaternion + bool, sonst oft null.</summary>
        private bool TrySpawnOneScarecrow(Vector3 rawPos, bool useTerrainHeight, out BaseCombatEntity zombie, out Vector3 finalSpawnPos)
        {
            zombie = null;
            finalSpawnPos = rawPos;
            if (useTerrainHeight && TerrainMeta.HeightMap == null) return false;

            Vector3 spawnPos = rawPos;
            if (useTerrainHeight)
            {
                float terrainY = TerrainMeta.HeightMap.GetHeight(rawPos.x, rawPos.z);
                float waterY = TerrainMeta.WaterMap != null ? TerrainMeta.WaterMap.GetHeight(rawPos) : float.MinValue;
                // Schutz gegen unterirdische Punkte: nie unter Wasser-/Terrain-Niveau spawnen.
                float safeY = Mathf.Max(terrainY, waterY, rawPos.y);
                spawnPos = new Vector3(rawPos.x, safeY + 1f, rawPos.z);
            }

            foreach (var prefab in ScarecrowPrefabCandidates)
            {
                BaseEntity entity = GameManager.server.CreateEntity(prefab, spawnPos, Quaternion.identity, true);
                if (entity == null)
                    continue;

                var scarecrow = entity as ScarecrowNPC;
                if (scarecrow == null)
                {
                    entity.Kill();
                    continue;
                }

                scarecrow.Spawn();
                ConfigureScarecrow(scarecrow);
                ActiveZombies.Add(scarecrow);
                zombie = scarecrow;
                finalSpawnPos = scarecrow.transform.position;
                return true;
            }

            PrintWarning("[ZombieMonsters] Kein gueltiger Scarecrow-Prefab (alle Kandidaten fehlgeschlagen).");
            return false;
        }

        private class ZoneDataFile
        {
            [JsonProperty("definitions")]
            public List<ZoneDefinition> Definitions = new List<ZoneDefinition>();
        }

        private class ZoneDefinition
        {
            [JsonProperty("Id")]
            public string Id;

            [JsonProperty("Name")]
            public string Name;

            [JsonProperty("Radius")]
            public float Radius;

            [JsonProperty("Location")]
            public string Location;

            [JsonProperty("EnterMessage")]
            public string EnterMessage;
        }

        private void ConfigureScarecrow(BaseCombatEntity zombie)
        {
            bool purge = IsPurgeActive();

            const float baseHealth = 120f;
            float purgeBonus = purge ? 1.5f : 1f;

            zombie.SetMaxHealth(baseHealth * purgeBonus);
            zombie.SetHealth(baseHealth * purgeBonus);

            if (zombie is BaseNpc npc)
            {
                npc.Stats.Speed = purge ? 1.1f : 0.75f;
                npc.Stats.VisionRange = 22f;
            }

            // Kein aggressives NavMesh-Repositioning:
            // PlaceOnNavMesh(0) hat in einigen Fällen NPCs unter die Map verschoben.

            if (zombie is ScarecrowNPC scare)
            {
                // Inventar ist oft erst nach Spawn vollständig — ein Tick später bereinigen.
                // Kein inventory.Strip(): kann Scarecrow/HumanNPC in manchen Builds instabil machen.
                var captured = scare;
                NextTick(() => ApplyZombieLoadout(captured));
            }

            zombie.gameObject.AddComponent<ZombieLifetime>().Init(
                ScarecrowLifetime,
                DespawnDistance,
                MinAliveBeforeDistanceDespawn
            );
        }

        private void ApplyZombieLoadout(ScarecrowNPC npc)
        {
            if (npc == null || npc.IsDestroyed) return;

            try
            {
                var inv = npc.inventory;
                if (inv == null) return;

                // Granaten/Waffen: typischerweise Gürtel + Rucksack. Kleidung (Wear) bleibt für Optik.
                inv.containerBelt?.Clear();
                inv.containerMain?.Clear();

                var melee = ItemManager.CreateByName(ZombieMeleeShortname, 1, 0UL);
                if (melee != null && !inv.GiveItem(melee))
                    melee.Remove();

                npc.SendNetworkUpdate();
            }
            catch (Exception ex)
            {
                PrintWarning($"[ZombieMonsters] Loadout konnte nicht gesetzt werden: {ex.Message}");
            }
        }

        // ---------------------------------------------------------
        // CLEANUP
        // ---------------------------------------------------------
        private void CleanupZombies()
        {
            for (int i = ActiveZombies.Count - 1; i >= 0; i--)
            {
                BaseCombatEntity zombie = ActiveZombies[i];

                if (zombie == null || zombie.IsDestroyed)
                {
                    ActiveZombies.RemoveAt(i);
                    continue;
                }
            }
        }

        private int CountZombiesNear(Vector3 pos)
        {
            int count = 0;

            foreach (var zombie in ActiveZombies)
            {
                if (zombie != null && !zombie.IsDestroyed && Vector3.Distance(zombie.transform.position, pos) < 40f)
                    count++;
            }

            return count;
        }

        // ---------------------------------------------------------
        // PURGE SUPPORT
        // ---------------------------------------------------------
        private bool IsPurgeActive()
        {
            if (Purge == null)
                return false;

            object result = Purge.Call("IsPurgeActive");
            return result is bool b && b;
        }

        // ---------------------------------------------------------
        // ADMIN COMMANDS
        // ---------------------------------------------------------
        private bool HasAdminAccess(BasePlayer player)
        {
            return player != null && (player.IsAdmin || permission.UserHasPermission(player.UserIDString, PermAdmin));
        }

        [ChatCommand("killzombies")]
        private void CmdKillZombies(BasePlayer player, string command, string[] args)
        {
            if (!HasAdminAccess(player))
            {
                player.ChatMessage(T("NoPermission", player.UserIDString));
                return;
            }

            KillAllZombies();
            player.ChatMessage(T("ZombiesKilled", player.UserIDString));
        }

        [ChatCommand("zspawnhere")]
        private void CmdSpawnHere(BasePlayer player, string command, string[] args)
        {
            if (!HasAdminAccess(player))
            {
                player.ChatMessage(T("NoPermission", player.UserIDString));
                return;
            }

            Vector3 front = player.transform.position + player.eyes.HeadForward() * 1.8f + Vector3.up * 0.15f;
            int n = 0;

            if (TrySpawnOneScarecrow(front, false, out _, out Vector3 spawnedPos))
            {
                n = 1;
                Puts($"[ZombieMonsters] /zspawnhere by {player.displayName}: spawn @ {spawnedPos}");
            }
            else
            {
                n = SpawnZombiesAt(player.transform.position, true, 22f);
            }

            player.ChatMessage(n > 0
                ? string.Format(T("SpawnSuccess", player.UserIDString), n)
                : T("SpawnFailed", player.UserIDString));
        }

        [ChatCommand("zwave")]
        private void CmdWave(BasePlayer player, string command, string[] args)
        {
            if (!HasAdminAccess(player))
            {
                player.ChatMessage(T("NoPermission", player.UserIDString));
                return;
            }

            SpawnWaveNearPlayers();
            CleanupZombies();
            player.ChatMessage(T("WaveTriggered", player.UserIDString));
        }

        [ConsoleCommand("zombies.killall")]
        private void ConKillAll(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null)
            {
                var player = arg.Player();
                if (!HasAdminAccess(player)) return;
            }

            KillAllZombies();
            Puts("[ZombieMonsters] Alle Zombies wurden per Konsole entfernt.");
        }

        [ConsoleCommand("zombies.spawnhere")]
        private void ConSpawnHere(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (!HasAdminAccess(player))
            {
                Puts("[ZombieMonsters] zombies.spawnhere nur als Admin-Player nutzbar.");
                return;
            }

            Vector3 front = player.transform.position + player.eyes.HeadForward() * 1.8f + Vector3.up * 0.15f;
            int n = 0;

            if (TrySpawnOneScarecrow(front, false, out _, out Vector3 spawnedPos))
            {
                n = 1;
                Puts($"[ZombieMonsters] zombies.spawnhere von {player.displayName}: Spawn @ {spawnedPos}");
            }
            else
            {
                // Fallback falls der direkte Spawnpunkt ungeeignet ist.
                n = SpawnZombiesAt(player.transform.position, true, 22f);
            }

            Puts($"[ZombieMonsters] Spawn an Position von {player.displayName}: {n} Scarecrow(s).");
        }

        [ConsoleCommand("zombies.wave")]
        private void ConWave(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null)
            {
                var player = arg.Player();
                if (!HasAdminAccess(player)) return;
            }

            SpawnWaveNearPlayers();
            CleanupZombies();
            Puts("[ZombieMonsters] Zombie-Wave wurde per Konsole ausgelöst.");
        }

        // ---------------------------------------------------------
        // ADMIN CONSOLE ALIASES (für Admin-Tools/Panel)
        // ---------------------------------------------------------
        [ConsoleCommand("zadmin.spawnhere")]
        private void ConAdminSpawnHere(ConsoleSystem.Arg arg) => ConSpawnHere(arg);

        [ConsoleCommand("zadmin.wave")]
        private void ConAdminWave(ConsoleSystem.Arg arg) => ConWave(arg);

        [ConsoleCommand("zadmin.killall")]
        private void ConAdminKillAll(ConsoleSystem.Arg arg) => ConKillAll(arg);

        private void KillAllZombies()
        {
            foreach (var npc in ActiveZombies)
            {
                if (npc != null && !npc.IsDestroyed)
                    npc.Kill();
            }

            ActiveZombies.Clear();
        }

        // ---------------------------------------------------------
        // ZOMBIE LIFETIME COMPONENT
        // ---------------------------------------------------------
        private class ZombieLifetime : MonoBehaviour
        {
            private BaseCombatEntity npc;
            private float lifetime;
            private float despawnDistance;
            private float minAliveBeforeDistanceDespawn;
            private float spawnTime;

            public void Init(float lifetime, float despawnDistance, float minAliveBeforeDistanceDespawn)
            {
                this.npc = GetComponent<BaseCombatEntity>();
                this.lifetime = lifetime;
                this.despawnDistance = despawnDistance;
                this.minAliveBeforeDistanceDespawn = minAliveBeforeDistanceDespawn;
                this.spawnTime = Time.realtimeSinceStartup;
            }

            void Update()
            {
                if (npc == null || npc.IsDestroyed)
                {
                    Destroy(this);
                    return;
                }

                if (Time.realtimeSinceStartup - spawnTime > lifetime)
                {
                    npc.Kill();
                    Destroy(this);
                    return;
                }
                
                float aliveFor = Time.realtimeSinceStartup - spawnTime;
                if (aliveFor < minAliveBeforeDistanceDespawn)
                    return;

                if (BasePlayer.activePlayerList == null || BasePlayer.activePlayerList.Count == 0)
                    return;

                foreach (var player in BasePlayer.activePlayerList)
                {
                    if (player == null || !player.IsConnected)
                        continue;
                    if (Vector3.Distance(player.transform.position, npc.transform.position) < despawnDistance)
                        return;
                }

                npc.Kill();
                Destroy(this);
            }
        }
    }
}

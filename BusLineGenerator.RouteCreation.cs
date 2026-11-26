using System;
using System.Collections.Generic;
using Colossal.Logging;
using Colossal.Mathematics;
using Game;
using Game.Common;
using Game.Net;
using Game.Notifications;
using Game.Objects;
using Game.Pathfind;
using Game.Prefabs;
using Game.Rendering;
using Game.Routes;
using Game.Tools;
using Game.Vehicles;
using Unity.Entities;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Edge = Game.Net.Edge;

namespace AutoBusLines
{
    public partial class BusLineGenerator
    {
        // ==========================================================================
        // Route Colors & Palette Systems
        // ==========================================================================
        // High-visibility, vibrant modern transit palette designed for crisp clarity on 3D terrain and UI
        public static readonly string[] VibrantTransitColors = new string[]
        {
            "#007AFF", // Electric Metro Blue
            "#FF5500", // Bright Safety Orange
            "#00C853", // Vivid Emerald Green
            "#9D00FF", // Neon Violet / Purple
            "#FF0055", // Hot Raspberry / Crimson
            "#00C4D6", // Electric Cyan / Aqua
            "#FFB800", // Vivid Amber Gold
            "#FF1493", // Deep Pink / Fuchsia
            "#00E676", // Bright Spring Green
            "#536DFE", // Royal Indigo Blue
            "#FF3D00", // Flame Vermilion
            "#00B0FF", // Vivid Sky Blue
            "#AA00FF", // Bright Purple
            "#76FF03", // Vivid Lime Green
            "#FF6E40", // Vivid Tangerine
            "#1DE9B6", // Turquoise Teal
        };

        // Curated anchor hues for station hubs, ensuring distinct, vibrant color families
        private static readonly float[] s_StationAnchorHues = new float[]
        {
            0.58f, // Station 0: Electric Metro Blue
            0.38f, // Station 1: Vivid Emerald Green
            0.07f, // Station 2: Warm Tangelo Orange
            0.76f, // Station 3: Electric Violet / Purple
            0.98f, // Station 4: Bright Crimson Red
            0.50f, // Station 5: Bright Cyan / Aqua
            0.12f, // Station 6: Golden Amber
            0.88f, // Station 7: Hot Pink / Fuchsia
            0.24f, // Station 8: Vivid Lime Green
            0.67f, // Station 9: Royal Indigo
        };

        public static string GetLineHexColor(int lineNumber)
        {
            if (lineNumber >= 1 && lineNumber <= VibrantTransitColors.Length)
            {
                return VibrantTransitColors[lineNumber - 1];
            }
            float lineHue = ((lineNumber - 1) * 0.618033988749895f) % 1.0f;
            Color32 c = HSVToRGB(lineHue, 0.92f, 0.98f);
            return $"#{c.r:X2}{c.g:X2}{c.b:X2}";
        }

        public static Color32 HexToColor(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return new Color32(255, 0, 0, 255);
            hex = hex.TrimStart('#');
            if (hex.Length == 6 &&
                byte.TryParse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber, null, out byte r) &&
                byte.TryParse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out byte g) &&
                byte.TryParse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out byte b))
            {
                return new Color32(r, g, b, 255);
            }
            return new Color32(255, 0, 0, 255);
        }

        public static List<string> ComputeTourColors(List<List<PlacedStop>> tours, LineColorMode mode)
        {
            var hexColors = new List<string>(tours.Count);

            if (mode == LineColorMode.Random)
            {
                for (int t = 0; t < tours.Count; t++)
                {
                    if (t < VibrantTransitColors.Length)
                    {
                        hexColors.Add(VibrantTransitColors[t]);
                    }
                    else
                    {
                        float lineHue = ((t * 0.618033988749895f) % 1.0f);
                        Color32 c = HSVToRGB(lineHue, 0.92f, 0.98f);
                        hexColors.Add($"#{c.r:X2}{c.g:X2}{c.b:X2}");
                    }
                }
                return hexColors;
            }

            // Per Station / Hub Mode:
            // 1. Routes connecting to an actual bus station or terminal share that station's color family.
            // 2. Regular street lines without a station hub are treated independently with unique vibrant transit colors.
            var hubToTours = new Dictionary<Entity, List<int>>();
            var distinctHubs = new List<Entity>();
            var nonHubTours = new List<int>();

            for (int t = 0; t < tours.Count; t++)
            {
                var tour = tours[t];
                Entity hub = Entity.Null;
                // Priority 1: Station platform bay
                for (int s = 0; s < tour.Count; s++)
                {
                    if (tour[s].IsStationBay && tour[s].HubEntity != Entity.Null)
                    {
                        hub = tour[s].HubEntity;
                        break;
                    }
                }
                // Priority 2: Any stop with HubEntity
                if (hub == Entity.Null)
                {
                    for (int s = 0; s < tour.Count; s++)
                    {
                        if (tour[s].HubEntity != Entity.Null)
                        {
                            hub = tour[s].HubEntity;
                            break;
                        }
                    }
                }

                if (hub != Entity.Null)
                {
                    if (!hubToTours.TryGetValue(hub, out var list))
                    {
                        list = new List<int>();
                        hubToTours[hub] = list;
                        distinctHubs.Add(hub);
                    }
                    list.Add(t);
                }
                else
                {
                    nonHubTours.Add(t);
                }
            }

            var tourColors = new string[tours.Count];

            // Assign cohesive, vibrant palettes to station hubs
            for (int h = 0; h < distinctHubs.Count; h++)
            {
                Entity hub = distinctHubs[h];
                var tourIndices = hubToTours[hub];
                int countInHub = tourIndices.Count;

                float hubBaseHue = s_StationAnchorHues[h % s_StationAnchorHues.Length];
                if (h >= s_StationAnchorHues.Length)
                {
                    hubBaseHue = (hubBaseHue + 0.618033988749895f * (h / s_StationAnchorHues.Length)) % 1.0f;
                }

                for (int k = 0; k < countInHub; k++)
                {
                    int tourIdx = tourIndices[k];
                    float hueOffset = 0f;
                    if (countInHub > 1)
                    {
                        float spread = math.min(0.12f, 0.035f * (countInHub - 1));
                        float tNorm = (float)k / (countInHub - 1);
                        hueOffset = (tNorm - 0.5f) * spread;
                    }

                    float lineHue = (hubBaseHue + hueOffset + 1.0f) % 1.0f;
                    // Maintain high saturation (0.88 - 0.96) and brightness (0.96 - 1.0) so lines never look dull
                    float saturation = (k % 2 == 0) ? 0.96f : 0.88f;
                    float value = ((k / 2) % 2 == 0) ? 1.0f : 0.96f;

                    Color32 c = HSVToRGB(lineHue, saturation, value);
                    tourColors[tourIdx] = $"#{c.r:X2}{c.g:X2}{c.b:X2}";
                }
            }

            // Assign distinct, punchy transit colors to street lines without a central station hub
            int colorOffset = distinctHubs.Count;
            for (int i = 0; i < nonHubTours.Count; i++)
            {
                int tourIdx = nonHubTours[i];
                int paletteIdx = colorOffset + i;
                if (paletteIdx < VibrantTransitColors.Length)
                {
                    tourColors[tourIdx] = VibrantTransitColors[paletteIdx];
                }
                else
                {
                    float lineHue = ((paletteIdx * 0.618033988749895f) % 1.0f);
                    Color32 c = HSVToRGB(lineHue, 0.92f, 0.98f);
                    tourColors[tourIdx] = $"#{c.r:X2}{c.g:X2}{c.b:X2}";
                }
            }

            hexColors.AddRange(tourColors);
            return hexColors;
        }

        public void UpdatePlanColors(LineColorMode mode)
        {
            if (_activePlan == null || _activePlan.Count == 0 || _plannedTours == null || _plannedTours.Count == 0)
                return;

            var tourColors = ComputeTourColors(_plannedTours, mode);
            for (int i = 0; i < _activePlan.Count && i < tourColors.Count; i++)
            {
                _activePlan[i].Color = tourColors[i];
            }

            AutoBusLinesUISystem.Instance?.UpdatePlan(_activePlan, _currentPlanSeed);
        }

        private static Color32 HSVToRGB(float h, float s, float v)
        {
            float r = 0, g = 0, b = 0;
            if (s == 0)
            {
                r = v; g = v; b = v;
            }
            else
            {
                float sectorPos = h * 6.0f;
                int sectorNumber = (int)math.floor(sectorPos);
                float fractionalSector = sectorPos - sectorNumber;
                float p = v * (1.0f - s);
                float q = v * (1.0f - (s * fractionalSector));
                float num = v * (1.0f - (s * (1.0f - fractionalSector)));

                switch (sectorNumber % 6)
                {
                    case 0: r = v; g = num; b = p; break;
                    case 1: r = q; g = v; b = p; break;
                    case 2: r = p; g = v; b = num; break;
                    case 3: r = p; g = q; b = v; break;
                    case 4: r = num; g = p; b = v; break;
                    case 5: r = v; g = p; b = q; break;
                }
            }

            return new Color32(
                (byte)math.clamp((int)(r * 255f), 0, 255),
                (byte)math.clamp((int)(g * 255f), 0, 255),
                (byte)math.clamp((int)(b * 255f), 0, 255),
                255
            );
        }

        // ==========================================================================
        // Maintenance, Deletion & Nudging Systems
        // ==========================================================================
        public void DeleteAllBusLinesAndStops()
        {
            int lineCount = 0;
            int waypointCount = 0;
            int segmentCount = 0;
            int vehicleCount = 0;

            // 1. Identify all bus line entities (TransportLine with Bus transport type)
            var lineQuery = GetEntityQuery(ComponentType.ReadOnly<Game.Routes.TransportLine>(), ComponentType.Exclude<Deleted>());
            using (var lines = lineQuery.ToEntityArray(Allocator.Temp))
            {
                var busLines = new List<Entity>();
                for (int i = 0; i < lines.Length; i++)
                {
                    var lineEntity = lines[i];
                    if (!EntityManager.Exists(lineEntity) || EntityManager.HasComponent<Deleted>(lineEntity))
                        continue;

                    if (EntityManager.HasComponent<PrefabRef>(lineEntity))
                    {
                        var prefabEntity = EntityManager.GetComponentData<PrefabRef>(lineEntity).m_Prefab;
                        if (EntityManager.HasComponent<TransportLineData>(prefabEntity))
                        {
                            var lineData = EntityManager.GetComponentData<TransportLineData>(prefabEntity);
                            if (lineData.m_TransportType == TransportType.Bus)
                            {
                                busLines.Add(lineEntity);
                            }
                        }
                    }
                }

                var busLinesSet = new HashSet<Entity>(busLines);
                var vehiclesToDelete = new HashSet<Entity>();

                // 2. Collect and safely delete all active buses and cancel pending requests on these lines.
                // Critical Fix: Leaving orphaned vehicles driving on deleted lines causes TransportCarAISystem
                // to query empty RouteWaypoint buffers or destroyed stops, causing an immediate Burst crash to desktop (CTD).
                for (int i = 0; i < busLines.Count; i++)
                {
                    var lineEntity = busLines[i];

                    // Check RouteVehicle buffer on line
                    if (EntityManager.HasBuffer<RouteVehicle>(lineEntity))
                    {
                        var routeVehicles = EntityManager.GetBuffer<RouteVehicle>(lineEntity);
                        for (int rv = 0; rv < routeVehicles.Length; rv++)
                        {
                            var veh = routeVehicles[rv].m_Vehicle;
                            if (veh != Entity.Null && EntityManager.Exists(veh) && !EntityManager.HasComponent<Deleted>(veh))
                            {
                                vehiclesToDelete.Add(veh);
                            }
                        }
                    }

                    // Cancel pending vehicle request
                    if (EntityManager.HasComponent<Game.Routes.TransportLine>(lineEntity))
                    {
                        var tLine = EntityManager.GetComponentData<Game.Routes.TransportLine>(lineEntity);
                        if (tLine.m_VehicleRequest != Entity.Null && EntityManager.Exists(tLine.m_VehicleRequest) && !EntityManager.HasComponent<Deleted>(tLine.m_VehicleRequest))
                        {
                            EntityManager.AddComponentData(tLine.m_VehicleRequest, default(Deleted));
                            EntityManager.AddComponentData(tLine.m_VehicleRequest, default(Updated));
                        }
                    }

                    // Cancel dispatched requests in buffer
                    if (EntityManager.HasBuffer<DispatchedRequest>(lineEntity))
                    {
                        var dispBuffer = EntityManager.GetBuffer<DispatchedRequest>(lineEntity);
                        for (int d = 0; d < dispBuffer.Length; d++)
                        {
                            var req = dispBuffer[d].m_VehicleRequest;
                            if (req != Entity.Null && EntityManager.Exists(req) && !EntityManager.HasComponent<Deleted>(req))
                            {
                                EntityManager.AddComponentData(req, default(Deleted));
                                EntityManager.AddComponentData(req, default(Updated));
                            }
                        }
                    }
                }

                // Global query for any vehicles whose CurrentRoute references one of our bus lines
                var vehicleQuery = GetEntityQuery(ComponentType.ReadOnly<CurrentRoute>(), ComponentType.Exclude<Deleted>());
                using (var worldVehicles = vehicleQuery.ToEntityArray(Allocator.Temp))
                {
                    for (int v = 0; v < worldVehicles.Length; v++)
                    {
                        var veh = worldVehicles[v];
                        if (!EntityManager.Exists(veh) || EntityManager.HasComponent<Deleted>(veh))
                            continue;
                        var cr = EntityManager.GetComponentData<CurrentRoute>(veh);
                        if (busLinesSet.Contains(cr.m_Route))
                        {
                            vehiclesToDelete.Add(veh);
                        }
                    }
                }

                // Safely delete each vehicle and all its trailer/articulated parts (LayoutElement)
                foreach (var veh in vehiclesToDelete)
                {
                    if (!EntityManager.Exists(veh) || EntityManager.HasComponent<Deleted>(veh))
                        continue;

                    if (EntityManager.HasBuffer<LayoutElement>(veh))
                    {
                        var layout = EntityManager.GetBuffer<LayoutElement>(veh);
                        for (int l = 0; l < layout.Length; l++)
                        {
                            var part = layout[l].m_Vehicle;
                            if (part != Entity.Null && EntityManager.Exists(part) && !EntityManager.HasComponent<Deleted>(part))
                            {
                                EntityManager.AddComponentData(part, default(Deleted));
                                EntityManager.AddComponentData(part, default(Updated));
                            }
                        }
                    }

                    if (!EntityManager.HasComponent<Deleted>(veh))
                    {
                        EntityManager.AddComponentData(veh, default(Deleted));
                        EntityManager.AddComponentData(veh, default(Updated));
                    }
                    vehicleCount++;
                }

                // 3. Delete child waypoints, clear stop references, and delete segments & line entities
                for (int i = 0; i < busLines.Count; i++)
                {
                    var lineEntity = busLines[i];
                    if (!EntityManager.Exists(lineEntity) || EntityManager.HasComponent<Deleted>(lineEntity))
                        continue;

                    // Delete child waypoints and unregister from ConnectedRoute buffers on stops.
                    // Tagging an entity Deleted is a structural change and invalidates live DynamicBuffer handles,
                    // so the child list is snapshotted to a plain array before tagging starts.
                    if (EntityManager.HasBuffer<RouteWaypoint>(lineEntity))
                    {
                        Entity[] wpEntities;
                        {
                            var waypoints = EntityManager.GetBuffer<RouteWaypoint>(lineEntity);
                            wpEntities = new Entity[waypoints.Length];
                            for (int w = 0; w < waypoints.Length; w++)
                                wpEntities[w] = waypoints[w].m_Waypoint;
                        }

                        for (int w = 0; w < wpEntities.Length; w++)
                        {
                            var wpEntity = wpEntities[w];
                            if (EntityManager.Exists(wpEntity) && !EntityManager.HasComponent<Deleted>(wpEntity))
                            {
                                if (EntityManager.HasComponent<Game.Routes.Connected>(wpEntity))
                                {
                                    var connectedStop = EntityManager.GetComponentData<Game.Routes.Connected>(wpEntity).m_Connected;
                                    if (connectedStop != Entity.Null && EntityManager.Exists(connectedStop))
                                    {
                                        // Remove BoardingVehicle on connected stop to avoid dangling vehicle references
                                        if (EntityManager.HasComponent<BoardingVehicle>(connectedStop))
                                        {
                                            EntityManager.RemoveComponent<BoardingVehicle>(connectedStop);
                                        }

                                        if (EntityManager.HasBuffer<ConnectedRoute>(connectedStop))
                                        {
                                            var connectedRoutes = EntityManager.GetBuffer<ConnectedRoute>(connectedStop);
                                            for (int cr = connectedRoutes.Length - 1; cr >= 0; cr--)
                                            {
                                                if (connectedRoutes[cr].m_Waypoint == wpEntity)
                                                {
                                                    connectedRoutes.RemoveAt(cr);
                                                }
                                            }
                                        }
                                    }
                                }

                                EntityManager.AddComponentData(wpEntity, default(Deleted));
                                EntityManager.AddComponentData(wpEntity, default(Updated));
                                waypointCount++;
                            }
                        }
                    }

                    // Delete child route segments (same snapshot-then-tag ordering)
                    if (EntityManager.HasBuffer<RouteSegment>(lineEntity))
                    {
                        Entity[] segEntities;
                        {
                            var segments = EntityManager.GetBuffer<RouteSegment>(lineEntity);
                            segEntities = new Entity[segments.Length];
                            for (int s = 0; s < segments.Length; s++)
                                segEntities[s] = segments[s].m_Segment;
                        }

                        for (int s = 0; s < segEntities.Length; s++)
                        {
                            var segEntity = segEntities[s];
                            if (EntityManager.Exists(segEntity) && !EntityManager.HasComponent<Deleted>(segEntity))
                            {
                                EntityManager.AddComponentData(segEntity, default(Deleted));
                                EntityManager.AddComponentData(segEntity, default(Updated));
                                segmentCount++;
                            }
                        }
                    }

                    EntityManager.AddComponentData(lineEntity, default(Deleted));
                    EntityManager.AddComponentData(lineEntity, default(Updated));
                    lineCount++;
                }
            }

            // 4. Delete all roadside TransportStop entities (excluding permanent stations/depots)
            var stopQuery = GetEntityQuery(
                ComponentType.ReadOnly<Game.Routes.TransportStop>(),
                ComponentType.Exclude<Deleted>()
            );
            int stopCount = 0;

            var affectedRoads = new HashSet<Entity>();
            using (var stops = stopQuery.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < stops.Length; i++)
                {
                    var stopEntity = stops[i];
                    if (!EntityManager.Exists(stopEntity) || EntityManager.HasComponent<Deleted>(stopEntity))
                        continue;

                    // Skip stops that are part of permanent stations or depots
                    if (EntityManager.HasComponent<Game.Buildings.TransportStation>(stopEntity) ||
                        EntityManager.HasComponent<Game.Buildings.TransportDepot>(stopEntity))
                        continue;

                    // Check if this stop has an Owner pointing to a station/depot (platform bays)
                    if (EntityManager.HasComponent<Game.Common.Owner>(stopEntity))
                    {
                        var owner = EntityManager.GetComponentData<Game.Common.Owner>(stopEntity).m_Owner;
                        if (owner != Entity.Null && (EntityManager.HasComponent<Game.Buildings.TransportStation>(owner) ||
                                                     EntityManager.HasComponent<Game.Buildings.TransportDepot>(owner)))
                        {
                            continue;
                        }
                    }

                    // Roadside stops are attached to the road via Attached component (without Owner)
                    Entity roadOwner = Entity.Null;
                    if (EntityManager.HasComponent<Attached>(stopEntity))
                    {
                        roadOwner = EntityManager.GetComponentData<Attached>(stopEntity).m_Parent;
                    }
                    else if (EntityManager.HasComponent<Game.Common.Owner>(stopEntity))
                    {
                        roadOwner = EntityManager.GetComponentData<Game.Common.Owner>(stopEntity).m_Owner;
                    }

                    // Delete roadside bus stops only (check prefab TransportType)
                    if (EntityManager.HasComponent<PrefabRef>(stopEntity))
                    {
                        var prefabEntity = EntityManager.GetComponentData<PrefabRef>(stopEntity).m_Prefab;
                        if (prefabEntity != Entity.Null && EntityManager.HasComponent<TransportStopData>(prefabEntity))
                        {
                            var stopData = EntityManager.GetComponentData<TransportStopData>(prefabEntity);
                            if (stopData.m_TransportType == TransportType.Bus)
                            {
                                if (EntityManager.HasComponent<BoardingVehicle>(stopEntity))
                                {
                                    EntityManager.RemoveComponent<BoardingVehicle>(stopEntity);
                                }

                                EntityManager.AddComponentData(stopEntity, default(Deleted));
                                EntityManager.AddComponentData(stopEntity, default(Updated));
                                EntityManager.AddComponentData(stopEntity, default(BatchesUpdated));

                                if (roadOwner != Entity.Null && EntityManager.Exists(roadOwner) && EntityManager.HasComponent<Edge>(roadOwner))
                                {
                                    if (EntityManager.HasBuffer<Game.Objects.SubObject>(roadOwner))
                                    {
                                        var subObjects = EntityManager.GetBuffer<Game.Objects.SubObject>(roadOwner);
                                        for (int s = subObjects.Length - 1; s >= 0; s--)
                                        {
                                            if (subObjects[s].m_SubObject == stopEntity)
                                            {
                                                subObjects.RemoveAt(s);
                                            }
                                        }
                                        affectedRoads.Add(roadOwner);
                                    }
                                    ClearRoadDeadEndNotifications(roadOwner);
                                }

                                stopCount++;
                            }
                        }
                    }
                }
            }

            if (affectedRoads.Count > 0)
                MarkRoadsUpdatedWithNodes(affectedRoads);

            // 5. Reset system state for regeneration
            _hasRun = false;
            _isManualRequest = false;
            _generationStage = GenerationStage.Idle;
            _waitFrameCounter = 0;
            _currentTourIndex = 0;
            _createdLineCount = 0;
            _plannedTours.Clear();
            _plannedTourColors?.Clear();
            _activePlan = null;
            _currentPlanSeed = 0;
            var depotFinder = World.GetExistingSystemManaged<DepotFinderSystem>();
            if (depotFinder != null)
                depotFinder.Reset();

            AutoBusLinesUISystem.Instance?.UpdatePlan(null, 0, "All bus lines and stops removed.");
            AutoBusLinesUISystem.Instance?.SetPlanStatus("idle", "All bus lines and stops removed.");

            log.Info($"Deleted {lineCount} bus lines (with {waypointCount} waypoints, {segmentCount} segments, {vehicleCount} vehicles) and {stopCount} roadside bus stops across the city.");
        }

        /// <summary>
        /// Scans all active bus transit lines in the city and returns the set of all stop entities
        /// that are currently served by at least one valid, active bus route.
        /// </summary>
        private HashSet<Entity> GetStopsCoveredByActiveBusLines(out int activeLineCount)
        {
            var coveredStops = new HashSet<Entity>();
            activeLineCount = 0;

            var lineQuery = GetEntityQuery(
                ComponentType.ReadOnly<Game.Routes.TransportLine>(),
                ComponentType.ReadOnly<Game.Routes.Route>(),
                ComponentType.Exclude<Deleted>()
            );

            if (lineQuery.IsEmptyIgnoreFilter)
                return coveredStops;

            using (var routeEntities = lineQuery.ToEntityArray(Allocator.Temp))
            {
                for (int r = 0; r < routeEntities.Length; r++)
                {
                    var routeEntity = routeEntities[r];
                    if (!EntityManager.Exists(routeEntity) || EntityManager.HasComponent<Deleted>(routeEntity))
                        continue;

                    if (!EntityManager.HasComponent<PrefabRef>(routeEntity))
                        continue;

                    var prefabEntity = EntityManager.GetComponentData<PrefabRef>(routeEntity).m_Prefab;
                    if (!EntityManager.HasComponent<TransportLineData>(prefabEntity))
                        continue;

                    var lineData = EntityManager.GetComponentData<TransportLineData>(prefabEntity);
                    if (lineData.m_TransportType != TransportType.Bus)
                        continue;

                    if (!EntityManager.HasBuffer<RouteWaypoint>(routeEntity))
                        continue;

                    var waypoints = EntityManager.GetBuffer<RouteWaypoint>(routeEntity);
                    if (waypoints.Length < 2)
                        continue;

                    activeLineCount++;

                    for (int w = 0; w < waypoints.Length; w++)
                    {
                        var wpEntity = waypoints[w].m_Waypoint;
                        if (!EntityManager.Exists(wpEntity) || EntityManager.HasComponent<Deleted>(wpEntity))
                            continue;

                        if (EntityManager.HasComponent<Game.Routes.Connected>(wpEntity))
                        {
                            var stopEntity = EntityManager.GetComponentData<Game.Routes.Connected>(wpEntity).m_Connected;
                            if (EntityManager.Exists(stopEntity) && !EntityManager.HasComponent<Deleted>(stopEntity))
                            {
                                coveredStops.Add(stopEntity);
                            }
                        }
                    }
                }
            }

            return coveredStops;
        }

        private void ExecuteRepairBrokenRoutes()
        {
            log.Info("=== Starting Manual Route Repair: Scanning bus lines for pathfinding failures ===");

            var lineQuery = GetEntityQuery(
                ComponentType.ReadOnly<Game.Routes.TransportLine>(),
                ComponentType.ReadOnly<Game.Routes.Route>(),
                ComponentType.Exclude<Deleted>()
            );

            if (lineQuery.IsEmptyIgnoreFilter)
            {
                log.Info("No bus routes found in city to repair.");
                return;
            }

            int totalRoutesExamined = 0;
            int brokenRoutesFound = 0;
            int totalBrokenSegments = 0;
            int stopsNudged = 0;

            var stopsToNudge = new HashSet<Entity>();
            var segmentsToReset = new HashSet<Entity>();
            var routesToUpdate = new HashSet<Entity>();
            var scannedBusRoutes = new List<Entity>();

            using (var routeEntities = lineQuery.ToEntityArray(Allocator.Temp))
            {
                for (int r = 0; r < routeEntities.Length; r++)
                {
                    var routeEntity = routeEntities[r];
                    if (!EntityManager.Exists(routeEntity) || EntityManager.HasComponent<Deleted>(routeEntity))
                        continue;

                    if (!EntityManager.HasComponent<PrefabRef>(routeEntity))
                        continue;

                    var prefabEntity = EntityManager.GetComponentData<PrefabRef>(routeEntity).m_Prefab;
                    if (!EntityManager.HasComponent<TransportLineData>(prefabEntity))
                        continue;

                    var lineData = EntityManager.GetComponentData<TransportLineData>(prefabEntity);
                    if (lineData.m_TransportType != TransportType.Bus)
                        continue;

                    totalRoutesExamined++;
                    scannedBusRoutes.Add(routeEntity);

                    if (!EntityManager.HasBuffer<RouteSegment>(routeEntity) || !EntityManager.HasBuffer<RouteWaypoint>(routeEntity))
                        continue;

                    var segBuf = EntityManager.GetBuffer<RouteSegment>(routeEntity);
                    var wpBuf = EntityManager.GetBuffer<RouteWaypoint>(routeEntity);

                    if (segBuf.Length == 0 || wpBuf.Length == 0)
                        continue;

                    bool routeHasBrokenSegment = false;

                    for (int s = 0; s < segBuf.Length; s++)
                    {
                        var segEnt = segBuf[s].m_Segment;
                        if (!EntityManager.Exists(segEnt) || EntityManager.HasComponent<Deleted>(segEnt))
                        {
                            routeHasBrokenSegment = true;
                            totalBrokenSegments++;
                            continue;
                        }

                        if (IsSegmentPathPending(segEnt))
                            continue;

                        if (IsSegmentBrokenCompleted(segEnt))
                        {
                            routeHasBrokenSegment = true;
                            totalBrokenSegments++;
                            segmentsToReset.Add(segEnt);

                            // The failed segment connects Waypoint s to Waypoint (s + 1) % wpBuf.Length
                            int wpIndexA = s;
                            int wpIndexB = (s + 1) % wpBuf.Length;
                            bool refreshA = wpIndexA < wpBuf.Length &&
                                ProcessBrokenEndpointWaypoint(wpBuf[wpIndexA].m_Waypoint, allowNudge: true, stopsToNudge, curbsideWaypointsToNudge: null);
                            bool refreshB = wpIndexB < wpBuf.Length &&
                                ProcessBrokenEndpointWaypoint(wpBuf[wpIndexB].m_Waypoint, allowNudge: true, stopsToNudge, curbsideWaypointsToNudge: null);

                            if (refreshA)
                            {
                                int prevSegIdx = (s - 1 + segBuf.Length) % segBuf.Length;
                                if (prevSegIdx < segBuf.Length)
                                    segmentsToReset.Add(segBuf[prevSegIdx].m_Segment);
                            }
                            if (refreshB)
                            {
                                int nextSegIdx = (s + 1) % segBuf.Length;
                                if (nextSegIdx < segBuf.Length)
                                    segmentsToReset.Add(segBuf[nextSegIdx].m_Segment);
                            }
                        }
                    }

                    if (routeHasBrokenSegment)
                    {
                        brokenRoutesFound++;
                        routesToUpdate.Add(routeEntity);
                    }
                }
            }

            log.Info($"Scanned {totalRoutesExamined} bus routes: Found {brokenRoutesFound} routes with {totalBrokenSegments} broken segments. Identified {stopsToNudge.Count} problematic roadside stops to adjust.");

            if (totalBrokenSegments == 0)
            {
                log.Info("All bus routes are already healthy! No repair actions needed.");
                return;
            }

            LogBrokenSegmentDiagnostics(scannedBusRoutes, "ManualRepair");

            // 1. Nudge problematic roadside bus stops along their road curves and batch road updates
            var nudgedRoads = new HashSet<Entity>();
            foreach (var stopEntity in stopsToNudge)
            {
                if (NudgeRoadsideBusStop(stopEntity, out Entity nudgedRoad, markRoadUpdated: false))
                {
                    stopsNudged++;
                    if (nudgedRoad != Entity.Null)
                        nudgedRoads.Add(nudgedRoad);
                }
            }
            if (nudgedRoads.Count > 0)
                MarkRoadsUpdatedWithNodes(nudgedRoads);

            // 2. Reset PathTargets and PathInformation on affected segments and mark affected routes Updated
            ResetRouteSegmentsForPathfinding(segmentsToReset, routesToUpdate);

            log.Info($"=== Repair Complete: Nudged {stopsNudged} stops, reset {segmentsToReset.Count} segments across {brokenRoutesFound} routes. Re-enqueued pathfinding. ===");

        }

        /// <summary>
        /// Returns true while a segment's pathfind has been enqueued by RoutePathSystem (Start/End lanes populated)
        /// and has not yet received its RoutePathReadySystem completion event.
        /// </summary>
        private bool IsSegmentPathPending(Entity segEnt)
        {
            if (!EntityManager.HasComponent<PathInformation>(segEnt) ||
                !EntityManager.HasComponent<PathTargets>(segEnt))
                return false;

            var pi = EntityManager.GetComponentData<PathInformation>(segEnt);
            if (pi.m_Distance != 0f)
                return false;

            if (EntityManager.HasBuffer<PathElement>(segEnt) &&
                EntityManager.GetBuffer<PathElement>(segEnt).Length > 0)
                return false;

            var pt = EntityManager.GetComponentData<PathTargets>(segEnt);
            if (pt.m_StartLane == Entity.Null || pt.m_EndLane == Entity.Null)
                return false;

            return math.lengthsq(pt.m_ReadyStartPosition) < 1e-6f &&
                   math.lengthsq(pt.m_ReadyEndPosition) < 1e-6f;
        }

        private bool IsSegmentBrokenCompleted(Entity segEnt)
        {
            if (!EntityManager.HasComponent<PathInformation>(segEnt))
                return true;

            var pi = EntityManager.GetComponentData<PathInformation>(segEnt);
            if (pi.m_Distance <= 0f)
                return true;

            return !EntityManager.HasBuffer<PathElement>(segEnt) ||
                   EntityManager.GetBuffer<PathElement>(segEnt).Length == 0;
        }

        /// <summary>
        /// Checks a broken segment endpoint waypoint; only queues stop/curbside nudges and marks the waypoint
        /// Updated when the waypoint actually lost lane binding or its stop will be nudged.
        /// </summary>
        private bool ProcessBrokenEndpointWaypoint(
            Entity wp,
            bool allowNudge,
            HashSet<Entity> stopsToNudge,
            HashSet<Entity> curbsideWaypointsToNudge)
        {
            if (!EntityManager.Exists(wp) || EntityManager.HasComponent<Deleted>(wp))
                return false;

            bool missingRouteLane = !EntityManager.HasComponent<RouteLane>(wp) ||
                                    EntityManager.GetComponentData<RouteLane>(wp).m_StartLane == Entity.Null ||
                                    EntityManager.GetComponentData<RouteLane>(wp).m_EndLane == Entity.Null;
            bool missingAccessLane = EntityManager.HasComponent<AccessLane>(wp) &&
                                     EntityManager.GetComponentData<AccessLane>(wp).m_Lane == Entity.Null;
            bool needsWpRefresh = missingRouteLane || missingAccessLane;

            if (EntityManager.HasComponent<Connected>(wp))
            {
                var stop = EntityManager.GetComponentData<Connected>(wp).m_Connected;
                if (stop != Entity.Null && EntityManager.Exists(stop) && !EntityManager.HasComponent<Deleted>(stop) &&
                    ShouldNudgeStopForWaypoint(wp, stop))
                {
                    stopsToNudge.Add(stop);
                    if (allowNudge)
                        needsWpRefresh = true;
                }
            }
            else if (allowNudge && curbsideWaypointsToNudge != null)
            {
                curbsideWaypointsToNudge.Add(wp);
                needsWpRefresh = true;
            }

            if (needsWpRefresh && !EntityManager.HasComponent<Updated>(wp))
                EntityManager.AddComponentData(wp, default(Updated));

            return needsWpRefresh;
        }

        private void ResetRouteSegmentsForPathfinding(HashSet<Entity> segmentsToReset, HashSet<Entity> routesToUpdate)
        {
            foreach (var segEnt in segmentsToReset)
            {
                if (!EntityManager.Exists(segEnt) || EntityManager.HasComponent<Deleted>(segEnt))
                    continue;

                if (EntityManager.HasComponent<PathTargets>(segEnt))
                    EntityManager.SetComponentData(segEnt, new PathTargets());

                if (EntityManager.HasComponent<PathInformation>(segEnt))
                    EntityManager.SetComponentData(segEnt, default(PathInformation));

                if (EntityManager.HasBuffer<PathElement>(segEnt))
                    EntityManager.GetBuffer<PathElement>(segEnt).Clear();

                if (EntityManager.HasBuffer<CurveElement>(segEnt))
                    EntityManager.GetBuffer<CurveElement>(segEnt).Clear();

                if (!EntityManager.HasComponent<Updated>(segEnt))
                    EntityManager.AddComponentData(segEnt, default(Updated));
            }

            foreach (var routeEntity in routesToUpdate)
            {
                if (!EntityManager.Exists(routeEntity) || EntityManager.HasComponent<Deleted>(routeEntity))
                    continue;

                if (!EntityManager.HasComponent<Updated>(routeEntity))
                    EntityManager.AddComponentData(routeEntity, default(Updated));
                if (!EntityManager.HasComponent<BatchesUpdated>(routeEntity))
                    EntityManager.AddComponentData(routeEntity, default(BatchesUpdated));
            }
        }

        /// <summary>
        /// Returns true only when the waypoint failed to bind to a valid road CarLane
        /// owned by the stop's attached road segment (e.g. null lane, Area ConnectionLane,
        /// or mismatched owner), avoiding unnecessary road rebuilds on healthy stops.
        /// </summary>
        private bool ShouldNudgeStopForWaypoint(Entity wpEntity, Entity stopEntity)
        {
            if (!EntityManager.Exists(wpEntity) || !EntityManager.HasComponent<RouteLane>(wpEntity))
                return true;

            var rl = EntityManager.GetComponentData<RouteLane>(wpEntity);
            if (rl.m_EndLane == Entity.Null || !EntityManager.Exists(rl.m_EndLane) ||
                !EntityManager.HasComponent<Game.Net.CarLane>(rl.m_EndLane))
                return true;

            if (stopEntity != Entity.Null && EntityManager.HasComponent<Attached>(stopEntity))
            {
                Entity roadEntity = EntityManager.GetComponentData<Attached>(stopEntity).m_Parent;
                if (roadEntity != Entity.Null && EntityManager.HasComponent<Owner>(rl.m_EndLane) &&
                    EntityManager.GetComponentData<Owner>(rl.m_EndLane).m_Owner != roadEntity)
                    return true;
            }

            return false;
        }

        private bool NudgeRoadsideBusStop(Entity stopEntity)
        {
            return NudgeRoadsideBusStop(stopEntity, out _, markRoadUpdated: true);
        }

        private bool NudgeRoadsideBusStop(Entity stopEntity, out Entity nudgedRoad, bool markRoadUpdated)
        {
            nudgedRoad = Entity.Null;
            if (!EntityManager.Exists(stopEntity) || EntityManager.HasComponent<Deleted>(stopEntity))
                return false;

            if (!EntityManager.HasComponent<Attached>(stopEntity) || !EntityManager.HasComponent<Game.Objects.Transform>(stopEntity))
                return false;

            var attached = EntityManager.GetComponentData<Attached>(stopEntity);
            var roadEntity = attached.m_Parent;
            if (roadEntity == Entity.Null || !EntityManager.Exists(roadEntity) || !EntityManager.HasComponent<Curve>(roadEntity))
                return false;

            if (IsDrawbridge(roadEntity))
                return false;

            var roadCurve = EntityManager.GetComponentData<Curve>(roadEntity);
            float roadLen = MathUtils.Length(roadCurve.m_Bezier);
            if (roadLen < 1.0f)
                return false;

            float curT = attached.m_CurvePosition;

            // Bounds on road segment to avoid intersection conflict zones
            float minT = (roadLen >= 42.0f) ? (20.0f / roadLen) : 0.25f;
            float maxT = (roadLen >= 42.0f) ? (1.0f - (22.0f / roadLen)) : 0.75f;
            if (minT > maxT)
            {
                minT = 0.30f;
                maxT = 0.70f;
            }

            // Nudge distance: 3.0 meters along the road curve
            float nudgeMeters = 3.0f;
            float deltaT = nudgeMeters / roadLen;

            float newT;
            if (curT < 0.45f)
            {
                // Closer to start of segment, nudge forward toward center
                newT = curT + deltaT;
            }
            else if (curT > 0.55f)
            {
                // Closer to end of segment, nudge backward toward center
                newT = curT - deltaT;
            }
            else
            {
                // Around center, nudge slightly forward or alternate
                newT = curT + deltaT;
                if (newT > maxT)
                    newT = curT - deltaT;
            }

            newT = math.clamp(newT, minT, maxT);
            if (math.abs(newT - curT) < 0.0005f)
            {
                newT = (curT < 0.5f) ? math.min(curT + deltaT, 0.5f) : math.max(curT - deltaT, 0.5f);
            }

            var currentTransform = EntityManager.GetComponentData<Game.Objects.Transform>(stopEntity);
            float3 curPos = currentTransform.m_Position;

            // Determine which side of the road the stop was placed on relative to the road curve
            float3 curRoadPos = MathUtils.Position(roadCurve.m_Bezier, curT);
            float3 curTangent = MathUtils.Tangent(roadCurve.m_Bezier, curT);
            float3 curRoadForward = math.normalizesafe(new float3(curTangent.x, 0f, curTangent.z));
            float3 curRightNormal = new float3(curRoadForward.z, 0f, -curRoadForward.x);
            float3 offsetVec = curPos - curRoadPos;

            float lateralDist = math.length(new float2(offsetVec.x, offsetVec.z));
            if (lateralDist < 2.0f)
                lateralDist = 5.0f; // default sidewalk curb offset

            // Dot product with right-hand normal preserves the EXACT same curb:
            // sideDot >= 0 means right side curb, sideDot < 0 means left side curb
            float sideDot = math.dot(offsetVec, curRightNormal);

            // Compute new position and right-hand normal along road curve at newT
            float3 newRoadPos = MathUtils.Position(roadCurve.m_Bezier, newT);
            float3 newTangent = MathUtils.Tangent(roadCurve.m_Bezier, newT);
            float3 newRoadForward = math.normalizesafe(new float3(newTangent.x, 0f, newTangent.z));
            float3 newRightNormal = new float3(newRoadForward.z, 0f, -newRoadForward.x);

            float3 normal = (sideDot >= 0f) ? newRightNormal : -newRightNormal;
            float3 newStopPos = newRoadPos + normal * lateralDist;
            newStopPos.y = newRoadPos.y;

            // Set stop rotation facing inward toward the road centerline from the curb
            float3 toRoad = math.normalizesafe(new float3(newRoadPos.x - newStopPos.x, 0f, newRoadPos.z - newStopPos.z));
            if (math.lengthsq(toRoad) < 0.001f)
                toRoad = -normal;
            quaternion newRot = quaternion.LookRotationSafe(toRoad, new float3(0, 1, 0));

            Entity stopPrefabEntity = EntityManager.HasComponent<PrefabRef>(stopEntity)
                ? EntityManager.GetComponentData<PrefabRef>(stopEntity).m_Prefab
                : Entity.Null;
            bool isRightSide = sideDot >= 0f;
            if (!TrySnapToRoadCurbAreaWithShift(roadEntity, newT, newRoadPos, normal, isRightSide, stopPrefabEntity,
                    out float3 areaPos, out quaternion areaRot, out float areaT, out _))
            {
                return false;
            }

            newStopPos = areaPos;
            newRot = areaRot;
            float finalAttachedT = areaT;

            SyncStopElevationWithRoad(stopEntity, roadEntity);

            // Apply updated Attached and Transform to stop
            EntityManager.SetComponentData(stopEntity, new Attached(roadEntity, Entity.Null, finalAttachedT));
            EntityManager.SetComponentData(stopEntity, new Game.Objects.Transform(newStopPos, newRot));

            if (!EntityManager.HasComponent<Updated>(stopEntity))
                EntityManager.AddComponentData(stopEntity, default(Updated));
            if (!EntityManager.HasComponent<BatchesUpdated>(stopEntity))
                EntityManager.AddComponentData(stopEntity, default(BatchesUpdated));

            // Ensure roadEntity maintains stopEntity in its SubObject buffer
            if (EntityManager.HasBuffer<Game.Objects.SubObject>(roadEntity))
            {
                var roadSubObjects = EntityManager.GetBuffer<Game.Objects.SubObject>(roadEntity);
                bool found = false;
                for (int s = 0; s < roadSubObjects.Length; s++)
                {
                    if (roadSubObjects[s].m_SubObject == stopEntity)
                    {
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    roadSubObjects.Add(new Game.Objects.SubObject(stopEntity));
                }
            }
            else
            {
                var roadSubObjects = EntityManager.AddBuffer<Game.Objects.SubObject>(roadEntity);
                roadSubObjects.Add(new Game.Objects.SubObject(stopEntity));
            }
            if (markRoadUpdated)
                MarkRoadUpdatedWithNodes(roadEntity);
            else
                nudgedRoad = roadEntity;

            // Clean up any legacy DeadEnd warnings on parent road
            ClearRoadDeadEndNotifications(roadEntity);

            // Update all connected waypoints
            if (EntityManager.HasBuffer<ConnectedRoute>(stopEntity))
            {
                var connBuf = EntityManager.GetBuffer<ConnectedRoute>(stopEntity);
                for (int c = 0; c < connBuf.Length; c++)
                {
                    var wpEnt = connBuf[c].m_Waypoint;
                    if (EntityManager.Exists(wpEnt) && !EntityManager.HasComponent<Deleted>(wpEnt))
                    {
                        if (EntityManager.HasComponent<Game.Routes.Position>(wpEnt))
                        {
                            EntityManager.SetComponentData(wpEnt, new Game.Routes.Position(newStopPos));
                        }
                        if (!EntityManager.HasComponent<Updated>(wpEnt))
                            EntityManager.AddComponentData(wpEnt, default(Updated));
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Marks the road edge, its direct endpoint/connected nodes, all adjacent neighbor edges meeting
        /// at those nodes, and those neighbor edges' far endpoint/connected nodes as Updated (plus
        /// BatchesUpdated on primary roads, direct nodes, and neighbor edges).
        /// Updating direct nodes + neighbor edges prevents GeometrySystem.InitializeNodeGeometryJob partial
        /// clamping (m_Bounds.min.x = 1f -> intersection seams), while updating farNodes ensures
        /// LaneOverlapSystem rebuilds far-end NodeLane.LaneOverlap buffers alongside neighbor edge SubLanes
        /// so CarLaneSpeedIterator.CheckOverlappingLanes never reads deleted SubLane entities.
        /// </summary>
        private void MarkRoadUpdatedWithNodes(Entity roadEntity)
        {
            if (roadEntity == Entity.Null || !EntityManager.Exists(roadEntity) || EntityManager.HasComponent<Deleted>(roadEntity))
                return;

            MarkRoadsUpdatedWithNodes(new[] { roadEntity });
        }

        /// <summary>
        /// Marks a batch of modified road edges together with their full 1-hop node-edge-node closure
        /// (primaryRoads + directNodes + neighborEdges + farNodes) so GeometrySystem, LaneSystem,
        /// LaneOverlapSystem, and WaypointConnectionSystem all see consistent boundaries.
        /// </summary>
        private void MarkRoadsUpdatedWithNodes(IEnumerable<Entity> roadEntities)
        {
            if (roadEntities == null)
                return;

            var primaryRoads = new HashSet<Entity>();
            foreach (var r in roadEntities)
                CollectValidEntity(r, primaryRoads);

            if (primaryRoads.Count == 0)
                return;

            var directNodes = new HashSet<Entity>();
            var neighborEdges = new HashSet<Entity>();
            var farNodes = new HashSet<Entity>();

            foreach (var roadEntity in primaryRoads)
                CollectEdgeNodes(roadEntity, directNodes);

            foreach (var node in directNodes)
            {
                if (!EntityManager.HasBuffer<Game.Net.ConnectedEdge>(node))
                    continue;

                var connectedEdges = EntityManager.GetBuffer<Game.Net.ConnectedEdge>(node);
                for (int i = 0; i < connectedEdges.Length; i++)
                {
                    Entity adjEdge = connectedEdges[i].m_Edge;
                    if (!primaryRoads.Contains(adjEdge))
                        CollectValidEntity(adjEdge, neighborEdges);
                }
            }

            foreach (var adjEdge in neighborEdges)
                CollectEdgeNodes(adjEdge, farNodes, excludeNodes: directNodes);

            // Apply structural ECS tag changes only after all DynamicBuffer reads finish.
            foreach (var roadEntity in primaryRoads)
            {
                if (EntityManager.HasComponent<Game.Objects.SubObjectsUpdated>(roadEntity))
                    EntityManager.RemoveComponent<Game.Objects.SubObjectsUpdated>(roadEntity);

                MarkEntityUpdatedIfValid(roadEntity, includeBatches: true);
            }

            foreach (var node in directNodes)
                MarkEntityUpdatedIfValid(node, includeBatches: true);

            foreach (var adjEdge in neighborEdges)
                MarkEntityUpdatedIfValid(adjEdge, includeBatches: true);

            foreach (var farNode in farNodes)
                MarkEntityUpdatedIfValid(farNode, includeBatches: false);
        }

        private void CollectEdgeNodes(Entity edgeEntity, HashSet<Entity> targetNodes, HashSet<Entity> excludeNodes = null)
        {
            if (EntityManager.HasComponent<Game.Net.Edge>(edgeEntity))
            {
                var edge = EntityManager.GetComponentData<Game.Net.Edge>(edgeEntity);
                if (excludeNodes == null || !excludeNodes.Contains(edge.m_Start))
                    CollectValidEntity(edge.m_Start, targetNodes);
                if (excludeNodes == null || !excludeNodes.Contains(edge.m_End))
                    CollectValidEntity(edge.m_End, targetNodes);
            }

            if (EntityManager.HasBuffer<Game.Net.ConnectedNode>(edgeEntity))
            {
                var connectedNodes = EntityManager.GetBuffer<Game.Net.ConnectedNode>(edgeEntity);
                for (int i = 0; i < connectedNodes.Length; i++)
                {
                    Entity midNode = connectedNodes[i].m_Node;
                    if (excludeNodes == null || !excludeNodes.Contains(midNode))
                        CollectValidEntity(midNode, targetNodes);
                }
            }
        }

        private bool CollectValidEntity(Entity entity, HashSet<Entity> target)
        {
            if (entity == Entity.Null || !EntityManager.Exists(entity) || EntityManager.HasComponent<Deleted>(entity))
                return false;

            target.Add(entity);
            return true;
        }

        private void MarkEntityUpdatedIfValid(Entity entity, bool includeBatches = false)
        {
            if (entity == Entity.Null || !EntityManager.Exists(entity) || EntityManager.HasComponent<Deleted>(entity))
                return;

            if (!EntityManager.HasComponent<Updated>(entity))
                EntityManager.AddComponentData(entity, default(Updated));

            if (includeBatches && !EntityManager.HasComponent<BatchesUpdated>(entity))
                EntityManager.AddComponentData(entity, default(BatchesUpdated));
        }

        private bool NudgeCurbsideWaypoint(Entity wpEntity)
        {
            if (!EntityManager.Exists(wpEntity) || EntityManager.HasComponent<Deleted>(wpEntity))
                return false;

            if (!EntityManager.HasComponent<RouteLane>(wpEntity) || !EntityManager.HasComponent<Game.Routes.Position>(wpEntity))
                return false;

            var routeLane = EntityManager.GetComponentData<RouteLane>(wpEntity);
            if (routeLane.m_StartLane == Entity.Null || !EntityManager.Exists(routeLane.m_StartLane) || !EntityManager.HasComponent<Curve>(routeLane.m_StartLane))
                return false;

            var laneCurve = EntityManager.GetComponentData<Curve>(routeLane.m_StartLane);
            float laneLen = MathUtils.Length(laneCurve.m_Bezier);
            if (laneLen < 2.0f)
                return false;

            float curT = routeLane.m_StartCurvePos;
            float deltaT = 3.0f / laneLen;
            float newT = (curT < 0.45f) ? (curT + deltaT) : (curT - deltaT);
            newT = math.clamp(newT, 0.05f, 0.95f);

            routeLane.m_StartCurvePos = newT;
            routeLane.m_EndCurvePos = newT;
            if (routeLane.m_EndLane == Entity.Null)
                routeLane.m_EndLane = routeLane.m_StartLane;
            if (routeLane.m_StartLane == Entity.Null)
                routeLane.m_StartLane = routeLane.m_EndLane;
            EntityManager.SetComponentData(wpEntity, routeLane);

            float3 newPos = MathUtils.Position(laneCurve.m_Bezier, newT);
            EntityManager.SetComponentData(wpEntity, new Game.Routes.Position(newPos));

            if (!EntityManager.HasComponent<Updated>(wpEntity))
                EntityManager.AddComponentData(wpEntity, default(Updated));

            return true;
        }

        private void ClearRoadDeadEndNotifications(Entity roadEntity)
        {
            if (roadEntity == Entity.Null || !EntityManager.Exists(roadEntity))
                return;

            // Look up TrafficConfigurationData singleton to identify m_DeadEndNotification
            var trafficConfigQuery = GetEntityQuery(ComponentType.ReadOnly<TrafficConfigurationData>());
            if (trafficConfigQuery.IsEmptyIgnoreFilter)
                return;

            var trafficConfig = trafficConfigQuery.GetSingleton<TrafficConfigurationData>();
            Entity deadEndPrefab = trafficConfig.m_DeadEndNotification;
            if (deadEndPrefab == Entity.Null)
                return;

            // Check roadEntity itself
            ClearDeadEndIconFromEntity(roadEntity, deadEndPrefab);

            // Check all SubLanes of the road
            if (EntityManager.HasBuffer<Game.Net.SubLane>(roadEntity))
            {
                var subLanes = EntityManager.GetBuffer<Game.Net.SubLane>(roadEntity);
                for (int l = 0; l < subLanes.Length; l++)
                {
                    ClearDeadEndIconFromEntity(subLanes[l].m_SubLane, deadEndPrefab);
                }
            }
        }

        private void ClearDeadEndIconFromEntity(Entity entity, Entity deadEndPrefab)
        {
            if (entity == Entity.Null || !EntityManager.Exists(entity) || !EntityManager.HasBuffer<Game.Notifications.IconElement>(entity))
                return;

            var iconElements = EntityManager.GetBuffer<Game.Notifications.IconElement>(entity);
            for (int i = iconElements.Length - 1; i >= 0; i--)
            {
                Entity icon = iconElements[i].m_Icon;
                if (EntityManager.Exists(icon) && EntityManager.HasComponent<PrefabRef>(icon))
                {
                    var pRef = EntityManager.GetComponentData<PrefabRef>(icon).m_Prefab;
                    if (pRef == deadEndPrefab)
                    {
                        if (!EntityManager.HasComponent<Deleted>(icon))
                            EntityManager.AddComponentData(icon, default(Deleted));
                        iconElements.RemoveAt(i);
                    }
                }
            }
        }

        // ==========================================================================
        // Plan Building & Route Instantiation Systems
        // ==========================================================================
        private void ExecuteBuildSelectedPlan()
        {
            if (_activePlan == null || _activePlan.Count == 0)
            {
                log.Warn("ExecuteBuildSelectedPlan: No active transit plan to build.");
                AutoBusLinesUISystem.Instance?.SetPlanStatus("idle", "No active transit plan to build.");
                return;
            }

            if (!FindBusLinePrefab(out Entity busLinePrefabEntity, out RouteData busLineRouteData))
            {
                log.Error("Could not find a valid Bus Line Prefab with RouteData archetypes!");
                AutoBusLinesUISystem.Instance?.SetPlanStatus("idle", "Error: Bus line prefab not found.");
                return;
            }

            _cachedBusLinePrefabEntity = busLinePrefabEntity;
            _cachedBusLineRouteData = busLineRouteData;

            // Collect unique virtual stops that need to be instantiated into the world
            var uniqueVirtualStops = new List<PlacedStop>();
            var virtualStopSet = new HashSet<int>();
            _virtualToRealEntityMap = new Dictionary<int, Entity>();

            // Re-validate virtual stops against the user's current Bus Stop Model selection in case settings changed after plan preview
            FindBusStopPrefabs(out var candidatePrefabs, out _);
            var candidateSet = new HashSet<Entity>(candidatePrefabs);
            string selectedModelSetting = Mod.setting != null ? (Mod.setting.SelectedStopPrefab ?? "") : "";
            bool modelSelectionChanged = !string.Equals(_activePlanSelectedStopModel ?? "", selectedModelSetting, StringComparison.OrdinalIgnoreCase);
            bool isCurbsideOnly = (!string.IsNullOrEmpty(selectedModelSetting) && selectedModelSetting.Equals("Curbside", StringComparison.OrdinalIgnoreCase))
                || (candidatePrefabs.Count > 0 && candidatePrefabs.TrueForAll(IsBusStopSign));
            var cityConfig = World.GetExistingSystemManaged<Game.City.CityConfigurationSystem>();
            bool isLeftHandTraffic = cityConfig != null && cityConfig.leftHandTraffic;
            var planRng = new Unity.Mathematics.Random((uint)Math.Max(1, _currentPlanSeed * 7919 + 185392));
            var placedPrefabEntities = new List<Entity>();
            var planPlacementCounts = new Dictionary<Entity, int>();

            for (int r = 0; r < _activePlan.Count; r++)
            {
                var route = _activePlan[r];
                if (!route.Enabled)
                    continue;

                for (int sIdx = 0; sIdx < route.Stops.Count; sIdx++)
                {
                    var stopData = route.Stops[sIdx];
                    if (!stopData.Enabled)
                        continue;

                    var st = stopData.InternalStop;
                    if (st.StopEntity.Index < 0 && virtualStopSet.Add(st.StopEntity.Index))
                    {
                        if (st.RoadEntity != Entity.Null && EntityManager.Exists(st.RoadEntity) && !EntityManager.HasComponent<Deleted>(st.RoadEntity))
                        {
                            bool needsPrefabRefresh = candidatePrefabs.Count > 0 && !st.IsStationBay &&
                                (modelSelectionChanged || st.PrefabEntity == Entity.Null || !EntityManager.Exists(st.PrefabEntity) || !candidateSet.Contains(st.PrefabEntity));

                            if (needsPrefabRefresh)
                            {
                                float3 roadCenterPos = st.Position;
                                if (EntityManager.HasComponent<Curve>(st.RoadEntity))
                                {
                                    roadCenterPos = MathUtils.Position(EntityManager.GetComponentData<Curve>(st.RoadEntity).m_Bezier, st.RoadT);
                                }
                                float3 curbNormal = isLeftHandTraffic
                                    ? new float3(-st.Forward.z, 0f, st.Forward.x)
                                    : new float3(st.Forward.z, 0f, -st.Forward.x);
                                bool isReverseSide = (st.SpanOrderInCorridor % 2 != 0);
                                bool isStopOnRightSide = isLeftHandTraffic ? isReverseSide : !isReverseSide;

                                st.PrefabEntity = SelectStopPrefabAndCurbSnap(
                                    st.RoadEntity, st.RoadT, roadCenterPos, curbNormal, st.Position, isStopOnRightSide,
                                    candidatePrefabs, isCurbsideOnly, ref planRng,
                                    out float3 updatedPos, out quaternion? updatedRot, out float updatedT, planPlacementCounts);
                                st.Position = updatedPos;
                                st.RoadT = updatedT;
                                st.HasRotationOverride = updatedRot.HasValue;
                                st.RotationOverride = updatedRot ?? quaternion.identity;
                                stopData.InternalStop = st;
                            }
                            else if (!st.IsStationBay && st.PrefabEntity != Entity.Null)
                            {
                                planPlacementCounts.TryGetValue(st.PrefabEntity, out int existingCount);
                                planPlacementCounts[st.PrefabEntity] = existingCount + 1;
                            }

                            if (st.PrefabEntity != Entity.Null)
                            {
                                uniqueVirtualStops.Add(st);
                                placedPrefabEntities.Add(st.PrefabEntity);
                            }
                            else
                            {
                                _virtualToRealEntityMap[st.StopEntity.Index] = Entity.Null;
                            }
                        }
                    }
                }
            }

            _pendingVirtualStops = uniqueVirtualStops;
            _currentVirtualStopIndex = 0;
            _totalVirtualStopsToCreate = uniqueVirtualStops.Count;
            _currentTourIndex = 0;
            _createdLineCount = 0;
            _waitFrameCounter = 0;
            _createdRouteEntities.Clear();
            _settlingWaitCounter = 0;
            _settlingPass = 0;
            _settlingPendingWaitPasses = 0;

            if (_totalVirtualStopsToCreate > 0)
            {
                _generationStage = GenerationStage.InstantiatingStops;
                AutoBusLinesUISystem.Instance?.SetBuildProgress(0f, "building", $"Creating bus stop 0 / {_totalVirtualStopsToCreate} (0%)...");
                log.Info($"Building Selected Plan: Beginning staged instantiation of {_totalVirtualStopsToCreate} bus stops across frames (Models: {FormatPlacedStopPrefabBreakdown(placedPrefabEntities)})...");
            }
            else
            {
                // All stops pre-existing or curbside; directly assemble tours
                var toursToBuild = new List<List<PlacedStop>>();
                _plannedTourColors = new List<Color32>();

                for (int r = 0; r < _activePlan.Count; r++)
                {
                    var route = _activePlan[r];
                    if (!route.Enabled) continue;

                    var tourStops = new List<PlacedStop>();
                    for (int sIdx = 0; sIdx < route.Stops.Count; sIdx++)
                    {
                        var stopData = route.Stops[sIdx];
                        if (!stopData.Enabled) continue;
                        var st = stopData.InternalStop;
                        if (st.StopEntity.Index < 0)
                        {
                            if (_virtualToRealEntityMap != null && _virtualToRealEntityMap.TryGetValue(st.StopEntity.Index, out Entity realStop))
                            {
                                st.StopEntity = realStop;
                            }
                            else if (st.PrefabEntity == Entity.Null)
                            {
                                st.StopEntity = Entity.Null;
                            }
                        }
                        if (st.StopEntity == Entity.Null || st.StopEntity.Index <= 0 || (EntityManager.Exists(st.StopEntity) && !EntityManager.HasComponent<Deleted>(st.StopEntity)))
                        {
                            tourStops.Add(st);
                        }
                    }

                    if (tourStops.Count >= ABSOLUTE_MIN_STOPS)
                    {
                        toursToBuild.Add(tourStops);
                        _plannedTourColors.Add(HexToColor(route.Color));
                    }
                }

                _plannedTours = toursToBuild;
                _activePlan = null;
                _currentPlanSeed = 0;
                _generationStage = GenerationStage.WaitingForStopIndexing;
                AutoBusLinesUISystem.Instance?.SetBuildProgress(0.60f, "building", "Indexing bus stops and road geometry...");
            }
        }

        private bool FindBusLinePrefab(out Entity prefabEntity, out RouteData routeData)
        {
            prefabEntity = Entity.Null;
            routeData = default;

            var entities = _busLinePrefabQuery.ToEntityArray(Allocator.Temp);
            var lineDataArray = _busLinePrefabQuery.ToComponentDataArray<TransportLineData>(Allocator.Temp);
            var routeDataArray = _busLinePrefabQuery.ToComponentDataArray<RouteData>(Allocator.Temp);

            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    if (lineDataArray[i].m_TransportType == TransportType.Bus && lineDataArray[i].m_PassengerTransport)
                    {
                        prefabEntity = entities[i];
                        routeData = routeDataArray[i];
                        return true;
                    }
                }
                return false;
            }
            finally
            {
                entities.Dispose();
                lineDataArray.Dispose();
                routeDataArray.Dispose();
            }
        }

        private List<PlacedStop> SubsampleTour(List<PlacedStop> tour, int maxStops)
        {
            if (tour.Count <= maxStops)
                return tour;

            var sampled = new List<PlacedStop>(maxStops);
            float step = (float)(tour.Count - 1) / (maxStops - 1);
            for (int k = 0; k < maxStops; k++)
            {
                int idx = (int)math.round(k * step);
                idx = math.clamp(idx, 0, tour.Count - 1);
                if (sampled.Count == 0 || sampled[sampled.Count - 1].StopEntity != tour[idx].StopEntity)
                    sampled.Add(tour[idx]);
            }

            if (sampled.Count > 1 && sampled[sampled.Count - 1].StopEntity == sampled[0].StopEntity)
                sampled.RemoveAt(sampled.Count - 1);

            return sampled;
        }

        private struct DirectedRoadEdge
        {
            public Entity EdgeEntity;
            public Entity FromNode;
            public Entity ToNode;
            public float Length;
            public float3 TangentAtFrom;
            public float3 TangentAtTo;
        }

        private class SimpleMinHeap
        {
            private struct HeapItem
            {
                public Entity Node;
                public float Priority;
            }

            private List<HeapItem> _items = new List<HeapItem>();

            public int Count => _items.Count;

            public void Enqueue(Entity node, float priority)
            {
                _items.Add(new HeapItem { Node = node, Priority = priority });
                int c = _items.Count - 1;
                while (c > 0)
                {
                    int p = (c - 1) / 2;
                    if (_items[c].Priority >= _items[p].Priority)
                        break;
                    var tmp = _items[c];
                    _items[c] = _items[p];
                    _items[p] = tmp;
                    c = p;
                }
            }

            public Entity Dequeue()
            {
                if (_items.Count == 0) return Entity.Null;
                Entity result = _items[0].Node;
                int last = _items.Count - 1;
                _items[0] = _items[last];
                _items.RemoveAt(last);
                int p = 0;
                while (true)
                {
                    int left = 2 * p + 1;
                    int right = 2 * p + 2;
                    int smallest = p;

                    if (left < _items.Count && _items[left].Priority < _items[smallest].Priority)
                        smallest = left;
                    if (right < _items.Count && _items[right].Priority < _items[smallest].Priority)
                        smallest = right;

                    if (smallest == p)
                        break;

                    var tmp = _items[p];
                    _items[p] = _items[smallest];
                    _items[smallest] = tmp;
                    p = smallest;
                }
                return result;
            }
        }

        private void BuildDirectedRoadGraph(
            out Dictionary<Entity, List<DirectedRoadEdge>> nodeOutEdges,
            out Dictionary<Entity, float3> nodePositions)
        {
            nodeOutEdges = new Dictionary<Entity, List<DirectedRoadEdge>>();
            nodePositions = new Dictionary<Entity, float3>();
            _nodeDegree = new Dictionary<Entity, int>();

            var segments = (_roadScanner != null && _roadScanner.DrivableSegments.IsCreated && _roadScanner.DrivableSegments.Length > 0)
                ? _roadScanner.DrivableSegments
                : _roadScanner.RoadSegments;

            for (int i = 0; i < segments.Length; i++)
            {
                var roadEnt = segments[i];
                if (!EntityManager.HasComponent<Edge>(roadEnt) || !EntityManager.HasComponent<Curve>(roadEnt))
                    continue;

                var edge = EntityManager.GetComponentData<Edge>(roadEnt);
                var curve = EntityManager.GetComponentData<Curve>(roadEnt);

                if (edge.m_Start == Entity.Null || edge.m_End == Entity.Null)
                    continue;

                float len = MathUtils.Length(curve.m_Bezier);
                if (len < 0.1f)
                    continue;

                float3 startPos = MathUtils.Position(curve.m_Bezier, 0f);
                float3 endPos = MathUtils.Position(curve.m_Bezier, 1f);

                float3 startTan = math.normalizesafe(MathUtils.Tangent(curve.m_Bezier, 0f), new float3(0, 0, 1));
                float3 endTan = math.normalizesafe(MathUtils.Tangent(curve.m_Bezier, 1f), new float3(0, 0, 1));

                _nodeDegree[edge.m_Start] = (_nodeDegree.TryGetValue(edge.m_Start, out int degStart) ? degStart : 0) + 1;
                _nodeDegree[edge.m_End] = (_nodeDegree.TryGetValue(edge.m_End, out int degEnd) ? degEnd : 0) + 1;

                if (!nodePositions.ContainsKey(edge.m_Start))
                    nodePositions[edge.m_Start] = startPos;
                if (!nodePositions.ContainsKey(edge.m_End))
                    nodePositions[edge.m_End] = endPos;

                bool isOneWay = IsOneWayRoad(roadEnt, out bool forwardFlow);

                if (!isOneWay || forwardFlow)
                {
                    if (!nodeOutEdges.ContainsKey(edge.m_Start))
                        nodeOutEdges[edge.m_Start] = new List<DirectedRoadEdge>();

                    nodeOutEdges[edge.m_Start].Add(new DirectedRoadEdge
                    {
                        EdgeEntity = roadEnt,
                        FromNode = edge.m_Start,
                        ToNode = edge.m_End,
                        Length = len,
                        TangentAtFrom = startTan,
                        TangentAtTo = endTan
                    });
                }

                if (!isOneWay || !forwardFlow)
                {
                    if (!nodeOutEdges.ContainsKey(edge.m_End))
                        nodeOutEdges[edge.m_End] = new List<DirectedRoadEdge>();

                    nodeOutEdges[edge.m_End].Add(new DirectedRoadEdge
                    {
                        EdgeEntity = roadEnt,
                        FromNode = edge.m_End,
                        ToNode = edge.m_Start,
                        Length = len,
                        TangentAtFrom = -endTan,
                        TangentAtTo = -startTan
                    });
                }
            }

            PopulateLegalNodeTurns(nodePositions.Keys);
        }

        private struct DirectedNodeArc
        {
            public ulong FromKey;
            public ulong ToKey;
        }

        private static ulong MakePathNodeKey(Game.Pathfind.PathNode pathNode)
        {
            return ((ulong)(uint)pathNode.GetOwnerIndex() << 32) | (uint)pathNode.GetLaneIndex();
        }

        /// <summary>
        /// Scans each graph junction node's live SubLane buffer and connected edge boundary PathNodes
        /// to record which (fromEdge, toEdge) vehicle turns exist for buses, respecting vanilla turn
        /// prohibitions, medians, node-owned PathNode seams, and Traffic mod custom connections.
        /// </summary>
        private void PopulateLegalNodeTurns(IEnumerable<Entity> graphNodes)
        {
            _legalNodeTurns = new Dictionary<Entity, HashSet<(Entity, Entity)>>();
            _nodesWithCarSubLanes = new HashSet<Entity>();

            foreach (Entity node in graphNodes)
            {
                if (node == Entity.Null || !EntityManager.Exists(node))
                    continue;

                CollectLegalTurnsForNode(node);
            }
        }

        private bool IsBusUsableCarSubLane(Entity subLane, out Game.Net.CarLane carLane)
        {
            carLane = default;
            if (subLane == Entity.Null ||
                EntityManager.HasComponent<Game.Net.SlaveLane>(subLane) ||
                !EntityManager.HasComponent<Game.Net.CarLane>(subLane) ||
                !EntityManager.HasComponent<Game.Net.Lane>(subLane))
            {
                return false;
            }

            carLane = EntityManager.GetComponentData<Game.Net.CarLane>(subLane);
            if ((carLane.m_Flags & Game.Net.CarLaneFlags.SideConnection) != 0)
                return false;

            if (EntityManager.HasComponent<PrefabRef>(subLane))
            {
                Entity lp = EntityManager.GetComponentData<PrefabRef>(subLane).m_Prefab;
                if (lp != Entity.Null && EntityManager.HasComponent<CarLaneData>(lp))
                {
                    var cld = EntityManager.GetComponentData<CarLaneData>(lp);
                    if ((cld.m_RoadTypes & Game.Net.RoadTypes.Car) == 0)
                        return false;
                    if (cld.m_MaxSize != SizeClass.Undefined &&
                        (int)cld.m_MaxSize < (int)SizeClass.Large)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private void CollectLegalTurnsForNode(Entity node)
        {
            if (!EntityManager.HasBuffer<Game.Net.SubLane>(node) ||
                !EntityManager.HasBuffer<ConnectedEdge>(node))
            {
                return;
            }

            var subLanes = EntityManager.GetBuffer<Game.Net.SubLane>(node);
            if (subLanes.Length == 0)
                return;

            bool hasAnyCarSubLane = false;
            var arcs = new List<DirectedNodeArc>(subLanes.Length * 2);

            for (int i = 0; i < subLanes.Length; i++)
            {
                Entity sl = subLanes[i].m_SubLane;
                if (sl == Entity.Null ||
                    EntityManager.HasComponent<Game.Net.SlaveLane>(sl) ||
                    !EntityManager.HasComponent<Game.Net.CarLane>(sl) ||
                    !EntityManager.HasComponent<Game.Net.Lane>(sl))
                {
                    continue;
                }

                var carLane = EntityManager.GetComponentData<Game.Net.CarLane>(sl);
                if ((carLane.m_Flags & Game.Net.CarLaneFlags.SideConnection) != 0)
                    continue;

                bool sizeTooSmall = false;
                if (EntityManager.HasComponent<PrefabRef>(sl))
                {
                    Entity lp = EntityManager.GetComponentData<PrefabRef>(sl).m_Prefab;
                    if (lp != Entity.Null && EntityManager.HasComponent<CarLaneData>(lp))
                    {
                        var cld = EntityManager.GetComponentData<CarLaneData>(lp);
                        if ((cld.m_RoadTypes & Game.Net.RoadTypes.Car) == 0)
                            continue;
                        if (cld.m_MaxSize != SizeClass.Undefined &&
                            (int)cld.m_MaxSize < (int)SizeClass.Large)
                        {
                            sizeTooSmall = true;
                        }
                    }
                }

                hasAnyCarSubLane = true;
                if (sizeTooSmall || (carLane.m_Flags & Game.Net.CarLaneFlags.Forbidden) != 0)
                    continue;

                var laneData = EntityManager.GetComponentData<Game.Net.Lane>(sl);
                ulong startKey = MakePathNodeKey(laneData.m_StartNode);
                ulong endKey = MakePathNodeKey(laneData.m_EndNode);
                arcs.Add(new DirectedNodeArc { FromKey = startKey, ToKey = endKey });
                if ((carLane.m_Flags & Game.Net.CarLaneFlags.Twoway) != 0)
                    arcs.Add(new DirectedNodeArc { FromKey = endKey, ToKey = startKey });
            }

            if (!hasAnyCarSubLane)
                return;

            _nodesWithCarSubLanes.Add(node);
            var legalTurns = new HashSet<(Entity, Entity)>();
            _legalNodeTurns[node] = legalTurns;
            if (arcs.Count == 0)
                return;

            var entryKeysByEdge = new Dictionary<Entity, List<ulong>>();
            var exitEdgesByPathKey = new Dictionary<ulong, List<Entity>>();
            var connEdges = EntityManager.GetBuffer<ConnectedEdge>(node);
            var seenEdges = new HashSet<Entity>();

            for (int i = 0; i < connEdges.Length; i++)
            {
                Entity edgeEnt = connEdges[i].m_Edge;
                if (edgeEnt == Entity.Null || !seenEdges.Add(edgeEnt) ||
                    !EntityManager.HasComponent<Game.Net.Edge>(edgeEnt) ||
                    !EntityManager.HasBuffer<Game.Net.SubLane>(edgeEnt))
                {
                    continue;
                }

                var edgeData = EntityManager.GetComponentData<Game.Net.Edge>(edgeEnt);
                bool atStart = edgeData.m_Start == node;
                bool atEnd = edgeData.m_End == node;
                if (!atStart && !atEnd)
                    continue;

                var edgeSubLanes = EntityManager.GetBuffer<Game.Net.SubLane>(edgeEnt);
                if (edgeSubLanes.Length == 0)
                    continue;

                // Track edge-owned keys that appear as both Start and End inside this edge (mid-block stop splits)
                // so internal seams are never treated as junction endpoints.
                var internalStartKeys = new HashSet<ulong>();
                var internalEndKeys = new HashSet<ulong>();
                int edgeIdx = edgeEnt.Index;
                for (int l = 0; l < edgeSubLanes.Length; l++)
                {
                    Entity esl = edgeSubLanes[l].m_SubLane;
                    if (!IsBusUsableCarSubLane(esl, out var eCar) ||
                        (eCar.m_Flags & Game.Net.CarLaneFlags.Forbidden) != 0)
                    {
                        continue;
                    }

                    var elData = EntityManager.GetComponentData<Game.Net.Lane>(esl);
                    if (elData.m_StartNode.GetOwnerIndex() == edgeIdx)
                        internalStartKeys.Add(MakePathNodeKey(elData.m_StartNode));
                    if (elData.m_EndNode.GetOwnerIndex() == edgeIdx)
                        internalEndKeys.Add(MakePathNodeKey(elData.m_EndNode));
                }

                for (int l = 0; l < edgeSubLanes.Length; l++)
                {
                    Entity esl = edgeSubLanes[l].m_SubLane;
                    if (!IsBusUsableCarSubLane(esl, out var eCar) ||
                        (eCar.m_Flags & Game.Net.CarLaneFlags.Forbidden) != 0)
                    {
                        continue;
                    }

                    bool invert = (eCar.m_Flags & Game.Net.CarLaneFlags.Invert) != 0;
                    bool twoway = (eCar.m_Flags & Game.Net.CarLaneFlags.Twoway) != 0;
                    var elData = EntityManager.GetComponentData<Game.Net.Lane>(esl);

                    bool endFacesNode = (atEnd && !invert) || (atStart && invert);
                    bool startFacesNode = (atStart && !invert) || (atEnd && invert);

                    if (endFacesNode)
                    {
                        ulong endKey = MakePathNodeKey(elData.m_EndNode);
                        bool isInternalSeam = elData.m_EndNode.GetOwnerIndex() == edgeIdx &&
                                              internalStartKeys.Contains(endKey);
                        if (!isInternalSeam)
                        {
                            AddEdgePathKey(entryKeysByEdge, edgeEnt, endKey);
                            if (twoway)
                                AddPathKeyExitEdge(exitEdgesByPathKey, endKey, edgeEnt);
                        }
                    }

                    if (startFacesNode)
                    {
                        ulong startKey = MakePathNodeKey(elData.m_StartNode);
                        bool isInternalSeam = elData.m_StartNode.GetOwnerIndex() == edgeIdx &&
                                              internalEndKeys.Contains(startKey);
                        if (!isInternalSeam)
                        {
                            AddPathKeyExitEdge(exitEdgesByPathKey, startKey, edgeEnt);
                            if (twoway)
                                AddEdgePathKey(entryKeysByEdge, edgeEnt, startKey);
                        }
                    }
                }
            }

            if (entryKeysByEdge.Count == 0 || exitEdgesByPathKey.Count == 0)
                return;

            var outgoingByKey = new Dictionary<ulong, List<ulong>>(arcs.Count);
            for (int i = 0; i < arcs.Count; i++)
            {
                DirectedNodeArc arc = arcs[i];
                if (!outgoingByKey.TryGetValue(arc.FromKey, out var list))
                {
                    list = new List<ulong>();
                    outgoingByKey[arc.FromKey] = list;
                }
                list.Add(arc.ToKey);
            }

            var queue = new Queue<ulong>();
            var visitedKeys = new HashSet<ulong>();

            foreach (var kv in entryKeysByEdge)
            {
                Entity fromEdge = kv.Key;
                queue.Clear();
                visitedKeys.Clear();

                List<ulong> entries = kv.Value;
                for (int i = 0; i < entries.Count; i++)
                {
                    ulong k = entries[i];
                    if (visitedKeys.Add(k))
                        queue.Enqueue(k);
                }

                while (queue.Count > 0)
                {
                    ulong curKey = queue.Dequeue();
                    if (exitEdgesByPathKey.TryGetValue(curKey, out var toEdges))
                    {
                        for (int i = 0; i < toEdges.Count; i++)
                            legalTurns.Add((fromEdge, toEdges[i]));
                    }

                    if (!outgoingByKey.TryGetValue(curKey, out var nextKeys))
                        continue;

                    for (int i = 0; i < nextKeys.Count; i++)
                    {
                        ulong nk = nextKeys[i];
                        if (visitedKeys.Add(nk))
                            queue.Enqueue(nk);
                    }
                }
            }
        }

        private static void AddEdgePathKey(Dictionary<Entity, List<ulong>> map, Entity edge, ulong key)
        {
            if (!map.TryGetValue(edge, out var list))
            {
                list = new List<ulong>();
                map[edge] = list;
            }
            list.Add(key);
        }

        private static void AddPathKeyExitEdge(Dictionary<ulong, List<Entity>> map, ulong key, Entity edge)
        {
            if (!map.TryGetValue(key, out var list))
            {
                list = new List<Entity>();
                map[key] = list;
            }
            if (!list.Contains(edge))
                list.Add(edge);
        }

        private void IndexStopsByDirectedEdge(
            List<PlacedStop> allGlobalStops,
            out Dictionary<Entity, List<PlacedStop>> edgeForwardStops,
            out Dictionary<Entity, List<PlacedStop>> edgeBackwardStops)
        {
            edgeForwardStops = new Dictionary<Entity, List<PlacedStop>>();
            edgeBackwardStops = new Dictionary<Entity, List<PlacedStop>>();

            for (int i = 0; i < allGlobalStops.Count; i++)
            {
                var stop = allGlobalStops[i];
                var roadEnt = stop.RoadEntity;
                if (roadEnt == Entity.Null || !EntityManager.HasComponent<Curve>(roadEnt))
                    continue;

                var curve = EntityManager.GetComponentData<Curve>(roadEnt);
                float3 roadTan = math.normalizesafe(MathUtils.Tangent(curve.m_Bezier, 0.5f), new float3(0, 0, 1));

                float3 startPos = MathUtils.Position(curve.m_Bezier, 0f);
                stop.DistFromHub = math.distance(stop.Position, startPos);

                // Direction comes from the stop's side of the road / the road's flow, not its rotation
                if (StopServesEdgeForward(stop))
                {
                    if (!edgeForwardStops.ContainsKey(roadEnt))
                        edgeForwardStops[roadEnt] = new List<PlacedStop>();
                    edgeForwardStops[roadEnt].Add(stop);
                }
                else
                {
                    if (!edgeBackwardStops.ContainsKey(roadEnt))
                        edgeBackwardStops[roadEnt] = new List<PlacedStop>();
                    edgeBackwardStops[roadEnt].Add(stop);
                }
            }

            foreach (var kvp in edgeForwardStops)
            {
                kvp.Value.Sort((a, b) => a.DistFromHub.CompareTo(b.DistFromHub));
            }

            foreach (var kvp in edgeBackwardStops)
            {
                kvp.Value.Sort((a, b) => b.DistFromHub.CompareTo(a.DistFromHub));
            }
        }

        private List<PlacedStop> GetStopsOnDirectedEdge(
            DirectedRoadEdge dEdge,
            Dictionary<Entity, List<PlacedStop>> edgeForwardStops,
            Dictionary<Entity, List<PlacedStop>> edgeBackwardStops)
        {
            if (!EntityManager.HasComponent<Edge>(dEdge.EdgeEntity))
                return null;

            var edge = EntityManager.GetComponentData<Edge>(dEdge.EdgeEntity);
            bool isForward = (dEdge.FromNode == edge.m_Start);

            if (isForward)
            {
                if (edgeForwardStops.TryGetValue(dEdge.EdgeEntity, out var stops))
                    return stops;
            }
            else
            {
                if (edgeBackwardStops.TryGetValue(dEdge.EdgeEntity, out var stops))
                    return stops;
            }

            return null;
        }

        private List<DirectedRoadEdge> GetCorridorDirectedEdges(RoadCorridor corr, bool forward)
        {
            var list = new List<DirectedRoadEdge>();
            if (corr.Segments.Count == 0) return list;

            if (forward)
            {
                for (int i = 0; i < corr.Segments.Count; i++)
                {
                    var seg = corr.Segments[i];
                    if (!EntityManager.HasComponent<Edge>(seg.RoadEntity) || !EntityManager.HasComponent<Curve>(seg.RoadEntity))
                        continue;

                    var edge = EntityManager.GetComponentData<Edge>(seg.RoadEntity);
                    var curve = EntityManager.GetComponentData<Curve>(seg.RoadEntity);
                    float len = MathUtils.Length(curve.m_Bezier);

                    Entity fromNode = seg.IsReversed ? edge.m_End : edge.m_Start;
                    Entity toNode = seg.IsReversed ? edge.m_Start : edge.m_End;
                    float3 tanFrom = seg.IsReversed
                        ? -math.normalizesafe(MathUtils.Tangent(curve.m_Bezier, 1f), new float3(0, 0, 1))
                        : math.normalizesafe(MathUtils.Tangent(curve.m_Bezier, 0f), new float3(0, 0, 1));
                    float3 tanTo = seg.IsReversed
                        ? -math.normalizesafe(MathUtils.Tangent(curve.m_Bezier, 0f), new float3(0, 0, 1))
                        : math.normalizesafe(MathUtils.Tangent(curve.m_Bezier, 1f), new float3(0, 0, 1));

                    list.Add(new DirectedRoadEdge
                    {
                        EdgeEntity = seg.RoadEntity,
                        FromNode = fromNode,
                        ToNode = toNode,
                        Length = len,
                        TangentAtFrom = tanFrom,
                        TangentAtTo = tanTo
                    });
                }
            }
            else
            {
                for (int i = corr.Segments.Count - 1; i >= 0; i--)
                {
                    var seg = corr.Segments[i];
                    if (!EntityManager.HasComponent<Edge>(seg.RoadEntity) || !EntityManager.HasComponent<Curve>(seg.RoadEntity))
                        continue;

                    var edge = EntityManager.GetComponentData<Edge>(seg.RoadEntity);
                    var curve = EntityManager.GetComponentData<Curve>(seg.RoadEntity);
                    float len = MathUtils.Length(curve.m_Bezier);

                    Entity fromNode = seg.IsReversed ? edge.m_Start : edge.m_End;
                    Entity toNode = seg.IsReversed ? edge.m_End : edge.m_Start;
                    float3 tanFrom = seg.IsReversed
                        ? math.normalizesafe(MathUtils.Tangent(curve.m_Bezier, 0f), new float3(0, 0, 1))
                        : -math.normalizesafe(MathUtils.Tangent(curve.m_Bezier, 1f), new float3(0, 0, 1));
                    float3 tanTo = seg.IsReversed
                        ? math.normalizesafe(MathUtils.Tangent(curve.m_Bezier, 1f), new float3(0, 0, 1))
                        : -math.normalizesafe(MathUtils.Tangent(curve.m_Bezier, 0f), new float3(0, 0, 1));

                    list.Add(new DirectedRoadEdge
                    {
                        EdgeEntity = seg.RoadEntity,
                        FromNode = fromNode,
                        ToNode = toNode,
                        Length = len,
                        TangentAtFrom = tanFrom,
                        TangentAtTo = tanTo
                    });
                }
            }

            return list;
        }

        private bool TryFindCarLane(Entity roadEnt, float3 stopPos, float roadT, out Entity bestLane, out float bestT, out float3 lanePos, bool? preferForward = null, float3? stopForward = null)
        {
            bestLane = Entity.Null;
            bestT = roadT;
            lanePos = stopPos;

            if (roadEnt == Entity.Null || !EntityManager.Exists(roadEnt) || !EntityManager.HasComponent<Curve>(roadEnt))
                return false;

            var roadCurve = EntityManager.GetComponentData<Curve>(roadEnt);
            lanePos = MathUtils.Position(roadCurve.m_Bezier, roadT);

            if (EntityManager.HasBuffer<Game.Net.SubLane>(roadEnt))
            {
                var subLanes = EntityManager.GetBuffer<Game.Net.SubLane>(roadEnt);
                float bestDistSq = float.MaxValue;
                Curve bestCurve = default;

                bool hasStopForward = stopForward.HasValue && math.lengthsq(stopForward.Value.xz) > 0.001f;

                // Pass 0: direction-matched candidate (matching stopForward or preferForward)
                // Pass 1: any valid car lane on road
                for (int pass = (hasStopForward || preferForward.HasValue ? 0 : 1); pass < 2; pass++)
                {
                    for (int l = 0; l < subLanes.Length; l++)
                    {
                        Entity subLane = subLanes[l].m_SubLane;
                        // Exclude SlaveLane (vehicles only drive and pathfind on MasterLane / primary CarLane)
                        if (EntityManager.HasComponent<Game.Net.SlaveLane>(subLane))
                            continue;

                        if (!EntityManager.HasComponent<Game.Net.CarLane>(subLane) ||
                            !EntityManager.HasComponent<Curve>(subLane))
                            continue;

                        var carLane = EntityManager.GetComponentData<Game.Net.CarLane>(subLane);
                        if ((carLane.m_Flags & (Game.Net.CarLaneFlags.Unsafe | Game.Net.CarLaneFlags.SideConnection | Game.Net.CarLaneFlags.Forbidden)) != 0)
                            continue;

                        if (EntityManager.HasComponent<PrefabRef>(subLane))
                        {
                            Entity lanePrefab = EntityManager.GetComponentData<PrefabRef>(subLane).m_Prefab;
                            if (EntityManager.HasComponent<CarLaneData>(lanePrefab))
                            {
                                var laneData = EntityManager.GetComponentData<CarLaneData>(lanePrefab);
                                if ((laneData.m_RoadTypes & Game.Net.RoadTypes.Car) == 0)
                                    continue;
                                if ((int)laneData.m_MaxSize < (int)SizeClass.Large)
                                    continue;
                            }
                        }

                        var laneCurve = EntityManager.GetComponentData<Curve>(subLane);
                        float dist = MathUtils.Distance(laneCurve.m_Bezier.xz, stopPos.xz, out float t);

                        if (pass == 0)
                        {
                            if (hasStopForward)
                            {
                                float3 laneDir = math.normalizesafe(MathUtils.Tangent(laneCurve.m_Bezier, t));
                                if (math.dot(laneDir.xz, stopForward.Value.xz) < 0.2f)
                                    continue;
                            }
                            else if (preferForward.HasValue)
                            {
                                bool isTwoway = (carLane.m_Flags & Game.Net.CarLaneFlags.Twoway) != 0;
                                bool isInvert = (carLane.m_Flags & Game.Net.CarLaneFlags.Invert) != 0;
                                if (!isTwoway)
                                {
                                    if (preferForward.Value && isInvert)
                                        continue;
                                    if (!preferForward.Value && !isInvert)
                                        continue;
                                }
                            }
                        }

                        float dSq = dist * dist;
                        if (dSq < bestDistSq)
                        {
                            bestDistSq = dSq;
                            bestLane = subLane;
                            bestCurve = laneCurve;
                            bestT = t;
                        }
                    }

                    if (bestLane != Entity.Null)
                        break;
                }

                if (bestLane != Entity.Null)
                {
                    lanePos = MathUtils.Position(bestCurve.m_Bezier, bestT);
                    return true;
                }
            }

            return false;
        }

        private static bool AreStopsSame(PlacedStop a, PlacedStop b)
        {
            if (a.StopEntity != Entity.Null && b.StopEntity != Entity.Null && a.StopEntity.Index > 0 && b.StopEntity.Index > 0)
                return a.StopEntity == b.StopEntity;
            if (a.RoadEntity != Entity.Null && b.RoadEntity != Entity.Null && a.RoadEntity == b.RoadEntity)
                return math.abs(a.RoadT - b.RoadT) < 0.05f;
            return math.distancesq(a.Position, b.Position) < 4f;
        }

        private List<PlacedStop> BuildTourFromRoadCycle(
            List<DirectedRoadEdge> cycleEdges,
            Dictionary<Entity, List<PlacedStop>> edgeForwardStops,
            Dictionary<Entity, List<PlacedStop>> edgeBackwardStops,
            int maxStops)
        {
            var tour = new List<PlacedStop>();
            var inTour = new HashSet<Entity>();   // a stop may appear on a line only once

            for (int i = 0; i < cycleEdges.Count; i++)
            {
                var dEdge = cycleEdges[i];
                var stops = GetStopsOnDirectedEdge(dEdge, edgeForwardStops, edgeBackwardStops);
                if (stops != null && stops.Count > 0)
                {
                    for (int s = 0; s < stops.Count; s++)
                    {
                        var stop = stops[s];
                        if (stop.StopEntity != Entity.Null && stop.StopEntity.Index > 0)
                        {
                            if (inTour.Add(stop.StopEntity))
                            {
                                tour.Add(stop);
                            }
                        }
                        else
                        {
                            bool duplicate = false;
                            for (int t = 0; t < tour.Count; t++)
                            {
                                if (AreStopsSame(tour[t], stop))
                                {
                                    duplicate = true;
                                    break;
                                }
                            }
                            if (!duplicate)
                            {
                                tour.Add(stop);
                            }
                        }
                    }
                }
            }

            if (tour.Count > 1 && AreStopsSame(tour[tour.Count - 1], tour[0]))
                tour.RemoveAt(tour.Count - 1);

            if (tour.Count > maxStops)
                tour = SubsampleTour(tour, maxStops);

            return tour;
        }

        private float CalculateTourLength(List<PlacedStop> tour)
        {
            if (tour.Count < 2)
                return 0f;

            float len = 0f;
            for (int i = 0; i < tour.Count; i++)
            {
                len += math.distance(tour[i].Position, tour[(i + 1) % tour.Count].Position);
            }
            return len * 1.25f; // Grid and curve driving factor
        }

        private bool CreateBusLine(Entity busLinePrefabEntity, RouteData routeData, List<PlacedStop> orderedStops, int lineNumber, int hubIndex = 0, int tourIndex = 0, Color32? customColor = null)
        {
            if (orderedStops == null || orderedStops.Count == 0)
                return false;

            // 1. Prune any stops that may have been demolished between planning and building
            orderedStops.RemoveAll(s =>
            {
                if (s.StopEntity != Entity.Null && s.StopEntity.Index > 0)
                    return !EntityManager.Exists(s.StopEntity) || EntityManager.HasComponent<Deleted>(s.StopEntity);
                return s.RoadEntity == Entity.Null || !EntityManager.Exists(s.RoadEntity) || EntityManager.HasComponent<Deleted>(s.RoadEntity);
            });
            if (orderedStops.Count < ABSOLUTE_MIN_STOPS)
            {
                log.Warn($"CreateBusLine: Line #{lineNumber} aborted because fewer than {ABSOLUTE_MIN_STOPS} valid stops exist ({orderedStops.Count} found).");
                return false;
            }

            // 2. Deduplicate consecutive identical stops and close loops cleanly
            var uniqueStops = new List<PlacedStop>(orderedStops.Count);
            for (int i = 0; i < orderedStops.Count; i++)
            {
                var s = orderedStops[i];
                if (uniqueStops.Count > 0 && AreStopsSame(uniqueStops[uniqueStops.Count - 1], s))
                    continue;
                uniqueStops.Add(s);
            }
            if (uniqueStops.Count > 1 && AreStopsSame(uniqueStops[0], uniqueStops[uniqueStops.Count - 1]))
            {
                uniqueStops.RemoveAt(uniqueStops.Count - 1);
            }

            if (uniqueStops.Count < ABSOLUTE_MIN_STOPS)
            {
                log.Warn($"CreateBusLine: Line #{lineNumber} aborted because fewer than {ABSOLUTE_MIN_STOPS} unique valid stops exist ({uniqueStops.Count} found).");
                return false;
            }

            // 3. Create the Route entity (TransportLine)
            Entity routeEntity = EntityManager.CreateEntity(routeData.m_RouteArchetype);

            EntityManager.SetComponentData(routeEntity, new PrefabRef(busLinePrefabEntity));
            EntityManager.SetComponentData(routeEntity, new Game.Routes.Route { m_Flags = RouteFlags.Complete });

            // Critical Fix: Properly initialize TransportLine struct with prefab data (vehicle interval & unbunching factor).
            // A parameterless default struct has 0f vehicle interval and 0f unbunching, which causes TransportLineSystem
            // vehicle-count calculations to fail and trigger permanent "Not Enough Vehicles" problem warnings.
            if (EntityManager.HasComponent<TransportLineData>(busLinePrefabEntity))
            {
                var transportLineData = EntityManager.GetComponentData<TransportLineData>(busLinePrefabEntity);
                EntityManager.SetComponentData(routeEntity, new Game.Routes.TransportLine(transportLineData));
            }
            else
            {
                EntityManager.SetComponentData(routeEntity, new Game.Routes.TransportLine());
            }

            EntityManager.SetComponentData(routeEntity, new RouteNumber { m_Number = lineNumber });

            Color32 lineColor = customColor ?? HexToColor(GetLineHexColor(lineNumber));
            EntityManager.SetComponentData(routeEntity, new Game.Routes.Color(lineColor));
            EntityManager.SetComponentData(routeEntity, new RouteBufferIndex { m_Index = -1 });

            // Assign custom names to curbside stops if not already set
            var stopStreetNames = new string[uniqueStops.Count];
            var streetCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int s = 0; s < uniqueStops.Count; s++)
            {
                var st = uniqueStops[s];
                if (!st.IsStationBay)
                {
                    string street = GetRoadStreetName(st.RoadEntity) ?? "Road";
                    stopStreetNames[s] = street;
                    streetCounts[street] = streetCounts.TryGetValue(street, out int c) ? c + 1 : 1;
                }
            }

            var streetIndices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < uniqueStops.Count; i++)
            {
                var stop = uniqueStops[i];
                if (!stop.IsStationBay && stop.StopEntity != Entity.Null && stop.StopEntity.Index > 0 && EntityManager.Exists(stop.StopEntity) && !EntityManager.HasComponent<Deleted>(stop.StopEntity))
                {
                    string street = stopStreetNames[i] ?? "Road";
                    int totalOnStreet = streetCounts[street];
                    int currentIndex = streetIndices.TryGetValue(street, out int cur) ? cur + 1 : 1;
                    streetIndices[street] = currentIndex;

                    if (!EntityManager.HasComponent<Game.UI.CustomName>(stop.StopEntity))
                    {
                        string prefix = stop.IsPreExisting ? "Stop" : "New Stop";
                        string stopName = (totalOnStreet > 1)
                            ? $"{prefix} {street} {currentIndex}"
                            : $"{prefix} {street}";

                        if (_nameSystem == null)
                            _nameSystem = World.GetOrCreateSystemManaged<Game.UI.NameSystem>();
                        _nameSystem?.SetCustomName(stop.StopEntity, stopName);
                    }
                }
            }

            // 4. Create Waypoints referencing placed stops with strict sequential indexing.
            // Critical Fix: Waypoint(wpIndex) MUST use waypointEntities.Count (0..N-1) rather than the raw loop index,
            // otherwise skipped stops create non-contiguous indices that cause WaypointConnectionSystem and RoutePathSystem
            // to access out-of-bounds segment buffer indices in Burst jobs -> Hard Crash To Desktop (CTD).
            // Waypoint Archetype Handling:
            // - Stops with physical shelters/signs: use m_ConnectedArchetype and set Connected(stop.StopEntity)
            // - Curbside stops (open curbs / no shelter): use m_WaypointArchetype (NO Connected, NO VehicleTiming, NO Attached)
            var waypointEntities = new List<Entity>(uniqueStops.Count);
            for (int i = 0; i < uniqueStops.Count; i++)
            {
                var stop = uniqueStops[i];
                bool hasPhysicalStop = stop.StopEntity != Entity.Null && stop.StopEntity.Index > 0 && EntityManager.Exists(stop.StopEntity) && !EntityManager.HasComponent<Deleted>(stop.StopEntity);

                // Fallback: If stop reached route creation without a physical stop entity (e.g. legacy plan or curbside mode),
                // instantiate a curbside bus stop sign entity on the road now to guarantee valid transit connectivity.
                if (!hasPhysicalStop)
                {
                    if (stop.RoadEntity == Entity.Null || !EntityManager.Exists(stop.RoadEntity) || EntityManager.HasComponent<Deleted>(stop.RoadEntity))
                        continue;

                    Entity signPrefab = stop.PrefabEntity;
                    if (signPrefab == Entity.Null || !EntityManager.Exists(signPrefab))
                    {
                        FindBusStopPrefabs(out var prefabs, out _);
                        for (int p = 0; p < prefabs.Count; p++)
                        {
                            if (IsBusStopSign(prefabs[p]))
                            {
                                signPrefab = prefabs[p];
                                break;
                            }
                        }
                        if (signPrefab == Entity.Null && prefabs.Count > 0)
                            signPrefab = prefabs[0];
                    }

                    if (signPrefab != Entity.Null && EntityManager.HasComponent<Curve>(stop.RoadEntity))
                    {
                        var fallbackCurve = EntityManager.GetComponentData<Curve>(stop.RoadEntity);
                        float3 roadCenterPos = MathUtils.Position(fallbackCurve.m_Bezier, stop.RoadT);
                        float3 approxCurbNormal = math.normalizesafe(new float3(stop.Position.x - roadCenterPos.x, 0f, stop.Position.z - roadCenterPos.z));
                        bool isRightSide = math.dot(math.cross(new float3(0f, 1f, 0f), stop.Forward), approxCurbNormal) >= 0f;

                        if (TrySnapToRoadCurbAreaWithShift(
                                stop.RoadEntity, stop.RoadT, roadCenterPos, approxCurbNormal, isRightSide, signPrefab,
                                out float3 snappedFallbackPos, out quaternion snappedFallbackRot, out float snappedFallbackT, out _)
                            && TryCreateBusStopEntity(
                                stop.RoadEntity, snappedFallbackT, snappedFallbackPos, stop.Forward, stop.HubEntity,
                                stop.CorridorIndex, stop.SpanOrderInCorridor, stop.IsOutbound, signPrefab, out PlacedStop createdStop, snappedFallbackRot))
                        {
                            stop.StopEntity = createdStop.StopEntity;
                            stop.PrefabEntity = signPrefab;
                            stop.Position = snappedFallbackPos;
                            stop.RoadT = snappedFallbackT;
                            uniqueStops[i] = stop;
                            hasPhysicalStop = true;
                        }
                    }
                }

                int wpIndex = waypointEntities.Count;
                Entity wpEntity;

                // Resolve best car lane and snapped position for both physical and curbside stops
                Entity roadEntity = stop.RoadEntity;
                if (roadEntity == Entity.Null && hasPhysicalStop && EntityManager.HasComponent<Attached>(stop.StopEntity))
                {
                    roadEntity = EntityManager.GetComponentData<Attached>(stop.StopEntity).m_Parent;
                }

                bool servesForward = StopServesEdgeForward(stop);
                Entity bestLane = Entity.Null;
                float bestT = stop.RoadT;
                float3 snappedPos = stop.Position;

                if (roadEntity != Entity.Null && EntityManager.Exists(roadEntity))
                {
                    if (!hasPhysicalStop && TryFindCarLane(roadEntity, stop.Position, stop.RoadT, out bestLane, out bestT, out float3 lPos, servesForward, stop.Forward))
                    {
                        snappedPos = lPos;
                    }
                    else if (!hasPhysicalStop && EntityManager.HasComponent<Curve>(roadEntity))
                    {
                        var rCurve = EntityManager.GetComponentData<Curve>(roadEntity);
                        snappedPos = MathUtils.Position(rCurve.m_Bezier, stop.RoadT);
                    }
                }

                float3 finalWpPos = hasPhysicalStop
                    ? stop.Position
                    : ((bestLane != Entity.Null) ? snappedPos : stop.Position);
                if (hasPhysicalStop)
                {
                    wpEntity = EntityManager.CreateEntity(routeData.m_ConnectedArchetype);
                    EntityManager.SetComponentData(wpEntity, new Game.Routes.Connected(stop.StopEntity));

                    // Ensure the ConnectedRoute buffer exists on the stop entity.
                    // Vanilla WaypointConnectionSystem automatically registers every Waypoint that has the Created component into
                    // the stop's ConnectedRoute buffer. Manually adding it causes duplicate entries.
                    if (!EntityManager.HasBuffer<ConnectedRoute>(stop.StopEntity))
                        EntityManager.AddBuffer<ConnectedRoute>(stop.StopEntity);

                    EntityManager.SetComponentData(wpEntity, new Game.Routes.Position(finalWpPos));
                }
                else
                {
                    // Curbside stop fallback: create with m_WaypointArchetype (NO Connected component, NO VehicleTiming component, NO Attached component)
                    wpEntity = EntityManager.CreateEntity(routeData.m_WaypointArchetype);
                    EntityManager.SetComponentData(wpEntity, new Game.Routes.Position(finalWpPos));
                }

                // For unconnected curbside waypoints, initialize RouteLane directly on the car lane.
                // For physical Connected stops, let CS2's WaypointConnectionSystem resolve AccessLane,
                // RouteLane, MoveLaneOffsets, and ValidateConnection from the stop entity.
                if (!hasPhysicalStop && bestLane != Entity.Null && EntityManager.HasComponent<RouteLane>(wpEntity))
                {
                    EntityManager.SetComponentData(wpEntity, new RouteLane(bestLane, bestLane, bestT, bestT));
                }

                EntityManager.SetComponentData(wpEntity, new PrefabRef(busLinePrefabEntity));
                EntityManager.SetComponentData(wpEntity, new Game.Routes.Waypoint(wpIndex));
                EntityManager.SetComponentData(wpEntity, new Game.Common.Owner(routeEntity));

                // Lifecycle components matching vanilla route waypoint creation
                if (!EntityManager.HasComponent<Created>(wpEntity))
                    EntityManager.AddComponentData(wpEntity, default(Created));
                if (!EntityManager.HasComponent<Updated>(wpEntity))
                    EntityManager.AddComponentData(wpEntity, default(Updated));

                waypointEntities.Add(wpEntity);
            }

            // Abort guard if too few valid waypoints were instantiated
            if (waypointEntities.Count < ABSOLUTE_MIN_STOPS)
            {
                log.Warn($"CreateBusLine: Line #{lineNumber} aborted because fewer than {ABSOLUTE_MIN_STOPS} valid waypoints could be created ({waypointEntities.Count} created). Cleaning up.");
                for (int w = 0; w < waypointEntities.Count; w++)
                {
                    EntityManager.DestroyEntity(waypointEntities[w]);
                }
                EntityManager.DestroyEntity(routeEntity);
                return false;
            }

            // 5. Create Route Segments connecting consecutive waypoints with CurveElement & PathElement buffers
            var segmentEntities = new List<Entity>(waypointEntities.Count);
            for (int i = 0; i < waypointEntities.Count; i++)
            {
                Entity segEntity = EntityManager.CreateEntity(routeData.m_SegmentArchetype);

                EntityManager.SetComponentData(segEntity, new PrefabRef(busLinePrefabEntity));
                EntityManager.SetComponentData(segEntity, new Game.Routes.Segment(i));
                EntityManager.SetComponentData(segEntity, new Game.Common.Owner(routeEntity));

                // Initialize PathTargets with clean defaults matching vanilla m_SegmentArchetype
                if (EntityManager.HasComponent<PathTargets>(segEntity))
                {
                    EntityManager.SetComponentData(segEntity, new PathTargets());
                }

                if (!EntityManager.HasBuffer<Game.Routes.CurveElement>(segEntity))
                    EntityManager.AddBuffer<Game.Routes.CurveElement>(segEntity);
                if (!EntityManager.HasBuffer<Game.Pathfind.PathElement>(segEntity))
                    EntityManager.AddBuffer<Game.Pathfind.PathElement>(segEntity);

                if (!EntityManager.HasComponent<Created>(segEntity))
                    EntityManager.AddComponentData(segEntity, default(Created));
                if (!EntityManager.HasComponent<Updated>(segEntity))
                    EntityManager.AddComponentData(segEntity, default(Updated));

                segmentEntities.Add(segEntity);
            }

            // 6. Fill the route's buffers last.
            var waypointsBuffer = EntityManager.GetBuffer<RouteWaypoint>(routeEntity);
            for (int i = 0; i < waypointEntities.Count; i++)
                waypointsBuffer.Add(new RouteWaypoint(waypointEntities[i]));

            var segmentsBuffer = EntityManager.GetBuffer<RouteSegment>(routeEntity);
            for (int i = 0; i < segmentEntities.Count; i++)
                segmentsBuffer.Add(new RouteSegment(segmentEntities[i]));

            if (!EntityManager.HasComponent<Created>(routeEntity))
                EntityManager.AddComponentData(routeEntity, default(Created));
            if (!EntityManager.HasComponent<Updated>(routeEntity))
                EntityManager.AddComponentData(routeEntity, default(Updated));
            if (!EntityManager.HasComponent<BatchesUpdated>(routeEntity))
                EntityManager.AddComponentData(routeEntity, default(BatchesUpdated));

            _createdRouteEntities.Add(routeEntity);

            return true;
        }

        /// <summary>
        /// Synchronizes <see cref="Game.Objects.Elevation"/> on a stop entity with its attached road's <see cref="Game.Net.Elevation"/>.
        /// Removes any stale object elevation when the road has zero or no elevation.
        /// </summary>
        private void SyncStopElevationWithRoad(Entity stopEntity, Entity roadEntity)
        {
            if (EntityManager.HasComponent<Game.Net.Elevation>(roadEntity))
            {
                var netElev = EntityManager.GetComponentData<Game.Net.Elevation>(roadEntity);
                if (math.any(netElev.m_Elevation != 0f))
                {
                    float elevVal = math.csum(netElev.m_Elevation) * 0.5f;
                    var elevFlags = (netElev.m_Elevation.x < 0f && netElev.m_Elevation.y < 0f)
                        ? Game.Objects.ElevationFlags.Lowered
                        : Game.Objects.ElevationFlags.OnAttachedParent;
                    var elevComp = new Game.Objects.Elevation(elevVal, elevFlags);
                    if (EntityManager.HasComponent<Game.Objects.Elevation>(stopEntity))
                        EntityManager.SetComponentData(stopEntity, elevComp);
                    else
                        EntityManager.AddComponentData(stopEntity, elevComp);
                    return;
                }
            }

            if (EntityManager.HasComponent<Game.Objects.Elevation>(stopEntity))
                EntityManager.RemoveComponent<Game.Objects.Elevation>(stopEntity);
        }

        private struct BrokenDiagSample
        {
            public int RouteIndex;
            public int SegmentIndex;
            public Entity RouteEntity;
            public Entity SegmentEntity;
            public Entity WaypointA;
            public Entity WaypointB;
        }

        private struct EndpointDiagFlags
        {
            public Entity StopEntity;
            public Entity RoadEntity;
            public Entity LaneEntity;
            public bool NoRouteLane;
            public bool NoAccessLane;
            public bool StopDetached;
            public bool NotInRoadSubObjects;
            public bool CarLaneSlaveOrUnsafe;
        }

        /// <summary>
        /// Logs categorized failure counts and detailed ECS component diagnostics for broken bus route segments
        /// alongside a small sample of healthy segments for side-by-side comparison.
        /// </summary>
        private void LogBrokenSegmentDiagnostics(IReadOnlyList<Entity> routeEntities, string contextLabel)
        {
            if (routeEntities == null || routeEntities.Count == 0)
                return;

            var prefabSystem = World.GetExistingSystemManaged<PrefabSystem>();

            int totalBusRoutes = 0;
            int totalSegments = 0;
            int brokenSegments = 0;
            int missingPathInfo = 0;
            int distLeZero = 0;
            int emptyPathElem = 0;
            int wpNoRouteLane = 0;
            int wpNoAccessLane = 0;
            int stopDetached = 0;
            int stopNotInRoadSubObjects = 0;
            int carLaneSlaveOrUnsafe = 0;

            var brokenSamples = new List<BrokenDiagSample>(12);
            var passingSamples = new List<BrokenDiagSample>(3);

            for (int r = 0; r < routeEntities.Count; r++)
            {
                Entity routeEntity = routeEntities[r];
                if (routeEntity == Entity.Null || !EntityManager.Exists(routeEntity))
                    continue;
                if (!EntityManager.HasBuffer<RouteWaypoint>(routeEntity) || !EntityManager.HasBuffer<RouteSegment>(routeEntity))
                    continue;
                if (!IsBusRouteEntity(routeEntity))
                    continue;

                totalBusRoutes++;
                var waypoints = EntityManager.GetBuffer<RouteWaypoint>(routeEntity);
                var segments = EntityManager.GetBuffer<RouteSegment>(routeEntity);
                if (waypoints.Length == 0 || segments.Length == 0)
                    continue;

                for (int s = 0; s < segments.Length; s++)
                {
                    Entity segEntity = segments[s].m_Segment;
                    if (segEntity == Entity.Null || !EntityManager.Exists(segEntity))
                        continue;

                    totalSegments++;
                    Entity wpA = s < waypoints.Length ? waypoints[s].m_Waypoint : Entity.Null;
                    Entity wpB = waypoints[(s + 1) % waypoints.Length].m_Waypoint;

                    bool hasPathInfo = EntityManager.HasComponent<Game.Pathfind.PathInformation>(segEntity);
                    float dist = hasPathInfo ? EntityManager.GetComponentData<Game.Pathfind.PathInformation>(segEntity).m_Distance : -1f;
                    int pathElemLen = EntityManager.HasBuffer<Game.Pathfind.PathElement>(segEntity)
                        ? EntityManager.GetBuffer<Game.Pathfind.PathElement>(segEntity).Length
                        : 0;

                    bool isBroken = !hasPathInfo || dist <= 0f || pathElemLen == 0;
                    if (isBroken)
                    {
                        brokenSegments++;
                        if (!hasPathInfo) missingPathInfo++;
                        else if (dist <= 0f) distLeZero++;
                        if (pathElemLen == 0) emptyPathElem++;

                        var flagsA = InspectEndpointDiag(wpA, isDeparture: true);
                        var flagsB = InspectEndpointDiag(wpB, isDeparture: false);

                        if (flagsA.NoRouteLane || flagsB.NoRouteLane) wpNoRouteLane++;
                        if (flagsA.NoAccessLane || flagsB.NoAccessLane) wpNoAccessLane++;
                        if (flagsA.StopDetached || flagsB.StopDetached) stopDetached++;
                        if (flagsA.NotInRoadSubObjects || flagsB.NotInRoadSubObjects) stopNotInRoadSubObjects++;
                        if (flagsA.CarLaneSlaveOrUnsafe || flagsB.CarLaneSlaveOrUnsafe) carLaneSlaveOrUnsafe++;

                        if (brokenSamples.Count < 12)
                        {
                            brokenSamples.Add(new BrokenDiagSample
                            {
                                RouteIndex = r,
                                SegmentIndex = s,
                                RouteEntity = routeEntity,
                                SegmentEntity = segEntity,
                                WaypointA = wpA,
                                WaypointB = wpB
                            });
                        }
                    }
                    else if (passingSamples.Count < 3)
                    {
                        passingSamples.Add(new BrokenDiagSample
                        {
                            RouteIndex = r,
                            SegmentIndex = s,
                            RouteEntity = routeEntity,
                            SegmentEntity = segEntity,
                            WaypointA = wpA,
                            WaypointB = wpB
                        });
                    }
                }
            }

            log.Info($"[BrokenDiag][{contextLabel}] Summary: routes={totalBusRoutes}, segments={totalSegments}, broken={brokenSegments} " +
                     $"(missingPathInfo={missingPathInfo}, distLeZero={distLeZero}, emptyPathElem={emptyPathElem}, " +
                     $"wp_noRouteLane={wpNoRouteLane}, wp_noAccessLane={wpNoAccessLane}, stop_detached={stopDetached}, " +
                     $"stop_notInRoadSubObjects={stopNotInRoadSubObjects}, carLaneSlaveOrUnsafe={carLaneSlaveOrUnsafe})");

            for (int i = 0; i < brokenSamples.Count; i++)
                LogSegmentDiagSample(contextLabel, "FAIL", i + 1, brokenSamples[i], prefabSystem);

            for (int i = 0; i < passingSamples.Count; i++)
                LogSegmentDiagSample(contextLabel, "PASS", i + 1, passingSamples[i], prefabSystem);
        }

        private bool IsBusRouteEntity(Entity routeEntity)
        {
            if (!EntityManager.HasComponent<PrefabRef>(routeEntity))
                return false;

            Entity prefab = EntityManager.GetComponentData<PrefabRef>(routeEntity).m_Prefab;
            if (prefab == Entity.Null || !EntityManager.HasComponent<TransportLineData>(prefab))
                return false;

            return EntityManager.GetComponentData<TransportLineData>(prefab).m_TransportType == Game.Prefabs.TransportType.Bus;
        }

        private EndpointDiagFlags InspectEndpointDiag(Entity wp, bool isDeparture)
        {
            var flags = new EndpointDiagFlags();
            if (wp == Entity.Null || !EntityManager.Exists(wp))
            {
                flags.NoRouteLane = true;
                flags.NoAccessLane = true;
                return flags;
            }

            if (EntityManager.HasComponent<RouteLane>(wp))
            {
                var rl = EntityManager.GetComponentData<RouteLane>(wp);
                flags.LaneEntity = isDeparture ? rl.m_StartLane : rl.m_EndLane;
                flags.NoRouteLane = rl.m_StartLane == Entity.Null || rl.m_EndLane == Entity.Null;
            }
            else
            {
                flags.NoRouteLane = true;
            }

            if (EntityManager.HasComponent<AccessLane>(wp))
                flags.NoAccessLane = EntityManager.GetComponentData<AccessLane>(wp).m_Lane == Entity.Null;
            else
                flags.NoAccessLane = true;

            if (EntityManager.HasComponent<Connected>(wp))
                flags.StopEntity = EntityManager.GetComponentData<Connected>(wp).m_Connected;

            if (flags.StopEntity != Entity.Null && EntityManager.Exists(flags.StopEntity))
            {
                if (EntityManager.HasComponent<Attached>(flags.StopEntity))
                {
                    flags.RoadEntity = EntityManager.GetComponentData<Attached>(flags.StopEntity).m_Parent;
                    if (flags.RoadEntity == Entity.Null)
                        flags.StopDetached = true;
                }
                else
                {
                    flags.StopDetached = true;
                }

                if (flags.RoadEntity != Entity.Null && EntityManager.Exists(flags.RoadEntity))
                    flags.NotInRoadSubObjects = !IsEntityInSubObjects(flags.RoadEntity, flags.StopEntity);
            }

            if (flags.LaneEntity != Entity.Null && EntityManager.Exists(flags.LaneEntity))
            {
                bool hasCarLane = EntityManager.HasComponent<Game.Net.CarLane>(flags.LaneEntity);
                bool isSlave = EntityManager.HasComponent<Game.Net.SlaveLane>(flags.LaneEntity);
                bool isUnsafe = hasCarLane && (EntityManager.GetComponentData<Game.Net.CarLane>(flags.LaneEntity).m_Flags & Game.Net.CarLaneFlags.Unsafe) != 0;
                flags.CarLaneSlaveOrUnsafe = !hasCarLane || isSlave || isUnsafe;
            }

            return flags;
        }

        private bool IsEntityInSubObjects(Entity parentEntity, Entity childEntity)
        {
            if (parentEntity == Entity.Null || !EntityManager.HasBuffer<Game.Objects.SubObject>(parentEntity))
                return false;

            var subObjects = EntityManager.GetBuffer<Game.Objects.SubObject>(parentEntity);
            for (int i = 0; i < subObjects.Length; i++)
            {
                if (subObjects[i].m_SubObject == childEntity)
                    return true;
            }

            return false;
        }

        private string SafePrefabName(PrefabSystem prefabSystem, Entity entity)
        {
            if (entity == Entity.Null || !EntityManager.Exists(entity) || !EntityManager.HasComponent<PrefabRef>(entity))
                return "none";

            Entity prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
            if (prefab == Entity.Null)
                return "nullPrefab";

            if (prefabSystem != null)
            {
                string name = prefabSystem.GetPrefabName(prefab);
                if (!string.IsNullOrEmpty(name))
                    return name;
            }

            return prefab.ToString();
        }

        private void LogSegmentDiagSample(string contextLabel, string kind, int sampleNum, BrokenDiagSample sample, PrefabSystem prefabSystem)
        {
            Entity seg = sample.SegmentEntity;
            string pathInfoStr = "none";
            if (EntityManager.HasComponent<Game.Pathfind.PathInformation>(seg))
            {
                var pi = EntityManager.GetComponentData<Game.Pathfind.PathInformation>(seg);
                pathInfoStr = $"dist={pi.m_Distance:F2},dur={pi.m_Duration:F2},state={pi.m_State},orig={pi.m_Origin},dest={pi.m_Destination}";
            }

            string pathTargetsStr = "none";
            if (EntityManager.HasComponent<Game.Routes.PathTargets>(seg))
            {
                var pt = EntityManager.GetComponentData<Game.Routes.PathTargets>(seg);
                pathTargetsStr = $"startLane={pt.m_StartLane},endLane={pt.m_EndLane},curve={pt.m_CurvePositions},readyA={pt.m_ReadyStartPosition},readyB={pt.m_ReadyEndPosition}";
            }

            int pathElems = EntityManager.HasBuffer<Game.Pathfind.PathElement>(seg) ? EntityManager.GetBuffer<Game.Pathfind.PathElement>(seg).Length : -1;
            int curveElems = EntityManager.HasBuffer<Game.Routes.CurveElement>(seg) ? EntityManager.GetBuffer<Game.Routes.CurveElement>(seg).Length : -1;

            if (sampleNum == 1 && kind == "FAIL")
                LogPrefabGeometryTable(prefabSystem);

            log.Info($"[BrokenDiag][{contextLabel}][{kind}#{sampleNum}] rIdx={sample.RouteIndex},sIdx={sample.SegmentIndex},route={sample.RouteEntity},seg={seg} " +
                     $"PathInfo=({pathInfoStr}) PathTargets=({pathTargetsStr}) pathElems={pathElems} curveElems={curveElems}");
            log.Info($"[BrokenDiag][{contextLabel}][{kind}#{sampleNum}]   EndpointA: {FormatEndpointDiag(sample.WaypointA, isDeparture: true, prefabSystem)}");
            log.Info($"[BrokenDiag][{contextLabel}][{kind}#{sampleNum}]   EndpointB: {FormatEndpointDiag(sample.WaypointB, isDeparture: false, prefabSystem)}");
            if (sampleNum <= 3)
                LogLaneConnectivityDiag(contextLabel, kind, sampleNum, sample.WaypointA, sample.WaypointB);
        }

        private string FormatEndpointDiag(Entity wp, bool isDeparture, PrefabSystem prefabSystem)
        {
            if (wp == Entity.Null || !EntityManager.Exists(wp))
                return "wp=null";

            string wpPos = EntityManager.HasComponent<Game.Routes.Position>(wp)
                ? EntityManager.GetComponentData<Game.Routes.Position>(wp).m_Position.ToString()
                : "none";

            string rlStr = "none";
            Entity targetLane = Entity.Null;
            if (EntityManager.HasComponent<RouteLane>(wp))
            {
                var rl = EntityManager.GetComponentData<RouteLane>(wp);
                targetLane = isDeparture ? rl.m_StartLane : rl.m_EndLane;
                rlStr = $"{rl.m_StartLane}({rl.m_StartCurvePos:F2})->{rl.m_EndLane}({rl.m_EndCurvePos:F2})";
            }

            string alStr = "none";
            if (EntityManager.HasComponent<AccessLane>(wp))
            {
                var al = EntityManager.GetComponentData<AccessLane>(wp);
                alStr = $"{al.m_Lane}({al.m_CurvePos:F2})";
            }

            Entity stopEntity = EntityManager.HasComponent<Connected>(wp)
                ? EntityManager.GetComponentData<Connected>(wp).m_Connected
                : Entity.Null;

            string stopStr = FormatStopDiag(stopEntity, prefabSystem, out Entity roadEntity);
            string roadStr = FormatRoadDiag(roadEntity, stopEntity, prefabSystem);
            string laneStr = FormatLaneDiag(targetLane);

            return $"wp={wp} pos={wpPos} RouteLane=({rlStr}) AccessLane=({alStr}) TargetLane=({laneStr}) | Stop=({stopStr}) | Road=({roadStr})";
        }

        private string FormatStopDiag(Entity stopEntity, PrefabSystem prefabSystem, out Entity roadEntity)
        {
            roadEntity = Entity.Null;
            if (stopEntity == Entity.Null || !EntityManager.Exists(stopEntity))
                return "none";

            string prefabName = SafePrefabName(prefabSystem, stopEntity);
            string trStr = EntityManager.HasComponent<Game.Objects.Transform>(stopEntity)
                ? EntityManager.GetComponentData<Game.Objects.Transform>(stopEntity).m_Position.ToString()
                : "none";

            string attStr = "detached";
            if (EntityManager.HasComponent<Attached>(stopEntity))
            {
                var att = EntityManager.GetComponentData<Attached>(stopEntity);
                roadEntity = att.m_Parent;
                attStr = $"{att.m_Parent}@{att.m_CurvePosition:F3}";
            }

            string elevStr = "none";
            if (EntityManager.HasComponent<Game.Objects.Elevation>(stopEntity))
            {
                var el = EntityManager.GetComponentData<Game.Objects.Elevation>(stopEntity);
                elevStr = $"{el.m_Elevation:F2}/{el.m_Flags}";
            }

            string tsStr = "none";
            if (EntityManager.HasComponent<Game.Routes.TransportStop>(stopEntity))
            {
                var ts = EntityManager.GetComponentData<Game.Routes.TransportStop>(stopEntity);
                tsStr = $"flags={ts.m_Flags},comfort={ts.m_ComfortFactor:F2},load={ts.m_LoadingFactor:F2}";
            }

            int subLanes = EntityManager.HasBuffer<Game.Net.SubLane>(stopEntity) ? EntityManager.GetBuffer<Game.Net.SubLane>(stopEntity).Length : -1;
            int connRoutes = EntityManager.HasBuffer<ConnectedRoute>(stopEntity) ? EntityManager.GetBuffer<ConnectedRoute>(stopEntity).Length : -1;

            string geomStr = "none";
            if (EntityManager.HasComponent<PrefabRef>(stopEntity))
            {
                Entity prefab = EntityManager.GetComponentData<PrefabRef>(stopEntity).m_Prefab;
                if (prefab != Entity.Null && EntityManager.HasComponent<ObjectGeometryData>(prefab))
                    geomStr = $"size={EntityManager.GetComponentData<ObjectGeometryData>(prefab).m_Size}";
            }

            return $"ent={stopEntity},prefab={prefabName},pos={trStr},att={attStr},elev={elevStr},subLanes={subLanes},connRoutes={connRoutes},ts=({tsStr}),geom=({geomStr})";
        }

        private string FormatRoadDiag(Entity roadEntity, Entity stopEntity, PrefabSystem prefabSystem)
        {
            if (roadEntity == Entity.Null || !EntityManager.Exists(roadEntity))
                return "none";

            string prefabName = SafePrefabName(prefabSystem, roadEntity);
            string compStr = "none";
            if (EntityManager.HasComponent<Game.Net.Composition>(roadEntity))
            {
                Entity compEdge = EntityManager.GetComponentData<Game.Net.Composition>(roadEntity).m_Edge;
                float compWidth = EntityManager.HasComponent<NetCompositionData>(compEdge)
                    ? EntityManager.GetComponentData<NetCompositionData>(compEdge).m_Width
                    : -1f;

                int buildableAreas = 0;
                float maxAreaW = 0f;
                if (EntityManager.HasBuffer<NetCompositionArea>(compEdge))
                {
                    var areas = EntityManager.GetBuffer<NetCompositionArea>(compEdge);
                    for (int i = 0; i < areas.Length; i++)
                    {
                        var area = areas[i];
                        if ((area.m_Flags & NetAreaFlags.Buildable) != 0 && (area.m_Flags & NetAreaFlags.Median) == 0)
                        {
                            buildableAreas++;
                            if (area.m_Width > maxAreaW)
                                maxAreaW = area.m_Width;
                        }
                    }
                }

                compStr = $"edge={compEdge},w={compWidth:F2},buildAreas={buildableAreas},maxAreaW={maxAreaW:F2}";
            }

            int subObjCount = EntityManager.HasBuffer<Game.Objects.SubObject>(roadEntity)
                ? EntityManager.GetBuffer<Game.Objects.SubObject>(roadEntity).Length
                : -1;
            bool inSubObj = stopEntity != Entity.Null && IsEntityInSubObjects(roadEntity, stopEntity);

            string elevStr = EntityManager.HasComponent<Game.Net.Elevation>(roadEntity)
                ? EntityManager.GetComponentData<Game.Net.Elevation>(roadEntity).m_Elevation.ToString()
                : "none";

            int carLaneCount = 0;
            int pedLaneCount = 0;
            if (EntityManager.HasBuffer<Game.Net.SubLane>(roadEntity))
            {
                var subLanes = EntityManager.GetBuffer<Game.Net.SubLane>(roadEntity);
                for (int i = 0; i < subLanes.Length; i++)
                {
                    Entity lane = subLanes[i].m_SubLane;
                    if (EntityManager.HasComponent<Game.Net.CarLane>(lane)) carLaneCount++;
                    if (EntityManager.HasComponent<Game.Net.PedestrianLane>(lane)) pedLaneCount++;
                }
            }

            return $"ent={roadEntity},prefab={prefabName},comp=({compStr}),subObjs={subObjCount},inSubObj={inSubObj},elev={elevStr},carLanes={carLaneCount},pedLanes={pedLaneCount}";
        }

        private string FormatLaneDiag(Entity laneEntity)
        {
            if (laneEntity == Entity.Null || !EntityManager.Exists(laneEntity))
                return "none";

            string carFlags = EntityManager.HasComponent<Game.Net.CarLane>(laneEntity)
                ? EntityManager.GetComponentData<Game.Net.CarLane>(laneEntity).m_Flags.ToString()
                : "notCar";

            string roadTypes = "none";
            if (EntityManager.HasComponent<PrefabRef>(laneEntity))
            {
                Entity lanePrefab = EntityManager.GetComponentData<PrefabRef>(laneEntity).m_Prefab;
                if (lanePrefab != Entity.Null && EntityManager.HasComponent<CarLaneData>(lanePrefab))
                    roadTypes = EntityManager.GetComponentData<CarLaneData>(lanePrefab).m_RoadTypes.ToString();
            }

            bool isSlave = EntityManager.HasComponent<Game.Net.SlaveLane>(laneEntity);
            bool isMaster = EntityManager.HasComponent<Game.Net.MasterLane>(laneEntity);
            bool isPed = EntityManager.HasComponent<Game.Net.PedestrianLane>(laneEntity);

            return $"ent={laneEntity},car={carFlags},types={roadTypes},slave={isSlave},master={isMaster},ped={isPed}";
        }

        private void LogPrefabGeometryTable(PrefabSystem prefabSystem)
        {
            if (_busStopPrefabQuery.IsEmptyIgnoreFilter)
                return;

            using var entities = _busStopPrefabQuery.ToEntityArray(Allocator.Temp);
            using var stopDataArray = _busStopPrefabQuery.ToComponentDataArray<TransportStopData>(Allocator.Temp);
            for (int i = 0; i < entities.Length; i++)
            {
                var sd = stopDataArray[i];
                if (sd.m_TransportType != TransportType.Bus || !sd.m_PassengerTransport)
                    continue;

                Entity p = entities[i];
                string pName = prefabSystem != null ? (prefabSystem.GetPrefabName(p) ?? p.ToString()) : p.ToString();
                string geomStr = "noGeom";
                if (EntityManager.HasComponent<ObjectGeometryData>(p))
                {
                    var g = EntityManager.GetComponentData<ObjectGeometryData>(p);
                    geomStr = $"flags={g.m_Flags},size={g.m_Size},legSize={g.m_LegSize},legOff={g.m_LegOffset}";
                }
                log.Info($"[BrokenDiag][Prefab] name={pName},sign={IsBusStopSign(p)},{geomStr}");
            }
        }

        private string FormatPathNode(Game.Pathfind.PathNode node)
        {
            return $"o={node.GetOwnerIndex()}:l={node.GetLaneIndex()}:c={node.GetCurvePos():F2}";
        }

        private string TryGetPfFlags(Game.Pathfind.NativePathfindData pfData, bool hasPf, Entity lane)
        {
            if (!hasPf || lane == Entity.Null)
                return "na";
            try
            {
                if (pfData.GetEdge(lane, out Game.Pathfind.EdgeID eid))
                    return pfData.GetFlags(eid).ToString();
                return "missingInPf";
            }
            catch (Exception ex)
            {
                return $"err:{ex.GetType().Name}";
            }
        }

        private string FormatDetailedLane(Entity lane, Game.Pathfind.NativePathfindData pfData, bool hasPf)
        {
            if (lane == Entity.Null || !EntityManager.Exists(lane))
                return "null";

            string nodesStr = EntityManager.HasComponent<Game.Net.Lane>(lane)
                ? $"s=({FormatPathNode(EntityManager.GetComponentData<Game.Net.Lane>(lane).m_StartNode)}),m=({FormatPathNode(EntityManager.GetComponentData<Game.Net.Lane>(lane).m_MiddleNode)}),e=({FormatPathNode(EntityManager.GetComponentData<Game.Net.Lane>(lane).m_EndNode)})"
                : "noLane";

            string edgeDeltaStr = EntityManager.HasComponent<Game.Net.EdgeLane>(lane)
                ? EntityManager.GetComponentData<Game.Net.EdgeLane>(lane).m_EdgeDelta.ToString()
                : "noEdgeLane";

            string carStr = "notCar";
            if (EntityManager.HasComponent<Game.Net.CarLane>(lane))
            {
                var cl = EntityManager.GetComponentData<Game.Net.CarLane>(lane);
                carStr = $"flags={cl.m_Flags},restr={cl.m_AccessRestriction},blk={cl.m_BlockageStart}-{cl.m_BlockageEnd},caut={cl.m_CautionStart}-{cl.m_CautionEnd},cwg={cl.m_CarriagewayGroup},spd={cl.m_SpeedLimit:F1}";
            }

            string masterStr = "notMaster";
            if (EntityManager.HasComponent<Game.Net.MasterLane>(lane))
            {
                var ml = EntityManager.GetComponentData<Game.Net.MasterLane>(lane);
                masterStr = $"mFlags={ml.m_Flags},grp={ml.m_Group},idx={ml.m_MinIndex}..{ml.m_MaxIndex}";
            }

            string maxSizeStr = "none";
            if (EntityManager.HasComponent<PrefabRef>(lane))
            {
                Entity p = EntityManager.GetComponentData<PrefabRef>(lane).m_Prefab;
                if (p != Entity.Null && EntityManager.HasComponent<CarLaneData>(p))
                {
                    var cld = EntityManager.GetComponentData<CarLaneData>(p);
                    maxSizeStr = $"{cld.m_RoadTypes}/{cld.m_MaxSize}";
                }
            }

            string ownerStr = EntityManager.HasComponent<Game.Common.Owner>(lane)
                ? EntityManager.GetComponentData<Game.Common.Owner>(lane).m_Owner.ToString()
                : "none";

            string pfFlags = TryGetPfFlags(pfData, hasPf, lane);
            return $"owner={ownerStr},{nodesStr},ed={edgeDeltaStr},{carStr},{masterStr},type={maxSizeStr},pf=({pfFlags})";
        }

        private string SummarizeRoadEdge(Entity roadEntity)
        {
            if (roadEntity == Entity.Null || !EntityManager.Exists(roadEntity))
                return "null";

            string edgeStr = "noEdge";
            if (EntityManager.HasComponent<Game.Net.Edge>(roadEntity))
            {
                var e = EntityManager.GetComponentData<Game.Net.Edge>(roadEntity);
                edgeStr = $"{e.m_Start}->{e.m_End}";
            }

            string curveStr = "";
            if (EntityManager.HasComponent<Curve>(roadEntity))
            {
                var c = EntityManager.GetComponentData<Curve>(roadEntity);
                float2 t0 = math.normalizesafe(MathUtils.Tangent(c.m_Bezier, 0f).xz);
                float2 t1 = math.normalizesafe(MathUtils.Tangent(c.m_Bezier, 1f).xz);
                curveStr = $",p0={c.m_Bezier.a.xz},p1={c.m_Bezier.d.xz},tan0=({t0.x:F2},{t0.y:F2}),tan1=({t1.x:F2},{t1.y:F2})";
            }

            return $"edge=({edgeStr}){curveStr}";
        }

        private void LogLaneConnectivityDiag(string contextLabel, string kind, int sampleNum, Entity wpA, Entity wpB)
        {
            Entity laneA = (wpA != Entity.Null && EntityManager.HasComponent<RouteLane>(wpA))
                ? EntityManager.GetComponentData<RouteLane>(wpA).m_StartLane
                : Entity.Null;
            Entity laneB = (wpB != Entity.Null && EntityManager.HasComponent<RouteLane>(wpB))
                ? EntityManager.GetComponentData<RouteLane>(wpB).m_EndLane
                : Entity.Null;

            Entity roadA = Entity.Null;
            if (wpA != Entity.Null && EntityManager.HasComponent<Connected>(wpA))
            {
                Entity sA = EntityManager.GetComponentData<Connected>(wpA).m_Connected;
                if (sA != Entity.Null && EntityManager.HasComponent<Attached>(sA))
                    roadA = EntityManager.GetComponentData<Attached>(sA).m_Parent;
            }

            Entity roadB = Entity.Null;
            if (wpB != Entity.Null && EntityManager.HasComponent<Connected>(wpB))
            {
                Entity sB = EntityManager.GetComponentData<Connected>(wpB).m_Connected;
                if (sB != Entity.Null && EntityManager.HasComponent<Attached>(sB))
                    roadB = EntityManager.GetComponentData<Attached>(sB).m_Parent;
            }

            bool hasPf = false;
            Game.Pathfind.NativePathfindData pfData = default;
            try
            {
                var pqs = World.GetExistingSystemManaged<Game.Pathfind.PathfindQueueSystem>();
                if (pqs != null)
                {
                    pfData = pqs.GetDataContainer(out Unity.Jobs.JobHandle pfDeps);
                    pfDeps.Complete();
                    hasPf = pfData.IsCreated;
                }
            }
            catch (Exception ex)
            {
                log.Info($"[BrokenDiag][{contextLabel}][{kind}#{sampleNum}]   PathfindData unavailable: {ex.Message}");
            }

            log.Info($"[BrokenDiag][{contextLabel}][{kind}#{sampleNum}]   GraphA: {SummarizeRoadEdge(roadA)} | LaneA: {FormatDetailedLane(laneA, pfData, hasPf)}");
            log.Info($"[BrokenDiag][{contextLabel}][{kind}#{sampleNum}]   GraphB: {SummarizeRoadEdge(roadB)} | LaneB: {FormatDetailedLane(laneB, pfData, hasPf)}");

            if (roadA != Entity.Null && laneA != Entity.Null && EntityManager.HasComponent<Game.Net.Edge>(roadA) && EntityManager.HasComponent<Game.Net.Lane>(laneA))
            {
                var eA = EntityManager.GetComponentData<Game.Net.Edge>(roadA);
                var lA = EntityManager.GetComponentData<Game.Net.Lane>(laneA);
                Entity exitNode = EntityManager.HasComponent<Game.Net.CarLane>(laneA) &&
                                  (EntityManager.GetComponentData<Game.Net.CarLane>(laneA).m_Flags & Game.Net.CarLaneFlags.Invert) != 0
                    ? eA.m_Start
                    : eA.m_End;

                int totalCar = 0;
                int matchEndNode = 0;
                int matchStartNode = 0;
                var matches = new System.Text.StringBuilder();
                var nonMatches = new System.Text.StringBuilder();
                int nonMatchCount = 0;
                if (exitNode != Entity.Null && EntityManager.HasBuffer<Game.Net.SubLane>(exitNode))
                {
                    var nodeSubLanes = EntityManager.GetBuffer<Game.Net.SubLane>(exitNode);
                    for (int i = 0; i < nodeSubLanes.Length; i++)
                    {
                        Entity sl = nodeSubLanes[i].m_SubLane;
                        if (!EntityManager.HasComponent<Game.Net.CarLane>(sl) || !EntityManager.HasComponent<Game.Net.Lane>(sl))
                            continue;
                        totalCar++;
                        var nl = EntityManager.GetComponentData<Game.Net.Lane>(sl);
                        bool fromEnd = nl.m_StartNode.EqualsIgnoreCurvePos(lA.m_EndNode) || nl.m_EndNode.EqualsIgnoreCurvePos(lA.m_EndNode);
                        bool fromStart = nl.m_StartNode.EqualsIgnoreCurvePos(lA.m_StartNode) || nl.m_EndNode.EqualsIgnoreCurvePos(lA.m_StartNode);
                        if (fromEnd) matchEndNode++;
                        if (fromStart) matchStartNode++;
                        if (fromEnd)
                        {
                            var cl = EntityManager.GetComponentData<Game.Net.CarLane>(sl);
                            string pf = TryGetPfFlags(pfData, hasPf, sl);
                            matches.Append($" [{sl} flags={cl.m_Flags} slave={EntityManager.HasComponent<Game.Net.SlaveLane>(sl)} pf=({pf}) s=({FormatPathNode(nl.m_StartNode)})->e=({FormatPathNode(nl.m_EndNode)})]");
                        }
                        else if (nonMatchCount < 4)
                        {
                            nonMatchCount++;
                            nonMatches.Append($" [{sl} slave={EntityManager.HasComponent<Game.Net.SlaveLane>(sl)} s=({FormatPathNode(nl.m_StartNode)})->e=({FormatPathNode(nl.m_EndNode)})]");
                        }
                    }
                }
                string sampleSuffix = matchEndNode > 0 ? matches.ToString() : (nonMatchCount > 0 ? $" sampleNonMatch:{nonMatches}" : "");
                log.Info($"[BrokenDiag][{contextLabel}][{kind}#{sampleNum}]   ExitNode={exitNode} totalCar={totalCar} matchEnd={matchEndNode} matchStart={matchStartNode}{sampleSuffix}");
            }

            if (roadB != Entity.Null && laneB != Entity.Null && EntityManager.HasComponent<Game.Net.Edge>(roadB) && EntityManager.HasComponent<Game.Net.Lane>(laneB))
            {
                var eB = EntityManager.GetComponentData<Game.Net.Edge>(roadB);
                Entity entryNodeB = EntityManager.HasComponent<Game.Net.CarLane>(laneB) &&
                                    (EntityManager.GetComponentData<Game.Net.CarLane>(laneB).m_Flags & Game.Net.CarLaneFlags.Invert) != 0
                    ? eB.m_End
                    : eB.m_Start;
                if (entryNodeB != Entity.Null)
                {
                    log.Info($"[BrokenDiag][{contextLabel}][{kind}#{sampleNum}]   EntryNodeB={entryNodeB}: {TrafficCompatState.DescribeNodeState(EntityManager, entryNodeB)}");
                }
            }

            if (laneA != Entity.Null && laneB != Entity.Null)
            {
                string bfsRes = TraceLaneGraphBfs(laneA, laneB, pfData, hasPf);
                log.Info($"[BrokenDiag][{contextLabel}][{kind}#{sampleNum}]   LaneBFS: {bfsRes}");
            }
        }

        private void AddOwnerAndConnected(Entity entity, List<Entity> list, HashSet<Entity> seen, bool includeNodeConnectedEdges)
        {
            if (entity == Entity.Null)
                return;
            if (seen.Add(entity))
                list.Add(entity);
            if (includeNodeConnectedEdges && EntityManager.HasBuffer<ConnectedEdge>(entity))
            {
                var ce = EntityManager.GetBuffer<ConnectedEdge>(entity);
                for (int i = 0; i < ce.Length; i++)
                {
                    Entity edgeEnt = ce[i].m_Edge;
                    if (edgeEnt != Entity.Null && seen.Add(edgeEnt))
                        list.Add(edgeEnt);
                }
            }
        }

        private string TraceLaneGraphBfs(Entity startLane, Entity endLane, Game.Pathfind.NativePathfindData pfData, bool hasPf)
        {
            var queue = new Queue<(Entity lane, int depth)>();
            var visited = new HashSet<Entity>();
            var visitedOrder = new List<Entity>(16);
            var parent = new Dictionary<Entity, Entity>();
            queue.Enqueue((startLane, 0));
            visited.Add(startLane);
            visitedOrder.Add(startLane);

            int expansions = 0;
            bool found = false;
            const int maxExpansions = 1500;

            while (queue.Count > 0 && expansions < maxExpansions)
            {
                var (curLane, depth) = queue.Dequeue();
                expansions++;
                if (curLane == endLane)
                {
                    found = true;
                    break;
                }
                if (depth >= 24)
                    continue;

                if (!EntityManager.HasComponent<Game.Net.Lane>(curLane) || !EntityManager.HasComponent<Game.Common.Owner>(curLane))
                    continue;

                var lData = EntityManager.GetComponentData<Game.Net.Lane>(curLane);
                Entity owner = EntityManager.GetComponentData<Game.Common.Owner>(curLane).m_Owner;

                var searchOwners = new List<Entity>(12);
                var seenOwners = new HashSet<Entity>();
                if (EntityManager.HasComponent<Game.Net.Edge>(owner))
                {
                    var ed = EntityManager.GetComponentData<Game.Net.Edge>(owner);
                    AddOwnerAndConnected(owner, searchOwners, seenOwners, false);
                    AddOwnerAndConnected(ed.m_Start, searchOwners, seenOwners, true);
                    AddOwnerAndConnected(ed.m_End, searchOwners, seenOwners, true);
                    if (EntityManager.HasBuffer<ConnectedNode>(owner))
                    {
                        var cns = EntityManager.GetBuffer<ConnectedNode>(owner);
                        for (int c = 0; c < cns.Length; c++)
                            AddOwnerAndConnected(cns[c].m_Node, searchOwners, seenOwners, true);
                    }
                }
                else if (EntityManager.HasBuffer<ConnectedEdge>(owner))
                {
                    AddOwnerAndConnected(owner, searchOwners, seenOwners, true);
                }

                for (int o = 0; o < searchOwners.Count; o++)
                {
                    Entity candOwner = searchOwners[o];
                    if (!EntityManager.HasBuffer<Game.Net.SubLane>(candOwner))
                        continue;

                    var subLanes = EntityManager.GetBuffer<Game.Net.SubLane>(candOwner);
                    for (int i = 0; i < subLanes.Length; i++)
                    {
                        Entity nextLane = subLanes[i].m_SubLane;
                        if (visited.Contains(nextLane))
                            continue;
                        if (!EntityManager.HasComponent<Game.Net.CarLane>(nextLane) || !EntityManager.HasComponent<Game.Net.Lane>(nextLane))
                            continue;
                        if (EntityManager.HasComponent<Game.Net.SlaveLane>(nextLane))
                            continue;

                        var nl = EntityManager.GetComponentData<Game.Net.Lane>(nextLane);
                        bool matchesFwd = nl.m_StartNode.EqualsIgnoreCurvePos(lData.m_EndNode);
                        bool nextTwoway = (EntityManager.GetComponentData<Game.Net.CarLane>(nextLane).m_Flags & Game.Net.CarLaneFlags.Twoway) != 0;
                        bool matchesBwd = nextTwoway && nl.m_EndNode.EqualsIgnoreCurvePos(lData.m_EndNode);
                        if (!matchesFwd && !matchesBwd)
                            continue;

                        if (EntityManager.HasComponent<PrefabRef>(nextLane))
                        {
                            Entity lp = EntityManager.GetComponentData<PrefabRef>(nextLane).m_Prefab;
                            if (lp != Entity.Null && EntityManager.HasComponent<CarLaneData>(lp))
                            {
                                if ((EntityManager.GetComponentData<CarLaneData>(lp).m_RoadTypes & Game.Net.RoadTypes.Car) == 0)
                                    continue;
                            }
                        }

                        visited.Add(nextLane);
                        if (visitedOrder.Count < 12)
                            visitedOrder.Add(nextLane);
                        parent[nextLane] = curLane;
                        queue.Enqueue((nextLane, depth + 1));
                    }
                }
            }

            if (!found)
            {
                if (visitedOrder.Count <= 12)
                {
                    var trace = new System.Text.StringBuilder();
                    for (int i = 0; i < visitedOrder.Count; i++)
                    {
                        Entity ve = visitedOrder[i];
                        Entity vo = EntityManager.HasComponent<Game.Common.Owner>(ve) ? EntityManager.GetComponentData<Game.Common.Owner>(ve).m_Owner : Entity.Null;
                        string edgeInfo = "";
                        if (vo != Entity.Null && EntityManager.HasComponent<Game.Net.Edge>(vo))
                        {
                            var ved = EntityManager.GetComponentData<Game.Net.Edge>(vo);
                            edgeInfo = $"({ved.m_Start}->{ved.m_End})";
                        }
                        var vl = EntityManager.HasComponent<Game.Net.Lane>(ve) ? EntityManager.GetComponentData<Game.Net.Lane>(ve) : default;
                        trace.Append($" {ve}[o={vo}{edgeInfo} s=({FormatPathNode(vl.m_StartNode)})->e=({FormatPathNode(vl.m_EndNode)})]");
                    }
                    return $"NOT_FOUND (visited={visited.Count}, expansions={expansions}) trace:{trace}";
                }
                return $"NOT_FOUND (visited={visited.Count}, expansions={expansions})";
            }

            var path = new List<Entity>();
            Entity pCur = endLane;
            while (pCur != Entity.Null)
            {
                path.Add(pCur);
                parent.TryGetValue(pCur, out pCur);
            }
            path.Reverse();

            var sb = new System.Text.StringBuilder();
            sb.Append($"FOUND hops={path.Count - 1} (visited={visited.Count}):");
            for (int i = 0; i < path.Count; i++)
            {
                Entity le = path[i];
                var cl = EntityManager.GetComponentData<Game.Net.CarLane>(le);
                string pf = TryGetPfFlags(pfData, hasPf, le);
                sb.Append($" {le}[flags={cl.m_Flags},restr={cl.m_AccessRestriction},pf={pf}]");
                if (i + 1 < path.Count) sb.Append(" ->");
            }
            return sb.ToString();
        }
    }
}

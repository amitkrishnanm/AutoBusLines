using System;
using System.Collections.Generic;
using Colossal.Logging;
using Colossal.Mathematics;
using Game;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Prefabs;
using Game.Rendering;
using Game.Routes;
using Game.Tools;
using Game.Areas;
using Unity.Entities;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Edge = Game.Net.Edge;

namespace AutoBusLines
{
    public partial class BusLineGenerator
    {
        public string GetRoadStreetName(Entity roadEntity)
        {
            if (roadEntity == Entity.Null || !EntityManager.Exists(roadEntity))
                return null;

            if (_nameSystem == null)
            {
                _nameSystem = World.GetOrCreateSystemManaged<Game.UI.NameSystem>();
            }

            try
            {
                // 1. Try to get the road's aggregate entity (where CS2 stores street names)
                if (EntityManager.HasComponent<Game.Net.Aggregated>(roadEntity))
                {
                    var agg = EntityManager.GetComponentData<Game.Net.Aggregated>(roadEntity);
                    if (agg.m_Aggregate != Entity.Null && EntityManager.Exists(agg.m_Aggregate))
                    {
                        string aggName = _nameSystem?.GetRenderedLabelName(agg.m_Aggregate);
                        if (IsValidStreetName(aggName))
                            return aggName.Trim();
                    }
                }

                // 2. Try directly on the road entity
                string directName = _nameSystem?.GetRenderedLabelName(roadEntity);
                if (IsValidStreetName(directName))
                    return directName.Trim();

                // 3. Fallback to BuildingUtils.GetAddress
                if (Game.Buildings.BuildingUtils.GetAddress(EntityManager, Entity.Null, roadEntity, 0.5f, out Entity addressRoad, out _))
                {
                    if (addressRoad != Entity.Null && EntityManager.Exists(addressRoad))
                    {
                        string addrName = _nameSystem?.GetRenderedLabelName(addressRoad);
                        if (IsValidStreetName(addrName))
                            return addrName.Trim();
                    }
                }
            }
            catch (Exception ex)
            {
                log.Warn($"GetRoadStreetName failed for road entity {roadEntity.Index}: {ex.Message}");
            }

            return null;
        }

        private static bool IsValidStreetName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;
            if (name.StartsWith("Assets.") || name.StartsWith("Roads.") || name.StartsWith("SubServices.") || name.StartsWith("Common."))
                return false;
            return true;
        }

        private bool TryFindExistingStopOnSpan(BlockSpan span,
            Dictionary<Entity, List<PlacedStop>> existingStopsByRoad, out PlacedStop foundStop)
        {
            foundStop = default;
            for (int e = 0; e < span.Edges.Count; e++)
            {
                var roadEntity = span.Edges[e].EdgeEntity;
                if (TryFindExistingStopOnRoad(roadEntity, span.MidPosition, span.HubEntity, existingStopsByRoad, out foundStop))
                {
                    return true;
                }
            }
            return false;
        }

        private bool TryFindExistingStopOnRoad(Entity roadEntity, float3 centerPos, Entity hubEntity,
            Dictionary<Entity, List<PlacedStop>> existingStopsByRoad, out PlacedStop foundStop)
        {
            foundStop = default;
            if (IsDrawbridge(roadEntity))
                return false;

            // 1. Check if attached road directly matches
            if (existingStopsByRoad.ContainsKey(roadEntity) && existingStopsByRoad[roadEntity].Count > 0)
            {
                foundStop = existingStopsByRoad[roadEntity][0];
                return true;
            }

            // 2. Check SubObject buffer on roadEntity
            if (EntityManager.HasBuffer<Game.Objects.SubObject>(roadEntity))
            {
                var subObjects = EntityManager.GetBuffer<Game.Objects.SubObject>(roadEntity);
                for (int sub = 0; sub < subObjects.Length; sub++)
                {
                    var subEnt = subObjects[sub].m_SubObject;
                    if (EntityManager.HasComponent<Game.Routes.TransportStop>(subEnt) &&
                        EntityManager.HasComponent<PrefabRef>(subEnt))
                    {
                        var p = EntityManager.GetComponentData<PrefabRef>(subEnt).m_Prefab;
                        if (EntityManager.HasComponent<TransportStopData>(p))
                        {
                            var td = EntityManager.GetComponentData<TransportStopData>(p);
                            if (td.m_TransportType == TransportType.Bus && td.m_PassengerTransport)
                            {
                                float3 sPos = centerPos;
                                float3 sFwd = new float3(0, 0, 1);
                                if (EntityManager.HasComponent<Game.Objects.Transform>(subEnt))
                                {
                                    var tForm = EntityManager.GetComponentData<Game.Objects.Transform>(subEnt);
                                    sPos = tForm.m_Position;
                                    sFwd = math.mul(tForm.m_Rotation, new float3(0, 0, 1));
                                }

                                foundStop = new PlacedStop
                                {
                                    StopEntity = subEnt,
                                    Position = sPos,
                                    Forward = sFwd,
                                    RoadEntity = roadEntity,
                                    HubEntity = hubEntity,
                                    AngleFromHub = 0f,
                                    DistFromHub = 0f,
                                    IsOutbound = true
                                };
                                return true;
                            }
                        }
                    }
                }
            }

            return false;
        }

        private bool IsOneWayRoad(Entity roadEntity, out bool forwardFlow)
        {
            forwardFlow = true;
            if (EntityManager.HasBuffer<Game.Net.SubLane>(roadEntity))
            {
                var subLanes = EntityManager.GetBuffer<Game.Net.SubLane>(roadEntity);
                int forwardCarLanes = 0;
                int backwardCarLanes = 0;

                for (int i = 0; i < subLanes.Length; i++)
                {
                    var laneEntity = subLanes[i].m_SubLane;
                    if (EntityManager.HasComponent<Game.Net.CarLane>(laneEntity))
                    {
                        if (EntityManager.HasComponent<PrefabRef>(laneEntity))
                        {
                            var prefab = EntityManager.GetComponentData<PrefabRef>(laneEntity).m_Prefab;
                            if (EntityManager.HasComponent<Game.Prefabs.CarLaneData>(prefab))
                            {
                                var laneData = EntityManager.GetComponentData<Game.Prefabs.CarLaneData>(prefab);
                                if ((laneData.m_RoadTypes & Game.Net.RoadTypes.Car) == 0)
                                    continue; // Skip bicycle tracks and non-car lanes
                            }
                        }

                        var carLane = EntityManager.GetComponentData<Game.Net.CarLane>(laneEntity);
                        if ((carLane.m_Flags & Game.Net.CarLaneFlags.Twoway) != 0)
                        {
                            return false;
                        }

                        if ((carLane.m_Flags & Game.Net.CarLaneFlags.Invert) != 0)
                        {
                            backwardCarLanes++;
                        }
                        else
                        {
                            forwardCarLanes++;
                        }
                    }
                }

                if (forwardCarLanes > 0 && backwardCarLanes == 0)
                {
                    forwardFlow = true;
                    return true;
                }
                if (backwardCarLanes > 0 && forwardCarLanes == 0)
                {
                    forwardFlow = false;
                    return true;
                }
            }
            return false;
        }

        private bool IsDrawbridge(Entity roadEntity)
        {
            if (roadEntity == Entity.Null || !EntityManager.Exists(roadEntity))
                return false;

            // 1. Direct PrefabRef check on roadEntity for MoveableBridgeData component
            if (EntityManager.HasComponent<PrefabRef>(roadEntity))
            {
                var prefabRef = EntityManager.GetComponentData<PrefabRef>(roadEntity);
                var prefab = prefabRef.m_Prefab;
                if (prefab != Entity.Null && EntityManager.Exists(prefab))
                {
                    if (EntityManager.HasComponent<Game.Prefabs.MoveableBridgeData>(prefab))
                        return true;

                    if (_prefabSystem != null)
                    {
                        string pName = _prefabSystem.GetPrefabName(prefab);
                        if (IsDrawbridgeName(pName))
                            return true;
                    }
                }
            }

            // 2. Check SubObjects on the roadEntity (movable bridge deck / lifting mechanisms)
            if (EntityManager.HasBuffer<Game.Objects.SubObject>(roadEntity))
            {
                var subObjects = EntityManager.GetBuffer<Game.Objects.SubObject>(roadEntity);
                for (int s = 0; s < subObjects.Length; s++)
                {
                    var sub = subObjects[s].m_SubObject;
                    if (sub != Entity.Null && EntityManager.Exists(sub) && EntityManager.HasComponent<PrefabRef>(sub))
                    {
                        var subPrefab = EntityManager.GetComponentData<PrefabRef>(sub).m_Prefab;
                        if (subPrefab != Entity.Null && EntityManager.Exists(subPrefab))
                        {
                            if (EntityManager.HasComponent<Game.Prefabs.MoveableBridgeData>(subPrefab))
                                return true;

                            if (_prefabSystem != null && IsDrawbridgeName(_prefabSystem.GetPrefabName(subPrefab)))
                                return true;
                        }
                    }
                }
            }

            return false;
        }

        private bool IsElevatedOrBridge(Entity roadEntity)
        {
            if (roadEntity == Entity.Null || !EntityManager.Exists(roadEntity))
                return false;

            if (IsDrawbridge(roadEntity))
                return true;

            if (EntityManager.HasComponent<Game.Net.Composition>(roadEntity))
            {
                var comp = EntityManager.GetComponentData<Game.Net.Composition>(roadEntity);
                if (comp.m_Edge != Entity.Null && EntityManager.HasComponent<Game.Prefabs.NetCompositionData>(comp.m_Edge))
                {
                    var netComp = EntityManager.GetComponentData<Game.Prefabs.NetCompositionData>(comp.m_Edge);
                    if ((netComp.m_Flags.m_General & (CompositionFlags.General.Elevated | CompositionFlags.General.Tunnel)) != 0)
                        return true;
                }
            }

            if (EntityManager.HasComponent<PrefabRef>(roadEntity))
            {
                var prefab = EntityManager.GetComponentData<PrefabRef>(roadEntity).m_Prefab;
                if (prefab != Entity.Null && _prefabSystem != null)
                {
                    string name = _prefabSystem.GetPrefabName(prefab);
                    if (!string.IsNullOrEmpty(name) && name.IndexOf("Bridge", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }

            return false;
        }

        private static bool IsDrawbridgeName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;

            return name.IndexOf("Drawbridge", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("MoveableBridge", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("MovableBridge", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("OpeningBridge", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("Bascule", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("LiftBridge", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("SwingBridge", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private bool TryGetSpanPointAtDistance(BlockSpan span, float targetDist, out Entity edgeEntity, out float t, out float3 pos, out float3 tangent)
        {
            edgeEntity = Entity.Null;
            t = 0.5f;
            pos = float3.zero;
            tangent = new float3(0, 0, 1);

            if (span.Edges == null || span.Edges.Count == 0)
                return false;

            float acc = 0f;
            SpanEdge chosenEdge = span.Edges[span.Edges.Count - 1];
            float distInEdge = 0f;

            for (int e = 0; e < span.Edges.Count; e++)
            {
                var se = span.Edges[e];
                if (acc + se.Length >= targetDist || e == span.Edges.Count - 1)
                {
                    chosenEdge = se;
                    distInEdge = math.clamp(targetDist - acc, 0f, se.Length);
                    break;
                }
                acc += se.Length;
            }

            if (!EntityManager.HasComponent<Curve>(chosenEdge.EdgeEntity))
                return false;

            var curve = EntityManager.GetComponentData<Curve>(chosenEdge.EdgeEntity);
            float edgeLen = chosenEdge.Length;
            float safeFraction = (edgeLen > 0.001f) ? (distInEdge / edgeLen) : 0.5f;

            if (edgeLen >= 42.0f)
            {
                float minF = 20.0f / edgeLen;
                float maxF = 1.0f - (22.0f / edgeLen);
                safeFraction = math.clamp(safeFraction, minF, maxF);
            }
            else
            {
                safeFraction = math.clamp(safeFraction, 0.2f, 0.8f);
            }

            float actualT = chosenEdge.IsReversed ? (1.0f - safeFraction) : safeFraction;
            pos = MathUtils.Position(curve.m_Bezier, actualT);

            float3 cTan = MathUtils.Tangent(curve.m_Bezier, actualT);
            if (chosenEdge.IsReversed)
                cTan = -cTan;
            tangent = math.normalizesafe(cTan, new float3(0, 0, 1));

            edgeEntity = chosenEdge.EdgeEntity;
            t = actualT;
            return true;
        }

        private bool TryPlaceAlternatingKerbStop(BlockSpan span, Entity roadEntity, float t,
                                                 float3 roadPos, float3 corridorTan,
                                                 int spanOrderInCorridor,
                                                 List<CommittedStopInfo> committedStops,
                                                 List<Entity> candidatePrefabs,
                                                 ref Unity.Mathematics.Random rng,
                                                 float minSpacingCap,
                                                 bool planOnly,
                                                 int currentGlobalStopCount,
                                                 out PlacedStop placedStop,
                                                 Dictionary<Entity, int> placementCounts = null)
        {
            placedStop = default;
            if (IsElevatedOrBridge(roadEntity))
                return false;

            bool isReverseSide = (spanOrderInCorridor % 2 != 0);

            float3 forward = corridorTan;
            if (math.lengthsq(forward) < 0.001f)
                forward = span.MidTangent;

            bool isOneWay = IsOneWayRoad(roadEntity, out bool forwardFlow);
            if (isOneWay && span.IsDeadEnd)
                return false;

            bool isOutbound;

            if (isOneWay)
            {
                // One-way road: align stop orientation with the legal traffic flow direction.
                // Never place stops on the reverse-flow side facing oncoming traffic.
                if (EntityManager.HasComponent<Game.Net.Curve>(roadEntity))
                {
                    var curve = EntityManager.GetComponentData<Game.Net.Curve>(roadEntity);
                    float3 roadTan = MathUtils.Tangent(curve.m_Bezier, t);
                    if (math.lengthsq(roadTan) > 0.001f)
                    {
                        roadTan = math.normalizesafe(new float3(roadTan.x, 0f, roadTan.z));
                        forward = forwardFlow ? roadTan : -roadTan;
                    }
                    else
                    {
                        forward = forwardFlow ? forward : -forward;
                    }
                }
                else
                {
                    forward = forwardFlow ? forward : -forward;
                }

                isOutbound = (math.dot(forward, span.CorridorForwardTangent) >= 0f);
            }
            else
            {
                // Two-way road: Staggered / Alternating stop placement along the corridor:
                // Even span index (k % 2 == 0): Stop on forward right sidewalk (outbound travel direction)
                // Odd span index (k % 2 == 1): Stop on reverse right sidewalk (opposite side / inbound travel direction)
                // This ensures balanced two-way transit service across adjacent blocks without clustering!
                if (isReverseSide)
                {
                    forward = -forward;
                    isOutbound = false;
                }
                else
                {
                    isOutbound = true;
                }
            }

            // Calculate road half-width dynamically from NetCompositionData to place stops accurately on the outer curb
            float roadHalfWidth = 6.0f;
            if (EntityManager.HasComponent<Game.Net.Composition>(roadEntity))
            {
                var comp = EntityManager.GetComponentData<Game.Net.Composition>(roadEntity);
                if (comp.m_Edge != Entity.Null && EntityManager.HasComponent<Game.Prefabs.NetCompositionData>(comp.m_Edge))
                {
                    var netComp = EntityManager.GetComponentData<Game.Prefabs.NetCompositionData>(comp.m_Edge);
                    if (netComp.m_Width > 2.0f)
                    {
                        roadHalfWidth = netComp.m_Width * 0.5f;
                    }
                }
            }

            // Check Left-Hand Traffic rule of the city
            bool isLeftHandTraffic = false;
            var cityConfig = World.GetExistingSystemManaged<Game.City.CityConfigurationSystem>();
            if (cityConfig != null)
            {
                isLeftHandTraffic = cityConfig.leftHandTraffic;
            }

            // Normal pointing towards the passenger boarding curb (right side for RHT, left side for LHT)
            float3 curbNormal = isLeftHandTraffic 
                ? new float3(-forward.z, 0f, forward.x) 
                : new float3(forward.z, 0f, -forward.x);

            // Sidewalk curb is typically at outer road boundary minus lane/curb inset
            float curbOffset = math.max(3.2f, roadHalfWidth - 1.2f);
            float3 stopPos = roadPos + curbNormal * curbOffset;

            // Proximity cap check: ensure no other stop is too close (e.g. at an intersection corner)
            if (minSpacingCap > 0f)
            {
                for (int k = 0; k < committedStops.Count; k++)
                {
                    if (math.distance(stopPos, committedStops[k].Position) < minSpacingCap)
                        return false;
                }
            }

            if (candidatePrefabs == null || candidatePrefabs.Count == 0)
            {
                FindBusStopPrefabs(out candidatePrefabs, out _);
            }

            if (candidatePrefabs == null || candidatePrefabs.Count == 0)
            {
                return false;
            }

            string selectedModel = Mod.setting != null ? Mod.setting.SelectedStopPrefab : "";
            bool isCurbsideOnly = (!string.IsNullOrEmpty(selectedModel) && selectedModel.Equals("Curbside", StringComparison.OrdinalIgnoreCase))
                || candidatePrefabs.TrueForAll(IsBusStopSign);

            // Check if road can support a bus shelter on the stop's curb side
            // In RHT: even span (not reverse) is on right side; odd span (reverse) is on left side
            // In LHT: reverse
            bool isStopOnRightSide = isLeftHandTraffic ? isReverseSide : !isReverseSide;

            Entity selectedPrefab = SelectStopPrefabAndCurbSnap(
                roadEntity, t, roadPos, curbNormal, stopPos, isStopOnRightSide,
                candidatePrefabs, isCurbsideOnly, ref rng,
                out float3 finalStopPos, out quaternion? snappedRotOverride, out float finalRoadT,
                placementCounts);

            if (selectedPrefab == Entity.Null)
            {
                return false;
            }

            if (planOnly)
            {
                placedStop = new PlacedStop
                {
                    StopEntity = new Entity { Index = -(currentGlobalStopCount + 1), Version = 1 },
                    Position = finalStopPos,
                    Forward = forward,
                    RoadEntity = roadEntity,
                    HubEntity = span.HubEntity,
                    CorridorIndex = span.CorridorIndex,
                    SpanOrderInCorridor = spanOrderInCorridor,
                    AngleFromHub = 0f,
                    DistFromHub = 0f,
                    IsOutbound = isOutbound,
                    RoadT = finalRoadT,
                    PrefabEntity = selectedPrefab,
                    HasRotationOverride = snappedRotOverride.HasValue,
                    RotationOverride = snappedRotOverride ?? quaternion.identity,
                    IsStationBay = false,
                    IsPreExisting = false
                };
            }
            else
            {
                if (!TryCreateBusStopEntity(roadEntity, finalRoadT, finalStopPos, forward, span.HubEntity, span.CorridorIndex, spanOrderInCorridor, isOutbound, selectedPrefab, out placedStop, snappedRotOverride))
                    return false;
            }

            committedStops.Add(new CommittedStopInfo
            {
                Position = finalStopPos,
                Forward = forward,
                CorridorIndex = span.CorridorIndex,
                SpanId = span.SpanId
            });

            return true;
        }

        private static void RecordStopPrefabPlacement(Dictionary<Entity, int> placementCounts, Entity prefabEntity)
        {
            if (placementCounts == null || prefabEntity == Entity.Null)
                return;
            placementCounts.TryGetValue(prefabEntity, out int count);
            placementCounts[prefabEntity] = count + 1;
        }

        private static Entity PickBalancedCandidate(List<Entity> pool, Dictionary<Entity, int> placementCounts, ref Unity.Mathematics.Random rng)
        {
            if (pool == null || pool.Count == 0)
                return Entity.Null;
            if (pool.Count == 1)
                return pool[0];

            if (placementCounts == null)
                return pool[rng.NextInt(0, pool.Count)];

            int minCount = int.MaxValue;
            for (int i = 0; i < pool.Count; i++)
            {
                placementCounts.TryGetValue(pool[i], out int c);
                if (c < minCount)
                    minCount = c;
            }

            var tied = new List<Entity>(pool.Count);
            for (int i = 0; i < pool.Count; i++)
            {
                placementCounts.TryGetValue(pool[i], out int c);
                if (c == minCount)
                    tied.Add(pool[i]);
            }

            return (tied.Count > 1) ? tied[rng.NextInt(0, tied.Count)] : tied[0];
        }

        /// <summary>
        /// Selects a stop prefab exclusively from <paramref name="candidatePrefabs"/> using balanced count selection
        /// so multiple chosen models stay evenly distributed, and snaps it to the road curb.
        /// Never substitutes an unselected bus stop model outside <paramref name="candidatePrefabs"/>.
        /// </summary>
        private Entity SelectStopPrefabAndCurbSnap(
            Entity roadEntity,
            float t,
            float3 roadPos,
            float3 curbNormal,
            float3 defaultCurbPos,
            bool isStopOnRightSide,
            List<Entity> candidatePrefabs,
            bool isCurbsideOnly,
            ref Unity.Mathematics.Random rng,
            out float3 finalStopPos,
            out quaternion? snappedRotOverride,
            out float finalRoadT,
            Dictionary<Entity, int> placementCounts = null)
        {
            finalStopPos = defaultCurbPos;
            snappedRotOverride = null;
            finalRoadT = t;

            if (candidatePrefabs == null || candidatePrefabs.Count == 0)
                return Entity.Null;

            Entity selectedPrefab = PickBalancedCandidate(candidatePrefabs, placementCounts, ref rng);

            if (isCurbsideOnly)
            {
                RecordStopPrefabPlacement(placementCounts, selectedPrefab);
                return selectedPrefab;
            }

            string selectedModel = Mod.setting != null ? Mod.setting.SelectedStopPrefab : "";
            bool isExplicitSubset = !string.IsNullOrEmpty(selectedModel)
                && !selectedModel.Equals("All", StringComparison.OrdinalIgnoreCase)
                && (Setting.DiscoveredStopPrefabNames.Count == 0 || candidatePrefabs.Count < Setting.DiscoveredStopPrefabNames.Count);

            // 1. Check the balanced candidate against strict shelter + curb-area fit.
            bool selectedIsSign = IsBusStopSign(selectedPrefab);
            bool roadHasPavedSidewalk = false;
            bool roadSupportsShelter = !selectedIsSign && CanRoadSupportBusShelter(
                roadEntity, isStopOnRightSide, selectedPrefab, t, roadPos, curbNormal, out roadHasPavedSidewalk);

            if ((selectedIsSign || roadSupportsShelter) && TrySnapToRoadCurbArea(
                    roadEntity, t, roadPos, curbNormal, isStopOnRightSide, selectedPrefab,
                    out float3 areaPos, out quaternion areaRot, out float areaT, out _))
            {
                finalStopPos = areaPos;
                snappedRotOverride = areaRot;
                finalRoadT = areaT;
                RecordStopPrefabPlacement(placementCounts, selectedPrefab);
                return selectedPrefab;
            }

            // 2. When the user explicitly chose a specific model/mix containing this shelter on a normal paved street
            // with sidewalks, preserve balance by snapping cleanly onto the buildable sidewalk strip even if oversize.
            if (isExplicitSubset && !selectedIsSign && roadHasPavedSidewalk && TrySnapToRoadCurbArea(
                    roadEntity, t, roadPos, curbNormal, isStopOnRightSide, selectedPrefab,
                    out float3 subsetPos, out quaternion subsetRot, out float subsetT, out _, allowOversize: true))
            {
                finalStopPos = subsetPos;
                snappedRotOverride = subsetRot;
                finalRoadT = subsetT;
                RecordStopPrefabPlacement(placementCounts, selectedPrefab);
                return selectedPrefab;
            }

            // 3. Check other non-sign candidates in the user's pool (preferring least-used among fitting ones).
            if (candidatePrefabs.Count > 1)
            {
                var fittingShelters = new List<Entity>();
                for (int i = 0; i < candidatePrefabs.Count; i++)
                {
                    Entity cand = candidatePrefabs[i];
                    if (cand == selectedPrefab || IsBusStopSign(cand))
                        continue;
                    if (!CanRoadSupportBusShelter(roadEntity, isStopOnRightSide, cand, t, roadPos, curbNormal, out _))
                        continue;
                    if (TrySnapToRoadCurbArea(roadEntity, t, roadPos, curbNormal, isStopOnRightSide, cand, out _, out _, out _, out _))
                    {
                        fittingShelters.Add(cand);
                    }
                }

                if (fittingShelters.Count > 0)
                {
                    Entity chosenShelter = PickBalancedCandidate(fittingShelters, placementCounts, ref rng);
                    if (TrySnapToRoadCurbArea(roadEntity, t, roadPos, curbNormal, isStopOnRightSide, chosenShelter,
                            out float3 fitPos, out quaternion fitRot, out float fitT, out _))
                    {
                        finalStopPos = fitPos;
                        snappedRotOverride = fitRot;
                        finalRoadT = fitT;
                        RecordStopPrefabPlacement(placementCounts, chosenShelter);
                        return chosenShelter;
                    }
                }
            }

            // 4. If no shelter in candidatePrefabs fits, prefer a sign FROM candidatePrefabs ONLY (never unselected signs).
            Entity selectedSign = PickSelectedSignPrefab(candidatePrefabs, selectedPrefab, placementCounts, ref rng);
            if (selectedSign != Entity.Null)
            {
                selectedPrefab = selectedSign;
                if (TrySnapToRoadCurbArea(roadEntity, t, roadPos, curbNormal, isStopOnRightSide, selectedPrefab,
                        out float3 signPos, out quaternion signRot, out float signT, out _))
                {
                    finalStopPos = signPos;
                    snappedRotOverride = signRot;
                    finalRoadT = signT;
                }
                RecordStopPrefabPlacement(placementCounts, selectedPrefab);
                return selectedPrefab;
            }

            // 5. User explicitly selected only non-sign model(s):
            // keep the user's selected prefab and snap cleanly to the buildable sidewalk strip if present.
            if (TrySnapToRoadCurbArea(roadEntity, t, roadPos, curbNormal, isStopOnRightSide, selectedPrefab,
                    out float3 oversizePos, out quaternion oversizeRot, out float oversizeT, out _, allowOversize: true))
            {
                finalStopPos = oversizePos;
                snappedRotOverride = oversizeRot;
                finalRoadT = oversizeT;
            }

            RecordStopPrefabPlacement(placementCounts, selectedPrefab);
            return selectedPrefab;
        }

        private Entity PickSelectedSignPrefab(List<Entity> candidatePrefabs, Entity preferredThemeEntity, Dictionary<Entity, int> placementCounts, ref Unity.Mathematics.Random rng)
        {
            var signCandidates = new List<Entity>();
            for (int i = 0; i < candidatePrefabs.Count; i++)
            {
                if (IsBusStopSign(candidatePrefabs[i]))
                    signCandidates.Add(candidatePrefabs[i]);
            }

            if (signCandidates.Count == 0)
                return Entity.Null;

            if (preferredThemeEntity != Entity.Null && signCandidates.Count > 1)
            {
                string targetName = GetPrefabName(preferredThemeEntity);
                if (targetName.StartsWith("NA_", StringComparison.OrdinalIgnoreCase))
                {
                    var naSigns = signCandidates.FindAll(s => GetPrefabName(s).StartsWith("NA_", StringComparison.OrdinalIgnoreCase));
                    if (naSigns.Count > 0)
                        return PickBalancedCandidate(naSigns, placementCounts, ref rng);
                }
                else if (targetName.StartsWith("EU_", StringComparison.OrdinalIgnoreCase))
                {
                    var euSigns = signCandidates.FindAll(s => GetPrefabName(s).StartsWith("EU_", StringComparison.OrdinalIgnoreCase));
                    if (euSigns.Count > 0)
                        return PickBalancedCandidate(euSigns, placementCounts, ref rng);
                }
            }

            return PickBalancedCandidate(signCandidates, placementCounts, ref rng);
        }

        private string FormatPlacedStopPrefabBreakdown(IEnumerable<Entity> prefabEntities)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in prefabEntities)
            {
                if (p == Entity.Null) continue;
                string name = GetPrefabName(p);
                if (string.IsNullOrEmpty(name)) name = $"Entity#{p.Index}";
                counts.TryGetValue(name, out int prev);
                counts[name] = prev + 1;
            }

            if (counts.Count == 0)
                return "none";

            var parts = new List<string>();
            foreach (var kv in counts)
            {
                parts.Add($"{kv.Key} x{kv.Value}");
            }
            return string.Join(", ", parts);
        }

        private float ComputeStopSnapRadius(Entity stopPrefabEntity, in NetCompositionData netComp)
        {
            if (stopPrefabEntity == Entity.Null || !EntityManager.HasComponent<ObjectGeometryData>(stopPrefabEntity))
                return 0f;

            var geom = EntityManager.GetComponentData<ObjectGeometryData>(stopPrefabEntity);
            bool isStanding = (geom.m_Flags & Game.Objects.GeometryFlags.Standing) != 0;
            float radius = isStanding
                ? math.max(0f, geom.m_LegSize.z * 0.5f + geom.m_LegOffset.y)
                : math.max(0f, geom.m_Size.z * 0.5f);

            if (isStanding && geom.m_LegSize.y <= netComp.m_HeightRange.max)
            {
                radius = math.max(radius, geom.m_Size.z * 0.5f);
            }

            return radius;
        }

        private bool TrySnapToRoadCurbArea(
            Entity roadEntity,
            float roadT,
            float3 approxRoadCenter,
            float3 approxCurbNormal,
            bool isRightSide,
            Entity stopPrefabEntity,
            out float3 snappedPos,
            out quaternion snappedRot,
            out float snappedRoadT,
            out bool hadBuildableArea,
            bool allowOversize = false)
        {
            snappedPos = default;
            snappedRot = quaternion.identity;
            snappedRoadT = roadT;
            hadBuildableArea = false;

            if (roadEntity == Entity.Null || !EntityManager.Exists(roadEntity))
                return false;
            if (!EntityManager.HasComponent<Composition>(roadEntity) ||
                !EntityManager.HasComponent<EdgeGeometry>(roadEntity) ||
                !EntityManager.HasComponent<Curve>(roadEntity))
                return false;

            var comp = EntityManager.GetComponentData<Composition>(roadEntity);
            if (comp.m_Edge == Entity.Null ||
                !EntityManager.HasComponent<NetCompositionData>(comp.m_Edge) ||
                !EntityManager.HasBuffer<NetCompositionArea>(comp.m_Edge))
                return false;

            var netComp = EntityManager.GetComponentData<NetCompositionData>(comp.m_Edge);
            if (netComp.m_Width <= 0.01f)
                return false;

            var areas = EntityManager.GetBuffer<NetCompositionArea>(comp.m_Edge);
            if (areas.Length == 0)
                return false;

            var edgeGeom = EntityManager.GetComponentData<EdgeGeometry>(roadEntity);
            var roadCurve = EntityManager.GetComponentData<Curve>(roadEntity);
            float radius = ComputeStopSnapRadius(stopPrefabEntity, in netComp);

            float2 targetXZ = approxRoadCenter.xz + math.normalizesafe(approxCurbNormal.xz) * (netComp.m_Width * 0.5f);

            float bestDistSq = float.MaxValue;
            bool found = false;
            float3 bestPos = default;
            float2 bestDirXZ = default;

            for (int segIdx = 0; segIdx < 2; segIdx++)
            {
                var seg = (segIdx == 0) ? edgeGeom.m_Start : edgeGeom.m_End;

                for (int i = 0; i < areas.Length; i++)
                {
                    var area = areas[i];
                    if ((area.m_Flags & NetAreaFlags.Buildable) == 0 || (area.m_Flags & NetAreaFlags.Median) != 0)
                        continue;

                    // Verify geometric side against approxCurbNormal when provided; fallback to m_Position.x sign
                    Bezier4x3 areaCurve = MathUtils.Lerp(seg.m_Left, seg.m_Right, area.m_Position.x / netComp.m_Width + 0.5f);
                    MathUtils.Distance(areaCurve.xz, targetXZ, out float segT);
                    float3 candidatePos = MathUtils.Position(areaCurve, segT);

                    if (math.lengthsq(approxCurbNormal.xz) > 0.001f)
                    {
                        if (math.dot(candidatePos.xz - approxRoadCenter.xz, approxCurbNormal.xz) <= 0f)
                            continue;
                    }
                    else
                    {
                        bool areaIsRight = area.m_Position.x > 0f;
                        if (areaIsRight != isRightSide)
                            continue;
                    }

                    hadBuildableArea = true;

                    // Must match CS2 AttachPositionSystem & ObjectToolSystem fit requirement unless explicitly overridden for oversized user-selected shelters
                    if (!allowOversize && radius >= area.m_Width * 0.51f)
                        continue;

                    float2 tanXZ = math.normalizesafe(MathUtils.Tangent(areaCurve, segT).xz);
                    if (math.lengthsq(tanXZ) < 0.0001f)
                        continue;

                    float2 dirXZ = ((area.m_Flags & NetAreaFlags.Invert) != 0)
                        ? MathUtils.Right(tanXZ)
                        : MathUtils.Left(tanXZ);

                    float3 snapLinePos = MathUtils.Position(
                        MathUtils.Lerp(seg.m_Left, seg.m_Right, area.m_SnapPosition.x / netComp.m_Width + 0.5f),
                        segT);
                    float maxLength = math.max(0f, math.min(
                        area.m_Width * 0.5f,
                        math.abs(area.m_SnapPosition.x - area.m_Position.x) + area.m_SnapWidth * 0.5f) - radius);

                    candidatePos.xz += MathUtils.ClampLength(snapLinePos.xz - candidatePos.xz, maxLength);
                    candidatePos.y += area.m_Position.y;

                    float dSq = math.distancesq(candidatePos.xz, targetXZ);
                    if (dSq < bestDistSq)
                    {
                        bestDistSq = dSq;
                        bestPos = candidatePos;
                        bestDirXZ = dirXZ;
                        found = true;
                    }
                }
            }

            if (!found)
                return false;

            snappedPos = bestPos;
            snappedRot = ToolUtils.CalculateRotation(bestDirXZ);
            MathUtils.Distance(roadCurve.m_Bezier.xz, bestPos.xz, out snappedRoadT);
            return true;
        }

        private bool CanRoadSupportBusShelter(
            Entity roadEntity,
            bool isRightSide,
            Entity candidateShelterPrefab,
            float roadT,
            float3 approxRoadCenter,
            float3 approxCurbNormal,
            out bool hasPavedSidewalk)
        {
            hasPavedSidewalk = false;

            if (roadEntity == Entity.Null || !EntityManager.Exists(roadEntity))
                return false;

            // 1. Check if the road is an alley, gravel/unpaved, or highway by prefab name
            if (EntityManager.HasComponent<PrefabRef>(roadEntity))
            {
                var prefabRef = EntityManager.GetComponentData<PrefabRef>(roadEntity);
                string pName = GetPrefabName(prefabRef.m_Prefab);
                if (!string.IsNullOrEmpty(pName))
                {
                    if (pName.IndexOf("Alley", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        pName.IndexOf("Gravel", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        pName.IndexOf("Dirt", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        pName.IndexOf("Unpaved", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        pName.IndexOf("Highway", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        pName.IndexOf("Freeway", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        pName.IndexOf("Motorway", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        pName.IndexOf("Ramp", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return false;
                    }
                }
            }

            // 2. Check Composition, NetCompositionData, and NetCompositionArea buildable curb fit
            if (EntityManager.HasComponent<Composition>(roadEntity))
            {
                var comp = EntityManager.GetComponentData<Composition>(roadEntity);
                if (comp.m_Edge != Entity.Null && EntityManager.HasComponent<NetCompositionData>(comp.m_Edge))
                {
                    var netComp = EntityManager.GetComponentData<NetCompositionData>(comp.m_Edge);

                    // Gravel or unpaved road surfaces cannot support bus shelters
                    if ((netComp.m_Flags.m_General & CompositionFlags.General.Gravel) != 0)
                        return false;

                    // Roads with total width <= 10m (like 8m alleys) are too narrow for bus shelters
                    if (netComp.m_Width <= 10.0f)
                        return false;

                    // Resolve geometric side relative to road curve tangent when approxCurbNormal is available
                    bool actualRoadRight = isRightSide;
                    if (math.lengthsq(approxCurbNormal.xz) > 0.001f && EntityManager.HasComponent<Curve>(roadEntity))
                    {
                        var curve = EntityManager.GetComponentData<Curve>(roadEntity);
                        float2 curveTanXZ = math.normalizesafe(MathUtils.Tangent(curve.m_Bezier, roadT).xz);
                        if (math.lengthsq(curveTanXZ) > 0.0001f)
                        {
                            actualRoadRight = math.dot(MathUtils.Right(curveTanXZ), math.normalizesafe(approxCurbNormal.xz)) >= 0f;
                        }
                    }

                    // Check if the target side has a sidewalk
                    var sideFlags = actualRoadRight ? netComp.m_Flags.m_Right : netComp.m_Flags.m_Left;
                    if ((sideFlags & (CompositionFlags.Side.Sidewalk | CompositionFlags.Side.WideSidewalk)) == 0)
                        return false;

                    // Verify NetCompositionArea buildable strip exists and can fit the shelter footprint
                    if (EntityManager.HasBuffer<NetCompositionArea>(comp.m_Edge))
                    {
                        var areas = EntityManager.GetBuffer<NetCompositionArea>(comp.m_Edge);
                        if (areas.Length > 0)
                        {
                            float requiredRadius = (candidateShelterPrefab != Entity.Null)
                                ? ComputeStopSnapRadius(candidateShelterPrefab, in netComp)
                                : 0f;
                            bool hasFittingArea = false;
                            for (int i = 0; i < areas.Length; i++)
                            {
                                var area = areas[i];
                                if ((area.m_Flags & NetAreaFlags.Buildable) != 0 && (area.m_Flags & NetAreaFlags.Median) == 0)
                                {
                                    bool sideMatches = (actualRoadRight && area.m_Position.x > 0f) || (!actualRoadRight && area.m_Position.x < 0f);
                                    if (!sideMatches)
                                        continue;

                                    hasPavedSidewalk = true;

                                    bool widthFits = (requiredRadius > 0f)
                                        ? (requiredRadius < area.m_Width * 0.51f)
                                        : (area.m_Width >= 1.5f);
                                    if (widthFits)
                                    {
                                        hasFittingArea = true;
                                        break;
                                    }
                                }
                            }
                            if (!hasFittingArea)
                                return false;
                        }
                        else
                        {
                            hasPavedSidewalk = true;
                        }
                    }
                    else
                    {
                        hasPavedSidewalk = true;
                    }
                }
            }

            return true;
        }

        private bool IsBusStopSign(Entity prefabEntity)
        {
            if (prefabEntity == Entity.Null || !EntityManager.Exists(prefabEntity))
                return false;

            string name = GetPrefabName(prefabEntity);
            if (!string.IsNullOrEmpty(name))
            {
                // Explicit sign checks
                if (name.IndexOf("Sign", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Pole", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.Equals("EU_BusStop01", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("NA_BusStop01", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                // Explicit shelter checks
                if (name.IndexOf("Shelter", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Bicycle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Bike", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.Equals("EU_BusStop02", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("NA_BusStop02", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            // Geometry check fallback for modded assets
            if (EntityManager.HasComponent<ObjectGeometryData>(prefabEntity))
            {
                var geom = EntityManager.GetComponentData<ObjectGeometryData>(prefabEntity);
                // Signs typically have small footprint (depth < 1.0m, width < 2.0m)
                if (geom.m_Size.z < 1.0f && geom.m_Size.x < 2.0f)
                    return true;
            }

            return false;
        }

        private bool TryCreateBusStopEntity(Entity roadEntity, float t, float3 pos, float3 forward, Entity hubEntity, int corridorIndex, int spanOrderInCorridor, bool isOutbound, Entity busStopPrefabEntity, out PlacedStop stop, quaternion? overrideRotation = null)
        {
            stop = default;
            if (IsElevatedOrBridge(roadEntity))
                return false;

            quaternion rot;
            if (overrideRotation.HasValue)
            {
                rot = overrideRotation.Value;
            }
            else
            {
                // Rotation: bus stop should face directly toward the road centerline from the curb
                float3 toRoad = float3.zero;
                if (EntityManager.HasComponent<Curve>(roadEntity))
                {
                    var roadCurve = EntityManager.GetComponentData<Curve>(roadEntity);
                    float3 roadCenter = MathUtils.Position(roadCurve.m_Bezier, t);
                    toRoad = math.normalizesafe(new float3(roadCenter.x - pos.x, 0f, roadCenter.z - pos.z));
                }

                if (math.lengthsq(toRoad) < 0.001f)
                {
                    var cityConfig = World.GetExistingSystemManaged<Game.City.CityConfigurationSystem>();
                    bool isLeftHandTraffic = cityConfig != null && cityConfig.leftHandTraffic;
                    float3 curbNormal = isLeftHandTraffic
                        ? new float3(-forward.z, 0f, forward.x)
                        : new float3(forward.z, 0f, -forward.x);
                    toRoad = -curbNormal;
                }

                rot = quaternion.LookRotationSafe(toRoad, new float3(0, 1, 0));
            }

            if (!EntityManager.HasComponent<ObjectData>(busStopPrefabEntity))
            {
                log.Error($"Bus stop prefab {busStopPrefabEntity.Index} is missing ObjectData component.");
                return false;
            }

            var objectData = EntityManager.GetComponentData<ObjectData>(busStopPrefabEntity);
            if (!objectData.m_Archetype.Valid)
            {
                log.Error($"Bus stop prefab {busStopPrefabEntity.Index} has invalid ObjectData archetype.");
                return false;
            }

            // 1. Create runtime world object entity from prefab's registered archetype
            Entity stopEntity = EntityManager.CreateEntity(objectData.m_Archetype);

            // 2. Set prefab reference & runtime transform
            EntityManager.SetComponentData(stopEntity, new PrefabRef(busStopPrefabEntity));
            EntityManager.SetComponentData(stopEntity, new Game.Objects.Transform(pos, rot));

            if (EntityManager.HasComponent<PseudoRandomSeed>(stopEntity))
            {
                ushort seedVal = (ushort)(((uint)stopEntity.Index * 73856093u ^ (uint)busStopPrefabEntity.Index * 19349663u) | 1u);
                EntityManager.SetComponentData(stopEntity, new PseudoRandomSeed(seedVal));
            }

            if (EntityManager.HasComponent<ObjectGeometryData>(busStopPrefabEntity))
            {
                var geomData = EntityManager.GetComponentData<ObjectGeometryData>(busStopPrefabEntity);
                if ((geomData.m_Flags & Game.Objects.GeometryFlags.Physical) != 0 &&
                    !EntityManager.HasComponent<Game.Objects.Surface>(stopEntity))
                {
                    EntityManager.AddComponentData(stopEntity, default(Game.Objects.Surface));
                }
            }

            SyncStopElevationWithRoad(stopEntity, roadEntity);

            if (EntityManager.HasComponent<CustomMeshColor>(stopEntity))
            {
                EntityManager.SetComponentEnabled<CustomMeshColor>(stopEntity, false);
            }

            // 3. Set transport stop properties
            if (EntityManager.HasComponent<TransportStopData>(busStopPrefabEntity))
            {
                var stopData = EntityManager.GetComponentData<TransportStopData>(busStopPrefabEntity);
                EntityManager.SetComponentData(stopEntity, new Game.Routes.TransportStop
                {
                    m_ComfortFactor = stopData.m_ComfortFactor,
                    m_LoadingFactor = stopData.m_LoadingFactor
                });
            }
            else
            {
                EntityManager.SetComponentData(stopEntity, new Game.Routes.TransportStop { m_ComfortFactor = 0.5f, m_LoadingFactor = 1.0f });
            }

            // 4. Set road attachment using exact XZ curve projection matching vanilla GenerateObjectsSystem.CreateAttached
            float attachedCurvePos = t;
            if (EntityManager.HasComponent<Curve>(roadEntity))
            {
                var roadCurve = EntityManager.GetComponentData<Curve>(roadEntity);
                MathUtils.Distance(roadCurve.m_Bezier.xz, pos.xz, out attachedCurvePos);
            }

            if (!EntityManager.HasComponent<Attached>(stopEntity))
                EntityManager.AddComponentData(stopEntity, new Attached(roadEntity, Entity.Null, attachedCurvePos));
            else
                EntityManager.SetComponentData(stopEntity, new Attached(roadEntity, Entity.Null, attachedCurvePos));

            if (EntityManager.HasComponent<Game.Common.Owner>(stopEntity))
                EntityManager.RemoveComponent<Game.Common.Owner>(stopEntity);

            // Register stopEntity in roadEntity's SubObject dynamic buffer and mark road updated
            DynamicBuffer<Game.Objects.SubObject> roadSubObjects;
            if (!EntityManager.HasBuffer<Game.Objects.SubObject>(roadEntity))
            {
                roadSubObjects = EntityManager.AddBuffer<Game.Objects.SubObject>(roadEntity);
            }
            else
            {
                roadSubObjects = EntityManager.GetBuffer<Game.Objects.SubObject>(roadEntity);
            }

            bool alreadyInSubObjects = false;
            for (int s = 0; s < roadSubObjects.Length; s++)
            {
                if (roadSubObjects[s].m_SubObject == stopEntity)
                {
                    alreadyInSubObjects = true;
                    break;
                }
            }
            if (!alreadyInSubObjects)
            {
                roadSubObjects.Add(new Game.Objects.SubObject(stopEntity));
            }

            MarkRoadUpdatedWithNodes(roadEntity);

            // Ensure BusStop marker tag exists for bus transport systems
            if (!EntityManager.HasComponent<Game.Routes.BusStop>(stopEntity))
                EntityManager.AddComponentData(stopEntity, default(Game.Routes.BusStop));

            // 5. Ensure ConnectedRoute buffer and BoardingVehicle component exist
            if (!EntityManager.HasBuffer<ConnectedRoute>(stopEntity))
                EntityManager.AddBuffer<ConnectedRoute>(stopEntity);

            if (!EntityManager.HasComponent<BoardingVehicle>(stopEntity))
                EntityManager.AddComponentData(stopEntity, default(BoardingVehicle));

            // 6. Add lifecycle tags for rendering and spatial search indexing
            if (!EntityManager.HasComponent<Game.Objects.Object>(stopEntity))
                EntityManager.AddComponentData(stopEntity, default(Game.Objects.Object));
            if (!EntityManager.HasComponent<Created>(stopEntity))
                EntityManager.AddComponentData(stopEntity, default(Created));
            if (!EntityManager.HasComponent<Updated>(stopEntity))
                EntityManager.AddComponentData(stopEntity, default(Updated));
            if (!EntityManager.HasComponent<BatchesUpdated>(stopEntity))
                EntityManager.AddComponentData(stopEntity, default(BatchesUpdated));

            stop = new PlacedStop
            {
                StopEntity = stopEntity,
                Position = pos,
                Forward = forward,
                RoadEntity = roadEntity,
                HubEntity = hubEntity,
                CorridorIndex = corridorIndex,
                SpanOrderInCorridor = spanOrderInCorridor,
                AngleFromHub = 0f,
                DistFromHub = 0f,
                IsOutbound = isOutbound,
                RoadT = t,
                PrefabEntity = busStopPrefabEntity,
                IsStationBay = false,
                IsPreExisting = false
            };
            return true;
        }

        private List<RoadCorridor> BuildCorridors()
        {
            var corridors = new List<RoadCorridor>();
            var visitedEdges = new HashSet<Entity>();

            var nodeToEdges = new Dictionary<Entity, List<EdgeEndpoint>>();

            for (int i = 0; i < _roadScanner.RoadSegments.Length; i++)
            {
                var roadEntity = _roadScanner.RoadSegments[i];
                if (!EntityManager.HasComponent<Edge>(roadEntity) || !EntityManager.HasComponent<Curve>(roadEntity))
                    continue;

                var edge = EntityManager.GetComponentData<Edge>(roadEntity);
                var curve = EntityManager.GetComponentData<Curve>(roadEntity);

                float3 startTan = math.normalizesafe(MathUtils.Tangent(curve.m_Bezier, 0f), new float3(0, 0, 1));
                float3 endTan = -math.normalizesafe(MathUtils.Tangent(curve.m_Bezier, 1f), new float3(0, 0, 1));

                if (edge.m_Start != Entity.Null)
                {
                    if (!nodeToEdges.ContainsKey(edge.m_Start))
                        nodeToEdges[edge.m_Start] = new List<EdgeEndpoint>();
                    nodeToEdges[edge.m_Start].Add(new EdgeEndpoint { EdgeEntity = roadEntity, IsStartNode = true, OutwardTangent = startTan });
                }

                if (edge.m_End != Entity.Null)
                {
                    if (!nodeToEdges.ContainsKey(edge.m_End))
                        nodeToEdges[edge.m_End] = new List<EdgeEndpoint>();
                    nodeToEdges[edge.m_End].Add(new EdgeEndpoint { EdgeEntity = roadEntity, IsStartNode = false, OutwardTangent = endTan });
                }
            }

            for (int i = 0; i < _roadScanner.RoadSegments.Length; i++)
            {
                var seedEntity = _roadScanner.RoadSegments[i];
                if (visitedEdges.Contains(seedEntity))
                    continue;
                if (!EntityManager.HasComponent<Edge>(seedEntity) || !EntityManager.HasComponent<Curve>(seedEntity))
                    continue;

                var corridor = new RoadCorridor();

                var seedCurve = EntityManager.GetComponentData<Curve>(seedEntity);
                var seedEdge = EntityManager.GetComponentData<Edge>(seedEntity);
                float seedLen = MathUtils.Length(seedCurve.m_Bezier);

                corridor.Segments.Add(new RoadSegmentChain { RoadEntity = seedEntity, Length = seedLen, IsReversed = false });
                corridor.TotalLength += seedLen;
                visitedEdges.Add(seedEntity);

                // Trace forward
                Entity currentEndNode = seedEdge.m_End;
                float3 currentHeading = math.normalizesafe(MathUtils.Tangent(seedCurve.m_Bezier, 1f), new float3(0, 0, 1));

                while (currentEndNode != Entity.Null && nodeToEdges.ContainsKey(currentEndNode))
                {
                    var candidates = nodeToEdges[currentEndNode];
                    Entity bestNextEdge = Entity.Null;
                    bool bestIsReversed = false;
                    Entity nextEndNode = Entity.Null;
                    float3 nextHeading = currentHeading;
                    float bestAlignment = 0.50f;

                    for (int c = 0; c < candidates.Count; c++)
                    {
                        var cand = candidates[c];
                        if (visitedEdges.Contains(cand.EdgeEntity))
                            continue;

                        float alignment = math.dot(currentHeading, cand.OutwardTangent);
                        if (alignment > bestAlignment)
                        {
                            bestAlignment = alignment;
                            bestNextEdge = cand.EdgeEntity;
                            bestIsReversed = !cand.IsStartNode;

                            var candEdge = EntityManager.GetComponentData<Edge>(cand.EdgeEntity);
                            nextEndNode = cand.IsStartNode ? candEdge.m_End : candEdge.m_Start;

                            var candCurve = EntityManager.GetComponentData<Curve>(cand.EdgeEntity);
                            nextHeading = cand.IsStartNode
                                ? math.normalizesafe(MathUtils.Tangent(candCurve.m_Bezier, 1f), new float3(0, 0, 1))
                                : -math.normalizesafe(MathUtils.Tangent(candCurve.m_Bezier, 0f), new float3(0, 0, 1));
                        }
                    }

                    if (bestNextEdge != Entity.Null)
                    {
                        var nextCurve = EntityManager.GetComponentData<Curve>(bestNextEdge);
                        float nextLen = MathUtils.Length(nextCurve.m_Bezier);
                        corridor.Segments.Add(new RoadSegmentChain { RoadEntity = bestNextEdge, Length = nextLen, IsReversed = bestIsReversed });
                        corridor.TotalLength += nextLen;
                        visitedEdges.Add(bestNextEdge);
                        currentEndNode = nextEndNode;
                        currentHeading = nextHeading;
                    }
                    else
                    {
                        break;
                    }
                }

                // Trace backward
                Entity currentStartNode = seedEdge.m_Start;
                float3 currentBackwardHeading = -math.normalizesafe(MathUtils.Tangent(seedCurve.m_Bezier, 0f), new float3(0, 0, 1));

                while (currentStartNode != Entity.Null && nodeToEdges.ContainsKey(currentStartNode))
                {
                    var candidates = nodeToEdges[currentStartNode];
                    Entity bestPrevEdge = Entity.Null;
                    bool bestIsReversed = false;
                    Entity nextStartNode = Entity.Null;
                    float3 nextBackwardHeading = currentBackwardHeading;
                    float bestAlignment = 0.50f;

                    for (int c = 0; c < candidates.Count; c++)
                    {
                        var cand = candidates[c];
                        if (visitedEdges.Contains(cand.EdgeEntity))
                            continue;

                        float alignment = math.dot(currentBackwardHeading, cand.OutwardTangent);
                        if (alignment > bestAlignment)
                        {
                            bestAlignment = alignment;
                            bestPrevEdge = cand.EdgeEntity;
                            bestIsReversed = cand.IsStartNode;

                            var candEdge = EntityManager.GetComponentData<Edge>(cand.EdgeEntity);
                            nextStartNode = cand.IsStartNode ? candEdge.m_End : candEdge.m_Start;

                            var candCurve = EntityManager.GetComponentData<Curve>(cand.EdgeEntity);
                            nextBackwardHeading = cand.IsStartNode
                                ? -math.normalizesafe(MathUtils.Tangent(candCurve.m_Bezier, 1f), new float3(0, 0, 1))
                                : math.normalizesafe(MathUtils.Tangent(candCurve.m_Bezier, 0f), new float3(0, 0, 1));
                        }
                    }

                    if (bestPrevEdge != Entity.Null)
                    {
                        var prevCurve = EntityManager.GetComponentData<Curve>(bestPrevEdge);
                        float prevLen = MathUtils.Length(prevCurve.m_Bezier);
                        corridor.Segments.Insert(0, new RoadSegmentChain { RoadEntity = bestPrevEdge, Length = prevLen, IsReversed = bestIsReversed });
                        corridor.TotalLength += prevLen;
                        visitedEdges.Add(bestPrevEdge);
                        currentStartNode = nextStartNode;
                        currentBackwardHeading = nextBackwardHeading;
                    }
                    else
                    {
                        break;
                    }
                }

                corridors.Add(corridor);
            }

            return corridors;
        }

        private float3 GetCorridorEndpoint(RoadCorridor corr, bool isStart)
        {
            if (corr.Segments.Count == 0)
                return float3.zero;

            var seg = isStart ? corr.Segments[0] : corr.Segments[corr.Segments.Count - 1];
            if (EntityManager.HasComponent<Curve>(seg.RoadEntity))
            {
                var curve = EntityManager.GetComponentData<Curve>(seg.RoadEntity);
                if (isStart)
                    return seg.IsReversed ? MathUtils.Position(curve.m_Bezier, 1f) : MathUtils.Position(curve.m_Bezier, 0f);
                else
                    return seg.IsReversed ? MathUtils.Position(curve.m_Bezier, 0f) : MathUtils.Position(curve.m_Bezier, 1f);
            }
            return float3.zero;
        }

        private struct SpanNodeConnection
        {
            public Entity EdgeEntity;
            public bool IsStartNode;
        }

        private List<BlockSpan> BuildBlockSpans(List<RoadCorridor> corridors)
        {
            var blockSpans = new List<BlockSpan>();
            var visitedEdges = new HashSet<Entity>();

            // Map each edge to its Corridor Index and Corridor Total Length for priority
            var edgeToCorridorIndex = new Dictionary<Entity, int>();
            var edgeToCorridorLength = new Dictionary<Entity, float>();
            for (int c = 0; c < corridors.Count; c++)
            {
                var corr = corridors[c];
                for (int s = 0; s < corr.Segments.Count; s++)
                {
                    edgeToCorridorIndex[corr.Segments[s].RoadEntity] = c;
                    edgeToCorridorLength[corr.Segments[s].RoadEntity] = corr.TotalLength;
                }
            }

            // Build node degree and adjacency for drivable roads
            var nodeToConnections = new Dictionary<Entity, List<SpanNodeConnection>>();
            for (int i = 0; i < _roadScanner.RoadSegments.Length; i++)
            {
                var roadEntity = _roadScanner.RoadSegments[i];
                if (!EntityManager.HasComponent<Edge>(roadEntity) || !EntityManager.HasComponent<Curve>(roadEntity))
                    continue;

                var edge = EntityManager.GetComponentData<Edge>(roadEntity);
                if (edge.m_Start != Entity.Null)
                {
                    if (!nodeToConnections.ContainsKey(edge.m_Start))
                        nodeToConnections[edge.m_Start] = new List<SpanNodeConnection>();
                    nodeToConnections[edge.m_Start].Add(new SpanNodeConnection { EdgeEntity = roadEntity, IsStartNode = true });
                }

                if (edge.m_End != Entity.Null)
                {
                    if (!nodeToConnections.ContainsKey(edge.m_End))
                        nodeToConnections[edge.m_End] = new List<SpanNodeConnection>();
                    nodeToConnections[edge.m_End].Add(new SpanNodeConnection { EdgeEntity = roadEntity, IsStartNode = false });
                }
            }

            int spanIdCounter = 0;
            int mergedDegree2Count = 0;

            for (int i = 0; i < _roadScanner.RoadSegments.Length; i++)
            {
                var seedEntity = _roadScanner.RoadSegments[i];
                if (visitedEdges.Contains(seedEntity))
                    continue;
                if (!EntityManager.HasComponent<Edge>(seedEntity) || !EntityManager.HasComponent<Curve>(seedEntity))
                    continue;

                var seedEdge = EntityManager.GetComponentData<Edge>(seedEntity);
                var seedCurve = EntityManager.GetComponentData<Curve>(seedEntity);
                float seedLen = MathUtils.Length(seedCurve.m_Bezier);

                var spanEdges = new List<SpanEdge>();
                spanEdges.Add(new SpanEdge { EdgeEntity = seedEntity, IsReversed = false, Length = seedLen });
                visitedEdges.Add(seedEntity);

                // 1. Trace forward through degree-2 pass-through nodes
                Entity currentEndNode = seedEdge.m_End;
                Entity currentEdge = seedEntity;

                while (currentEndNode != Entity.Null &&
                       nodeToConnections.ContainsKey(currentEndNode) &&
                       nodeToConnections[currentEndNode].Count == 2)
                {
                    var conns = nodeToConnections[currentEndNode];
                    Entity nextEdge = Entity.Null;
                    bool nextIsStart = false;

                    for (int c = 0; c < conns.Count; c++)
                    {
                        if (conns[c].EdgeEntity != currentEdge)
                        {
                            nextEdge = conns[c].EdgeEntity;
                            nextIsStart = conns[c].IsStartNode;
                            break;
                        }
                    }

                    if (nextEdge == Entity.Null || visitedEdges.Contains(nextEdge))
                        break;
                    if (!EntityManager.HasComponent<Edge>(nextEdge) || !EntityManager.HasComponent<Curve>(nextEdge))
                        break;

                    // Do not merge across bridge/elevated boundary so land roads keep their own spans
                    if (IsElevatedOrBridge(currentEdge) != IsElevatedOrBridge(nextEdge))
                        break;

                    var nextEdgeData = EntityManager.GetComponentData<Edge>(nextEdge);
                    var nextCurve = EntityManager.GetComponentData<Curve>(nextEdge);
                    float nextLen = MathUtils.Length(nextCurve.m_Bezier);

                    bool isReversed = !nextIsStart;
                    spanEdges.Add(new SpanEdge { EdgeEntity = nextEdge, IsReversed = isReversed, Length = nextLen });
                    visitedEdges.Add(nextEdge);
                    mergedDegree2Count++;

                    currentEdge = nextEdge;
                    currentEndNode = nextIsStart ? nextEdgeData.m_End : nextEdgeData.m_Start;
                }

                // 2. Trace backward through degree-2 pass-through nodes
                Entity currentStartNode = seedEdge.m_Start;
                currentEdge = seedEntity;

                while (currentStartNode != Entity.Null &&
                       nodeToConnections.ContainsKey(currentStartNode) &&
                       nodeToConnections[currentStartNode].Count == 2)
                {
                    var conns = nodeToConnections[currentStartNode];
                    Entity prevEdge = Entity.Null;
                    bool prevIsStart = false;

                    for (int c = 0; c < conns.Count; c++)
                    {
                        if (conns[c].EdgeEntity != currentEdge)
                        {
                            prevEdge = conns[c].EdgeEntity;
                            prevIsStart = conns[c].IsStartNode;
                            break;
                        }
                    }

                    if (prevEdge == Entity.Null || visitedEdges.Contains(prevEdge))
                        break;
                    if (!EntityManager.HasComponent<Edge>(prevEdge) || !EntityManager.HasComponent<Curve>(prevEdge))
                        break;

                    // Do not merge across bridge/elevated boundary so land roads keep their own spans
                    if (IsElevatedOrBridge(currentEdge) != IsElevatedOrBridge(prevEdge))
                        break;

                    var prevEdgeData = EntityManager.GetComponentData<Edge>(prevEdge);
                    var prevCurve = EntityManager.GetComponentData<Curve>(prevEdge);
                    float prevLen = MathUtils.Length(prevCurve.m_Bezier);

                    bool isReversed = prevIsStart;
                    spanEdges.Insert(0, new SpanEdge { EdgeEntity = prevEdge, IsReversed = isReversed, Length = prevLen });
                    visitedEdges.Add(prevEdge);
                    mergedDegree2Count++;

                    currentEdge = prevEdge;
                    currentStartNode = prevIsStart ? prevEdgeData.m_End : prevEdgeData.m_Start;
                }

                // 3. Compute total length and find exact midpoint along the multi-edge span
                float totalLength = 0f;
                for (int e = 0; e < spanEdges.Count; e++)
                {
                    totalLength += spanEdges[e].Length;
                }

                int startDegree = (currentStartNode != Entity.Null && nodeToConnections.TryGetValue(currentStartNode, out var startConns)) ? startConns.Count : 0;
                int endDegree = (currentEndNode != Entity.Null && nodeToConnections.TryGetValue(currentEndNode, out var endConns)) ? endConns.Count : 0;

                // Exclude isolated road spans where both ends are dead ends (no network connectivity)
                if (startDegree <= 1 && endDegree <= 1)
                {
                    log.Info($"Excluded isolated road span (both ends dead-end): Length {totalLength:F1}m, Edges {spanEdges.Count}");
                    continue;
                }

                bool isDeadEnd = (startDegree <= 1 || endDegree <= 1);

                // Safe Center-Edge Stop Placement:
                // NEVER place stops near edge boundary nodes or intersection conflict zones.
                // A bus stopping box requires 15m vehicle length + 5m clearance.
                // We pick the best host edge in spanEdges (preferring length >= 42m, closest to span center)
                // and place the stop at safeT (clamped to >= 20m from start node and >= 22m from end node).
                SpanEdge bestEdge = spanEdges[0];
                float targetSpanDist = totalLength * 0.5f;

                // 1. Identify if any edge has length >= 42m (room for 20m start clearance + 22m end clearance)
                bool hasEligibleEdge = false;
                for (int e = 0; e < spanEdges.Count; e++)
                {
                    if (spanEdges[e].Length >= 42.0f)
                    {
                        hasEligibleEdge = true;
                        break;
                    }
                }

                if (hasEligibleEdge)
                {
                    // Pick the eligible edge whose center is closest to span midpoint
                    float bestSpanDistDiff = float.MaxValue;
                    float accDist = 0f;
                    for (int e = 0; e < spanEdges.Count; e++)
                    {
                        var se = spanEdges[e];
                        float edgeCenterSpanDist = accDist + se.Length * 0.5f;
                        if (se.Length >= 42.0f)
                        {
                            float diff = math.abs(edgeCenterSpanDist - targetSpanDist);
                            if (diff < bestSpanDistDiff)
                            {
                                bestSpanDistDiff = diff;
                                bestEdge = se;
                            }
                        }
                        accDist += se.Length;
                    }
                }
                else
                {
                    // If no edge is >= 42m, pick the longest edge in the span
                    for (int e = 1; e < spanEdges.Count; e++)
                    {
                        if (spanEdges[e].Length > bestEdge.Length)
                            bestEdge = spanEdges[e];
                    }
                }

                float bestLen = bestEdge.Length;
                float safeT = 0.5f;
                if (bestLen >= 42.0f)
                {
                    float minT = 20.0f / bestLen;
                    float maxT = 1.0f - (22.0f / bestLen);
                    safeT = math.clamp(0.5f, minT, maxT);
                }
                else
                {
                    safeT = 0.5f;
                }

                float actualT = bestEdge.IsReversed ? (1.0f - safeT) : safeT;
                var bestCurve = EntityManager.GetComponentData<Curve>(bestEdge.EdgeEntity);

                Entity midEdge = bestEdge.EdgeEntity;
                float midT = actualT;
                float3 midPos = MathUtils.Position(bestCurve.m_Bezier, actualT);

                float3 curveTan = MathUtils.Tangent(bestCurve.m_Bezier, actualT);
                if (bestEdge.IsReversed)
                    curveTan = -curveTan;

                float3 midTan = math.normalizesafe(curveTan, new float3(0, 0, 1));

                // Determine nearest Hub for this span based on its midpoint
                Entity nearestHub = Entity.Null;
                float minDistance = float.MaxValue;
                for (int j = 0; j < _depotFinder.AllHubs.Length; j++)
                {
                    var hub = _depotFinder.AllHubs[j];
                    float dist = math.distance(midPos, hub.Position);
                    if (hub.IsStation) dist *= 0.85f;
                    if (dist < minDistance)
                    {
                        minDistance = dist;
                        nearestHub = hub.HubEntity;
                    }
                }

                int corrIdx = -1;
                float corrLen = 0f;
                if (edgeToCorridorIndex.ContainsKey(midEdge))
                {
                    corrIdx = edgeToCorridorIndex[midEdge];
                    corrLen = edgeToCorridorLength[midEdge];
                }
                else
                {
                    for (int e = 0; e < spanEdges.Count; e++)
                    {
                        if (edgeToCorridorIndex.ContainsKey(spanEdges[e].EdgeEntity))
                        {
                            corrIdx = edgeToCorridorIndex[spanEdges[e].EdgeEntity];
                            corrLen = edgeToCorridorLength[spanEdges[e].EdgeEntity];
                            break;
                        }
                    }
                }

                // Compute start and end tangents and positions for planar face extraction
                var firstSpanEdge = spanEdges[0];
                var firstCurve = EntityManager.GetComponentData<Curve>(firstSpanEdge.EdgeEntity);
                float3 startPos = firstSpanEdge.IsReversed ? MathUtils.Position(firstCurve.m_Bezier, 1f) : MathUtils.Position(firstCurve.m_Bezier, 0f);
                float3 startTan = firstSpanEdge.IsReversed
                    ? -math.normalizesafe(MathUtils.Tangent(firstCurve.m_Bezier, 1f), new float3(0, 0, 1))
                    : math.normalizesafe(MathUtils.Tangent(firstCurve.m_Bezier, 0f), new float3(0, 0, 1));

                var lastSpanEdge = spanEdges[spanEdges.Count - 1];
                var lastCurve = EntityManager.GetComponentData<Curve>(lastSpanEdge.EdgeEntity);
                float3 endPos = lastSpanEdge.IsReversed ? MathUtils.Position(lastCurve.m_Bezier, 0f) : MathUtils.Position(lastCurve.m_Bezier, 1f);
                float3 endTan = lastSpanEdge.IsReversed
                    ? -math.normalizesafe(MathUtils.Tangent(lastCurve.m_Bezier, 0f), new float3(0, 0, 1))
                    : math.normalizesafe(MathUtils.Tangent(lastCurve.m_Bezier, 1f), new float3(0, 0, 1));

                bool isSideStreet = (corrLen < 160.0f);

                var blockSpan = new BlockSpan
                {
                    SpanId = spanIdCounter++,
                    Edges = spanEdges,
                    StartNode = currentStartNode,
                    EndNode = currentEndNode,
                    StartDegree = startDegree,
                    EndDegree = endDegree,
                    IsDeadEnd = isDeadEnd,
                    DeadEndBranchLength = isDeadEnd ? totalLength : 0f,
                    TotalLength = totalLength,
                    MidPosition = midPos,
                    MidTangent = midTan,
                    MidEdge = midEdge,
                    MidT = midT,
                    HubEntity = nearestHub,
                    CorridorIndex = corrIdx,
                    CorridorLength = corrLen,
                    IsSuppressed = false,
                    IsSideStreet = isSideStreet,
                    StartPosition = startPos,
                    EndPosition = endPos,
                    StartTangent = startTan,
                    EndTangent = endTan,
                    SpanOrderInCorridor = 0,
                    CorridorForwardTangent = midTan
                };

                blockSpans.Add(blockSpan);
            }

            // Post-process corridors to assign sequential SpanOrderInCorridor and CorridorForwardTangent
            var edgeToSpan = new Dictionary<Entity, BlockSpan>();
            for (int s = 0; s < blockSpans.Count; s++)
            {
                var span = blockSpans[s];
                for (int e = 0; e < span.Edges.Count; e++)
                {
                    edgeToSpan[span.Edges[e].EdgeEntity] = span;
                }
            }

            for (int c = 0; c < corridors.Count; c++)
            {
                var corr = corridors[c];
                var visitedSpansInCorr = new HashSet<int>();
                int order = 0;

                for (int s = 0; s < corr.Segments.Count; s++)
                {
                    var seg = corr.Segments[s];
                    if (edgeToSpan.TryGetValue(seg.RoadEntity, out BlockSpan span))
                    {
                        if (visitedSpansInCorr.Contains(span.SpanId))
                            continue;

                        visitedSpansInCorr.Add(span.SpanId);
                        span.SpanOrderInCorridor = order++;

                        // Compute tangent aligned with corridor forward direction
                        if (EntityManager.HasComponent<Curve>(seg.RoadEntity))
                        {
                            var curve = EntityManager.GetComponentData<Curve>(seg.RoadEntity);
                            float3 segTan = math.normalizesafe(MathUtils.Tangent(curve.m_Bezier, 0.5f), new float3(0, 0, 1));
                            if (seg.IsReversed)
                                segTan = -segTan;

                            span.CorridorForwardTangent = segTan;
                        }
                    }
                }
            }

            for (int s = 0; s < blockSpans.Count; s++)
            {
                if (math.lengthsq(blockSpans[s].CorridorForwardTangent) < 0.001f)
                {
                    blockSpans[s].CorridorForwardTangent = blockSpans[s].MidTangent;
                    blockSpans[s].SpanOrderInCorridor = 0;
                }
            }

            // Propagate dead-end status along branching cul-de-sacs (stems leading only to dead ends)
            bool deadEndPropagated = true;
            int pass = 0;
            while (deadEndPropagated && pass < 8)
            {
                deadEndPropagated = false;
                pass++;

                var nodeSpanMap = new Dictionary<Entity, List<BlockSpan>>();
                for (int s = 0; s < blockSpans.Count; s++)
                {
                    var sp = blockSpans[s];
                    if (sp.StartNode != Entity.Null)
                    {
                        if (!nodeSpanMap.TryGetValue(sp.StartNode, out var list))
                        {
                            list = new List<BlockSpan>();
                            nodeSpanMap[sp.StartNode] = list;
                        }
                        list.Add(sp);
                    }
                    if (sp.EndNode != Entity.Null)
                    {
                        if (!nodeSpanMap.TryGetValue(sp.EndNode, out var list))
                        {
                            list = new List<BlockSpan>();
                            nodeSpanMap[sp.EndNode] = list;
                        }
                        list.Add(sp);
                    }
                }

                foreach (var kvp in nodeSpanMap)
                {
                    var conns = kvp.Value;
                    if (conns.Count < 2) continue;

                    int nonDeadEndCount = 0;
                    BlockSpan candidateStem = null;
                    float maxDeadEndChildBranch = 0f;
                    for (int c = 0; c < conns.Count; c++)
                    {
                        if (!conns[c].IsDeadEnd)
                        {
                            nonDeadEndCount++;
                            candidateStem = conns[c];
                        }
                        else
                        {
                            maxDeadEndChildBranch = math.max(maxDeadEndChildBranch, conns[c].DeadEndBranchLength);
                        }
                    }

                    if (nonDeadEndCount == 1 && candidateStem != null)
                    {
                        candidateStem.IsDeadEnd = true;
                        float combinedBranch = candidateStem.TotalLength + maxDeadEndChildBranch;
                        candidateStem.DeadEndBranchLength = math.max(candidateStem.DeadEndBranchLength, combinedBranch);

                        // Propagate total combined branch length to all connected dead-end children on this branch
                        for (int c = 0; c < conns.Count; c++)
                        {
                            if (conns[c] != candidateStem)
                            {
                                conns[c].DeadEndBranchLength = math.max(conns[c].DeadEndBranchLength, candidateStem.DeadEndBranchLength);
                            }
                        }
                        deadEndPropagated = true;
                    }
                }
            }

            // Flood-fill maximum branch length across all connected dead-end spans in each cul-de-sac system
            // so every span along a branching cul-de-sac shares the full branch distance to the main network
            var deadEndNodeSpanMap = new Dictionary<Entity, List<BlockSpan>>();
            for (int s = 0; s < blockSpans.Count; s++)
            {
                var sp = blockSpans[s];
                if (!sp.IsDeadEnd) continue;
                if (sp.StartNode != Entity.Null)
                {
                    if (!deadEndNodeSpanMap.TryGetValue(sp.StartNode, out var list))
                    {
                        list = new List<BlockSpan>();
                        deadEndNodeSpanMap[sp.StartNode] = list;
                    }
                    list.Add(sp);
                }
                if (sp.EndNode != Entity.Null)
                {
                    if (!deadEndNodeSpanMap.TryGetValue(sp.EndNode, out var list))
                    {
                        list = new List<BlockSpan>();
                        deadEndNodeSpanMap[sp.EndNode] = list;
                    }
                    list.Add(sp);
                }
            }

            bool branchLengthExpanded = true;
            int expandPass = 0;
            while (branchLengthExpanded && expandPass < 10)
            {
                branchLengthExpanded = false;
                expandPass++;
                foreach (var kvp in deadEndNodeSpanMap)
                {
                    var conns = kvp.Value;
                    float maxBranchInNode = 0f;
                    for (int c = 0; c < conns.Count; c++)
                    {
                        if (conns[c].DeadEndBranchLength > maxBranchInNode)
                            maxBranchInNode = conns[c].DeadEndBranchLength;
                    }

                    if (maxBranchInNode > 0f)
                    {
                        for (int c = 0; c < conns.Count; c++)
                        {
                            if (conns[c].DeadEndBranchLength < maxBranchInNode)
                            {
                                conns[c].DeadEndBranchLength = maxBranchInNode;
                                branchLengthExpanded = true;
                            }
                        }
                    }
                }
            }

            int deadEndSpanCount = 0;
            float maxBranchLength = 0f;
            for (int s = 0; s < blockSpans.Count; s++)
            {
                if (blockSpans[s].IsDeadEnd)
                {
                    deadEndSpanCount++;
                    maxBranchLength = math.max(maxBranchLength, blockSpans[s].DeadEndBranchLength);
                }
            }

            log.Info($"Block Spans Built: {blockSpans.Count} spans ({deadEndSpanCount} dead-end/cul-de-sac spans identified, longest branch {maxBranchLength:F1}m) from {_roadScanner.RoadSegments.Length} edges ({mergedDegree2Count} degree-2 pass-through splits merged)");
            return blockSpans;
        }

        private List<CityBlockFace> BuildCityBlockFaces(List<BlockSpan> blockSpans)
        {
            var faces = new List<CityBlockFace>();

            // Group outgoing half-edges by node
            var nodeToOutgoing = new Dictionary<Entity, List<NodeOutgoingHalfEdge>>();

            for (int i = 0; i < blockSpans.Count; i++)
            {
                var span = blockSpans[i];
                if (span.StartNode != Entity.Null)
                {
                    if (!nodeToOutgoing.ContainsKey(span.StartNode))
                        nodeToOutgoing[span.StartNode] = new List<NodeOutgoingHalfEdge>();

                    float angle = math.atan2(span.StartTangent.z, span.StartTangent.x);
                    nodeToOutgoing[span.StartNode].Add(new NodeOutgoingHalfEdge
                    {
                        HalfEdge = new DirectedSpanHalfEdge { SpanIndex = i, IsForward = true },
                        Angle = angle
                    });
                }

                if (span.EndNode != Entity.Null)
                {
                    if (!nodeToOutgoing.ContainsKey(span.EndNode))
                        nodeToOutgoing[span.EndNode] = new List<NodeOutgoingHalfEdge>();

                    float3 outTan = -span.EndTangent;
                    float angle = math.atan2(outTan.z, outTan.x);
                    nodeToOutgoing[span.EndNode].Add(new NodeOutgoingHalfEdge
                    {
                        HalfEdge = new DirectedSpanHalfEdge { SpanIndex = i, IsForward = false },
                        Angle = angle
                    });
                }
            }

            // Sort outgoing half-edges around each node in counter-clockwise order by angle
            foreach (var kvp in nodeToOutgoing)
            {
                kvp.Value.Sort((a, b) => a.Angle.CompareTo(b.Angle));
            }

            // Track visited directed half-edges
            var visitedHalfEdges = new HashSet<long>();

            long GetHalfEdgeKey(int spanIndex, bool isForward)
            {
                return ((long)spanIndex << 1) | (isForward ? 1L : 0L);
            }

            int faceIdCounter = 0;

            for (int i = 0; i < blockSpans.Count; i++)
            {
                for (int dir = 0; dir < 2; dir++)
                {
                    bool isForward = (dir == 0);
                    long startKey = GetHalfEdgeKey(i, isForward);
                    if (visitedHalfEdges.Contains(startKey))
                        continue;

                    var cycleHalfEdges = new List<DirectedSpanHalfEdge>();
                    var polygonPoints = new List<float2>();
                    var stepVisited = new HashSet<long>();

                    var currHalfEdge = new DirectedSpanHalfEdge { SpanIndex = i, IsForward = isForward };
                    bool cycleFound = false;

                    while (true)
                    {
                        long currKey = GetHalfEdgeKey(currHalfEdge.SpanIndex, currHalfEdge.IsForward);
                        if (stepVisited.Contains(currKey) || visitedHalfEdges.Contains(currKey))
                        {
                            if (currKey == startKey)
                                cycleFound = true;
                            break;
                        }

                        stepVisited.Add(currKey);
                        cycleHalfEdges.Add(currHalfEdge);

                        var span = blockSpans[currHalfEdge.SpanIndex];
                        Entity arrivalNode = currHalfEdge.IsForward ? span.EndNode : span.StartNode;

                        // Add sample points along span for exact polygon area and perimeter
                        if (currHalfEdge.IsForward)
                        {
                            polygonPoints.Add(new float2(span.StartPosition.x, span.StartPosition.z));
                            polygonPoints.Add(new float2(span.MidPosition.x, span.MidPosition.z));
                        }
                        else
                        {
                            polygonPoints.Add(new float2(span.EndPosition.x, span.EndPosition.z));
                            polygonPoints.Add(new float2(span.MidPosition.x, span.MidPosition.z));
                        }

                        if (arrivalNode == Entity.Null || !nodeToOutgoing.ContainsKey(arrivalNode))
                            break;

                        var outgoingList = nodeToOutgoing[arrivalNode];
                        if (outgoingList.Count == 0)
                            break;

                        // Find index of the reverse half-edge leaving arrivalNode
                        DirectedSpanHalfEdge reverseHalfEdge = new DirectedSpanHalfEdge
                        {
                            SpanIndex = currHalfEdge.SpanIndex,
                            IsForward = !currHalfEdge.IsForward
                        };

                        int revIdx = -1;
                        for (int o = 0; o < outgoingList.Count; o++)
                        {
                            if (outgoingList[o].HalfEdge.SpanIndex == reverseHalfEdge.SpanIndex &&
                                outgoingList[o].HalfEdge.IsForward == reverseHalfEdge.IsForward)
                            {
                                revIdx = o;
                                break;
                            }
                        }

                        if (revIdx < 0)
                            break;

                        // Cyclic next in counter-clockwise order
                        int nextIdx = (revIdx + 1) % outgoingList.Count;
                        currHalfEdge = outgoingList[nextIdx].HalfEdge;
                    }

                    if (cycleFound)
                    {
                        visitedHalfEdges.UnionWith(stepVisited);

                        if (cycleHalfEdges.Count >= 3 && polygonPoints.Count >= 3)
                        {
                            // Calculate signed 2D polygon area
                            float signedArea = 0f;
                            float perimeter = 0f;
                            float2 centroidSum = float2.zero;

                            int numPts = polygonPoints.Count;
                            for (int p = 0; p < numPts; p++)
                            {
                                var p1 = polygonPoints[p];
                                var p2 = polygonPoints[(p + 1) % numPts];

                                float cross = (p1.x * p2.y) - (p2.x * p1.y);
                                signedArea += cross;
                                perimeter += math.distance(p1, p2);
                                centroidSum += (p1 + p2) * cross;
                            }

                            signedArea *= 0.5f;
                            float absArea = math.abs(signedArea);

                            // Absolute area within valid block dimensions
                            if (absArea >= MIN_BLOCK_AREA && absArea <= MAX_BLOCK_AREA && perimeter > 0f)
                            {
                                float thicknessRatio = absArea / perimeter;
                                if (thicknessRatio >= MIN_BLOCK_THICKNESS_RATIO)
                                {
                                    float2 centroid = float2.zero;
                                    if (absArea > 0.001f && math.abs(signedArea) > 0.001f)
                                    {
                                        centroid = centroidSum / (6.0f * signedArea);
                                    }

                                    var face = new CityBlockFace
                                    {
                                        FaceId = faceIdCounter++,
                                        Area = absArea,
                                        Perimeter = perimeter,
                                        Centroid = centroid,
                                        IsServed = false
                                    };

                                    for (int c = 0; c < cycleHalfEdges.Count; c++)
                                    {
                                        int sIdx = cycleHalfEdges[c].SpanIndex;
                                        if (!face.SpanIndices.Contains(sIdx))
                                            face.SpanIndices.Add(sIdx);
                                    }

                                    faces.Add(face);
                                }
                            }
                        }
                    }
                }
            }

            log.Info($"City Block Faces Enumerated: {faces.Count} enclosed building blocks discovered across the network");
            return faces;
        }

        public void UpdateDiscoveredPrefabs()
        {
            FindBusStopPrefabs(out _, out _);
        }

        private string GetPrefabName(Entity e)
        {
            if (_prefabSystem != null)
            {
                if (_prefabSystem.TryGetPrefab<PrefabBase>(e, out var pBase) && pBase != null)
                    return pBase.name ?? "";
                return _prefabSystem.GetPrefabName(e) ?? "";
            }
            return "";
        }

        private string GetPrefabIcon(Entity e)
        {
            if (_prefabSystem != null)
            {
                if (_prefabSystem.TryGetPrefab<PrefabBase>(e, out var pBase) && pBase != null)
                {
                    if (pBase.TryGet<UIObject>(out var uiObj) && uiObj != null && !string.IsNullOrEmpty(uiObj.m_Icon))
                    {
                        return uiObj.m_Icon;
                    }
                }
            }
            return "Media/Game/Icons/BusStop.svg";
        }

        private bool FindBusStopPrefabs(out List<Entity> candidatePrefabs, out Entity defaultPrefab)
        {
            candidatePrefabs = new List<Entity>();
            defaultPrefab = Entity.Null;

            if (_busStopPrefabQuery.IsEmptyIgnoreFilter)
                return false;

            var entities = _busStopPrefabQuery.ToEntityArray(Allocator.Temp);
            var stopDataArray = _busStopPrefabQuery.ToComponentDataArray<TransportStopData>(Allocator.Temp);

            var allValidPrefabs = new List<Entity>();
            var nameToEntity = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase);
            var discovered = new List<string>();

            for (int i = 0; i < entities.Length; i++)
            {
                var e = entities[i];
                if (EntityManager.HasComponent<Game.Prefabs.OutsideConnectionData>(e))
                    continue;

                var stopData = stopDataArray[i];

                if (stopData.m_TransportType == TransportType.Bus && stopData.m_PassengerTransport)
                {
                    string pName = GetPrefabName(e);
                    if (string.IsNullOrEmpty(pName))
                        continue;

                    if (pName.IndexOf("Outside Connection", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        pName.IndexOf("OutsideConnection", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        pName.IndexOf("Placeholder", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        pName.IndexOf("Integrated", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        pName.IndexOf("Platform", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        pName.IndexOf("Subway", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        pName.IndexOf("Train", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        continue;
                    }

                    allValidPrefabs.Add(e);

                    if (!discovered.Contains(pName))
                        discovered.Add(pName);

                    string pIcon = GetPrefabIcon(e);
                    Setting.DiscoveredStopPrefabIcons[pName] = pIcon;

                    if (!nameToEntity.ContainsKey(pName))
                    {
                        nameToEntity[pName] = e;
                    }
                }
            }

            entities.Dispose();
            stopDataArray.Dispose();

            if (discovered.Count > 0)
            {
                bool changed = false;
                if (Setting.DiscoveredStopPrefabNames.Count != discovered.Count)
                {
                    changed = true;
                }
                else
                {
                    for (int i = 0; i < discovered.Count; i++)
                    {
                        if (Setting.DiscoveredStopPrefabNames[i] != discovered[i])
                        {
                            changed = true;
                            break;
                        }
                    }
                }

                if (changed)
                {
                    Setting.DiscoveredStopPrefabNames.Clear();
                    Setting.DiscoveredStopPrefabNames.AddRange(discovered);
                    Setting.DiscoveredStopPrefabVersion++;
                    AutoBusLinesUISystem.Instance?.UpdateStopPrefabOptions();
                    log.Info($"Discovered {discovered.Count} exact Bus Stop Prefab models in the game: {string.Join(", ", discovered)}");
                }
            }

            if (allValidPrefabs.Count == 0)
                return false;

            defaultPrefab = allValidPrefabs[0];

            string selectedModel = Mod.setting != null ? Mod.setting.SelectedStopPrefab : "";
            if (string.IsNullOrEmpty(selectedModel) || selectedModel.Equals("All", StringComparison.OrdinalIgnoreCase))
            {
                candidatePrefabs.AddRange(allValidPrefabs);
            }
            else if (selectedModel.Equals("Curbside", StringComparison.OrdinalIgnoreCase))
            {
                candidatePrefabs.Clear();
                for (int i = 0; i < allValidPrefabs.Count; i++)
                {
                    if (IsBusStopSign(allValidPrefabs[i]))
                        candidatePrefabs.Add(allValidPrefabs[i]);
                }
                if (candidatePrefabs.Count == 0 && allValidPrefabs.Count > 0)
                    candidatePrefabs.AddRange(allValidPrefabs);

                defaultPrefab = candidatePrefabs.Count > 0 ? candidatePrefabs[0] : allValidPrefabs[0];
                log.Info($"Active Bus Stop Prefab Pool: Curbside Mode ({candidatePrefabs.Count} curbside stop pole sign prefabs)");
                return true;
            }
            else if (selectedModel.Equals("Signs", StringComparison.OrdinalIgnoreCase) || selectedModel.Equals("SignsOnly", StringComparison.OrdinalIgnoreCase))
            {
                for (int i = 0; i < allValidPrefabs.Count; i++)
                {
                    if (IsBusStopSign(allValidPrefabs[i]))
                        candidatePrefabs.Add(allValidPrefabs[i]);
                }
                if (candidatePrefabs.Count == 0)
                    candidatePrefabs.AddRange(allValidPrefabs);
            }
            else if (selectedModel.Equals("Shelters", StringComparison.OrdinalIgnoreCase) || selectedModel.Equals("SheltersOnly", StringComparison.OrdinalIgnoreCase))
            {
                for (int i = 0; i < allValidPrefabs.Count; i++)
                {
                    if (!IsBusStopSign(allValidPrefabs[i]))
                        candidatePrefabs.Add(allValidPrefabs[i]);
                }
                if (candidatePrefabs.Count == 0)
                    candidatePrefabs.AddRange(allValidPrefabs);
            }
            else
            {
                var selectedNames = selectedModel.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                var matched = new HashSet<Entity>();
                for (int i = 0; i < selectedNames.Length; i++)
                {
                    string trimmed = selectedNames[i].Trim();
                    if (nameToEntity.TryGetValue(trimmed, out Entity match) && match != Entity.Null)
                    {
                        if (matched.Add(match))
                        {
                            candidatePrefabs.Add(match);
                        }
                    }
                }

                if (candidatePrefabs.Count == 0)
                {
                    // Reset stale/missing saved model names to "All" so UI and placement stay aligned
                    candidatePrefabs.AddRange(allValidPrefabs);
                    if (Mod.setting != null)
                    {
                        Mod.setting.SelectedStopPrefab = "All";
                        Mod.setting.Apply();
                        selectedModel = "All";
                        AutoBusLinesUISystem.Instance?.SyncStopPrefabSelection("All");
                    }
                }
            }

            log.Info($"Active Bus Stop Prefab Pool: {candidatePrefabs.Count} prefabs available for placement (Selected Models: '{selectedModel}')");
            return true;
        }

        public List<PlacedStop> DiscoverExistingBusStops(out Dictionary<Entity, List<PlacedStop>> existingStopsByRoad)
        {
            existingStopsByRoad = new Dictionary<Entity, List<PlacedStop>>();
            var allExistingStops = new List<PlacedStop>();

            if (_existingBusStopQuery.IsEmptyIgnoreFilter)
                return allExistingStops;

            var existingStopEntities = _existingBusStopQuery.ToEntityArray(Allocator.Temp);
            var existingStopPrefabRefs = _existingBusStopQuery.ToComponentDataArray<PrefabRef>(Allocator.Temp);
            var existingStopTransforms = _existingBusStopQuery.ToComponentDataArray<Game.Objects.Transform>(Allocator.Temp);

            for (int i = 0; i < existingStopEntities.Length; i++)
            {
                var stopEntity = existingStopEntities[i];
                var prefabEntity = existingStopPrefabRefs[i].m_Prefab;

                if (EntityManager.HasComponent<Game.Objects.OutsideConnection>(stopEntity) ||
                    EntityManager.HasComponent<Game.Net.OutsideConnection>(stopEntity) ||
                    EntityManager.HasComponent<Game.Prefabs.OutsideConnectionData>(prefabEntity))
                {
                    continue;
                }

                string pName = GetPrefabName(prefabEntity);
                if (!string.IsNullOrEmpty(pName) && (
                    pName.IndexOf("Outside Connection", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    pName.IndexOf("OutsideConnection", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    pName.IndexOf("Placeholder", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    continue;
                }

                if (!EntityManager.HasComponent<TransportStopData>(prefabEntity))
                    continue;

                var stopData = EntityManager.GetComponentData<TransportStopData>(prefabEntity);
                if (stopData.m_TransportType != TransportType.Bus || !stopData.m_PassengerTransport)
                    continue;

                // Station platform bays are handled separately as hub anchors
                if (_depotFinder != null && _depotFinder.StationPlatformStops.Contains(stopEntity))
                    continue;

                var transform = existingStopTransforms[i];
                float3 stopPos = transform.m_Position;
                float3 stopForward = math.mul(transform.m_Rotation, new float3(0, 0, 1));

                Entity attachedRoad = Entity.Null;
                if (EntityManager.HasComponent<Attached>(stopEntity))
                {
                    attachedRoad = EntityManager.GetComponentData<Attached>(stopEntity).m_Parent;
                }
                else if (EntityManager.HasComponent<Game.Common.Owner>(stopEntity))
                {
                    attachedRoad = EntityManager.GetComponentData<Game.Common.Owner>(stopEntity).m_Owner;
                }

                // If attached to a road with a Curve, derive true road travel forward tangent
                if (attachedRoad != Entity.Null && EntityManager.HasComponent<Curve>(attachedRoad))
                {
                    var roadCurve = EntityManager.GetComponentData<Curve>(attachedRoad);
                    float attachedT = 0.5f;
                    if (EntityManager.HasComponent<Attached>(stopEntity))
                    {
                        attachedT = EntityManager.GetComponentData<Attached>(stopEntity).m_CurvePosition;
                    }
                    else
                    {
                        MathUtils.Distance(roadCurve.m_Bezier, stopPos, out attachedT);
                    }

                    float3 roadTangent = MathUtils.Tangent(roadCurve.m_Bezier, attachedT);
                    float3 roadForward = math.normalizesafe(new float3(roadTangent.x, 0f, roadTangent.z));

                    bool isLeftHandTraffic = false;
                    var cityConfig = World.GetExistingSystemManaged<Game.City.CityConfigurationSystem>();
                    if (cityConfig != null)
                    {
                        isLeftHandTraffic = cityConfig.leftHandTraffic;
                    }

                    float3 roadPosOnCurve = MathUtils.Position(roadCurve.m_Bezier, attachedT);
                    float3 offset = stopPos - roadPosOnCurve;
                    float3 rightNorm = new float3(roadForward.z, 0f, -roadForward.x);
                    bool isOnRightSide = math.dot(offset, rightNorm) >= 0f;

                    // In RHT: right side travels in roadForward, left side travels in -roadForward
                    // In LHT: left side travels in roadForward, right side travels in -roadForward
                    if (isLeftHandTraffic)
                    {
                        stopForward = isOnRightSide ? -roadForward : roadForward;
                    }
                    else
                    {
                        stopForward = isOnRightSide ? roadForward : -roadForward;
                    }
                }

                // If a roadside stop erroneously has Owner(road), strip it so it becomes interactable immediately
                if (EntityManager.HasComponent<Game.Common.Owner>(stopEntity))
                {
                    var owner = EntityManager.GetComponentData<Game.Common.Owner>(stopEntity).m_Owner;
                    if (owner != Entity.Null && EntityManager.HasComponent<Game.Net.Edge>(owner))
                    {
                        EntityManager.RemoveComponent<Game.Common.Owner>(stopEntity);
                        log.Info($"Healed roadside bus stop {stopEntity.Index}: removed erroneous Owner component to restore interactivity.");
                    }
                }

                // Determine nearest Hub
                Entity nearestHub = Entity.Null;
                float minHubDist = float.MaxValue;

                if (_depotFinder != null)
                {
                    for (int h = 0; h < _depotFinder.AllHubs.Length; h++)
                    {
                        var hub = _depotFinder.AllHubs[h];
                        float d = math.distance(stopPos, hub.Position);
                        if (hub.IsStation) d *= 0.85f;
                        if (d < minHubDist)
                        {
                            minHubDist = d;
                            nearestHub = hub.HubEntity;
                        }
                    }
                }

                var placed = new PlacedStop
                {
                    StopEntity = stopEntity,
                    Position = stopPos,
                    Forward = stopForward,
                    RoadEntity = attachedRoad,
                    HubEntity = nearestHub,
                    IsPreExisting = true,
                    IsStationBay = false
                };

                allExistingStops.Add(placed);

                if (attachedRoad != Entity.Null)
                {
                    if (!existingStopsByRoad.ContainsKey(attachedRoad))
                        existingStopsByRoad[attachedRoad] = new List<PlacedStop>();
                    existingStopsByRoad[attachedRoad].Add(placed);
                }
            }

            existingStopEntities.Dispose();
            existingStopPrefabRefs.Dispose();
            existingStopTransforms.Dispose();

            return allExistingStops;
        }

        private void PlaceStopsForSpan(
            BlockSpan span,
            float effectiveSpacing,
            bool touchesBridge,
            float minCollisionSpacing,
            bool planOnly,
            List<float3> exclusionPositions,
            Dictionary<Entity, List<PlacedStop>> existingStopsByRoad,
            List<CommittedStopInfo> committedStops,
            List<Entity> candidatePrefabs,
            Dictionary<Entity, int> placementCounts,
            ref Unity.Mathematics.Random rng,
            ref int totalStopsPlaced,
            ref int existingStopsReused,
            List<PlacedStop> allGlobalStops,
            Action<Entity, PlacedStop> onNewStopPlaced = null)
        {
            if (TryFindExistingStopOnSpan(span, existingStopsByRoad, out PlacedStop reusedStop))
            {
                reusedStop.CorridorIndex = span.CorridorIndex;
                reusedStop.SpanOrderInCorridor = span.SpanOrderInCorridor;
                reusedStop.IsOutbound = (math.dot(reusedStop.Forward, span.CorridorForwardTangent) >= 0f);
                allGlobalStops?.Add(reusedStop);
                existingStopsReused++;
                committedStops.Add(new CommittedStopInfo
                {
                    Position = reusedStop.Position,
                    Forward = reusedStop.Forward,
                    CorridorIndex = span.CorridorIndex,
                    SpanId = span.SpanId
                });

                if (span.TotalLength <= effectiveSpacing * 1.6f)
                    return;
            }

            int stopsToPlace = math.max(1, (int)math.round(span.TotalLength / effectiveSpacing));
            if (touchesBridge && span.TotalLength >= 350f)
                stopsToPlace = math.min(stopsToPlace, 2);

            for (int i = 0; i < stopsToPlace; i++)
            {
                Entity roadEdge;
                float edgeT;
                float3 roadPos;
                float3 roadTan;

                if (stopsToPlace == 1)
                {
                    roadEdge = span.MidEdge;
                    edgeT = span.MidT;
                    roadPos = span.MidPosition;
                    roadTan = span.MidTangent;

                    if (EntityManager.HasComponent<Curve>(roadEdge))
                    {
                        var midCurve = EntityManager.GetComponentData<Curve>(roadEdge);
                        if (MathUtils.Length(midCurve.m_Bezier) < 14.0f && span.Edges.Count > 1)
                        {
                            float maxLen = 0f;
                            for (int e = 0; e < span.Edges.Count; e++)
                            {
                                var cand = span.Edges[e].EdgeEntity;
                                if (cand != Entity.Null && EntityManager.HasComponent<Curve>(cand) && !IsElevatedOrBridge(cand))
                                {
                                    var candCurve = EntityManager.GetComponentData<Curve>(cand);
                                    float candLen = MathUtils.Length(candCurve.m_Bezier);
                                    if (candLen > maxLen)
                                    {
                                        maxLen = candLen;
                                        roadEdge = cand;
                                        edgeT = 0.5f;
                                        roadPos = MathUtils.Position(candCurve.m_Bezier, 0.5f);
                                        float3 cTan = MathUtils.Tangent(candCurve.m_Bezier, 0.5f);
                                        if (span.Edges[e].IsReversed) cTan = -cTan;
                                        roadTan = math.normalizesafe(cTan, new float3(0, 0, 1));
                                    }
                                }
                            }
                        }
                    }

                    if (IsElevatedOrBridge(roadEdge))
                    {
                        bool foundAlternative = false;
                        for (int e = 0; e < span.Edges.Count; e++)
                        {
                            var candEdge = span.Edges[e].EdgeEntity;
                            if (!IsElevatedOrBridge(candEdge) && EntityManager.HasComponent<Curve>(candEdge))
                            {
                                var candCurve = EntityManager.GetComponentData<Curve>(candEdge);
                                roadEdge = candEdge;
                                edgeT = 0.5f;
                                roadPos = MathUtils.Position(candCurve.m_Bezier, 0.5f);
                                float3 cTan = MathUtils.Tangent(candCurve.m_Bezier, 0.5f);
                                if (span.Edges[e].IsReversed) cTan = -cTan;
                                roadTan = math.normalizesafe(cTan, new float3(0, 0, 1));
                                foundAlternative = true;
                                break;
                            }
                        }
                        if (!foundAlternative)
                            continue;
                    }
                }
                else
                {
                    float targetDist = (i + 1.0f) / (stopsToPlace + 1.0f) * span.TotalLength;
                    if (!TryGetSpanPointAtDistance(span, targetDist, out roadEdge, out edgeT, out roadPos, out roadTan))
                        continue;

                    if (IsElevatedOrBridge(roadEdge))
                        continue;
                }

                bool ptNearHub = false;
                for (int ep = 0; ep < exclusionPositions.Count; ep++)
                {
                    if (math.distance(roadPos, exclusionPositions[ep]) < HUB_EXCLUSION_RADIUS)
                    {
                        ptNearHub = true;
                        break;
                    }
                }
                if (ptNearHub)
                    continue;

                bool isAlreadyServed = false;
                for (int k = 0; k < committedStops.Count; k++)
                {
                    float checkDist = (committedStops[k].SpanId == span.SpanId) ? minCollisionSpacing : 25.0f;
                    if (math.distance(roadPos, committedStops[k].Position) < checkDist)
                    {
                        isAlreadyServed = true;
                        break;
                    }
                }
                if (isAlreadyServed)
                    continue;

                int order = span.SpanOrderInCorridor + i;
                float3 forwardTan = (math.lengthsq(span.CorridorForwardTangent) > 0.001f) ? span.CorridorForwardTangent : roadTan;

                int globalIndex = (allGlobalStops != null) ? allGlobalStops.Count : totalStopsPlaced;
                if (TryPlaceAlternatingKerbStop(span, roadEdge, edgeT, roadPos, forwardTan,
                                                order,
                                                committedStops, candidatePrefabs, ref rng,
                                                minCollisionSpacing, planOnly, globalIndex,
                                                out PlacedStop placedStop, placementCounts))
                {
                    allGlobalStops?.Add(placedStop);
                    totalStopsPlaced++;
                    onNewStopPlaced?.Invoke(roadEdge, placedStop);
                }
            }
        }

        public (int placed, int reused, string districtName) PlaceStopsOnly(
            Entity districtFilter,
            string stopModel,
            StopDensityMode densityMode,
            int targetSpacing)
        {
            string districtName = "All City";
            if (districtFilter != Entity.Null && EntityManager.Exists(districtFilter))
            {
                districtName = _nameSystem?.GetRenderedLabelName(districtFilter);
                if (string.IsNullOrWhiteSpace(districtName))
                    districtName = $"District {districtFilter.Index}";
            }

            if (!FindBusStopPrefabs(out List<Entity> candidatePrefabs, out Entity defaultPrefab))
            {
                log.Warn("PlaceStopsOnly: No valid bus stop prefabs found.");
                return (0, 0, districtName);
            }

            // Filter candidate prefabs to chosen models if specified
            if (!string.IsNullOrEmpty(stopModel) && !stopModel.Equals("All", StringComparison.OrdinalIgnoreCase))
            {
                var selectedNames = stopModel.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                var specific = new List<Entity>();
                for (int i = 0; i < candidatePrefabs.Count; i++)
                {
                    string name = GetPrefabName(candidatePrefabs[i]);
                    for (int j = 0; j < selectedNames.Length; j++)
                    {
                        if (string.Equals(name, selectedNames[j].Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            if (!specific.Contains(candidatePrefabs[i]))
                                specific.Add(candidatePrefabs[i]);
                            break;
                        }
                    }
                }
                if (specific.Count > 0)
                {
                    candidatePrefabs = specific;
                }
            }

            // Calculate effective spacing based on density
            float effectiveSpacing = targetSpacing > 0 ? (float)targetSpacing : 200f;
            switch (densityMode)
            {
                case StopDensityMode.Ultra:
                    effectiveSpacing = 130f;
                    break;
                case StopDensityMode.Dense:
                    effectiveSpacing = 170f;
                    break;
                case StopDensityMode.Balanced:
                    effectiveSpacing = 220f;
                    break;
                case StopDensityMode.Low:
                    effectiveSpacing = 320f;
                    break;
                case StopDensityMode.Custom:
                    effectiveSpacing = math.clamp(targetSpacing, 60, 500);
                    break;
            }

            var corridors = BuildCorridors();
            var blockSpans = BuildBlockSpans(corridors);

            // Filter spans by district if active
            if (districtFilter != Entity.Null)
            {
                var districtSpans = new List<BlockSpan>();
                for (int s = 0; s < blockSpans.Count; s++)
                {
                    var span = blockSpans[s];
                    if (IsSpanInDistrict(span, districtFilter))
                    {
                        districtSpans.Add(span);
                    }
                }
                blockSpans = districtSpans;
            }

            log.Info($"[PlaceStopsOnly] Target District: '{districtName}', Matching Spans: {blockSpans.Count}, Effective Spacing: {effectiveSpacing:F0}m, Model: '{stopModel}'");

            var allExistingStops = DiscoverExistingBusStops(out var existingStopsByRoad);
            var committedStops = new List<CommittedStopInfo>();

            for (int e = 0; e < allExistingStops.Count; e++)
            {
                committedStops.Add(new CommittedStopInfo
                {
                    Position = allExistingStops[e].Position,
                    Forward = allExistingStops[e].Forward,
                    CorridorIndex = allExistingStops[e].CorridorIndex,
                    SpanId = -1
                });
            }

            var exclusionPositions = new List<float3>();
            if (_depotFinder != null && _depotFinder.AllHubs.IsCreated)
            {
                for (int h = 0; h < _depotFinder.AllHubs.Length; h++)
                    exclusionPositions.Add(_depotFinder.AllHubs[h].Position);
            }

            bool excludeDeadEnds = Mod.setting != null ? Mod.setting.ExcludeDeadEnds : true;
            float deadEndThreshold = Mod.setting != null ? (float)Mod.setting.DeadEndDistanceThreshold : 300f;
            float minSpanLength = math.max(45.0f, effectiveSpacing * 0.25f);
            uint seed = (uint)Math.Max(1, Math.Abs(Environment.TickCount));
            if (seed == 0) seed = 185392u;
            var rng = new Unity.Mathematics.Random(seed);

            int totalStopsPlaced = 0;
            int existingReused = 0;
            var placedPrefabEntities = new List<Entity>();
            var placementCounts = new Dictionary<Entity, int>();

            for (int s = 0; s < blockSpans.Count; s++)
            {
                var span = blockSpans[s];
                if (span.TotalLength < minSpanLength)
                    continue;

                if (excludeDeadEnds && span.IsDeadEnd)
                {
                    float effectiveLength = math.max(span.DeadEndBranchLength, span.TotalLength);
                    if (effectiveLength < deadEndThreshold)
                        continue;
                }

                bool isNearHub = false;
                for (int ep = 0; ep < exclusionPositions.Count; ep++)
                {
                    if (math.distance(span.MidPosition, exclusionPositions[ep]) < HUB_EXCLUSION_RADIUS)
                    {
                        isNearHub = true;
                        break;
                    }
                }
                if (isNearHub)
                    continue;

                PlaceStopsForSpan(
                    span,
                    effectiveSpacing,
                    touchesBridge: false,
                    minCollisionSpacing: 50.0f,
                    planOnly: false,
                    exclusionPositions,
                    existingStopsByRoad,
                    committedStops,
                    candidatePrefabs,
                    placementCounts,
                    ref rng,
                    ref totalStopsPlaced,
                    ref existingReused,
                    allGlobalStops: null,
                    onNewStopPlaced: (edgeEntity, placedStop) =>
                    {
                        if (!existingStopsByRoad.TryGetValue(edgeEntity, out var list))
                        {
                            list = new List<PlacedStop>();
                            existingStopsByRoad[edgeEntity] = list;
                        }
                        list.Add(placedStop);
                        placedPrefabEntities.Add(placedStop.PrefabEntity);
                    });
            }

            log.Info($"[PlaceStopsOnly] Finished placing {totalStopsPlaced} roadside bus stops in '{districtName}' ({existingReused} pre-existing stops preserved). Placed models: {FormatPlacedStopPrefabBreakdown(placedPrefabEntities)}.");
            return (totalStopsPlaced, existingReused, districtName);
        }

        public (int deletedCount, string districtName) ClearUnusedStops(Entity districtFilter)
        {
            string districtName = "All City";
            if (districtFilter != Entity.Null && EntityManager.Exists(districtFilter))
            {
                districtName = _nameSystem?.GetRenderedLabelName(districtFilter);
                if (string.IsNullOrWhiteSpace(districtName))
                    districtName = $"District {districtFilter.Index}";
            }

            var stopQuery = GetEntityQuery(
                ComponentType.ReadOnly<Game.Routes.TransportStop>(),
                ComponentType.Exclude<Deleted>()
            );

            int deletedCount = 0;
            using (var stops = stopQuery.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < stops.Length; i++)
                {
                    var stopEntity = stops[i];
                    if (!EntityManager.Exists(stopEntity) || EntityManager.HasComponent<Deleted>(stopEntity))
                        continue;

                    // Skip permanent stations or depots
                    if (EntityManager.HasComponent<Game.Buildings.TransportStation>(stopEntity) ||
                        EntityManager.HasComponent<Game.Buildings.TransportDepot>(stopEntity))
                        continue;

                    // Skip platform bays owned by a station/depot
                    if (EntityManager.HasComponent<Game.Common.Owner>(stopEntity))
                    {
                        var owner = EntityManager.GetComponentData<Game.Common.Owner>(stopEntity).m_Owner;
                        if (owner != Entity.Null && (EntityManager.HasComponent<Game.Buildings.TransportStation>(owner) ||
                                                     EntityManager.HasComponent<Game.Buildings.TransportDepot>(owner)))
                            continue;
                    }

                    // Check district membership if filtered
                    if (districtFilter != Entity.Null)
                    {
                        Entity attachedRoad = Entity.Null;
                        if (EntityManager.HasComponent<Game.Objects.Attached>(stopEntity))
                        {
                            attachedRoad = EntityManager.GetComponentData<Game.Objects.Attached>(stopEntity).m_Parent;
                        }
                        else if (EntityManager.HasComponent<Game.Common.Owner>(stopEntity))
                        {
                            attachedRoad = EntityManager.GetComponentData<Game.Common.Owner>(stopEntity).m_Owner;
                        }

                        bool inDist = false;
                        if (EntityManager.HasComponent<CurrentDistrict>(stopEntity))
                        {
                            inDist = (EntityManager.GetComponentData<CurrentDistrict>(stopEntity).m_District == districtFilter);
                        }
                        if (!inDist && attachedRoad != Entity.Null)
                        {
                            inDist = IsRoadInDistrict(attachedRoad, districtFilter);
                        }
                        if (!inDist && EntityManager.HasComponent<Game.Objects.Transform>(stopEntity))
                        {
                            var tr = EntityManager.GetComponentData<Game.Objects.Transform>(stopEntity);
                            inDist = IsPointInDistrict(districtFilter, tr.m_Position);
                        }
                        if (!inDist)
                        {
                            continue;
                        }
                    }

                    // Check if this stop has any active, connected transit routes
                    if (EntityManager.HasBuffer<ConnectedRoute>(stopEntity))
                    {
                        var routes = EntityManager.GetBuffer<ConnectedRoute>(stopEntity);
                        bool hasActiveRoute = false;
                        for (int r = 0; r < routes.Length; r++)
                        {
                            var wp = routes[r].m_Waypoint;
                            if (wp != Entity.Null && EntityManager.Exists(wp) && !EntityManager.HasComponent<Deleted>(wp))
                            {
                                hasActiveRoute = true;
                                break;
                            }
                        }
                        if (hasActiveRoute)
                        {
                            continue; // In use!
                        }
                    }

                    if (EntityManager.HasComponent<Attached>(stopEntity))
                    {
                        var attachedRoad = EntityManager.GetComponentData<Attached>(stopEntity).m_Parent;
                        if (attachedRoad != Entity.Null && EntityManager.Exists(attachedRoad) && EntityManager.HasBuffer<Game.Objects.SubObject>(attachedRoad))
                        {
                            var subObjects = EntityManager.GetBuffer<Game.Objects.SubObject>(attachedRoad);
                            for (int s = subObjects.Length - 1; s >= 0; s--)
                            {
                                if (subObjects[s].m_SubObject == stopEntity)
                                {
                                    subObjects.RemoveAt(s);
                                }
                            }
                            MarkRoadUpdatedWithNodes(attachedRoad);
                        }
                    }

                    EntityManager.AddComponentData(stopEntity, default(Deleted));
                    EntityManager.AddComponentData(stopEntity, default(Updated));
                    deletedCount++;
                }
            }

            log.Info($"[ClearUnusedStops] Removed {deletedCount} unused roadside bus stops in '{districtName}'.");
            return (deletedCount, districtName);
        }
    }
}

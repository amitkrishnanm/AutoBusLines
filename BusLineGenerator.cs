// AutoBusLines v2.1.0 - Modular transit planning and generation system
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Colossal.Logging;
using Colossal.Mathematics;
using Game;
using Game.Areas;
using Game.Common;
using Game.Net;
using Game.Notifications;
using Game.Objects;
using Game.Pathfind;
using Game.Prefabs;
using Game.Rendering;
using Game.Routes;
using Game.Tools;
using Unity.Entities;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Edge = Game.Net.Edge;

namespace AutoBusLines
{
    public partial class BusLineGenerator : GameSystemBase
    {
        private static readonly ILog log = LogManager.GetLogger($"{nameof(AutoBusLines)}.{nameof(BusLineGenerator)}");

        public const int ABSOLUTE_MIN_STOPS = 3;
        private const int INDEXING_DELAY_FRAMES = 15;
        private const int ROUTES_PER_FRAME = 2;

        public int TargetDistrictId { get; set; } = 0;
        private readonly Dictionary<Entity, float4> _districtBoundsCache = new Dictionary<Entity, float4>();

        public Entity FindDistrictEntity(int districtId)
        {
            if (districtId <= 0) return Entity.Null;

            var districtQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[]
                {
                    ComponentType.ReadOnly<Game.Areas.District>(),
                    ComponentType.ReadOnly<Game.Areas.Area>(),
                },
                None = new ComponentType[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Game.Tools.Temp>(),
                }
            });

            if (districtQuery.IsEmptyIgnoreFilter) return Entity.Null;

            using (var entities = districtQuery.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    if (entities[i].Index == districtId)
                    {
                        return entities[i];
                    }
                }
            }
            return Entity.Null;
        }

        /// <summary>
        /// Checks whether a 2D world position lies inside the boundary or area of a given district entity.
        /// Uses bounding box pre-filtering and exact triangulation buffers (Game.Areas.Node, Game.Areas.Triangle).
        /// </summary>
        public bool IsPointInDistrict(Entity districtEntity, float3 worldPos)
        {
            if (districtEntity == Entity.Null) return true;
            if (!EntityManager.Exists(districtEntity)) return false;
            if (!EntityManager.HasBuffer<Game.Areas.Node>(districtEntity) ||
                !EntityManager.HasBuffer<Game.Areas.Triangle>(districtEntity))
                return false;

            var nodes = EntityManager.GetBuffer<Game.Areas.Node>(districtEntity);
            var triangles = EntityManager.GetBuffer<Game.Areas.Triangle>(districtEntity);
            if (nodes.Length < 3 || triangles.Length == 0) return false;

            float2 p = worldPos.xz;

            // Fast Bounding Box Pre-Check (cached per district entity)
            if (!_districtBoundsCache.TryGetValue(districtEntity, out float4 bounds))
            {
                float minX = float.MaxValue, maxX = float.MinValue;
                float minZ = float.MaxValue, maxZ = float.MinValue;
                for (int i = 0; i < nodes.Length; i++)
                {
                    float2 np = nodes[i].m_Position.xz;
                    if (np.x < minX) minX = np.x;
                    if (np.x > maxX) maxX = np.x;
                    if (np.y < minZ) minZ = np.y;
                    if (np.y > maxZ) maxZ = np.y;
                }
                bounds = new float4(minX, maxX, minZ, maxZ);
                _districtBoundsCache[districtEntity] = bounds;
            }

            // Expand bounds slightly to account for road widths and curb offsets (approx 35m)
            const float kMargin = 35f;
            if (p.x < bounds.x - kMargin || p.x > bounds.y + kMargin ||
                p.y < bounds.z - kMargin || p.y > bounds.w + kMargin)
            {
                return false;
            }

            // Triangulated area check
            for (int i = 0; i < triangles.Length; i++)
            {
                var tri = triangles[i];
                if (tri.m_Indices.x < nodes.Length && tri.m_Indices.y < nodes.Length && tri.m_Indices.z < nodes.Length)
                {
                    var tri2 = AreaUtils.GetTriangle2(nodes, tri);
                    if (MathUtils.Intersect(tri2, p))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Checks whether a road edge belongs to or touches the specified district entity.
        /// Checks BorderDistrict (vanilla road district component), CurrentDistrict, endpoints, and geometry.
        /// </summary>
        public bool IsRoadInDistrict(Entity roadEntity, Entity districtEntity)
        {
            if (districtEntity == Entity.Null) return true;
            if (roadEntity == Entity.Null || !EntityManager.Exists(roadEntity)) return false;

            // 1. Check BorderDistrict on road edge (standard CS2 road component)
            if (EntityManager.HasComponent<BorderDistrict>(roadEntity))
            {
                var bd = EntityManager.GetComponentData<BorderDistrict>(roadEntity);
                if (bd.m_Left == districtEntity || bd.m_Right == districtEntity)
                    return true;
            }

            // 2. Check CurrentDistrict on road edge
            if (EntityManager.HasComponent<CurrentDistrict>(roadEntity))
            {
                var cd = EntityManager.GetComponentData<CurrentDistrict>(roadEntity);
                if (cd.m_District == districtEntity)
                    return true;
            }

            // 3. Check start and end nodes of the road edge
            if (EntityManager.HasComponent<Edge>(roadEntity))
            {
                var edge = EntityManager.GetComponentData<Edge>(roadEntity);
                if (edge.m_Start != Entity.Null && EntityManager.Exists(edge.m_Start))
                {
                    if (EntityManager.HasComponent<CurrentDistrict>(edge.m_Start) &&
                        EntityManager.GetComponentData<CurrentDistrict>(edge.m_Start).m_District == districtEntity)
                        return true;
                    if (EntityManager.HasComponent<BorderDistrict>(edge.m_Start))
                    {
                        var nbd = EntityManager.GetComponentData<BorderDistrict>(edge.m_Start);
                        if (nbd.m_Left == districtEntity || nbd.m_Right == districtEntity)
                            return true;
                    }
                }
                if (edge.m_End != Entity.Null && EntityManager.Exists(edge.m_End))
                {
                    if (EntityManager.HasComponent<CurrentDistrict>(edge.m_End) &&
                        EntityManager.GetComponentData<CurrentDistrict>(edge.m_End).m_District == districtEntity)
                        return true;
                    if (EntityManager.HasComponent<BorderDistrict>(edge.m_End))
                    {
                        var nbd = EntityManager.GetComponentData<BorderDistrict>(edge.m_End);
                        if (nbd.m_Left == districtEntity || nbd.m_Right == districtEntity)
                            return true;
                    }
                }
            }

            // 4. Fallback: Geometric containment via Curve midpoint
            if (EntityManager.HasComponent<Curve>(roadEntity))
            {
                var curve = EntityManager.GetComponentData<Curve>(roadEntity);
                float3 midPos = MathUtils.Position(curve.m_Bezier, 0.5f);
                if (IsPointInDistrict(districtEntity, midPos))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Checks whether a block span belongs to or touches the specified district entity.
        /// </summary>
        private bool IsSpanInDistrict(BlockSpan span, Entity districtEntity)
        {
            if (districtEntity == Entity.Null) return true;
            if (span == null) return false;

            for (int e = 0; e < span.Edges.Count; e++)
            {
                if (IsRoadInDistrict(span.Edges[e].EdgeEntity, districtEntity))
                    return true;
            }

            if (IsPointInDistrict(districtEntity, span.MidPosition))
                return true;

            return false;
        }

        private enum GenerationStage
        {
            Idle,
            WaitingForCleanup,
            PlanningSetup,
            PlanningStops,
            PlanningHubs,
            PlanningCorridors,
            PlanningFinish,
            InstantiatingStops,
            WaitingForStopIndexing,
            InstantiatingRoutes,
            SettlingRoutes
        }

        private class PlanningSessionState
        {
            public bool PlanOnly;
            public int SeedOffset;
            public bool AutoOpenPanel;
            public Entity DistrictFilter;
            public string DistrictName;
            public Entity BusLinePrefabEntity;
            public RouteData BusLineRouteData;
            public List<Entity> CandidatePrefabs;
            public Dictionary<Entity, int> StopPlacementCounts = new Dictionary<Entity, int>();
            public Unity.Mathematics.Random Rng;
            public List<float3> ExclusionPositions;
            public Dictionary<Entity, List<PlacedStop>> ExistingStopsByRoad;
            public List<RoadCorridor> AllCorridors;
            public List<BlockSpan> BlockSpans;
            public List<PlacedStop> AllGlobalStops = new List<PlacedStop>();
            public List<CommittedStopInfo> CommittedStops = new List<CommittedStopInfo>();
            public HashSet<Entity> BridgeNodes = new HashSet<Entity>();
            public int CurrentSpanIndex = 0;
            public int TotalDistrictSpans = 0;
            public int TotalStopsPlaced = 0;
            public int ExistingStopsReused = 0;
            public int MinStopsPerLine;
            public int MaxStopsPerLine;
            public float MaxRouteLength;
            public float TargetSpacing;
            public float MinCollisionSpacing;
            public float MinSpanLength;
            public bool ExcludeDeadEnds;
            public float DeadEndThreshold;
            public HashSet<Entity> AlreadyCoveredStops;
            public int ActiveBusLinesInCity;
            public int UnservedStopCount;
            public PlanContext PlanCtx;
        }

        private PlanningSessionState _planningSession = null;

        private GenerationStage _generationStage = GenerationStage.Idle;
        private int _waitFrameCounter = 0;
        private int _currentTourIndex = 0;
        private int _createdLineCount = 0;
        private List<List<PlacedStop>> _plannedTours = new List<List<PlacedStop>>();
        private List<Color32> _plannedTourColors = new List<Color32>();
        private List<Entity> _createdRouteEntities = new List<Entity>();
        private int _settlingWaitCounter = 0;
        private int _settlingPass = 0;
        private Entity _cachedBusLinePrefabEntity = Entity.Null;
        private RouteData _cachedBusLineRouteData;
        private int _totalStopsPlaced = 0;
        private int _totalStopsActive = 0;

        private List<PlacedStop> _pendingVirtualStops = new List<PlacedStop>();
        private Dictionary<int, Entity> _virtualToRealEntityMap = new Dictionary<int, Entity>();
        private int _currentVirtualStopIndex = 0;
        private int _totalVirtualStopsToCreate = 0;

        private bool _hasRun = false;
        private bool _isManualRequest = false;
        private bool _isRepairRequested = false;

        private DepotFinderSystem _depotFinder;
        private RoadNetworkScanner _roadScanner;
        private RoadDepotAssigner _roadAssigner;
        private PrefabSystem _prefabSystem;
        private Game.UI.NameSystem _nameSystem;

        private EntityQuery _busStopPrefabQuery;
        private EntityQuery _busLinePrefabQuery;
        private EntityQuery _existingBusStopQuery;

        private const float HUB_EXCLUSION_RADIUS = 120f;            // Exclusion radius around stations & depots (passengers use station bays)
        public const float DEFAULT_MAX_ROUTE_LENGTH = 15000f;       // Default maximum bus loop length: 15 km
        private const int MIN_STOPS_PER_LINE = 6;                   // Minimum stops per bus loop
        private const int MAX_STOPS_PER_LINE = 16;                  // Maximum stops per bus loop
        private const float SIDEWALK_OFFSET = 5.0f;                 // Distance from road centerline to sidewalk stop (meters)
        private const float ABSOLUTE_MIN_STOP_SPACING = 120.0f;     // Safety net: never drop two stops closer than this (halves stop count & prevents corner pile-ups)
        private const float MIN_SEGMENT_LENGTH_FOR_STOP = 40.0f;    // Minimum road length to be a valid block face (filters tiny junction splits)
        private const float STOP_PROXIMITY_SERVED_RADIUS = 220.0f;  // Blocks within 220m are considered served by nearby stop (halves stop density)
        private const float MIN_BLOCK_AREA = 500.0f;                // Faces smaller than this are junction slivers, not city blocks
        private const float MAX_BLOCK_AREA = 400000.0f;             // Faces bigger than this are the outer boundary / rural ring roads, not city blocks
        private const float MIN_BLOCK_THICKNESS_RATIO = 5.0f;       // area/perimeter; filters long thin medians between divided carriageways
        private const float HUB_BLOCK_SERVED_RADIUS = 220.0f;       // Blocks this close to a station/depot are already served by its own platform bays
        private const float ORPHAN_SPAN_MAX_UNSERVED = 220.0f;      // Dead-end / tree branches: place a stop if nothing is within this range
        private const float EXISTING_STOP_SPAN_TOLERANCE = 30.0f;   // An existing stop this close to a span midpoint already serves that span

        private struct CommittedStopInfo
        {
            public float3 Position;
            public float3 Forward;
            public int CorridorIndex;
            public int SpanId;
        }

        public struct PlacedStop
        {
            public Entity StopEntity;
            public float3 Position;
            public float3 Forward;
            public Entity RoadEntity;
            public Entity HubEntity;
            public int CorridorIndex;
            public int SpanOrderInCorridor;
            public float AngleFromHub;
            public float DistFromHub;
            public bool IsOutbound;
            public float RoadT;
            public Entity PrefabEntity;
            public bool HasRotationOverride;
            public quaternion RotationOverride;
            public bool IsStationBay;
            public bool IsPreExisting;
        }

        private class RoadSegmentChain
        {
            public Entity RoadEntity;
            public float Length;
            public bool IsReversed;
        }

        private class RoadCorridor
        {
            public float TotalLength;
            public List<RoadSegmentChain> Segments = new List<RoadSegmentChain>();
        }

        private struct EdgeEndpoint
        {
            public Entity EdgeEntity;
            public bool IsStartNode;
            public float3 OutwardTangent;
        }

        private struct SpanEdge : IEquatable<SpanEdge>
        {
            public Entity EdgeEntity;
            public bool IsReversed;
            public float Length;

            public bool Equals(SpanEdge other)
            {
                return EdgeEntity == other.EdgeEntity && IsReversed == other.IsReversed;
            }

            public override bool Equals(object obj)
            {
                return obj is SpanEdge other && Equals(other);
            }

            public override int GetHashCode()
            {
                return unchecked((EdgeEntity.GetHashCode() * 397) ^ IsReversed.GetHashCode());
            }
        }

        private class BlockSpan
        {
            public int SpanId;
            public List<SpanEdge> Edges = new List<SpanEdge>();
            public Entity StartNode;
            public Entity EndNode;
            public float TotalLength;
            public float3 MidPosition;
            public float3 MidTangent;
            public Entity MidEdge;
            public float MidT;
            public Entity HubEntity;
            public int CorridorIndex = -1;
            public float CorridorLength = 0f;
            public bool IsSuppressed = false;
            public bool IsSideStreet = false;
            public int StartDegree;
            public int EndDegree;
            public bool IsDeadEnd;
            public float DeadEndBranchLength = 0f;
            public float3 StartPosition;
            public float3 EndPosition;
            public float3 StartTangent;
            public float3 EndTangent;
            public int SpanOrderInCorridor = 0;
            public float3 CorridorForwardTangent;
        }

        private struct DirectedSpanHalfEdge
        {
            public int SpanIndex;
            public bool IsForward; // true: StartNode -> EndNode; false: EndNode -> StartNode
        }

        private struct NodeOutgoingHalfEdge
        {
            public DirectedSpanHalfEdge HalfEdge;
            public float Angle; // atan2(outTangent.z, outTangent.x)
        }

        private class CityBlockFace
        {
            public int FaceId;
            public List<int> SpanIndices = new List<int>();
            public float Area;
            public float Perimeter;
            public float2 Centroid;
            public bool IsServed;
        }

        protected override void OnCreate()
        {
            base.OnCreate();

            _depotFinder = World.GetOrCreateSystemManaged<DepotFinderSystem>();
            _roadScanner = World.GetOrCreateSystemManaged<RoadNetworkScanner>();
            _roadAssigner = World.GetOrCreateSystemManaged<RoadDepotAssigner>();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
            _nameSystem = World.GetOrCreateSystemManaged<Game.UI.NameSystem>();

            _busStopPrefabQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[]
                {
                    ComponentType.ReadOnly<PrefabData>(),
                    ComponentType.ReadOnly<TransportStopData>(),
                    ComponentType.ReadOnly<ObjectData>(),
                },
                None = new ComponentType[]
                {
                    ComponentType.ReadOnly<Game.Prefabs.OutsideConnectionData>(),
                }
            });

            _busLinePrefabQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[]
                {
                    ComponentType.ReadOnly<PrefabData>(),
                    ComponentType.ReadOnly<TransportLineData>(),
                    ComponentType.ReadOnly<RouteData>(),
                }
            });

            _existingBusStopQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[]
                {
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Game.Routes.TransportStop>(),
                    ComponentType.ReadOnly<Game.Objects.Transform>(),
                },
                None = new ComponentType[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Game.Tools.Temp>(),
                    ComponentType.ReadOnly<Game.Objects.OutsideConnection>(),
                    ComponentType.ReadOnly<Game.Net.OutsideConnection>(),
                }
            });

            log.Info("BusLineGenerator (Balanced 1-Stop-Per-Block Corridor-First System) created");
        }

        protected override void OnGameLoaded(Colossal.Serialization.Entities.Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);
            ResetOnSaveLoad();
        }

        public void ResetOnSaveLoad()
        {
            TargetDistrictId = 0;
            _hasRun = false;
            _isManualRequest = false;
            _isPlanRequest = false;
            _isBuildPlanRequest = false;
            _isDiscardPlanRequest = false;
            _isRepairRequested = false;
            _generationStage = GenerationStage.Idle;
            _planningSession = null;
            _waitFrameCounter = 0;
            _currentTourIndex = 0;
            _activePlan = null;
            _currentPlanSeed = 0;
            _plannedTours.Clear();
            _plannedTourColors?.Clear();
            _createdRouteEntities.Clear();
            _settlingWaitCounter = 0;
            _settlingPass = 0;
            _pendingVirtualStops.Clear();
            _virtualToRealEntityMap.Clear();
            _currentVirtualStopIndex = 0;
            _totalVirtualStopsToCreate = 0;
            AutoBusLinesUISystem.Instance?.SetBuildProgress(0f, "idle", "");

            _depotFinder?.Reset();
            _roadScanner?.Reset();
            _roadAssigner?.Reset();
            _districtBoundsCache.Clear();
        }

        public void RequestGeneration()
        {
            // Wipe previously generated bus lines and roadside stops so new settings/density apply cleanly
            DeleteAllBusLinesAndStops();

            _hasRun = false;
            _isManualRequest = true;
            _generationStage = GenerationStage.WaitingForCleanup;
            _waitFrameCounter = 0;
            _currentTourIndex = 0;
            _plannedTours.Clear();
            _createdRouteEntities.Clear();
            _settlingWaitCounter = 0;
            _settlingPass = 0;
            _depotFinder?.Reset();
            _roadScanner?.Reset();
            _roadAssigner?.Reset();
        }

        public void RequestRepair()
        {
            _isRepairRequested = true;
        }

        private List<PlannedRoute> _activePlan = null;
        private string _activePlanSelectedStopModel = null;
        private int _currentPlanSeed = 0;
        private bool _isPlanRequest = false;
        private int _requestedSeed = 0;
        private bool _isBuildPlanRequest = false;
        private bool _isDiscardPlanRequest = false;

        public void RequestPlan(int seedOffset = 0)
        {
            _requestedSeed = seedOffset;
            _isPlanRequest = true;
            _hasRun = false;
            _depotFinder?.Reset();
            _roadScanner?.Reset();
            _roadAssigner?.Reset();
        }

        public void RequestBuildSelectedPlan()
        {
            _isBuildPlanRequest = true;
        }

        public void RequestDiscardPlan()
        {
            _isDiscardPlanRequest = true;
        }

        public void ToggleRoute(int routeId, bool enabled)
        {
            if (_activePlan == null) return;
            var route = _activePlan.Find(r => r.Id == routeId);
            if (route != null)
            {
                route.Enabled = enabled;
                AutoBusLinesUISystem.Instance?.UpdatePlan(_activePlan, _currentPlanSeed);
            }
        }

        public void ToggleStop(int routeId, int stopIndex, bool enabled)
        {
            if (_activePlan == null) return;
            var route = _activePlan.Find(r => r.Id == routeId);
            if (route != null)
            {
                var stop = route.Stops.Find(s => s.Index == stopIndex);
                if (stop != null)
                {
                    stop.Enabled = enabled;
                    AutoBusLinesUISystem.Instance?.UpdatePlan(_activePlan, _currentPlanSeed);
                }
            }
        }

        public List<PlannedRoute> GetCurrentPlan() => _activePlan;
        public bool HasActivePlan() => _activePlan != null && _activePlan.Count > 0;
        public PlannedRoute GetActivePlannedRoute(int routeId) => _activePlan?.Find(r => r.Id == routeId);


        protected override void OnUpdate()
        {
            // Stage 1: Wait for any pending cleanup from previous runs to settle
            if (_generationStage == GenerationStage.WaitingForCleanup)
            {
                _waitFrameCounter++;
                if (_waitFrameCounter >= 5)
                {
                    _generationStage = GenerationStage.Idle;
                    _waitFrameCounter = 0;
                    StartStagedGeneration(planOnly: false, seedOffset: 0, autoOpenPanel: false);
                }
                return;
            }

            if (_generationStage == GenerationStage.PlanningSetup)
            {
                StepPlanningSetup();
                return;
            }

            if (_generationStage == GenerationStage.PlanningStops)
            {
                StepPlanningStops();
                return;
            }

            if (_generationStage == GenerationStage.PlanningHubs)
            {
                StepPlanningHubs();
                return;
            }

            if (_generationStage == GenerationStage.PlanningCorridors)
            {
                StepPlanningCorridors();
                return;
            }

            if (_generationStage == GenerationStage.PlanningFinish)
            {
                StepPlanningFinish();
                return;
            }

            // Stage 1.5: Incrementally instantiate virtual stops one-by-one across simulation frames
            if (_generationStage == GenerationStage.InstantiatingStops)
            {
                int stopsProcessed = 0;
                while (_pendingVirtualStops != null && _currentVirtualStopIndex < _pendingVirtualStops.Count && stopsProcessed < 2)
                {
                    var st = _pendingVirtualStops[_currentVirtualStopIndex];
                    _currentVirtualStopIndex++;
                    stopsProcessed++;

                    if (EntityManager.Exists(st.RoadEntity) && !EntityManager.HasComponent<Deleted>(st.RoadEntity))
                    {
                        if (TryCreateBusStopEntity(st.RoadEntity, st.RoadT, st.Position, st.Forward, st.HubEntity,
                                                   st.CorridorIndex, st.SpanOrderInCorridor, st.IsOutbound,
                                                   st.PrefabEntity, out PlacedStop realPlacedStop,
                                                   st.HasRotationOverride ? st.RotationOverride : (quaternion?)null))
                        {
                            _virtualToRealEntityMap[st.StopEntity.Index] = realPlacedStop.StopEntity;
                        }
                    }
                }

                float stopProgress = (float)_currentVirtualStopIndex / math.max(1, _totalVirtualStopsToCreate);
                float overallProgress = stopProgress * 0.60f;
                int pct = (int)math.round(stopProgress * 100f);
                AutoBusLinesUISystem.Instance?.SetBuildProgress(overallProgress, "building", $"Creating bus stop {_currentVirtualStopIndex} / {_totalVirtualStopsToCreate} ({pct}%)...");

                if (_currentVirtualStopIndex < _pendingVirtualStops.Count)
                    return;

                // All virtual stops created! Assemble tours from _activePlan with newly created stop entities
                var toursToBuild = new List<List<PlacedStop>>();
                _plannedTourColors = new List<Color32>();

                if (_activePlan != null)
                {
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
                                if (_virtualToRealEntityMap.TryGetValue(st.StopEntity.Index, out Entity realStop))
                                {
                                    st.StopEntity = realStop;
                                    if (realStop != Entity.Null && !string.IsNullOrEmpty(stopData.Name))
                                    {
                                        if (_nameSystem == null)
                                            _nameSystem = World.GetOrCreateSystemManaged<Game.UI.NameSystem>();
                                        _nameSystem?.SetCustomName(realStop, stopData.Name);
                                    }
                                    tourStops.Add(st);
                                }
                                else if (st.PrefabEntity == Entity.Null)
                                {
                                    st.StopEntity = Entity.Null;
                                    tourStops.Add(st);
                                }
                            }
                            else if (st.StopEntity == Entity.Null || (EntityManager.Exists(st.StopEntity) && !EntityManager.HasComponent<Deleted>(st.StopEntity)))
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
                }

                _plannedTours = toursToBuild;
                _totalStopsPlaced = _totalVirtualStopsToCreate;
                _totalStopsActive = _totalVirtualStopsToCreate;
                _pendingVirtualStops.Clear();
                _virtualToRealEntityMap.Clear();
                _activePlan = null;
                _currentPlanSeed = 0;

                _generationStage = GenerationStage.WaitingForStopIndexing;
                _waitFrameCounter = 0;
                AutoBusLinesUISystem.Instance?.SetBuildProgress(0.60f, "building", "Indexing bus stops and road geometry...");
                return;
            }

            // Stage 2: Wait for newly created bus stops to be indexed in the spatial world
            if (_generationStage == GenerationStage.WaitingForStopIndexing)
            {
                _waitFrameCounter++;
                float indexProgress = 0.60f + 0.10f * math.clamp(_waitFrameCounter / (float)INDEXING_DELAY_FRAMES, 0f, 1f);
                AutoBusLinesUISystem.Instance?.SetBuildProgress(indexProgress, "building", "Indexing bus stops and road geometry...");

                if (_waitFrameCounter >= INDEXING_DELAY_FRAMES)
                {
                    _generationStage = GenerationStage.InstantiatingRoutes;
                    _currentTourIndex = 0;
                    _createdLineCount = 0;
                    _waitFrameCounter = 0;
                    log.Info($"Stage 2: Bus stop indexing settled across {INDEXING_DELAY_FRAMES} frames. Beginning staged route instantiation...");
                }
                return;
            }

            // Stage 3: Incrementally instantiate bus lines one-by-one across simulation frames
            if (_generationStage == GenerationStage.InstantiatingRoutes)
            {
                if (_currentTourIndex < _plannedTours.Count)
                {
                    var tour = _plannedTours[_currentTourIndex];
                    int nextLineNumber = _createdLineCount + 1;
                    Color32? tourColor = (_plannedTourColors != null && _currentTourIndex < _plannedTourColors.Count)
                        ? _plannedTourColors[_currentTourIndex]
                        : (Color32?)null;

                    if (tour.Count >= ABSOLUTE_MIN_STOPS)
                    {
                        if (CreateBusLine(_cachedBusLinePrefabEntity, _cachedBusLineRouteData, tour, nextLineNumber, 0, _currentTourIndex, tourColor))
                        {
                            _createdLineCount++;
                            float routeDistanceKm = CalculateTourLength(tour) / 1000f;
                            log.Info($"Stage 3: Created Bus Line #{nextLineNumber} with {tour.Count} stops (Est. Length: {routeDistanceKm:F1} km) [Progress: {_currentTourIndex + 1}/{_plannedTours.Count}]");
                        }
                        else
                        {
                            log.Warn($"Stage 3: Skipped creating Bus Line for tour #{_currentTourIndex + 1} due to insufficient valid stops.");
                        }
                    }

                    _currentTourIndex++;
                    float routeProgress = (float)_currentTourIndex / math.max(1, _plannedTours.Count);
                    float overallProgress = 0.70f + routeProgress * 0.25f;
                    int pct = (int)math.round(routeProgress * 100f);
                    AutoBusLinesUISystem.Instance?.SetBuildProgress(overallProgress, "building", $"Building bus line {_currentTourIndex} / {_plannedTours.Count} ({pct}%)...");
                    return; // Yield to next frame: 1 line per frame gives pathfinding and engine route creation full time to settle
                }

                // Transition to Stage 4: SettlingRoutes to allow async PathfindQueueSystem threads to complete and verify paths
                _generationStage = GenerationStage.SettlingRoutes;
                _settlingWaitCounter = 0;
                _settlingPass = 0;
                AutoBusLinesUISystem.Instance?.SetBuildProgress(0.95f, "building", "Verifying route pathfinding and road connections...");
                log.Info($"Stage 3 Complete: {_createdLineCount} bus lines created. Transitioning to Stage 4 (Settling & Path Verification) for {_createdRouteEntities.Count} routes...");
                return;
            }

            // Stage 4: Verify asynchronous pathfinding and auto-recover any broken segments
            if (_generationStage == GenerationStage.SettlingRoutes)
            {
                _settlingWaitCounter++;
                if (_settlingWaitCounter < 25)
                {
                    return; // Wait 25 frames between verification passes to allow background pathfinding threads to complete
                }

                _settlingWaitCounter = 0;
                _settlingPass++;

                int totalBrokenSegments = 0;
                var brokenSegmentsToReset = new HashSet<Entity>();
                var routesWithBrokenSegments = new HashSet<Entity>();
                var stopsToNudge = new HashSet<Entity>();
                var curbsideWaypointsToNudge = new HashSet<Entity>();

                for (int r = 0; r < _createdRouteEntities.Count; r++)
                {
                    var routeEnt = _createdRouteEntities[r];
                    if (!EntityManager.Exists(routeEnt) || EntityManager.HasComponent<Deleted>(routeEnt))
                        continue;

                    if (!EntityManager.HasBuffer<RouteSegment>(routeEnt) || !EntityManager.HasBuffer<RouteWaypoint>(routeEnt))
                        continue;

                    var segBuf = EntityManager.GetBuffer<RouteSegment>(routeEnt);
                    var wpBuf = EntityManager.GetBuffer<RouteWaypoint>(routeEnt);

                    if (segBuf.Length == 0 || wpBuf.Length == 0)
                        continue;

                    bool routeHasBroken = false;
                    for (int s = 0; s < segBuf.Length; s++)
                    {
                        var segEnt = segBuf[s].m_Segment;
                        if (!EntityManager.Exists(segEnt) || EntityManager.HasComponent<Deleted>(segEnt))
                        {
                            routeHasBroken = true;
                            totalBrokenSegments++;
                            continue;
                        }

                        bool isBroken = false;
                        if (EntityManager.HasComponent<PathInformation>(segEnt))
                        {
                            var pi = EntityManager.GetComponentData<PathInformation>(segEnt);
                            if (pi.m_Distance <= 0f)
                            {
                                isBroken = true;
                            }
                        }
                        else
                        {
                            isBroken = true;
                        }

                        if (!EntityManager.HasBuffer<PathElement>(segEnt) || EntityManager.GetBuffer<PathElement>(segEnt).Length == 0)
                        {
                            isBroken = true;
                        }

                        if (isBroken)
                        {
                            routeHasBroken = true;
                            totalBrokenSegments++;
                            brokenSegmentsToReset.Add(segEnt);

                            // The failed segment connects Waypoint s to Waypoint (s + 1) % wpBuf.Length
                            int wpIndexA = s;
                            int wpIndexB = (s + 1) % wpBuf.Length;

                            if (wpIndexA < wpBuf.Length)
                            {
                                var wpA = wpBuf[wpIndexA].m_Waypoint;
                                if (EntityManager.Exists(wpA) && !EntityManager.HasComponent<Deleted>(wpA))
                                {
                                    if (!EntityManager.HasComponent<Updated>(wpA))
                                        EntityManager.AddComponentData(wpA, default(Updated));

                                    if (EntityManager.HasComponent<Connected>(wpA))
                                    {
                                        var stopA = EntityManager.GetComponentData<Connected>(wpA).m_Connected;
                                        if (stopA != Entity.Null && EntityManager.Exists(stopA) && !EntityManager.HasComponent<Deleted>(stopA))
                                        {
                                            stopsToNudge.Add(stopA);
                                        }
                                    }
                                    else
                                    {
                                        curbsideWaypointsToNudge.Add(wpA);
                                    }
                                }
                            }

                            if (wpIndexB < wpBuf.Length)
                            {
                                var wpB = wpBuf[wpIndexB].m_Waypoint;
                                if (EntityManager.Exists(wpB) && !EntityManager.HasComponent<Deleted>(wpB))
                                {
                                    if (!EntityManager.HasComponent<Updated>(wpB))
                                        EntityManager.AddComponentData(wpB, default(Updated));

                                    if (EntityManager.HasComponent<Connected>(wpB))
                                    {
                                        var stopB = EntityManager.GetComponentData<Connected>(wpB).m_Connected;
                                        if (stopB != Entity.Null && EntityManager.Exists(stopB) && !EntityManager.HasComponent<Deleted>(stopB))
                                        {
                                            stopsToNudge.Add(stopB);
                                        }
                                    }
                                    else
                                    {
                                        curbsideWaypointsToNudge.Add(wpB);
                                    }
                                }
                            }

                            // Also reset adjacent segments touching these two waypoints
                            int prevSegIdx = (s - 1 + segBuf.Length) % segBuf.Length;
                            int nextSegIdx = (s + 1) % segBuf.Length;
                            if (prevSegIdx < segBuf.Length)
                                brokenSegmentsToReset.Add(segBuf[prevSegIdx].m_Segment);
                            if (nextSegIdx < segBuf.Length)
                                brokenSegmentsToReset.Add(segBuf[nextSegIdx].m_Segment);
                        }
                    }

                    if (routeHasBroken)
                    {
                        routesWithBrokenSegments.Add(routeEnt);
                    }
                }

                if (totalBrokenSegments > 0 && _settlingPass <= 3)
                {
                    log.Info($"SettlingRoutes Pass {_settlingPass}: Found {totalBrokenSegments} incomplete/broken segments across {routesWithBrokenSegments.Count} routes. Re-enqueuing...");

                    // If pass >= 2 and physical roadside stops failed, nudge them along road curve to adjust curb position
                    if (_settlingPass >= 2)
                    {
                        foreach (var stopEntity in stopsToNudge)
                        {
                            NudgeRoadsideBusStop(stopEntity);
                        }
                        foreach (var wpEntity in curbsideWaypointsToNudge)
                        {
                            NudgeCurbsideWaypoint(wpEntity);
                        }
                    }

                    // Reset PathTargets and PathInformation on affected segments
                    foreach (var segEnt in brokenSegmentsToReset)
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

                    foreach (var rEnt in routesWithBrokenSegments)
                    {
                        if (!EntityManager.Exists(rEnt) || EntityManager.HasComponent<Deleted>(rEnt))
                            continue;

                        if (!EntityManager.HasComponent<Updated>(rEnt))
                            EntityManager.AddComponentData(rEnt, default(Updated));
                        if (!EntityManager.HasComponent<BatchesUpdated>(rEnt))
                            EntityManager.AddComponentData(rEnt, default(BatchesUpdated));
                    }

                    float settleProgress = 0.95f + 0.01f * _settlingPass;
                    AutoBusLinesUISystem.Instance?.SetBuildProgress(settleProgress, "building", $"Resolving route pathfinding (pass {_settlingPass}/3, {totalBrokenSegments} segments remaining)...");
                    return; // Wait next 25 frames
                }

                if (totalBrokenSegments > 0)
                    LogBrokenSegmentDiagnostics(_createdRouteEntities, "SettlingFinal");

                int lineCount = _createdLineCount;
                log.Info($"=== Generation Complete: Successfully created {lineCount} bus lines across city neighborhoods ({_totalStopsActive}/{_totalStopsPlaced} stops active across staged simulation frames). Broken segments remaining: {totalBrokenSegments}. ===");
                _generationStage = GenerationStage.Idle;
                _hasRun = true;
                _isManualRequest = false;
                _plannedTours.Clear();
                _createdRouteEntities.Clear();

                AutoBusLinesUISystem.Instance?.SetBuildProgress(1f, "idle", $"Successfully built {lineCount} bus lines with {_totalStopsPlaced} stops!");
                AutoBusLinesUISystem.Instance?.UpdatePlan(null, 0, $"Successfully built {lineCount} bus lines with {_totalStopsPlaced} stops!");
                return;
            }

            if (_isDiscardPlanRequest)
            {
                _isDiscardPlanRequest = false;
                _planningSession = null;
                if (_generationStage == GenerationStage.PlanningSetup ||
                    _generationStage == GenerationStage.PlanningStops ||
                    _generationStage == GenerationStage.PlanningHubs ||
                    _generationStage == GenerationStage.PlanningCorridors ||
                    _generationStage == GenerationStage.PlanningFinish)
                {
                    _generationStage = GenerationStage.Idle;
                }
                _activePlan = null;
                _currentPlanSeed = 0;
                _plannedTours.Clear();
                _plannedTourColors.Clear();
                AutoBusLinesUISystem.Instance?.SetBuildProgress(0f, "idle", "Plan discarded.");
                AutoBusLinesUISystem.Instance?.UpdatePlan(null, 0, "Plan discarded.");
                return;
            }

            if (_isRepairRequested)
            {
                _isRepairRequested = false;
                ExecuteRepairBrokenRoutes();
                return;
            }

            if (_isBuildPlanRequest)
            {
                _isBuildPlanRequest = false;
                ExecuteBuildSelectedPlan();
                return;
            }

            if (_isPlanRequest)
            {
                _isPlanRequest = false;
                StartStagedGeneration(planOnly: true, seedOffset: _requestedSeed, autoOpenPanel: true);
                return;
            }

            if (_hasRun)
                return;

            if (!_isManualRequest)
                return;

            if (Mod.setting != null && Mod.setting.EnablePlanMode)
            {
                _hasRun = true;
                _isManualRequest = false;
                StartStagedGeneration(planOnly: true, seedOffset: 0, autoOpenPanel: false);
                return;
            }

            StartStagedGeneration(planOnly: false, seedOffset: 0, autoOpenPanel: false);
        }

        private void StartStagedGeneration(bool planOnly, int seedOffset, bool autoOpenPanel)
        {
            _planningSession = new PlanningSessionState
            {
                PlanOnly = planOnly,
                SeedOffset = seedOffset,
                AutoOpenPanel = autoOpenPanel
            };
            _generationStage = GenerationStage.PlanningSetup;
            string phaseStatus = planOnly ? "planning" : "building";
            AutoBusLinesUISystem.Instance?.SetBuildProgress(0.08f, phaseStatus, "Analyzing road network and depot connections...");
        }

        private void StepPlanningSetup()
        {
            var state = _planningSession;
            if (state == null)
            {
                _generationStage = GenerationStage.Idle;
                return;
            }

            string phaseStatus = state.PlanOnly ? "planning" : "building";
            _districtBoundsCache.Clear();
            if (_depotFinder == null) _depotFinder = World.GetOrCreateSystemManaged<DepotFinderSystem>();
            if (_roadScanner == null) _roadScanner = World.GetOrCreateSystemManaged<RoadNetworkScanner>();
            if (_roadAssigner == null) _roadAssigner = World.GetOrCreateSystemManaged<RoadDepotAssigner>();

            _depotFinder.ScanNow();
            _roadScanner.ScanNow();

            if (_depotFinder.AllHubs.Length > 0 && _roadScanner.RoadSegments.Length > 0)
                _roadAssigner.AssignNow();

            var roadToHub = _roadAssigner.GetRoadToDepotMap();

            if (_depotFinder.AllHubs.Length == 0)
            {
                log.Warn("ExecuteGeneration: No bus depots or stations found in city.");
                _generationStage = GenerationStage.Idle;
                _planningSession = null;
                _hasRun = true;
                _isManualRequest = false;
                AutoBusLinesUISystem.Instance?.SetBuildProgress(0f, "idle", "No Bus Depot or Bus Station found! Please build at least one Bus Depot or Bus Station first.");
                return;
            }

            if (_roadScanner.RoadSegments.Length == 0)
            {
                log.Warn("ExecuteGeneration: No municipal paved roads found in city.");
                _generationStage = GenerationStage.Idle;
                _planningSession = null;
                _hasRun = true;
                _isManualRequest = false;
                AutoBusLinesUISystem.Instance?.SetBuildProgress(0f, "idle", "No municipal paved roads found in city.");
                return;
            }

            if (roadToHub.Count == 0)
            {
                log.Warn("ExecuteGeneration: Could not map any roads to transit hubs.");
                _generationStage = GenerationStage.Idle;
                _planningSession = null;
                _hasRun = true;
                _isManualRequest = false;
                AutoBusLinesUISystem.Instance?.SetBuildProgress(0f, "idle", "Could not connect city roads to available transit depots.");
                return;
            }

            log.Info($"=== Starting AutoBusLines {(state.PlanOnly ? "Plan Mode Preview" : "Direct Generation")} (Variant #{state.SeedOffset + 1}) ===");

            if (!FindBusStopPrefabs(out List<Entity> candidatePrefabs, out _))
            {
                log.Error("Could not find any valid Bus Stop Prefabs with ObjectData archetype!");
                _generationStage = GenerationStage.Idle;
                _planningSession = null;
                _hasRun = true;
                _isManualRequest = false;
                AutoBusLinesUISystem.Instance?.SetBuildProgress(0f, "idle", "Error: No bus stop prefabs found.");
                return;
            }

            if (!FindBusLinePrefab(out Entity busLinePrefabEntity, out RouteData busLineRouteData))
            {
                log.Error("Could not find a valid Bus Line Prefab with RouteData archetypes!");
                _generationStage = GenerationStage.Idle;
                _planningSession = null;
                _hasRun = true;
                _isManualRequest = false;
                AutoBusLinesUISystem.Instance?.SetBuildProgress(0f, "idle", "Error: Bus line prefab not found.");
                return;
            }

            log.Info($"Found {candidatePrefabs.Count} Bus Stop Candidate Prefabs and Bus Line Prefab {busLinePrefabEntity.Index}");

            state.CandidatePrefabs = candidatePrefabs;
            state.BusLinePrefabEntity = busLinePrefabEntity;
            state.BusLineRouteData = busLineRouteData;
            state.Rng = new Unity.Mathematics.Random(185392u + (uint)(state.SeedOffset * 7919));

            state.ExclusionPositions = new List<float3>();
            for (int h = 0; h < _depotFinder.AllHubs.Length; h++)
                state.ExclusionPositions.Add(_depotFinder.AllHubs[h].Position);
            for (int p = 0; p < _depotFinder.StationPlatformStops.Length; p++)
            {
                var platformEntity = _depotFinder.StationPlatformStops[p];
                if (EntityManager.HasComponent<Game.Objects.Transform>(platformEntity))
                    state.ExclusionPositions.Add(EntityManager.GetComponentData<Game.Objects.Transform>(platformEntity).m_Position);
            }

            var allExistingStops = DiscoverExistingBusStops(out var existingStopsByRoad);
            state.ExistingStopsByRoad = existingStopsByRoad;
            log.Info($"Discovered {allExistingStops.Count} pre-existing bus stops in the city across all neighborhoods");

            int minStopsPerLine = Mod.setting != null ? Mod.setting.MinStopsPerLine : MIN_STOPS_PER_LINE;
            int maxStopsPerLine = Mod.setting != null ? Mod.setting.MaxStopsPerLine : MAX_STOPS_PER_LINE;
            float maxRouteLength = Mod.setting != null ? (float)Mod.setting.MaxRouteLength : DEFAULT_MAX_ROUTE_LENGTH;
            float targetSpacing = Mod.setting != null ? (float)Mod.setting.TargetStopSpacing : 200f;

            if (minStopsPerLine < ABSOLUTE_MIN_STOPS) minStopsPerLine = ABSOLUTE_MIN_STOPS;
            if (maxStopsPerLine < minStopsPerLine) maxStopsPerLine = minStopsPerLine;
            if (maxRouteLength < 2000f) maxRouteLength = 2000f;
            if (targetSpacing < 50f) targetSpacing = 50f;
            if (targetSpacing > 600f) targetSpacing = 600f;

            state.MinStopsPerLine = minStopsPerLine;
            state.MaxStopsPerLine = maxStopsPerLine;
            state.MaxRouteLength = maxRouteLength;
            state.TargetSpacing = targetSpacing;
            state.MinCollisionSpacing = math.clamp(targetSpacing * 0.40f, 35f, 120f);
            state.MinSpanLength = math.clamp(targetSpacing * 0.20f, 20f, 35f);
            state.ExcludeDeadEnds = Mod.setting != null ? Mod.setting.ExcludeDeadEnds : true;
            state.DeadEndThreshold = Mod.setting != null ? (float)Mod.setting.DeadEndDistanceThreshold : 500f;

            var allCorridors = BuildCorridors();
            allCorridors.Sort((a, b) => b.TotalLength.CompareTo(a.TotalLength));

            if (state.SeedOffset > 0)
            {
                var prng = new System.Random(state.SeedOffset * 1013);
                for (int i = allCorridors.Count - 1; i > 0; i--)
                {
                    int j = prng.Next(i + 1);
                    var temp = allCorridors[i];
                    allCorridors[i] = allCorridors[j];
                    allCorridors[j] = temp;
                }
            }

            state.AllCorridors = allCorridors;
            state.BlockSpans = BuildBlockSpans(allCorridors);

            for (int e = 0; e < allExistingStops.Count; e++)
            {
                state.CommittedStops.Add(new CommittedStopInfo
                {
                    Position = allExistingStops[e].Position,
                    Forward = allExistingStops[e].Forward,
                    CorridorIndex = allExistingStops[e].CorridorIndex,
                    SpanId = -1
                });
            }

            if (_roadScanner != null && _roadScanner.DrivableSegments.IsCreated)
            {
                for (int i = 0; i < _roadScanner.DrivableSegments.Length; i++)
                {
                    var e = _roadScanner.DrivableSegments[i];
                    if (IsElevatedOrBridge(e) && EntityManager.HasComponent<Edge>(e))
                    {
                        var edge = EntityManager.GetComponentData<Edge>(e);
                        if (edge.m_Start != Entity.Null) state.BridgeNodes.Add(edge.m_Start);
                        if (edge.m_End != Entity.Null) state.BridgeNodes.Add(edge.m_End);
                    }
                }
            }

            state.BlockSpans.Sort((a, b) =>
            {
                int cmp = b.CorridorLength.CompareTo(a.CorridorLength);
                if (cmp != 0) return cmp;
                return b.TotalLength.CompareTo(a.TotalLength);
            });

            state.DistrictFilter = FindDistrictEntity(TargetDistrictId);
            state.DistrictName = state.DistrictFilter != Entity.Null
                ? (_nameSystem?.GetRenderedLabelName(state.DistrictFilter) ?? $"District {state.DistrictFilter.Index}")
                : "All City";
            if (state.DistrictFilter != Entity.Null)
            {
                log.Info($"[ExecuteGeneration] Scoping transit network generation to District: '{state.DistrictName}' (Entity {state.DistrictFilter.Index})");
            }

            state.CurrentSpanIndex = 0;
            _generationStage = GenerationStage.PlanningStops;
            AutoBusLinesUISystem.Instance?.SetBuildProgress(0.25f, phaseStatus, $"Calculating transit corridors & stop locations (0 / {state.BlockSpans.Count} spans)...");
        }

        private void StepPlanningStops()
        {
            var state = _planningSession;
            if (state == null)
            {
                _generationStage = GenerationStage.Idle;
                return;
            }

            string phaseStatus = state.PlanOnly ? "planning" : "building";
            var sw = Stopwatch.StartNew();

            while (state.CurrentSpanIndex < state.BlockSpans.Count)
            {
                var span = state.BlockSpans[state.CurrentSpanIndex];
                state.CurrentSpanIndex++;

                if (span.TotalLength >= state.MinSpanLength)
                {
                    if (state.DistrictFilter != Entity.Null)
                    {
                        if (!IsSpanInDistrict(span, state.DistrictFilter))
                            goto CheckFrameBudget;
                        state.TotalDistrictSpans++;
                    }

                    if (state.ExcludeDeadEnds && span.IsDeadEnd)
                    {
                        float effectiveLength = math.max(span.DeadEndBranchLength, span.TotalLength);
                        if (effectiveLength < state.DeadEndThreshold)
                        {
                            log.Info($"[ExcludeDeadEnds] Skipping stop on short dead-end span #{span.SpanId}: branch={effectiveLength:F1}m (span={span.TotalLength:F1}m) (< threshold {state.DeadEndThreshold:F0}m)");
                            goto CheckFrameBudget;
                        }
                        else
                        {
                            log.Info($"[ExcludeDeadEnds] Allowing stop on long dead-end span #{span.SpanId}: branch={effectiveLength:F1}m (span={span.TotalLength:F1}m) (>= threshold {state.DeadEndThreshold:F0}m)");
                        }
                    }

                    bool touchesBridge = (span.StartNode != Entity.Null && state.BridgeNodes.Contains(span.StartNode)) ||
                                         (span.EndNode != Entity.Null && state.BridgeNodes.Contains(span.EndNode));
                    float effectiveSpacing = (touchesBridge && span.TotalLength >= 350f)
                        ? math.max(state.TargetSpacing * 2.2f, 450f)
                        : state.TargetSpacing;

                    bool isNearHub = false;
                    for (int ep = 0; ep < state.ExclusionPositions.Count; ep++)
                    {
                        if (math.distance(span.MidPosition, state.ExclusionPositions[ep]) < HUB_EXCLUSION_RADIUS)
                        {
                            isNearHub = true;
                            break;
                        }
                    }
                    if (isNearHub)
                        goto CheckFrameBudget;

                    PlaceStopsForSpan(
                        span,
                        effectiveSpacing,
                        touchesBridge,
                        state.MinCollisionSpacing,
                        state.PlanOnly,
                        state.ExclusionPositions,
                        state.ExistingStopsByRoad,
                        state.CommittedStops,
                        state.CandidatePrefabs,
                        state.StopPlacementCounts,
                        ref state.Rng,
                        ref state.TotalStopsPlaced,
                        ref state.ExistingStopsReused,
                        state.AllGlobalStops);
                }

            CheckFrameBudget:
                if (state.CurrentSpanIndex < state.BlockSpans.Count && (state.CurrentSpanIndex % 25 == 0) && sw.ElapsedMilliseconds >= 12)
                {
                    float frac = (float)state.CurrentSpanIndex / math.max(1, state.BlockSpans.Count);
                    AutoBusLinesUISystem.Instance?.SetBuildProgress(
                        0.25f + 0.30f * frac,
                        phaseStatus,
                        $"Evaluating road spans & placing bus stops ({state.CurrentSpanIndex} / {state.BlockSpans.Count})...");
                    return;
                }
            }

            string scopeDesc = state.DistrictFilter != Entity.Null ? $"in District '{state.DistrictName}' ({state.TotalDistrictSpans} matching spans)" : "across the city";
            var newlyPlacedPrefabs = new List<Entity>();
            for (int i = 0; i < state.AllGlobalStops.Count; i++)
            {
                if (!state.AllGlobalStops[i].IsPreExisting && !state.AllGlobalStops[i].IsStationBay)
                    newlyPlacedPrefabs.Add(state.AllGlobalStops[i].PrefabEntity);
            }
            log.Info($"Step 2 Complete: {state.AllGlobalStops.Count} total stops available {scopeDesc} ({state.TotalStopsPlaced} newly placed, {state.ExistingStopsReused} existing reused) with target spacing {state.TargetSpacing:F0}m. Newly placed models: {FormatPlacedStopPrefabBreakdown(newlyPlacedPrefabs)}.");

            state.AlreadyCoveredStops = GetStopsCoveredByActiveBusLines(out int activeBusLinesInCity);
            state.ActiveBusLinesInCity = activeBusLinesInCity;
            log.Info($"Coverage Check: Discovered {activeBusLinesInCity} active bus lines currently serving {state.AlreadyCoveredStops.Count} stops in the city.");

            int unservedStopCount = 0;
            for (int i = 0; i < state.AllGlobalStops.Count; i++)
            {
                if (!state.AllGlobalStops[i].IsStationBay && !state.AlreadyCoveredStops.Contains(state.AllGlobalStops[i].StopEntity))
                    unservedStopCount++;
            }
            state.UnservedStopCount = unservedStopCount;

            if (activeBusLinesInCity > 0 && unservedStopCount == 0)
            {
                log.Info($"ExecuteGeneration: All {state.AllGlobalStops.Count} stops are already covered by {activeBusLinesInCity} active bus lines. Skipping duplicate line generation.");
                _generationStage = GenerationStage.Idle;
                _planningSession = null;
                _hasRun = true;
                _isManualRequest = false;
                string statusMsg = (state.DistrictFilter != Entity.Null)
                    ? $"All stops in '{state.DistrictName}' are already covered by active bus lines."
                    : $"City transit network is already fully covered by {activeBusLinesInCity} active bus lines ({state.AlreadyCoveredStops.Count} stops served). Use 'Delete All' if you wish to redesign from scratch.";
                AutoBusLinesUISystem.Instance?.SetBuildProgress(1f, "idle", statusMsg);
                return;
            }

            _generationStage = GenerationStage.PlanningHubs;
            AutoBusLinesUISystem.Instance?.SetBuildProgress(0.58f, phaseStatus, "Building transit graph & planning station hub loops...");
        }

        private void StepPlanningHubs()
        {
            var state = _planningSession;
            if (state == null)
            {
                _generationStage = GenerationStage.Idle;
                return;
            }

            state.PlanCtx = PreparePlanContextAndHubs(
                state.AllGlobalStops, state.AllCorridors,
                state.MinStopsPerLine, state.MaxStopsPerLine, state.MaxRouteLength,
                state.AlreadyCoveredStops, state.DistrictFilter);

            _generationStage = GenerationStage.PlanningCorridors;
            string phaseStatus = state.PlanOnly ? "planning" : "building";
            AutoBusLinesUISystem.Instance?.SetBuildProgress(0.72f, phaseStatus, "Calculating arterial transit corridors...");
        }

        private void StepPlanningCorridors()
        {
            var state = _planningSession;
            if (state == null || state.PlanCtx == null)
            {
                _generationStage = GenerationStage.Idle;
                return;
            }

            PlanCorridorsPhase(state.PlanCtx);
            _generationStage = GenerationStage.PlanningFinish;
            string phaseStatus = state.PlanOnly ? "planning" : "building";
            AutoBusLinesUISystem.Instance?.SetBuildProgress(0.86f, phaseStatus, "Planning neighborhood feeder loops & validating paths...");
        }

        private void StepPlanningFinish()
        {
            var state = _planningSession;
            if (state == null || state.PlanCtx == null)
            {
                _generationStage = GenerationStage.Idle;
                return;
            }

            PlanLocalAndValidatePhase(state.PlanCtx);
            var allTours = state.PlanCtx.Tours;
            var servedStopEntities = state.PlanCtx.Served;
            _planningSession = null;

            FinalizePlannedTours(
                state.PlanOnly, state.SeedOffset, state.AutoOpenPanel,
                state.DistrictFilter, state.DistrictName,
                state.AllGlobalStops, state.ActiveBusLinesInCity, state.UnservedStopCount,
                state.BusLinePrefabEntity, state.BusLineRouteData,
                allTours, servedStopEntities);
        }

        private void FinalizePlannedTours(
            bool planOnly,
            int seedOffset,
            bool autoOpenPanel,
            Entity districtFilter,
            string districtName,
            List<PlacedStop> allGlobalStops,
            int activeBusLinesInCity,
            int unservedStopCount,
            Entity busLinePrefabEntity,
            RouteData busLineRouteData,
            List<List<PlacedStop>> allTours,
            HashSet<Entity> servedStopEntities)
        {

            // Ensure any station platform bays included in planned tours are present in allGlobalStops for accurate tracking
            for (int t = 0; t < allTours.Count; t++)
            {
                var tour = allTours[t];
                for (int s = 0; s < tour.Count; s++)
                {
                    bool found = false;
                    for (int g = 0; g < allGlobalStops.Count; g++)
                    {
                        if (allGlobalStops[g].StopEntity == tour[s].StopEntity)
                        {
                            found = true;
                            break;
                        }
                    }
                    if (!found)
                    {
                        allGlobalStops.Add(tour[s]);
                    }
                }
            }

            if (allTours.Count == 0)
            {
                log.Warn($"No new bus tours formed. Total available stops in scope: {allGlobalStops.Count}. Active lines in city: {activeBusLinesInCity}.");
                _generationStage = GenerationStage.Idle;
                _hasRun = true;
                _isManualRequest = false;
                string msg;
                if (allGlobalStops.Count < ABSOLUTE_MIN_STOPS)
                {
                    msg = districtFilter != Entity.Null
                        ? $"District '{districtName}' contains only {allGlobalStops.Count} bus stops (minimum {ABSOLUTE_MIN_STOPS} required to form a bus route)."
                        : $"Only {allGlobalStops.Count} bus stops could be placed across the city (minimum {ABSOLUTE_MIN_STOPS} required).";
                }
                else if (activeBusLinesInCity > 0 && unservedStopCount == 0)
                {
                    msg = districtFilter != Entity.Null
                        ? $"All stops in District '{districtName}' are already served by active bus lines."
                        : $"All road corridors and stops are already covered by {activeBusLinesInCity} active bus lines.";
                }
                else
                {
                    msg = districtFilter != Entity.Null
                        ? $"No valid bus loops could be closed within District '{districtName}'. Try including adjacent roads or placing more stops."
                        : "No valid bus tours could be formed from the road network.";
                }
                AutoBusLinesUISystem.Instance?.SetPlanStatus("idle", msg);
                return;
            }

            if (planOnly)
            {
                _plannedTours = new List<List<PlacedStop>>(allTours);
                LineColorMode colorMode = Mod.setting != null ? Mod.setting.LineColoring : LineColorMode.PerStation;
                var tourColors = ComputeTourColors(allTours, colorMode);

                var plannedRoutes = new List<PlannedRoute>();
                for (int t = 0; t < allTours.Count; t++)
                {
                    var tour = allTours[t];
                    if (tour.Count < ABSOLUTE_MIN_STOPS) continue;

                    int lineNumber = plannedRoutes.Count + 1;
                    float routeDistKm = CalculateTourLength(tour) / 1000f;
                    string hexColor = (t < tourColors.Count) ? tourColors[t] : GetLineHexColor(lineNumber);

                    var routeData = new PlannedRoute
                    {
                        Id = lineNumber,
                        Name = $"Line {lineNumber}",
                        Color = hexColor,
                        LengthKm = (float)Math.Round(routeDistKm, 1),
                        Enabled = true
                    };

                    // First pass: collect street names and count occurrences on this tour
                    var stopStreetNames = new string[tour.Count];
                    var streetCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                    for (int s = 0; s < tour.Count; s++)
                    {
                        var st = tour[s];
                        if (!st.IsStationBay)
                        {
                            string street = GetRoadStreetName(st.RoadEntity) ?? "Road";
                            stopStreetNames[s] = street;
                            streetCounts[street] = streetCounts.TryGetValue(street, out int c) ? c + 1 : 1;
                        }
                    }

                    // Second pass: assign formatted names with incremental numbering if duplicated
                    var streetIndices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                    for (int s = 0; s < tour.Count; s++)
                    {
                        var st = tour[s];
                        string stopDesc;

                        if (st.IsStationBay)
                        {
                            stopDesc = "Station Bay";
                        }
                        else
                        {
                            string street = stopStreetNames[s] ?? "Road";
                            int totalOnStreet = streetCounts[street];
                            int currentIndex = streetIndices.TryGetValue(street, out int cur) ? cur + 1 : 1;
                            streetIndices[street] = currentIndex;

                            string prefix = st.IsPreExisting ? "Stop" : "New Stop";
                            if (totalOnStreet > 1)
                            {
                                stopDesc = $"{prefix} {street} {currentIndex}";
                            }
                            else
                            {
                                stopDesc = $"{prefix} {street}";
                            }
                        }

                        routeData.Stops.Add(new PlannedStopData
                        {
                            Index = s + 1,
                            VirtualId = st.StopEntity.Index,
                            Name = stopDesc,
                            PosX = (float)Math.Round(st.Position.x, 1),
                            PosY = (float)Math.Round(st.Position.y, 1),
                            PosZ = (float)Math.Round(st.Position.z, 1),
                            IsStationBay = st.IsStationBay,
                            IsPreExisting = st.IsPreExisting,
                            Enabled = true,
                            InternalStop = st
                        });
                    }
                    plannedRoutes.Add(routeData);
                }

                _activePlan = plannedRoutes;
                _activePlanSelectedStopModel = Mod.setting != null ? (Mod.setting.SelectedStopPrefab ?? "") : "";
                _currentPlanSeed = seedOffset;
                _generationStage = GenerationStage.Idle;
                _hasRun = true;
                _isManualRequest = false;

                log.Info($"Plan Mode: Successfully generated {plannedRoutes.Count} proposed lines with {servedStopEntities.Count} stops (Variant #{seedOffset + 1}).");
                AutoBusLinesUISystem.Instance?.SetBuildProgress(1f);
                AutoBusLinesUISystem.Instance?.UpdatePlan(plannedRoutes, seedOffset, $"Preview Plan Ready: {plannedRoutes.Count} lines proposed ({servedStopEntities.Count} stops).");
                if (autoOpenPanel)
                {
                    AutoBusLinesUISystem.Instance?.OpenPanel();
                }
                return;
            }

            // Stage 1 Completion: Store planned tours and prefab info for staged execution across subsequent frames
            _plannedTours = new List<List<PlacedStop>>(allTours);
            LineColorMode directColorMode = Mod.setting != null ? Mod.setting.LineColoring : LineColorMode.PerStation;
            var directTourColors = ComputeTourColors(allTours, directColorMode);
            _plannedTourColors = new List<Color32>();
            for (int i = 0; i < directTourColors.Count; i++)
            {
                _plannedTourColors.Add(HexToColor(directTourColors[i]));
            }
            _cachedBusLinePrefabEntity = busLinePrefabEntity;
            _cachedBusLineRouteData = busLineRouteData;
            _totalStopsPlaced = allGlobalStops.Count;
            _totalStopsActive = servedStopEntities.Count;
            _generationStage = GenerationStage.WaitingForStopIndexing;
            _waitFrameCounter = 0;
            AutoBusLinesUISystem.Instance?.SetBuildProgress(0.60f, "building", "Indexing bus stops and road geometry...");

            log.Info($"Stage 1 Complete: Placed {_totalStopsPlaced} roadside bus stops and planned {_plannedTours.Count} bus lines. Yielding {INDEXING_DELAY_FRAMES} simulation frames for spatial quadtrees and net indexing to settle before drawing routes...");
            return;
        }

    }
}

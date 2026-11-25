using Colossal.Logging;
using Game;
using Game.Net;
using Game.Common;
using Game.Tools;
using Game.Prefabs;
using Unity.Entities;
using Unity.Collections;

namespace AutoBusLines
{
    public partial class RoadNetworkScanner : GameSystemBase
    {
        private static readonly ILog log = LogManager.GetLogger($"{nameof(AutoBusLines)}.{nameof(RoadNetworkScanner)}");

        private EntityQuery _roadQuery;
        private PrefabSystem _prefabSystem;
        private bool _hasRun = false;
        private int _lastLoggedRoadCount = -1;
        private int _frameThrottleCounter = 0;

        public NativeList<Entity> RoadSegments { get; private set; }
        public NativeList<Entity> DrivableSegments { get; private set; }

        protected override void OnCreate()
        {
            base.OnCreate();

            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();

            _roadQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[]
                {
                    ComponentType.ReadOnly<Edge>(),
                    ComponentType.ReadOnly<Curve>(),
                    ComponentType.ReadOnly<Road>(),
                },
                None = new ComponentType[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Game.Tools.Temp>(),
                    ComponentType.ReadOnly<Game.Tools.Hidden>(),
                    ComponentType.ReadOnly<Game.Common.Owner>(),
                    ComponentType.ReadOnly<Game.Net.OutsideConnection>(),
                }
            });

            RoadSegments = new NativeList<Entity>(Allocator.Persistent);
            DrivableSegments = new NativeList<Entity>(Allocator.Persistent);

            log.Info("RoadNetworkScanner created");
        }

        protected override void OnUpdate()
        {
            if (_hasRun)
                return;

            if (_frameThrottleCounter > 0)
            {
                _frameThrottleCounter--;
                return;
            }

            RoadSegments.Clear();
            DrivableSegments.Clear();
            var entities = _roadQuery.ToEntityArray(Allocator.Temp);

            int gravelExcludedCount = 0;
            int highwayExcludedCount = 0;
            int nonDrivableExcludedCount = 0;

            for (int i = 0; i < entities.Length; i++)
            {
                var roadEntity = entities[i];
                if (!HasDrivableCarLane(roadEntity))
                {
                    nonDrivableExcludedCount++;
                    continue;
                }

                if (IsGravelRoad(roadEntity))
                {
                    gravelExcludedCount++;
                    continue;
                }

                DrivableSegments.Add(roadEntity);

                if (IsHighwayOrRamp(roadEntity))
                {
                    highwayExcludedCount++;
                    continue;
                }

                RoadSegments.Add(roadEntity);
            }

            if (RoadSegments.Length > 0)
            {
                if (_lastLoggedRoadCount != RoadSegments.Length)
                {
                    log.Info($"Scanned {entities.Length} total roads -> Added {RoadSegments.Length} municipal paved roads (Excluded {gravelExcludedCount} gravel/unpaved, {highwayExcludedCount} highways/ramps, {nonDrivableExcludedCount} non-car/pedestrian segments)");
                    _lastLoggedRoadCount = RoadSegments.Length;
                }
                _hasRun = true;
                _frameThrottleCounter = 0;
            }
            else
            {
                if (_lastLoggedRoadCount != 0)
                {
                    log.Info("RoadNetworkScanner: No paved municipal roads found in city yet.");
                    _lastLoggedRoadCount = 0;
                }
                _frameThrottleCounter = 60; // Throttle empty map rescanning to once every ~60 frames
            }

            entities.Dispose();
        }

        private readonly System.Collections.Generic.Dictionary<Entity, bool> _highwayPrefabCache = new System.Collections.Generic.Dictionary<Entity, bool>();
        private readonly System.Collections.Generic.Dictionary<Entity, bool> _gravelPrefabCache = new System.Collections.Generic.Dictionary<Entity, bool>();

        private static readonly string[] HighwayKeywords = { "Highway", "Freeway", "Motorway", "Ramp", "Sliproad", "Interchange" };
        private static readonly string[] GravelKeywords = { "Gravel", "Dirt", "Unpaved" };

        private bool HasDrivableCarLane(Entity roadEntity)
        {
            if (!EntityManager.HasBuffer<Game.Net.SubLane>(roadEntity))
                return false;

            var subLanes = EntityManager.GetBuffer<Game.Net.SubLane>(roadEntity);
            for (int i = 0; i < subLanes.Length; i++)
            {
                Entity subLane = subLanes[i].m_SubLane;
                if (!EntityManager.HasComponent<Game.Net.CarLane>(subLane) ||
                    EntityManager.HasComponent<Game.Net.SlaveLane>(subLane))
                {
                    continue;
                }

                var carLane = EntityManager.GetComponentData<Game.Net.CarLane>(subLane);
                if ((carLane.m_Flags & Game.Net.CarLaneFlags.Forbidden) != 0)
                    continue;

                if (EntityManager.HasComponent<PrefabRef>(subLane))
                {
                    Entity lanePrefab = EntityManager.GetComponentData<PrefabRef>(subLane).m_Prefab;
                    if (lanePrefab != Entity.Null && EntityManager.HasComponent<CarLaneData>(lanePrefab))
                    {
                        var laneData = EntityManager.GetComponentData<CarLaneData>(lanePrefab);
                        if ((laneData.m_RoadTypes & RoadTypes.Car) == 0)
                            continue;
                    }
                }

                return true;
            }

            return false;
        }

        private string GetRoadPrefabName(Entity prefabEntity)
        {
            if (_prefabSystem == null || prefabEntity == Entity.Null)
                return null;

            if (_prefabSystem.TryGetPrefab<PrefabBase>(prefabEntity, out var prefabBase) && prefabBase != null)
                return prefabBase.name;

            return _prefabSystem.GetPrefabName(prefabEntity);
        }

        private static bool ContainsAnyKeyword(string value, string[] keywords)
        {
            if (string.IsNullOrEmpty(value))
                return false;

            for (int i = 0; i < keywords.Length; i++)
            {
                if (value.IndexOf(keywords[i], System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        private bool IsHighwayOrRamp(Entity roadEntity)
        {
            if (!EntityManager.HasComponent<PrefabRef>(roadEntity))
                return false;

            Entity prefabEntity = EntityManager.GetComponentData<PrefabRef>(roadEntity).m_Prefab;
            if (prefabEntity == Entity.Null)
                return false;

            if (_highwayPrefabCache.TryGetValue(prefabEntity, out bool cached))
                return cached;

            bool isHighway = false;
            if (_prefabSystem != null && _prefabSystem.TryGetPrefab<PrefabBase>(prefabEntity, out var prefabBase) &&
                prefabBase is RoadPrefab roadPrefab && roadPrefab.m_HighwayRules)
            {
                isHighway = true;
            }
            else
            {
                isHighway = ContainsAnyKeyword(GetRoadPrefabName(prefabEntity), HighwayKeywords);
            }

            _highwayPrefabCache[prefabEntity] = isHighway;
            return isHighway;
        }

        private bool IsGravelRoad(Entity roadEntity)
        {
            // 1. Check Composition component on edge entity (Composition.m_Edge -> NetCompositionData)
            if (EntityManager.HasComponent<Composition>(roadEntity))
            {
                var comp = EntityManager.GetComponentData<Composition>(roadEntity);
                if (comp.m_Edge != Entity.Null && EntityManager.HasComponent<NetCompositionData>(comp.m_Edge))
                {
                    var compData = EntityManager.GetComponentData<NetCompositionData>(comp.m_Edge);
                    if ((compData.m_Flags.m_General & CompositionFlags.General.Gravel) != 0)
                        return true;
                }
            }

            // 2. Check PrefabRef
            if (!EntityManager.HasComponent<PrefabRef>(roadEntity))
                return false;

            Entity prefabEntity = EntityManager.GetComponentData<PrefabRef>(roadEntity).m_Prefab;
            if (prefabEntity == Entity.Null)
                return false;

            if (_gravelPrefabCache.TryGetValue(prefabEntity, out bool cached))
                return cached;

            bool isGravel = false;
            if (EntityManager.HasComponent<NetCompositionData>(prefabEntity))
            {
                var compData = EntityManager.GetComponentData<NetCompositionData>(prefabEntity);
                if ((compData.m_Flags.m_General & CompositionFlags.General.Gravel) != 0)
                    isGravel = true;
            }

            if (!isGravel)
                isGravel = ContainsAnyKeyword(GetRoadPrefabName(prefabEntity), GravelKeywords);

            _gravelPrefabCache[prefabEntity] = isGravel;
            return isGravel;
        }

        protected override void OnGameLoaded(Colossal.Serialization.Entities.Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);
            Reset();
        }

        public void Reset()
        {
            _hasRun = false;
            _frameThrottleCounter = 0;
            _lastLoggedRoadCount = -1;
            _highwayPrefabCache.Clear();
            _gravelPrefabCache.Clear();
            if (RoadSegments.IsCreated)
                RoadSegments.Clear();
            if (DrivableSegments.IsCreated)
                DrivableSegments.Clear();
        }

        public void ScanNow()
        {
            _hasRun = false;
            _frameThrottleCounter = 0;
            OnUpdate();
        }

        protected override void OnDestroy()
        {
            if (RoadSegments.IsCreated)
                RoadSegments.Dispose();
            if (DrivableSegments.IsCreated)
                DrivableSegments.Dispose();
            base.OnDestroy();
        }
    }
}

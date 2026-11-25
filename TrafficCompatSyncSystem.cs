using System;
using System.Collections.Generic;
using System.Reflection;
using Colossal.Logging;
using Game;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace AutoBusLines
{
    /// <summary>
    /// Holds snapshotted pre-CompositionSelectSystem edge compositions and coordinates optional
    /// runtime synchronization with the "Traffic" mod without any compile-time assembly dependency.
    /// </summary>
    internal static class TrafficCompatState
    {
        private static readonly ILog Log = LogManager.GetLogger($"{nameof(AutoBusLines)}.{nameof(TrafficCompatState)}").SetShowsErrorsInUI(false);

        private struct MappedLaneInfo
        {
            public int Index;
            public int Carriageway;
            public int Group;
            public float3 RawPosition;
        }

        private interface ITrafficBridge
        {
            ComponentType ModifiedLaneConnectionsReadWrite { get; }
            ComponentType ModifiedConnectionsType { get; }
            ComponentType LanePriorityReadWrite { get; }

            void RemapEdgePriorities(EntityManager em, Entity edge, Dictionary<int, MappedLaneInfo> laneMap);
            void RemapNodeConnections(
                EntityManager em,
                Entity node,
                Dictionary<Entity, Dictionary<int, MappedLaneInfo>> edgeMappings,
                List<Entity> deletedGeneratedEntities,
                List<Entity> emptyNodes);
            void RemoveEmptyNodeComponents(EntityManager em, Entity node);
        }

        private sealed class ReflectionTrafficBridge<TModConn, TGenConn, TLanePrio> : ITrafficBridge
            where TModConn : unmanaged, IBufferElementData
            where TGenConn : unmanaged, IBufferElementData
            where TLanePrio : unmanaged, IBufferElementData
        {
            private readonly FieldInfo _modLaneIndex;
            private readonly FieldInfo _modCarriagewayAndGroup;
            private readonly FieldInfo _modLanePosition;
            private readonly FieldInfo _modEdgeEntity;
            private readonly FieldInfo _modConnectionsEntity;

            private readonly FieldInfo _genSourceEntity;
            private readonly FieldInfo _genTargetEntity;
            private readonly FieldInfo _genLaneIndexMap;
            private readonly FieldInfo _genLanePositionMap;
            private readonly FieldInfo _genCarriagewayAndGroupMap;

            private readonly FieldInfo _prioLaneIndex;

            public ComponentType ModifiedLaneConnectionsReadWrite { get; }
            public ComponentType ModifiedConnectionsType { get; }
            public ComponentType LanePriorityReadWrite { get; }

            public ReflectionTrafficBridge(Type modifiedConnectionsTagType)
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
                var modType = typeof(TModConn);
                var genType = typeof(TGenConn);
                var prioType = typeof(TLanePrio);

                _modLaneIndex = modType.GetField("laneIndex", flags);
                _modCarriagewayAndGroup = modType.GetField("carriagewayAndGroup", flags);
                _modLanePosition = modType.GetField("lanePosition", flags);
                _modEdgeEntity = modType.GetField("edgeEntity", flags);
                _modConnectionsEntity = modType.GetField("modifiedConnections", flags);

                _genSourceEntity = genType.GetField("sourceEntity", flags);
                _genTargetEntity = genType.GetField("targetEntity", flags);
                _genLaneIndexMap = genType.GetField("laneIndexMap", flags);
                _genLanePositionMap = genType.GetField("lanePositionMap", flags);
                _genCarriagewayAndGroupMap = genType.GetField("carriagewayAndGroupIndexMap", flags);

                _prioLaneIndex = prioType.GetField("laneIndex", flags);

                if (_modLaneIndex == null || _modCarriagewayAndGroup == null || _modLanePosition == null ||
                    _modEdgeEntity == null || _modConnectionsEntity == null ||
                    _genSourceEntity == null || _genTargetEntity == null || _genLaneIndexMap == null ||
                    _genLanePositionMap == null || _genCarriagewayAndGroupMap == null ||
                    _prioLaneIndex == null)
                {
                    throw new MissingFieldException("One or more expected Traffic mod fields were not found.");
                }

                ModifiedLaneConnectionsReadWrite = ComponentType.ReadWrite<TModConn>();
                ModifiedConnectionsType = new ComponentType(modifiedConnectionsTagType, ComponentType.AccessMode.ReadWrite);
                LanePriorityReadWrite = ComponentType.ReadWrite<TLanePrio>();
            }

            public void RemapEdgePriorities(EntityManager em, Entity edge, Dictionary<int, MappedLaneInfo> laneMap)
            {
                if (!em.HasBuffer<TLanePrio>(edge))
                    return;

                var buffer = em.GetBuffer<TLanePrio>(edge);
                for (int i = buffer.Length - 1; i >= 0; i--)
                {
                    object boxed = buffer[i];
                    int3 laneIndex = (int3)_prioLaneIndex.GetValue(boxed);
                    if (laneMap.TryGetValue(laneIndex.x, out MappedLaneInfo mapped))
                    {
                        _prioLaneIndex.SetValue(boxed, new int3(mapped.Index, mapped.Carriageway, mapped.Group));
                        buffer[i] = (TLanePrio)boxed;
                    }
                    else
                    {
                        buffer.RemoveAt(i);
                    }
                }
            }

            public void RemapNodeConnections(
                EntityManager em,
                Entity node,
                Dictionary<Entity, Dictionary<int, MappedLaneInfo>> edgeMappings,
                List<Entity> deletedGeneratedEntities,
                List<Entity> emptyNodes)
            {
                if (!em.HasBuffer<TModConn>(node))
                    return;

                var modBuffer = em.GetBuffer<TModConn>(node);
                bool anyModBufferChanged = false;

                for (int k = modBuffer.Length - 1; k >= 0; k--)
                {
                    object boxedMod = modBuffer[k];
                    Entity modEdge = (Entity)_modEdgeEntity.GetValue(boxedMod);
                    Entity modConnEnt = (Entity)_modConnectionsEntity.GetValue(boxedMod);

                    bool keepEntry = true;
                    if (edgeMappings.TryGetValue(modEdge, out var srcMap))
                    {
                        int oldLaneIdx = (int)_modLaneIndex.GetValue(boxedMod);
                        if (srcMap.TryGetValue(oldLaneIdx, out var mappedSrc))
                        {
                            float3 signedPos = MakeSignedEdgePos(em, modEdge, node, mappedSrc.RawPosition);
                            _modLaneIndex.SetValue(boxedMod, mappedSrc.Index);
                            _modCarriagewayAndGroup.SetValue(boxedMod, new int2(mappedSrc.Carriageway, mappedSrc.Group));
                            _modLanePosition.SetValue(boxedMod, signedPos);
                            modBuffer[k] = (TModConn)boxedMod;
                            anyModBufferChanged = true;
                        }
                        else
                        {
                            keepEntry = false;
                        }
                    }

                    if (!keepEntry)
                    {
                        modBuffer.RemoveAt(k);
                        anyModBufferChanged = true;
                        if (modConnEnt != Entity.Null && em.Exists(modConnEnt))
                            deletedGeneratedEntities.Add(modConnEnt);
                        continue;
                    }

                    if (modConnEnt == Entity.Null || !em.Exists(modConnEnt) || !em.HasBuffer<TGenConn>(modConnEnt))
                        continue;

                    var genBuffer = em.GetBuffer<TGenConn>(modConnEnt);
                    for (int g = genBuffer.Length - 1; g >= 0; g--)
                    {
                        object boxedGen = genBuffer[g];
                        Entity sourceEnt = (Entity)_genSourceEntity.GetValue(boxedGen);
                        Entity targetEnt = (Entity)_genTargetEntity.GetValue(boxedGen);

                        bool srcChanged = edgeMappings.TryGetValue(sourceEnt, out var gSrcMap);
                        bool tgtChanged = edgeMappings.TryGetValue(targetEnt, out var gTgtMap);
                        if (!srcChanged && !tgtChanged)
                            continue;

                        int2 idxMap = (int2)_genLaneIndexMap.GetValue(boxedGen);
                        float3x2 posMap = (float3x2)_genLanePositionMap.GetValue(boxedGen);
                        int4 cgMap = (int4)_genCarriagewayAndGroupMap.GetValue(boxedGen);

                        if (srcChanged)
                        {
                            if (!gSrcMap.TryGetValue(idxMap.x, out var mSrc))
                            {
                                genBuffer.RemoveAt(g);
                                continue;
                            }
                            idxMap.x = mSrc.Index;
                            posMap.c0 = MakeSignedEdgePos(em, sourceEnt, node, mSrc.RawPosition);
                            cgMap.xy = new int2(mSrc.Carriageway, mSrc.Group);
                        }

                        if (tgtChanged)
                        {
                            if (!gTgtMap.TryGetValue(idxMap.y, out var mTgt))
                            {
                                genBuffer.RemoveAt(g);
                                continue;
                            }
                            idxMap.y = mTgt.Index;
                            posMap.c1 = MakeSignedEdgePos(em, targetEnt, node, mTgt.RawPosition);
                            cgMap.zw = new int2(mTgt.Carriageway, mTgt.Group);
                        }

                        _genLaneIndexMap.SetValue(boxedGen, idxMap);
                        _genLanePositionMap.SetValue(boxedGen, posMap);
                        _genCarriagewayAndGroupMap.SetValue(boxedGen, cgMap);
                        genBuffer[g] = (TGenConn)boxedGen;
                    }

                    if (genBuffer.Length == 0)
                    {
                        modBuffer.RemoveAt(k);
                        anyModBufferChanged = true;
                        deletedGeneratedEntities.Add(modConnEnt);
                    }
                }

                if (anyModBufferChanged && modBuffer.Length == 0)
                {
                    emptyNodes.Add(node);
                }
            }

            public void RemoveEmptyNodeComponents(EntityManager em, Entity node)
            {
                if (em.HasBuffer<TModConn>(node))
                    em.RemoveComponent<TModConn>(node);
                if (em.HasComponent(node, ModifiedConnectionsType))
                    em.RemoveComponent(node, ModifiedConnectionsType);
            }

            private static float3 MakeSignedEdgePos(EntityManager em, Entity edgeEntity, Entity node, float3 rawPos)
            {
                bool isEnd = em.HasComponent<Edge>(edgeEntity) && em.GetComponentData<Edge>(edgeEntity).m_End == node;
                rawPos.x = isEnd ? rawPos.x : -rawPos.x;
                return rawPos;
            }
        }

        private static bool _probeAttempted;
        private static ITrafficBridge _bridge;
        private static Type _trafficLaneSystemType;

        public static readonly Dictionary<Entity, Entity> PendingEdgeCompositions = new Dictionary<Entity, Entity>();

        public static bool EnsureInitialized(World world)
        {
            if (!_probeAttempted)
            {
                _probeAttempted = true;
                try
                {
                    Assembly trafficAsm = FindLoadedAssembly("Traffic");
                    if (trafficAsm != null)
                    {
                        _trafficLaneSystemType = trafficAsm.GetType("Traffic.Systems.TrafficLaneSystem", false);
                        Type modConnType = trafficAsm.GetType("Traffic.Components.LaneConnections.ModifiedLaneConnections", false);
                        Type genConnType = trafficAsm.GetType("Traffic.Components.LaneConnections.GeneratedConnection", false);
                        Type lanePrioType = trafficAsm.GetType("Traffic.Components.PrioritySigns.LanePriority", false);
                        Type modTagType = trafficAsm.GetType("Traffic.Components.ModifiedConnections", false);

                        if (_trafficLaneSystemType != null && modConnType != null && genConnType != null &&
                            lanePrioType != null && modTagType != null)
                        {
                            var genericBridge = typeof(ReflectionTrafficBridge<,,>).MakeGenericType(modConnType, genConnType, lanePrioType);
                            _bridge = (ITrafficBridge)Activator.CreateInstance(genericBridge, modTagType);
                            Log.Info($"Traffic mod integration initialized cleanly (version {trafficAsm.GetName().Version}).");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"Traffic mod integration disabled due to reflection mismatch: {ex.Message}");
                    _bridge = null;
                    _trafficLaneSystemType = null;
                }
            }

            if (_bridge == null || _trafficLaneSystemType == null || world == null)
                return false;

            var trafficSys = world.GetExistingSystemManaged(_trafficLaneSystemType);
            return trafficSys != null && trafficSys.Enabled;
        }

        public static void SyncChangedCompositions(EntityManager em)
        {
            if (_bridge == null || PendingEdgeCompositions.Count == 0)
                return;

            var edgeMappings = new Dictionary<Entity, Dictionary<int, MappedLaneInfo>>();
            var affectedNodes = new HashSet<Entity>();

            foreach (var kv in PendingEdgeCompositions)
            {
                Entity edge = kv.Key;
                Entity oldCompEdge = kv.Value;

                if (edge == Entity.Null || oldCompEdge == Entity.Null || !em.Exists(edge) ||
                    em.HasComponent<Deleted>(edge) || em.HasComponent<Temp>(edge) ||
                    !em.HasComponent<Composition>(edge))
                {
                    continue;
                }

                Entity newCompEdge = em.GetComponentData<Composition>(edge).m_Edge;
                if (newCompEdge == Entity.Null || newCompEdge == oldCompEdge)
                    continue;

                if (!TryBuildEdgeLaneMapping(em, oldCompEdge, newCompEdge, out var laneMap))
                    continue;

                edgeMappings[edge] = laneMap;
                CollectAffectedNodesForEdge(em, edge, affectedNodes);
            }

            if (edgeMappings.Count == 0)
                return;

            var deletedGeneratedEntities = new List<Entity>();
            var emptyNodes = new List<Entity>();

            foreach (var edge in edgeMappings.Keys)
            {
                _bridge.RemapEdgePriorities(em, edge, edgeMappings[edge]);
            }

            foreach (var node in affectedNodes)
            {
                if (node == Entity.Null || !em.Exists(node) || em.HasComponent<Deleted>(node))
                    continue;

                _bridge.RemapNodeConnections(em, node, edgeMappings, deletedGeneratedEntities, emptyNodes);
            }

            for (int i = 0; i < deletedGeneratedEntities.Count; i++)
            {
                Entity genEnt = deletedGeneratedEntities[i];
                if (em.Exists(genEnt) && !em.HasComponent<Deleted>(genEnt))
                    em.AddComponentData(genEnt, default(Deleted));
            }

            for (int i = 0; i < emptyNodes.Count; i++)
            {
                Entity node = emptyNodes[i];
                if (!em.Exists(node))
                    continue;

                _bridge.RemoveEmptyNodeComponents(em, node);
            }
        }

        private static bool TryBuildEdgeLaneMapping(
            EntityManager em,
            Entity oldCompEdge,
            Entity newCompEdge,
            out Dictionary<int, MappedLaneInfo> laneMap)
        {
            laneMap = null;
            if (!em.Exists(oldCompEdge) || !em.Exists(newCompEdge) ||
                !em.HasComponent<NetCompositionData>(oldCompEdge) || !em.HasComponent<NetCompositionData>(newCompEdge) ||
                !em.HasBuffer<NetCompositionLane>(oldCompEdge) || !em.HasBuffer<NetCompositionLane>(newCompEdge))
            {
                return false;
            }

            NetCompositionData oldCompData = em.GetComponentData<NetCompositionData>(oldCompEdge);
            NetCompositionData newCompData = em.GetComponentData<NetCompositionData>(newCompEdge);
            var oldLanes = em.GetBuffer<NetCompositionLane>(oldCompEdge, isReadOnly: true);
            var newLanes = em.GetBuffer<NetCompositionLane>(newCompEdge, isReadOnly: true);

            float3 oldMidRight = new float3(oldCompData.m_MiddleOffset, 0f, 0f);
            float3 newMidRight = new float3(newCompData.m_MiddleOffset, 0f, 0f);
            const LaneFlags matchMask = LaneFlags.Road | LaneFlags.Track | LaneFlags.Pedestrian | LaneFlags.Invert;

            laneMap = new Dictionary<int, MappedLaneInfo>(oldLanes.Length);
            for (int i = 0; i < oldLanes.Length; i++)
            {
                NetCompositionLane oldLane = oldLanes[i];
                if ((oldLane.m_Flags & (LaneFlags.Road | LaneFlags.Track)) == 0)
                    continue;

                float3 oldRelPos = oldLane.m_Position - oldMidRight;
                float bestDistSq = 1e-4f; // < 1 cm difference relative to road middle offset
                int bestNewIdx = -1;

                for (int j = 0; j < newLanes.Length; j++)
                {
                    NetCompositionLane newLane = newLanes[j];
                    if ((oldLane.m_Flags & matchMask) != (newLane.m_Flags & matchMask))
                        continue;

                    float3 newRelPos = newLane.m_Position - newMidRight;
                    float distSq = math.lengthsq(oldRelPos - newRelPos);
                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        bestNewIdx = j;
                    }
                }

                if (bestNewIdx >= 0)
                {
                    NetCompositionLane matched = newLanes[bestNewIdx];
                    laneMap[oldLane.m_Index] = new MappedLaneInfo
                    {
                        Index = matched.m_Index,
                        Carriageway = matched.m_Carriageway,
                        Group = matched.m_Group,
                        RawPosition = matched.m_Position
                    };
                }
            }

            return true;
        }

        private static void CollectAffectedNodesForEdge(EntityManager em, Entity edge, HashSet<Entity> nodes)
        {
            if (em.HasComponent<Edge>(edge))
            {
                Edge edgeData = em.GetComponentData<Edge>(edge);
                if (edgeData.m_Start != Entity.Null)
                    nodes.Add(edgeData.m_Start);
                if (edgeData.m_End != Entity.Null)
                    nodes.Add(edgeData.m_End);
            }

            if (em.HasBuffer<ConnectedNode>(edge))
            {
                var connNodes = em.GetBuffer<ConnectedNode>(edge, isReadOnly: true);
                for (int i = 0; i < connNodes.Length; i++)
                {
                    Entity midNode = connNodes[i].m_Node;
                    if (midNode != Entity.Null)
                        nodes.Add(midNode);
                }
            }
        }

        private static Assembly FindLoadedAssembly(string simpleName)
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                var name = assemblies[i].GetName().Name;
                if (string.Equals(name, simpleName, StringComparison.OrdinalIgnoreCase))
                    return assemblies[i];
            }
            return null;
        }
    }

    /// <summary>
    /// Runs in Modification3 right before CompositionSelectSystem to capture the pre-update
    /// Composition.m_Edge of permanent Updated road edges so Traffic mod custom connections
    /// and priority signs can be remapped accurately in Modification4.
    /// </summary>
    public partial class AutoBusLinesPreCompositionSystem : GameSystemBase
    {
        private EntityQuery _updatedEdgesQuery;

        protected override void OnCreate()
        {
            base.OnCreate();
            _updatedEdgesQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Edge>(),
                    ComponentType.ReadOnly<Composition>(),
                    ComponentType.ReadOnly<Updated>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>()
                }
            });
            RequireForUpdate(_updatedEdgesQuery);
        }

        protected override void OnUpdate()
        {
            TrafficCompatState.PendingEdgeCompositions.Clear();
            if (!TrafficCompatState.EnsureInitialized(World))
                return;

            using (var entities = _updatedEdgesQuery.ToEntityArray(Allocator.Temp))
            using (var compositions = _updatedEdgesQuery.ToComponentDataArray<Composition>(Allocator.Temp))
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity compEdge = compositions[i].m_Edge;
                    if (compEdge != Entity.Null)
                        TrafficCompatState.PendingEdgeCompositions[entities[i]] = compEdge;
                }
            }
        }
    }

    /// <summary>
    /// Runs in Modification4 immediately after NetCompositionSystem (before TrafficLaneSystem)
    /// to remap Traffic mod's ModifiedLaneConnections, GeneratedConnection, and LanePriority
    /// buffers whenever a non-Temp road edge's Composition.m_Edge changed.
    /// </summary>
    public partial class AutoBusLinesTrafficSyncSystem : GameSystemBase
    {
        protected override void OnUpdate()
        {
            if (TrafficCompatState.PendingEdgeCompositions.Count == 0)
                return;

            try
            {
                if (TrafficCompatState.EnsureInitialized(World))
                    TrafficCompatState.SyncChangedCompositions(EntityManager);
            }
            finally
            {
                TrafficCompatState.PendingEdgeCompositions.Clear();
            }
        }
    }
}

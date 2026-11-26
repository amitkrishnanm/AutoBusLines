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
            public bool HasCar;
            public bool HasBicycle;
        }

        private struct SyncStats
        {
            public int RemappedEdges;
            public int Pass1MatchedLanes;
            public int Pass2ShiftedLanes;
            public int UpdatedNodeEntries;
            public int UpdatedGenConnections;
            public int DroppedGenConnections;
            public int DroppedModEntries;
            public int DroppedPriorities;
            public int DropDiagLogsRemaining;
        }

        private readonly struct LaneRole
        {
            public readonly LaneFlags MatchFlags;
            public readonly bool HasCar;
            public readonly bool HasBicycle;

            public LaneRole(EntityManager em, in NetCompositionLane lane)
            {
                const LaneFlags matchMask = LaneFlags.Road | LaneFlags.Track | LaneFlags.Pedestrian | LaneFlags.Invert | LaneFlags.Master;
                MatchFlags = lane.m_Flags & matchMask;

                bool hasCar = false;
                bool hasBike = false;
                if ((lane.m_Flags & LaneFlags.Road) != 0 &&
                    lane.m_Lane != Entity.Null &&
                    em.Exists(lane.m_Lane) &&
                    em.HasComponent<CarLaneData>(lane.m_Lane))
                {
                    RoadTypes roadTypes = em.GetComponentData<CarLaneData>(lane.m_Lane).m_RoadTypes;
                    hasCar = (roadTypes & RoadTypes.Car) != 0;
                    hasBike = (roadTypes & RoadTypes.Bicycle) != 0;
                }

                HasCar = hasCar;
                HasBicycle = hasBike;
            }

            public bool MatchesExact(in LaneRole other)
            {
                return MatchFlags == other.MatchFlags && HasCar == other.HasCar && HasBicycle == other.HasBicycle;
            }

            public bool MatchesCarSplitFallback(in LaneRole other)
            {
                return MatchFlags == other.MatchFlags && HasCar && other.HasCar;
            }

            public bool IsBicycleOnlyRoad()
            {
                return (MatchFlags & LaneFlags.Road) != 0 && !HasCar && HasBicycle;
            }

            public bool MatchesMergedBicycleLane(in LaneRole other)
            {
                return MatchFlags == other.MatchFlags && other.HasBicycle;
            }
        }

        private readonly struct ShiftedCandidate
        {
            public readonly int OldIdx;
            public readonly int NewIdx;
            public readonly float Score;

            public ShiftedCandidate(int oldIdx, int newIdx, float score)
            {
                OldIdx = oldIdx;
                NewIdx = newIdx;
                Score = score;
            }
        }

        private interface ITrafficBridge
        {
            void RemapEdgePriorities(EntityManager em, Entity edge, Dictionary<int, MappedLaneInfo> laneMap, ref SyncStats stats);
            void RemapNodeConnections(
                EntityManager em,
                Entity node,
                Dictionary<Entity, Dictionary<int, MappedLaneInfo>> edgeMappings,
                List<Entity> deletedGeneratedEntities,
                List<Entity> emptyNodes,
                ref SyncStats stats);
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
            private readonly FieldInfo _genMethod;
            private readonly FieldInfo _genIsUnsafe;

            private readonly FieldInfo _prioLaneIndex;

            private readonly ComponentType _modifiedConnectionsType;

            private readonly struct EdgeLaneKey : IEquatable<EdgeLaneKey>
            {
                public readonly Entity Edge;
                public readonly int LaneIndex;

                public EdgeLaneKey(Entity edge, int laneIndex)
                {
                    Edge = edge;
                    LaneIndex = laneIndex;
                }

                public bool Equals(EdgeLaneKey other) => Edge == other.Edge && LaneIndex == other.LaneIndex;
                public override bool Equals(object obj) => obj is EdgeLaneKey other && Equals(other);
                public override int GetHashCode() => unchecked(Edge.GetHashCode() * 397 ^ LaneIndex);
            }

            private readonly struct GenConnKey : IEquatable<GenConnKey>
            {
                public readonly Entity Source;
                public readonly Entity Target;
                public readonly int2 LaneIndexMap;

                public GenConnKey(Entity source, Entity target, int2 laneIndexMap)
                {
                    Source = source;
                    Target = target;
                    LaneIndexMap = laneIndexMap;
                }

                public bool Equals(GenConnKey other) =>
                    Source == other.Source && Target == other.Target && LaneIndexMap.Equals(other.LaneIndexMap);
                public override bool Equals(object obj) => obj is GenConnKey other && Equals(other);
                public override int GetHashCode()
                {
                    unchecked
                    {
                        int h = Source.GetHashCode();
                        h = (h * 397) ^ Target.GetHashCode();
                        h = (h * 397) ^ LaneIndexMap.x;
                        h = (h * 397) ^ LaneIndexMap.y;
                        return h;
                    }
                }
            }

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
                _genMethod = genType.GetField("method", flags);
                _genIsUnsafe = genType.GetField("isUnsafe", flags);

                _prioLaneIndex = prioType.GetField("laneIndex", flags);

                if (_modLaneIndex == null || _modCarriagewayAndGroup == null || _modLanePosition == null ||
                    _modEdgeEntity == null || _modConnectionsEntity == null ||
                    _genSourceEntity == null || _genTargetEntity == null || _genLaneIndexMap == null ||
                    _genLanePositionMap == null || _genCarriagewayAndGroupMap == null ||
                    _prioLaneIndex == null)
                {
                    throw new MissingFieldException("One or more expected Traffic mod fields were not found.");
                }

                _modifiedConnectionsType = new ComponentType(modifiedConnectionsTagType, ComponentType.AccessMode.ReadWrite);
            }

            public void RemapEdgePriorities(EntityManager em, Entity edge, Dictionary<int, MappedLaneInfo> laneMap, ref SyncStats stats)
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
                        stats.DroppedPriorities++;
                    }
                }
            }

            public void RemapNodeConnections(
                EntityManager em,
                Entity node,
                Dictionary<Entity, Dictionary<int, MappedLaneInfo>> edgeMappings,
                List<Entity> deletedGeneratedEntities,
                List<Entity> emptyNodes,
                ref SyncStats stats)
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
                            stats.UpdatedNodeEntries++;
                        }
                        else
                        {
                            keepEntry = false;
                        }
                    }

                    if (!keepEntry)
                    {
                        if (stats.DropDiagLogsRemaining > 0)
                        {
                            stats.DropDiagLogsRemaining--;
                            Log.Info($"Traffic drop[mod]: node={node}, modEdge={modEdge}, oldLaneIdx={(int)_modLaneIndex.GetValue(boxedMod)}, mapCount={(edgeMappings.TryGetValue(modEdge, out var dbgMap) ? dbgMap.Count : -1)}");
                            LogEdgeCompMismatch(em, modEdge, (int)_modLaneIndex.GetValue(boxedMod));
                        }
                        modBuffer.RemoveAt(k);
                        anyModBufferChanged = true;
                        stats.DroppedModEntries++;
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
                        MappedLaneInfo mSrc = default;
                        MappedLaneInfo mTgt = default;

                        if (srcChanged)
                        {
                            if (!gSrcMap.TryGetValue(idxMap.x, out mSrc))
                            {
                                if (stats.DropDiagLogsRemaining > 0)
                                {
                                    stats.DropDiagLogsRemaining--;
                                    Log.Info($"Traffic drop[gen-src]: node={node}, src={sourceEnt}, oldSrcIdx={idxMap.x}, tgt={targetEnt}, oldTgtIdx={idxMap.y}, srcMapCount={gSrcMap.Count}");
                                    LogEdgeCompMismatch(em, sourceEnt, idxMap.x);
                                }
                                genBuffer.RemoveAt(g);
                                stats.DroppedGenConnections++;
                                continue;
                            }
                            idxMap.x = mSrc.Index;
                            posMap.c0 = MakeSignedEdgePos(em, sourceEnt, node, mSrc.RawPosition);
                            cgMap.xy = new int2(mSrc.Carriageway, mSrc.Group);
                        }

                        if (tgtChanged)
                        {
                            if (!gTgtMap.TryGetValue(idxMap.y, out mTgt))
                            {
                                if (stats.DropDiagLogsRemaining > 0)
                                {
                                    stats.DropDiagLogsRemaining--;
                                    Log.Info($"Traffic drop[gen-tgt]: node={node}, src={sourceEnt}, newSrcIdx={idxMap.x}, tgt={targetEnt}, oldTgtIdx={idxMap.y}, tgtMapCount={gTgtMap.Count}");
                                    LogEdgeCompMismatch(em, targetEnt, idxMap.y);
                                }
                                genBuffer.RemoveAt(g);
                                stats.DroppedGenConnections++;
                                continue;
                            }
                            idxMap.y = mTgt.Index;
                            posMap.c1 = MakeSignedEdgePos(em, targetEnt, node, mTgt.RawPosition);
                            cgMap.zw = new int2(mTgt.Carriageway, mTgt.Group);
                        }

                        if (_genMethod != null)
                        {
                            bool lostBike = (srcChanged && !mSrc.HasBicycle) || (tgtChanged && !mTgt.HasBicycle);
                            if (lostBike)
                            {
                                int methodMask = Convert.ToInt32(_genMethod.GetValue(boxedGen));
                                if ((methodMask & (int)Game.Pathfind.PathMethod.Bicycle) != 0)
                                {
                                    int remainingMethod = methodMask & ~(int)Game.Pathfind.PathMethod.Bicycle;
                                    if (remainingMethod == 0)
                                    {
                                        genBuffer.RemoveAt(g);
                                        stats.DroppedGenConnections++;
                                        continue;
                                    }
                                    _genMethod.SetValue(boxedGen, Enum.ToObject(_genMethod.FieldType, remainingMethod));
                                }
                            }
                        }

                        _genLaneIndexMap.SetValue(boxedGen, idxMap);
                        _genLanePositionMap.SetValue(boxedGen, posMap);
                        _genCarriagewayAndGroupMap.SetValue(boxedGen, cgMap);
                        genBuffer[g] = (TGenConn)boxedGen;
                        stats.UpdatedGenConnections++;
                    }

                    if (genBuffer.Length > 1)
                        DeduplicateGenBuffer(genBuffer);

                    if (genBuffer.Length == 0)
                    {
                        modBuffer.RemoveAt(k);
                        anyModBufferChanged = true;
                        stats.DroppedModEntries++;
                        deletedGeneratedEntities.Add(modConnEnt);
                    }
                }

                // If distinct old lanes (e.g. a car lane and its adjacent curb bike lane) merged onto the
                // same new lane, merge their GeneratedConnection records so Traffic has one valid entry per lane.
                if (modBuffer.Length > 1)
                {
                    var seenModByEdgeLane = new Dictionary<EdgeLaneKey, int>(modBuffer.Length);
                    var modIndicesToRemove = new List<int>();
                    for (int k = 0; k < modBuffer.Length; k++)
                    {
                        object boxedMod = modBuffer[k];
                        Entity edgeEnt = (Entity)_modEdgeEntity.GetValue(boxedMod);
                        int laneIdx = (int)_modLaneIndex.GetValue(boxedMod);
                        Entity modConnEnt = (Entity)_modConnectionsEntity.GetValue(boxedMod);
                        var key = new EdgeLaneKey(edgeEnt, laneIdx);

                        if (!seenModByEdgeLane.TryGetValue(key, out int keptIdx))
                        {
                            seenModByEdgeLane[key] = k;
                            continue;
                        }

                        Entity keptConnEnt = (Entity)_modConnectionsEntity.GetValue(modBuffer[keptIdx]);
                        if (modConnEnt != Entity.Null && modConnEnt != keptConnEnt && em.Exists(modConnEnt) && em.HasBuffer<TGenConn>(modConnEnt))
                        {
                            if (keptConnEnt != Entity.Null && em.Exists(keptConnEnt) && em.HasBuffer<TGenConn>(keptConnEnt))
                            {
                                var dupBuf = em.GetBuffer<TGenConn>(modConnEnt, isReadOnly: true);
                                var dupItems = new List<TGenConn>(dupBuf.Length);
                                for (int g = 0; g < dupBuf.Length; g++)
                                    dupItems.Add(dupBuf[g]);

                                if (dupItems.Count > 0)
                                {
                                    var keptBuf = em.GetBuffer<TGenConn>(keptConnEnt);
                                    for (int g = 0; g < dupItems.Count; g++)
                                        keptBuf.Add(dupItems[g]);
                                    if (keptBuf.Length > 1)
                                        DeduplicateGenBuffer(keptBuf);
                                }
                            }

                            if (!deletedGeneratedEntities.Contains(modConnEnt))
                                deletedGeneratedEntities.Add(modConnEnt);
                        }

                        modIndicesToRemove.Add(k);
                        anyModBufferChanged = true;
                    }

                    for (int r = modIndicesToRemove.Count - 1; r >= 0; r--)
                        modBuffer.RemoveAt(modIndicesToRemove[r]);
                }

                if (anyModBufferChanged && modBuffer.Length == 0)
                {
                    emptyNodes.Add(node);
                }
            }

            private void DeduplicateGenBuffer(DynamicBuffer<TGenConn> genBuffer)
            {
                var seen = new Dictionary<GenConnKey, int>(genBuffer.Length);
                var toRemove = new List<int>();
                for (int i = 0; i < genBuffer.Length; i++)
                {
                    object boxed = genBuffer[i];
                    var key = new GenConnKey(
                        (Entity)_genSourceEntity.GetValue(boxed),
                        (Entity)_genTargetEntity.GetValue(boxed),
                        (int2)_genLaneIndexMap.GetValue(boxed));

                    if (!seen.TryGetValue(key, out int keptIdx))
                    {
                        seen[key] = i;
                        continue;
                    }

                    object keptBoxed = genBuffer[keptIdx];
                    if (_genMethod != null)
                    {
                        int keptMethod = Convert.ToInt32(_genMethod.GetValue(keptBoxed));
                        int dupMethod = Convert.ToInt32(_genMethod.GetValue(boxed));
                        _genMethod.SetValue(keptBoxed, Enum.ToObject(_genMethod.FieldType, keptMethod | dupMethod));
                    }
                    if (_genIsUnsafe != null)
                    {
                        bool keptUnsafe = (bool)_genIsUnsafe.GetValue(keptBoxed);
                        bool dupUnsafe = (bool)_genIsUnsafe.GetValue(boxed);
                        _genIsUnsafe.SetValue(keptBoxed, keptUnsafe && dupUnsafe);
                    }
                    genBuffer[keptIdx] = (TGenConn)keptBoxed;
                    toRemove.Add(i);
                }

                for (int r = toRemove.Count - 1; r >= 0; r--)
                    genBuffer.RemoveAt(toRemove[r]);
            }

            public void RemoveEmptyNodeComponents(EntityManager em, Entity node)
            {
                if (em.HasBuffer<TModConn>(node))
                    em.RemoveComponent<TModConn>(node);
                if (em.HasComponent(node, _modifiedConnectionsType))
                    em.RemoveComponent(node, _modifiedConnectionsType);
            }

            /// <summary>
            /// Formats all ModifiedLaneConnections entries and child GeneratedConnection records on a node.
            /// </summary>
            public string DescribeNode(EntityManager em, Entity node)
            {
                if (!em.HasBuffer<TModConn>(node))
                    return $"ModConn=none(tag={em.HasComponent(node, _modifiedConnectionsType)})";

                var modBuffer = em.GetBuffer<TModConn>(node, isReadOnly: true);
                var sb = new System.Text.StringBuilder();
                sb.Append($"ModConn={modBuffer.Length}(tag={em.HasComponent(node, _modifiedConnectionsType)})");

                FieldInfo methodField = _genMethod;
                FieldInfo unsafeField = _genIsUnsafe;

                for (int i = 0; i < modBuffer.Length; i++)
                {
                    object modObj = modBuffer[i];
                    Entity edgeEnt = (Entity)_modEdgeEntity.GetValue(modObj);
                    int laneIdx = (int)_modLaneIndex.GetValue(modObj);
                    int2 cwg = (int2)_modCarriagewayAndGroup.GetValue(modObj);
                    Entity modConnEnt = (Entity)_modConnectionsEntity.GetValue(modObj);
                    sb.Append($" [{edgeEnt}#{laneIdx} cwg={cwg.x}/{cwg.y} conn={modConnEnt}");

                    if (modConnEnt != Entity.Null && em.Exists(modConnEnt) && em.HasBuffer<TGenConn>(modConnEnt))
                    {
                        bool connDel = em.HasComponent<Deleted>(modConnEnt);
                        var genBuffer = em.GetBuffer<TGenConn>(modConnEnt, isReadOnly: true);
                        sb.Append(connDel ? "(DEL){" : "{");
                        for (int g = 0; g < genBuffer.Length; g++)
                        {
                            object genObj = genBuffer[g];
                            Entity src = _genSourceEntity != null ? (Entity)_genSourceEntity.GetValue(genObj) : Entity.Null;
                            Entity tgt = (Entity)_genTargetEntity.GetValue(genObj);
                            int2 idxMap = (int2)_genLaneIndexMap.GetValue(genObj);
                            int4 cwgMap = (int4)_genCarriagewayAndGroupMap.GetValue(genObj);
                            object methodVal = methodField != null ? methodField.GetValue(genObj) : "?";
                            bool isUnsafe = unsafeField != null && (bool)unsafeField.GetValue(genObj);
                            if (g > 0) sb.Append(", ");
                            sb.Append($"{src}:{idxMap.x}->{tgt}:{idxMap.y}");
                            sb.Append($" cwg=({cwgMap.x}/{cwgMap.y}->{cwgMap.z}/{cwgMap.w}) m={methodVal}{(isUnsafe ? " unsafe" : "")}");
                        }
                        sb.Append("}");
                    }
                    else
                    {
                        sb.Append("(noBuf)");
                    }
                    sb.Append("]");
                }

                return sb.ToString();
            }

            private static float3 MakeSignedEdgePos(EntityManager em, Entity edgeEntity, Entity node, float3 rawPos)
            {
                bool isEnd = em.HasComponent<Edge>(edgeEntity) && em.GetComponentData<Edge>(edgeEntity).m_End == node;
                rawPos.x = isEnd ? rawPos.x : -rawPos.x;
                return rawPos;
            }
        }

        private static bool _probeAttempted;
        private static bool _mod4OrderVerified;
        private static ITrafficBridge _bridge;
        private static Func<EntityManager, Entity, string> _describeNodeFn;
        private static Assembly _trafficAssembly;
        private static Type _trafficLaneSystemType;
        private static Type _syncCustomLaneConnSystemType;
        private static Type _syncCustomPrioritiesSystemType;
        private static Type _trafficDataMigrationSystemType;
        private static readonly HashSet<Entity> _dumpedDropNodes = new HashSet<Entity>();

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
                        _trafficAssembly = trafficAsm;
                        _trafficLaneSystemType = trafficAsm.GetType("Traffic.Systems.TrafficLaneSystem", false);
                        _syncCustomLaneConnSystemType = trafficAsm.GetType("Traffic.Systems.LaneConnections.SyncCustomLaneConnectionsSystem", false);
                        _syncCustomPrioritiesSystemType = trafficAsm.GetType("Traffic.Systems.PrioritySigns.SyncCustomPrioritiesSystem", false);
                        _trafficDataMigrationSystemType = trafficAsm.GetType("Traffic.Systems.ModCompatibility.TrafficDataMigrationSystem", false);
                        Type modConnType = trafficAsm.GetType("Traffic.Components.LaneConnections.ModifiedLaneConnections", false);
                        Type genConnType = trafficAsm.GetType("Traffic.Components.LaneConnections.GeneratedConnection", false);
                        Type lanePrioType = trafficAsm.GetType("Traffic.Components.PrioritySigns.LanePriority", false);
                        Type modTagType = trafficAsm.GetType("Traffic.Components.ModifiedConnections", false);

                        if (_trafficLaneSystemType != null && modConnType != null && genConnType != null &&
                            lanePrioType != null && modTagType != null)
                        {
                            var genericBridge = typeof(ReflectionTrafficBridge<,,>).MakeGenericType(modConnType, genConnType, lanePrioType);
                            _bridge = (ITrafficBridge)Activator.CreateInstance(genericBridge, modTagType);
                            _describeNodeFn = (Func<EntityManager, Entity, string>)Delegate.CreateDelegate(
                                typeof(Func<EntityManager, Entity, string>), _bridge, "DescribeNode");
                            Log.Info($"Traffic mod integration initialized cleanly (version {trafficAsm.GetName().Version}).");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"Traffic mod integration disabled due to reflection mismatch: {ex.Message}");
                    _bridge = null;
                    _describeNodeFn = null;
                    _trafficAssembly = null;
                    _trafficLaneSystemType = null;
                    _syncCustomLaneConnSystemType = null;
                    _syncCustomPrioritiesSystemType = null;
                    _trafficDataMigrationSystemType = null;
                }
            }

            if (_bridge != null && !_mod4OrderVerified && world != null)
            {
                _mod4OrderVerified = true;
                VerifyAndFixModification4Order(world);
            }

            if (_bridge == null || _trafficLaneSystemType == null || world == null)
                return false;

            var trafficSys = world.GetExistingSystemManaged(_trafficLaneSystemType);
            return trafficSys != null && trafficSys.Enabled;
        }

        private static readonly Dictionary<Entity, (Entity OldComp, Entity NewComp)> _currentEdgeCompPairs =
            new Dictionary<Entity, (Entity OldComp, Entity NewComp)>();

        private static void LogEdgeCompMismatch(EntityManager em, Entity edge, int missingOldIdx)
        {
            if (!_currentEdgeCompPairs.TryGetValue(edge, out var pair))
                return;

            string FormatCompLanes(Entity compEnt)
            {
                if (compEnt == Entity.Null || !em.Exists(compEnt) ||
                    !em.HasComponent<NetCompositionData>(compEnt) || !em.HasBuffer<NetCompositionLane>(compEnt))
                    return "invalid";
                var cd = em.GetComponentData<NetCompositionData>(compEnt);
                var lanes = em.GetBuffer<NetCompositionLane>(compEnt, isReadOnly: true);
                var sb = new System.Text.StringBuilder();
                sb.Append($"comp={compEnt},w={cd.m_Width:F2},mid={cd.m_MiddleOffset:F2}:");
                for (int i = 0; i < lanes.Length; i++)
                {
                    var l = lanes[i];
                    if ((l.m_Flags & (LaneFlags.Road | LaneFlags.Track)) == 0)
                        continue;
                    var role = new LaneRole(em, l);
                    sb.Append($" [i={l.m_Index},cwg={l.m_Carriageway}/{l.m_Group},x={l.m_Position.x - cd.m_MiddleOffset:F2},f=({role.MatchFlags}),car={role.HasCar},bike={role.HasBicycle}]");
                }
                return sb.ToString();
            }

            Log.Info($"Traffic drop detail edge={edge} missingOldIdx={missingOldIdx} | OLD: {FormatCompLanes(pair.OldComp)} | NEW: {FormatCompLanes(pair.NewComp)}");
        }

        public static void SyncChangedCompositions(EntityManager em)
        {
            if (_bridge == null || PendingEdgeCompositions.Count == 0)
                return;

            SyncStats stats = new SyncStats { DropDiagLogsRemaining = 12 };
            var edgeMappings = new Dictionary<Entity, Dictionary<int, MappedLaneInfo>>();
            var affectedNodes = new HashSet<Entity>();
            _currentEdgeCompPairs.Clear();

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

                if (!TryBuildEdgeLaneMapping(em, oldCompEdge, newCompEdge, ref stats, out var laneMap))
                    continue;

                edgeMappings[edge] = laneMap;
                _currentEdgeCompPairs[edge] = (oldCompEdge, newCompEdge);
                CollectAffectedNodesForEdge(em, edge, affectedNodes);
            }

            if (edgeMappings.Count == 0)
            {
                _currentEdgeCompPairs.Clear();
                return;
            }

            stats.RemappedEdges = edgeMappings.Count;
            var deletedGeneratedEntities = new List<Entity>();
            var emptyNodes = new List<Entity>();

            foreach (var edge in edgeMappings.Keys)
            {
                _bridge.RemapEdgePriorities(em, edge, edgeMappings[edge], ref stats);
            }

            var updatedNodes = new HashSet<Entity>();
            var dropDiagNodes = new List<Entity>();
            foreach (var node in affectedNodes)
            {
                if (node == Entity.Null || !em.Exists(node) || em.HasComponent<Deleted>(node))
                    continue;

                int prevModUpd = stats.UpdatedNodeEntries;
                int prevGenUpd = stats.UpdatedGenConnections;
                int prevDropMod = stats.DroppedModEntries;
                int prevDropGen = stats.DroppedGenConnections;

                _bridge.RemapNodeConnections(em, node, edgeMappings, deletedGeneratedEntities, emptyNodes, ref stats);

                bool nodeChanged =
                    stats.UpdatedNodeEntries != prevModUpd ||
                    stats.UpdatedGenConnections != prevGenUpd ||
                    stats.DroppedModEntries != prevDropMod ||
                    stats.DroppedGenConnections != prevDropGen;
                if (nodeChanged)
                    updatedNodes.Add(node);

                bool nodeDropped =
                    stats.DroppedModEntries != prevDropMod ||
                    stats.DroppedGenConnections != prevDropGen;
                if (nodeDropped && _dumpedDropNodes.Count < 16 && _dumpedDropNodes.Add(node))
                    dropDiagNodes.Add(node);
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
                updatedNodes.Add(node);
            }

            int addedUpdatedCount = 0;
            foreach (var node in updatedNodes)
            {
                if (em.Exists(node) && !em.HasComponent<Deleted>(node) && !em.HasComponent<Updated>(node))
                {
                    em.AddComponentData(node, default(Updated));
                    addedUpdatedCount++;
                }
            }

            if (_describeNodeFn != null)
            {
                for (int i = 0; i < dropDiagNodes.Count; i++)
                {
                    Entity n = dropDiagNodes[i];
                    if (em.Exists(n))
                        Log.Info($"Traffic node post-remap state for {n}: {_describeNodeFn(em, n)}");
                }
            }

            if (stats.Pass2ShiftedLanes > 0 || stats.UpdatedNodeEntries > 0 || stats.UpdatedGenConnections > 0 ||
                stats.DroppedGenConnections > 0 || stats.DroppedModEntries > 0 || stats.DroppedPriorities > 0 || addedUpdatedCount > 0)
            {
                Log.Info(
                    $"Traffic sync: edges={stats.RemappedEdges}, pass1={stats.Pass1MatchedLanes}, " +
                    $"pass2Shifted={stats.Pass2ShiftedLanes}, modUpdated={stats.UpdatedNodeEntries}, " +
                    $"genUpdated={stats.UpdatedGenConnections}, genDropped={stats.DroppedGenConnections}, " +
                    $"modDropped={stats.DroppedModEntries}, prioDropped={stats.DroppedPriorities}, addedUpdated={addedUpdatedCount}");
            }
        }

        /// <summary>
        /// Verifies that AutoBusLinesTrafficSyncSystem runs in Modification4 strictly after
        /// NetCompositionSystem and strictly before Traffic mod systems and LaneSystem.
        /// If out of order (for example due to load order changes), updates UpdateSystem and refreshes
        /// when safe (outside Modification4 iteration).
        /// </summary>
        private static void VerifyAndFixModification4Order(World world)
        {
            try
            {
                var updateSystem = world.GetExistingSystemManaged<UpdateSystem>();
                if (updateSystem == null)
                    return;

                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                Type usType = typeof(UpdateSystem);
                FieldInfo updatesField = usType.GetField("m_Updates", flags);
                FieldInfo rangesField = usType.GetField("m_UpdateRanges", flags);
                FieldInfo isDirtyField = usType.GetField("m_IsDirty", flags);
                FieldInfo refMapField = usType.GetField("m_RefMap", flags);
                FieldInfo systemsField = usType.GetField("m_Systems", flags);
                MethodInfo refreshMethod = usType.GetMethod("Refresh", flags);
                Type sysDataType = usType.GetNestedType("SystemData", BindingFlags.Public | BindingFlags.NonPublic);
                if (updatesField == null || rangesField == null || isDirtyField == null || sysDataType == null)
                    return;

                FieldInfo sdSystemField = sysDataType.GetField("m_System", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                FieldInfo sdPhaseField = sysDataType.GetField("m_Phase", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (sdSystemField == null || sdPhaseField == null)
                    return;

                if ((bool)isDirtyField.GetValue(updateSystem))
                {
                    if (updateSystem.currentPhase == SystemUpdatePhase.Modification3 ||
                        updateSystem.currentPhase == SystemUpdatePhase.Modification4 ||
                        refreshMethod == null)
                    {
                        // Wait for a clean frame boundary rather than mutating m_Updates mid-phase loop.
                        _mod4OrderVerified = false;
                        return;
                    }
                    refreshMethod.Invoke(updateSystem, null);
                }

                if (!TryInspectMod4Order(updateSystem, updatesField, rangesField, sdSystemField,
                        out int netCompIdx, out int syncIdx, out int syncCount,
                        out int earliestTrafficIdx, out ComponentSystemBase earliestTrafficSys, out string summary))
                {
                    return;
                }

                Log.Info($"Modification4 order check: {summary}");

                bool orderBad =
                    syncCount != 1 ||
                    (netCompIdx >= 0 && syncIdx <= netCompIdx) ||
                    (earliestTrafficIdx >= 0 && syncIdx >= earliestTrafficIdx);
                if (!orderBad || refMapField == null)
                    return;

                var syncSys = world.GetExistingSystemManaged<AutoBusLinesTrafficSyncSystem>();
                var netCompSys = world.GetExistingSystemManaged<Game.Prefabs.NetCompositionSystem>();
                if (syncSys == null)
                    return;

                // Remove any existing Modification4 entries for AutoBusLinesTrafficSyncSystem so it runs exactly once.
                var systemsList = systemsField?.GetValue(updateSystem) as System.Collections.IList;
                RemoveMod4SyncEntries(systemsList, sdPhaseField, sdSystemField, syncSys);

                var refMap = refMapField.GetValue(updateSystem) as System.Collections.IDictionary;
                if (refMap != null)
                {
                    foreach (System.Collections.DictionaryEntry entry in refMap)
                    {
                        RemoveMod4SyncEntries(entry.Value as System.Collections.IList, sdPhaseField, sdSystemField, syncSys);
                    }
                }

                // Prefer anchoring immediately before the earliest Traffic/LaneSystem system whenever that system
                // already executes after NetCompositionSystem; otherwise anchor directly after NetCompositionSystem.
                if (earliestTrafficSys != null && (netCompIdx < 0 || earliestTrafficIdx > netCompIdx))
                {
                    MethodInfo registerMethod = usType.GetMethod("Register",
                        flags,
                        null,
                        new[] { typeof(int), typeof(ComponentSystemBase), typeof(ComponentSystemBase), typeof(SystemUpdatePhase) },
                        null);
                    FieldInfo addIdxField = usType.GetField("m_AddIndex", flags);
                    if (registerMethod != null && addIdxField != null)
                    {
                        int addIdx = (int)addIdxField.GetValue(updateSystem) + 1;
                        addIdxField.SetValue(updateSystem, addIdx);
                        registerMethod.Invoke(updateSystem, new object[] { addIdx - 1000000, syncSys, earliestTrafficSys, SystemUpdatePhase.Modification4 });
                    }
                }
                else if (netCompSys != null)
                {
                    updateSystem.UpdateAfter<AutoBusLinesTrafficSyncSystem, Game.Prefabs.NetCompositionSystem>(SystemUpdatePhase.Modification4);
                }

                if (updateSystem.currentPhase != SystemUpdatePhase.Modification4 && refreshMethod != null)
                {
                    refreshMethod.Invoke(updateSystem, null);
                    if (TryInspectMod4Order(updateSystem, updatesField, rangesField, sdSystemField,
                            out _, out _, out _, out _, out _, out string fixedSummary))
                    {
                        Log.Warn($"Modification4 order corrected -> {fixedSummary}");
                    }
                }
                else
                {
                    Log.Warn("Modification4 order update queued for next frame.");
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Failed to verify Modification4 system order: {ex.Message}");
            }
        }

        private static void RemoveMod4SyncEntries(
            System.Collections.IList list,
            FieldInfo sdPhaseField,
            FieldInfo sdSystemField,
            ComponentSystemBase syncSys)
        {
            if (list == null)
                return;

            for (int i = list.Count - 1; i >= 0; i--)
            {
                object item = list[i];
                if ((SystemUpdatePhase)sdPhaseField.GetValue(item) == SystemUpdatePhase.Modification4 &&
                    ReferenceEquals(sdSystemField.GetValue(item), syncSys))
                {
                    list.RemoveAt(i);
                }
            }
        }

        private static bool IsTrafficOrLaneSystemType(Type st)
        {
            if (st == typeof(Game.Net.LaneSystem))
                return true;
            if (_trafficAssembly != null && st.Assembly == _trafficAssembly)
                return true;
            return st == _trafficDataMigrationSystemType ||
                   st == _syncCustomLaneConnSystemType ||
                   st == _syncCustomPrioritiesSystemType ||
                   st == _trafficLaneSystemType;
        }

        private static bool TryInspectMod4Order(
            UpdateSystem updateSystem,
            FieldInfo updatesField,
            FieldInfo rangesField,
            FieldInfo sdSystemField,
            out int netCompIdx,
            out int syncIdx,
            out int syncCount,
            out int earliestTrafficIdx,
            out ComponentSystemBase earliestTrafficSys,
            out string summary)
        {
            netCompIdx = -1;
            syncIdx = -1;
            syncCount = 0;
            earliestTrafficIdx = -1;
            earliestTrafficSys = null;
            summary = string.Empty;

            var updates = updatesField.GetValue(updateSystem) as System.Collections.IList;
            var ranges = rangesField.GetValue(updateSystem) as List<int2>;
            int mod4Phase = (int)SystemUpdatePhase.Modification4;
            if (updates == null || ranges == null || mod4Phase >= ranges.Count)
                return false;

            int2 range = ranges[mod4Phase];
            var items = new List<string>();
            for (int i = range.x; i < range.y && i < updates.Count; i++)
            {
                var sys = sdSystemField.GetValue(updates[i]) as ComponentSystemBase;
                if (sys == null) continue;
                Type st = sys.GetType();
                int rel = i - range.x;

                bool interesting = false;
                if (st == typeof(Game.Prefabs.NetCompositionSystem))
                {
                    netCompIdx = rel;
                    interesting = true;
                }
                else if (st == typeof(AutoBusLinesTrafficSyncSystem))
                {
                    if (syncIdx < 0) syncIdx = rel;
                    syncCount++;
                    interesting = true;
                }
                else if (IsTrafficOrLaneSystemType(st))
                {
                    if (earliestTrafficIdx < 0)
                    {
                        earliestTrafficIdx = rel;
                        earliestTrafficSys = sys;
                    }
                    interesting = true;
                }

                if (interesting)
                    items.Add($"{st.Name}@{rel}");
            }

            summary = string.Join(" -> ", items);
            return true;
        }

        /// <summary>
        /// Describes a junction node's connected edge compositions, live Car SubLanes, and
        /// Traffic mod custom connections for broken-segment diagnostics.
        /// </summary>
        public static string DescribeNodeState(EntityManager em, Entity node)
        {
            if (node == Entity.Null || !em.Exists(node))
                return "missing";

            var sb = new System.Text.StringBuilder();
            sb.Append($"updated={em.HasComponent<Updated>(node)}");

            if (em.HasBuffer<ConnectedEdge>(node))
            {
                var ce = em.GetBuffer<ConnectedEdge>(node, isReadOnly: true);
                sb.Append($" edges={ce.Length}[");
                for (int i = 0; i < ce.Length; i++)
                {
                    Entity edge = ce[i].m_Edge;
                    Entity start = Entity.Null;
                    Entity end = Entity.Null;
                    Entity comp = Entity.Null;
                    if (em.HasComponent<Edge>(edge))
                    {
                        var ed = em.GetComponentData<Edge>(edge);
                        start = ed.m_Start;
                        end = ed.m_End;
                    }
                    if (em.HasComponent<Composition>(edge))
                        comp = em.GetComponentData<Composition>(edge).m_Edge;
                    if (i > 0) sb.Append("; ");
                    sb.Append($"{edge}({start}->{end},c={comp}");
                    if (comp != Entity.Null && em.HasBuffer<NetCompositionLane>(comp))
                    {
                        var cl = em.GetBuffer<NetCompositionLane>(comp, isReadOnly: true);
                        sb.Append(":{");
                        bool firstL = true;
                        for (int l = 0; l < cl.Length; l++)
                        {
                            var lane = cl[l];
                            if ((lane.m_Flags & LaneFlags.Road) == 0) continue;
                            bool inv = (lane.m_Flags & LaneFlags.Invert) != 0;
                            bool mst = (lane.m_Flags & LaneFlags.Master) != 0;
                            bool slv = (lane.m_Flags & LaneFlags.Slave) != 0;
                            if (!firstL) sb.Append(",");
                            firstL = false;
                            sb.Append($"{lane.m_Index}:{(inv ? "Inv" : "Fwd")}/g{lane.m_Group}{(mst ? "M" : slv ? "S" : "")}");
                        }
                        sb.Append("}");
                    }
                    sb.Append(")");
                }
                sb.Append("]");
            }

            if (em.HasBuffer<Game.Net.SubLane>(node))
            {
                var slBuf = em.GetBuffer<Game.Net.SubLane>(node, isReadOnly: true);
                int carCount = 0;
                int shown = 0;
                sb.Append(" carSubLanes=(");
                for (int i = 0; i < slBuf.Length; i++)
                {
                    Entity sl = slBuf[i].m_SubLane;
                    if (!em.HasComponent<Game.Net.CarLane>(sl) || !em.HasComponent<Game.Net.Lane>(sl))
                        continue;
                    carCount++;
                    if (shown >= 28) continue;
                    shown++;
                    var lData = em.GetComponentData<Game.Net.Lane>(sl);
                    var cData = em.GetComponentData<Game.Net.CarLane>(sl);
                    bool mst = em.HasComponent<MasterLane>(sl);
                    bool slv = em.HasComponent<SlaveLane>(sl);
                    RoadTypes rt = RoadTypes.None;
                    if (em.HasComponent<PrefabRef>(sl))
                    {
                        Entity pr = em.GetComponentData<PrefabRef>(sl).m_Prefab;
                        if (pr != Entity.Null && em.HasComponent<CarLaneData>(pr))
                            rt = em.GetComponentData<CarLaneData>(pr).m_RoadTypes;
                    }
                    if (shown > 1) sb.Append(" ");
                    sb.Append($"[{sl} {lData.m_StartNode.GetOwnerIndex()}:{lData.m_StartNode.GetLaneIndex()}->{lData.m_EndNode.GetOwnerIndex()}:{lData.m_EndNode.GetLaneIndex()} rt={rt}{(mst ? " M" : slv ? " S" : "")} f=({cData.m_Flags})]");
                }
                sb.Append($") totalCar={carCount}");
            }

            if (_describeNodeFn != null)
                sb.Append(" | Traffic: ").Append(_describeNodeFn(em, node));

            return sb.ToString();
        }

        private static bool TryBuildEdgeLaneMapping(
            EntityManager em,
            Entity oldCompEdge,
            Entity newCompEdge,
            ref SyncStats stats,
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

            var oldRoles = new LaneRole[oldLanes.Length];
            var newRoles = new LaneRole[newLanes.Length];
            var oldUsed = new bool[oldLanes.Length];
            var newUsed = new bool[newLanes.Length];

            for (int i = 0; i < oldLanes.Length; i++)
                oldRoles[i] = new LaneRole(em, oldLanes[i]);
            for (int j = 0; j < newLanes.Length; j++)
                newRoles[j] = new LaneRole(em, newLanes[j]);

            laneMap = new Dictionary<int, MappedLaneInfo>(oldLanes.Length);

            // Pass 1: exact (< 1 cm) position match relative to m_MiddleOffset with strict role equality.
            for (int i = 0; i < oldLanes.Length; i++)
            {
                NetCompositionLane oldLane = oldLanes[i];
                if ((oldLane.m_Flags & (LaneFlags.Road | LaneFlags.Track)) == 0)
                    continue;

                float3 oldRelPos = oldLane.m_Position - oldMidRight;
                float bestDistSq = 1e-4f;
                int bestNewIdx = -1;

                for (int j = 0; j < newLanes.Length; j++)
                {
                    if (newUsed[j] || !oldRoles[i].MatchesExact(in newRoles[j]))
                        continue;

                    float3 newRelPos = newLanes[j].m_Position - newMidRight;
                    float distSq = math.lengthsq(oldRelPos - newRelPos);
                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        bestNewIdx = j;
                    }
                }

                if (bestNewIdx >= 0)
                {
                    RecordLaneMatch(oldLanes, newLanes, newRoles, oldUsed, newUsed, laneMap, i, bestNewIdx, markNewUsed: true);
                    stats.Pass1MatchedLanes++;
                }
            }

            // Pass 2a: shifted curb/bay lanes (> 1 cm up to 5.5 m lateral) with exact role equality.
            MatchShiftedLanes(oldLanes, newLanes, oldRoles, newRoles, oldUsed, newUsed, oldMidRight, newMidRight,
                allowCarSplitFallback: false, laneMap, ref stats);

            // Pass 2b: fallback for shared Car+Bicycle curb lanes whose Bicycle bit split off beside a bus bay.
            MatchShiftedLanes(oldLanes, newLanes, oldRoles, newRoles, oldUsed, newUsed, oldMidRight, newMidRight,
                allowCarSplitFallback: true, laneMap, ref stats);

            // Pass 2c: many-to-one fallback for bike-only curb lanes that merged into a shared Car+Bicycle lane.
            MatchMergedBicycleLanes(oldLanes, newLanes, oldRoles, newRoles, oldUsed, newUsed, oldMidRight, newMidRight,
                laneMap, ref stats);

            return true;
        }

        private static void MatchShiftedLanes(
            DynamicBuffer<NetCompositionLane> oldLanes,
            DynamicBuffer<NetCompositionLane> newLanes,
            LaneRole[] oldRoles,
            LaneRole[] newRoles,
            bool[] oldUsed,
            bool[] newUsed,
            float3 oldMidRight,
            float3 newMidRight,
            bool allowCarSplitFallback,
            Dictionary<int, MappedLaneInfo> laneMap,
            ref SyncStats stats)
        {
            const float maxLateralShift = 5.5f;
            const float maxVerticalShift = 2.0f;

            List<ShiftedCandidate> candidates = null;
            for (int i = 0; i < oldLanes.Length; i++)
            {
                if (oldUsed[i] || (oldLanes[i].m_Flags & (LaneFlags.Road | LaneFlags.Track)) == 0)
                    continue;

                float3 oldRelPos = oldLanes[i].m_Position - oldMidRight;
                for (int j = 0; j < newLanes.Length; j++)
                {
                    if (newUsed[j])
                        continue;

                    bool roleOk = allowCarSplitFallback
                        ? oldRoles[i].MatchesCarSplitFallback(in newRoles[j])
                        : oldRoles[i].MatchesExact(in newRoles[j]);
                    if (!roleOk)
                        continue;

                    float3 newRelPos = newLanes[j].m_Position - newMidRight;
                    float dx = math.abs(oldRelPos.x - newRelPos.x);
                    float dy = math.abs(oldRelPos.y - newRelPos.y);
                    if (dx > maxLateralShift || dy > maxVerticalShift)
                        continue;

                    candidates ??= new List<ShiftedCandidate>();
                    candidates.Add(new ShiftedCandidate(i, j, dx + 0.25f * dy));
                }
            }

            if (candidates == null)
                return;

            candidates.Sort((a, b) => a.Score.CompareTo(b.Score));
            for (int c = 0; c < candidates.Count; c++)
            {
                ShiftedCandidate cand = candidates[c];
                if (oldUsed[cand.OldIdx] || newUsed[cand.NewIdx])
                    continue;

                RecordLaneMatch(oldLanes, newLanes, newRoles, oldUsed, newUsed, laneMap, cand.OldIdx, cand.NewIdx, markNewUsed: true);
                stats.Pass2ShiftedLanes++;
            }
        }

        private static void MatchMergedBicycleLanes(
            DynamicBuffer<NetCompositionLane> oldLanes,
            DynamicBuffer<NetCompositionLane> newLanes,
            LaneRole[] oldRoles,
            LaneRole[] newRoles,
            bool[] oldUsed,
            bool[] newUsed,
            float3 oldMidRight,
            float3 newMidRight,
            Dictionary<int, MappedLaneInfo> laneMap,
            ref SyncStats stats)
        {
            const float maxLateralShift = 5.5f;
            const float maxVerticalShift = 2.0f;

            for (int i = 0; i < oldLanes.Length; i++)
            {
                if (oldUsed[i] || !oldRoles[i].IsBicycleOnlyRoad())
                    continue;

                float3 oldRelPos = oldLanes[i].m_Position - oldMidRight;
                int bestNewIdx = -1;
                float bestScore = float.MaxValue;

                for (int j = 0; j < newLanes.Length; j++)
                {
                    if (!oldRoles[i].MatchesMergedBicycleLane(in newRoles[j]))
                        continue;

                    float3 newRelPos = newLanes[j].m_Position - newMidRight;
                    float dx = math.abs(oldRelPos.x - newRelPos.x);
                    float dy = math.abs(oldRelPos.y - newRelPos.y);
                    if (dx > maxLateralShift || dy > maxVerticalShift)
                        continue;

                    float score = dx + 0.25f * dy;
                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestNewIdx = j;
                    }
                }

                if (bestNewIdx >= 0)
                {
                    RecordLaneMatch(oldLanes, newLanes, newRoles, oldUsed, newUsed, laneMap, i, bestNewIdx, markNewUsed: false);
                    stats.Pass2ShiftedLanes++;
                }
            }
        }

        private static void RecordLaneMatch(
            DynamicBuffer<NetCompositionLane> oldLanes,
            DynamicBuffer<NetCompositionLane> newLanes,
            LaneRole[] newRoles,
            bool[] oldUsed,
            bool[] newUsed,
            Dictionary<int, MappedLaneInfo> laneMap,
            int oldIdx,
            int newIdx,
            bool markNewUsed)
        {
            oldUsed[oldIdx] = true;
            if (markNewUsed)
                newUsed[newIdx] = true;
            NetCompositionLane matched = newLanes[newIdx];
            laneMap[oldLanes[oldIdx].m_Index] = new MappedLaneInfo
            {
                Index = matched.m_Index,
                Carriageway = matched.m_Carriageway,
                Group = matched.m_Group,
                RawPosition = matched.m_Position,
                HasCar = newRoles[newIdx].HasCar,
                HasBicycle = newRoles[newIdx].HasBicycle
            };
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

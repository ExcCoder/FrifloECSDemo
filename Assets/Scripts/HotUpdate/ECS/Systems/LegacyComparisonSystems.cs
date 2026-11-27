using System;
using System.Collections.Generic;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using HotUpdate.ECS.Components;
using HotUpdate.ECS.FixedPoint;
using HotUpdate.ECS.Spatial;
using HotUpdate.ECS.State;
using UnityEngine;

namespace HotUpdate.ECS.Systems
{
    /// <summary>
    /// 教学对照组 A：未开启空间哈希网格时的 O(N^2) 暴力全量空间查询器。
    /// 与 SpatialGrid 使用完全一致的 Q48.16 定点数判断，纯粹对比算法复杂度差异（O(N^2) vs O(N)）。
    /// </summary>
    public sealed class BruteForceSpatialIndex
    {
        private readonly List<SpatialEntry> _entries = new(4096);

        public int Count => _entries.Count;

        public void Clear() => _entries.Clear();

        public void Insert(int entityId, in FPVector3 position, FP radius, byte team)
        {
            _entries.Add(new SpatialEntry(entityId, position, radius, team));
        }

        public bool FindNearest(in FPVector3 origin, FP maxRadius, byte targetTeam, int excludeEntityId, out SpatialEntry nearest, out FP nearestSqrDist)
        {
            nearest = default;
            nearestSqrDist = maxRadius * maxRadius;
            bool found = false;

            int n = _entries.Count;
            for (int i = 0; i < n; i++)
            {
                var entry = _entries[i];
                if (entry.Team == targetTeam && entry.EntityId != excludeEntityId)
                {
                    FP dx = entry.Position.X - origin.X;
                    FP dz = entry.Position.Z - origin.Z;
                    FP sqr = dx * dx + dz * dz;
                    if (sqr < nearestSqrDist || (found && sqr == nearestSqrDist && entry.EntityId < nearest.EntityId))
                    {
                        nearestSqrDist = sqr;
                        nearest = entry;
                        found = true;
                    }
                }
            }
            return found;
        }

        public void QueryTeamInRadius(in FPVector3 origin, FP radius, byte targetTeam, List<SpatialEntry> results)
        {
            results.Clear();
            int n = _entries.Count;
            for (int i = 0; i < n; i++)
            {
                var entry = _entries[i];
                if (entry.Team == targetTeam)
                {
                    FP dx = entry.Position.X - origin.X;
                    FP dz = entry.Position.Z - origin.Z;
                    FP eff = radius + entry.Radius;
                    if (dx * dx + dz * dz <= eff * eff)
                    {
                        results.Add(entry);
                    }
                }
            }
        }

        public FPVector3 ComputeSeparation(int selfEntityId, in FPVector3 origin, FP desiredSeparation, byte team, int maxNeighbors = 12)
        {
            FP sqrLimit = desiredSeparation * desiredSeparation;
            FP pushX = FP.Zero;
            FP pushZ = FP.Zero;
            int checkedCount = 0;

            int n = _entries.Count;
            for (int i = 0; i < n; i++)
            {
                var entry = _entries[i];
                if (entry.Team == team && entry.EntityId != selfEntityId)
                {
                    FP dx = origin.X - entry.Position.X;
                    FP dz = origin.Z - entry.Position.Z;
                    FP sqr = dx * dx + dz * dz;
                    if (sqr < sqrLimit)
                    {
                        if (sqr.RawValue <= 8L)
                        {
                            FP angle = FP.FromRaw((selfEntityId * 106039L) % FP.TwoPiRaw);
                            pushX += FPMath.Cos(angle);
                            pushZ += FPMath.Sin(angle);
                        }
                        else
                        {
                            FP dist = FPMath.Sqrt(sqr);
                            FP weight = (desiredSeparation - dist) / desiredSeparation;
                            pushX += (dx / dist) * weight;
                            pushZ += (dz / dist) * weight;
                        }

                        if (++checkedCount >= maxNeighbors)
                        {
                            return new FPVector3(pushX, FP.Zero, pushZ);
                        }
                    }
                }
            }
            return new FPVector3(pushX, FP.Zero, pushZ);
        }
    }

    /// <summary>
    /// 教学对照组 B（渲染反面教材）：每实体实例化一个真实 Unity Cube GameObject 并逐帧同步 Transform + MaterialPropertyBlock。
    /// 用于与 CubeInstancedRenderSystem（0 GameObject）直观对比帧率与 DrawCall / Transform 同步开销。
    /// </summary>
    public sealed class GameObjectCubeRenderSystem : QuerySystem<UnitTransform, CubeVisual>, IDisposable
    {
        private static readonly int ColorPropId = Shader.PropertyToID("_Color");
        private static readonly Color StunTint = new(0.98f, 0.88f, 0.20f);
        private static readonly Color SlowTint = new(0.35f, 0.85f, 1.0f);
        private static readonly Color ArmorTint = new(1.0f, 0.93f, 0.45f);

        private readonly BattleWorld _world;
        private readonly Dictionary<int, (GameObject Go, UnityEngine.Transform Tr, MeshRenderer Renderer)> _views = new(2048);
        private readonly HashSet<int> _aliveIds = new(2048);
        private readonly List<int> _removeIds = new(256);
        private readonly MaterialPropertyBlock _mpb = new();

        private GameObject _rootGo;
        private Material _sharedMat;
        private bool _disposed;

        public int ActiveGameObjectCount => _views.Count;

        public GameObjectCubeRenderSystem(BattleWorld world)
        {
            _world = world;
            Filter.WithoutAllTags(Tags.Get<DeadTag>());

            _rootGo = new GameObject("LegacyUnitGameObjects");
            Shader shader = Shader.Find("Demo/InstancedCubeLit") ?? Shader.Find("Standard");
            _sharedMat = new Material(shader) { name = "Legacy_CubeMat", enableInstancing = false };
        }

        protected override void OnUpdate()
        {
            if (_disposed) return;

            _aliveIds.Clear();
            bool cullingEnabled = _world.Config.UseFrustumCulling;

            Query.ForEachEntity((ref UnitTransform tr, ref CubeVisual vis, Entity entity) =>
            {
                int id = entity.Id;
                _aliveIds.Add(id);

                if (!_views.TryGetValue(id, out var view))
                {
                    GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    go.name = $"Unit_{id}";
                    go.transform.SetParent(_rootGo.transform, false);
                    var col = go.GetComponent<Collider>();
                    if (col != null) DestroyObj(col);
                    var mr = go.GetComponent<MeshRenderer>();
                    mr.sharedMaterial = _sharedMat;
                    view = (go, go.transform, mr);
                    _views[id] = view;
                }

                bool visible = !cullingEnabled || vis.IsVisible;
                if (view.Renderer.enabled != visible)
                {
                    view.Renderer.enabled = visible;
                }
                if (!visible) return;

                float yaw = tr.Yaw.ToFloat();
                view.Tr.SetPositionAndRotation(tr.Position.ToVector3(), yaw != 0f ? Quaternion.Euler(0f, yaw, 0f) : Quaternion.identity);
                view.Tr.localScale = tr.Scale.ToVector3();

                Color c = vis.BaseColor;
                if (entity.TryGetComponent<UnitState>(out var st))
                {
                    if (st.Has(UnitStateType.HitFlash)) c = Color.white;
                    else if (st.Has(UnitStateType.Stunned)) c = StunTint;
                    else if (st.Has(UnitStateType.Slowed)) c = Color.Lerp(c, SlowTint, 0.55f);
                    else if (st.Has(UnitStateType.SuperArmor)) c = Color.Lerp(c, ArmorTint, 0.35f);
                }

                view.Renderer.GetPropertyBlock(_mpb);
                _mpb.SetColor(ColorPropId, c);
                view.Renderer.SetPropertyBlock(_mpb);
            });

            // 清理已死亡/离开实体的 GameObject
            _removeIds.Clear();
            foreach (var kv in _views)
            {
                if (!_aliveIds.Contains(kv.Key))
                {
                    _removeIds.Add(kv.Key);
                }
            }
            for (int i = 0; i < _removeIds.Count; i++)
            {
                int id = _removeIds[i];
                if (_views.TryGetValue(id, out var view))
                {
                    if (view.Go != null) DestroyObj(view.Go);
                    _views.Remove(id);
                }
            }
            _removeIds.Clear();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _views.Clear();
            if (_rootGo != null)
            {
                DestroyObj(_rootGo);
                _rootGo = null;
            }
            if (_sharedMat != null)
            {
                DestroyObj(_sharedMat);
                _sharedMat = null;
            }
        }

        private static void DestroyObj(UnityEngine.Object obj)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(obj);
            else UnityEngine.Object.DestroyImmediate(obj);
        }
    }

    /// <summary>
    /// 教学对照组 C（旧版响应式查询反面教材）：
    /// 忠实还原笔记中的旧实现——每帧 new HashSet / new List + 逐实体闭包挂载 OnComponentChanged + 全量比对订阅字典。
    /// 挂在所有带 CombatStats 的千级单位上，用于直观演示每帧 GC Alloc 与 O(N) 订阅维护开销。
    /// </summary>
    public sealed class LegacyPerEntityReactiveSystem : QuerySystem<CombatStats>, IDisposable
    {
        private readonly Dictionary<int, Action<ComponentChanged>> _entityHandlers = new();
        private readonly Dictionary<int, Entity> _subscribedEntities = new();
        private readonly HashSet<int> _dirtyEntities = new();

        public int ProcessedCount { get; private set; }

        protected override void OnUpdate()
        {
            // 故意保留反面教材写法：每帧 new HashSet + 两次遍历 + 逐实体闭包 + new List
            var matched = new HashSet<int>();
            Query.ForEachEntity((ref CombatStats _, Entity e) => matched.Add(e.Id));

            Query.ForEachEntity((ref CombatStats _, Entity e) =>
            {
                if (!_subscribedEntities.ContainsKey(e.Id))
                {
                    Action<ComponentChanged> handler = ev => OnEntityComponentChanged(e, ev);
                    e.OnComponentChanged += handler;
                    _entityHandlers[e.Id] = handler;
                    _subscribedEntities[e.Id] = e;
                }
            });

            var toRemove = new List<int>();
            foreach (var kv in _subscribedEntities)
            {
                if (!matched.Contains(kv.Key)) toRemove.Add(kv.Key);
            }
            for (int i = 0; i < toRemove.Count; i++)
            {
                int id = toRemove[i];
                if (_entityHandlers.TryGetValue(id, out var h) && _subscribedEntities.TryGetValue(id, out var ent))
                {
                    if (!ent.IsNull) ent.OnComponentChanged -= h;
                }
                _entityHandlers.Remove(id);
                _subscribedEntities.Remove(id);
                _dirtyEntities.Remove(id);
            }

            if (_dirtyEntities.Count > 0)
            {
                var ids = new List<int>(_dirtyEntities);
                _dirtyEntities.Clear();
                ProcessedCount += ids.Count;
            }
        }

        private void OnEntityComponentChanged(Entity entity, ComponentChanged changed)
        {
            if (changed.Action == ComponentChangedAction.Update || changed.Action == ComponentChangedAction.Add)
            {
                if (changed.Type == typeof(CombatStats))
                {
                    _dirtyEntities.Add(entity.Id);
                }
            }
        }

        public void Dispose()
        {
            foreach (var kv in _subscribedEntities)
            {
                if (!kv.Value.IsNull && _entityHandlers.TryGetValue(kv.Key, out var h))
                {
                    kv.Value.OnComponentChanged -= h;
                }
            }
            _entityHandlers.Clear();
            _subscribedEntities.Clear();
            _dirtyEntities.Clear();
        }
    }

    /// <summary>
    /// 教学实验组 C（优化版响应式查询）：
    /// 同样监控所有千级单位的 CombatStats，但基于 ReactiveDirtyHub 分桶脏中心，稳态每帧 O(K)、零 GC、零逐实体订阅。
    /// </summary>
    public sealed class BucketedCombatReactiveSystem : ReactiveQuerySystem<CombatStats>
    {
        public int ProcessedCount { get; private set; }
        protected override bool TriggerOnEntityEnter => false;

        protected override void OnReactiveUpdateEntity(Entity entity, Type componentType)
        {
            ProcessedCount++;
        }
    }

    /// <summary>
    /// 教学对照组 D（旧版实体清理反面教材）：
    /// 还原笔记里的公司旧实现——额外维护 List&lt;Entity&gt;，每帧倒序 TryGetEntityById 全量扫一遍并调用 RemoveAt(i)。
    /// </summary>
    public sealed class LegacyEntityListCleanupSystem : BaseSystem
    {
        private readonly BattleWorld _world;
        private readonly List<Entity> _legacyEntities = new(4096);
        private readonly HashSet<int> _knownIds = new(4096);

        public int TrackedListCount => _legacyEntities.Count;

        public LegacyEntityListCleanupSystem(BattleWorld world)
        {
            _world = world;
        }

        protected override void OnUpdateGroup()
        {
            var store = _world.Store;

            // 模拟旧架构同步收集全部实体进外部 List
            foreach (var ent in store.Entities)
            {
                if (_knownIds.Add(ent.Id))
                {
                    _legacyEntities.Add(ent);
                }
            }

            // 反面教材：每帧倒序全量 TryGetEntityById + RemoveAt(i)（删除密集时退化为 O(N^2)）
            for (int i = _legacyEntities.Count - 1; i >= 0; --i)
            {
                int id = _legacyEntities[i].Id;
                if (!store.TryGetEntityById(id, out var _))
                {
                    _knownIds.Remove(id);
                    _legacyEntities.RemoveAt(i);
                }
            }
        }
    }
}

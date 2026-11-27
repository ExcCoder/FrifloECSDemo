using System;
using System.Collections.Generic;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;

namespace HotUpdate.ECS
{
    /// <summary>
    /// 供 <see cref="ReactiveQuerySystem{T1}"/> 及多组件泛型版本复用的 O(K) 响应式分桶核心。
    /// 升级特性：
    /// 1. 基于 <see cref="ReactiveDirtyHub"/>（Store 级按 ComponentType 分桶），彻底取消逐实体 OnComponentChanged 订阅与退订；
    /// 2. 稳态帧不再全量遍历 Query.Entities，仅消费增量脏桶并调用 Query.IsMatch O(1) 校验，复杂度从 O(N) 降至 O(K)；
    /// 3. 全程复用内部容器，每帧零 GC 分配；
    /// 4. 支持同一实体在单帧内多个受监控组件同时变更而不互相覆盖，以及多 EntityStore 隔离。
    /// </summary>
    internal sealed class ReactiveQueryCore
    {
        private sealed class StoreState
        {
            public readonly ReactiveDirtyHub Hub;
            public readonly Dictionary<int, Entity> TrackedEntities = new();
            public long LastConsumedSequence;
            public bool Initialized;

            public StoreState(EntityStore store)
            {
                Hub = ReactiveDirtyHub.GetOrCreate(store);
            }
        }

        private readonly struct DirtyKey : IEquatable<DirtyKey>
        {
            public readonly int EntityId;
            public readonly Type ComponentType;

            public DirtyKey(int entityId, Type componentType)
            {
                EntityId = entityId;
                ComponentType = componentType;
            }

            public bool Equals(DirtyKey other) => EntityId == other.EntityId && ComponentType == other.ComponentType;
            public override bool Equals(object obj) => obj is DirtyKey other && Equals(other);
            public override int GetHashCode() => HashCode.Combine(EntityId, ComponentType);
        }

        private readonly struct DirtyDispatchItem
        {
            public readonly int EntityId;
            public readonly Type ComponentType;
            public readonly ComponentChangedAction Action;

            public DirtyDispatchItem(int entityId, Type componentType, ComponentChangedAction action)
            {
                EntityId = entityId;
                ComponentType = componentType;
                Action = action;
            }
        }

        private readonly Type[] _watchedTypes;
        private readonly Func<Type, bool> _componentFilter;
        private readonly Dictionary<EntityStore, StoreState> _storeStates = new();

        private readonly HashSet<DirtyKey> _dirtyDedupe = new();
        private readonly List<DirtyDispatchItem> _dispatchBuffer = new();
        private readonly HashSet<int> _exitCandidates = new();
        private readonly List<int> _exitIds = new();

        public int TrackedCount
        {
            get
            {
                int total = 0;
                foreach (var kv in _storeStates)
                {
                    total += kv.Value.TrackedEntities.Count;
                }
                return total;
            }
        }

        public ReactiveQueryCore(Type[] watchedTypes, Func<Type, bool> componentFilter)
        {
            _watchedTypes = watchedTypes;
            _componentFilter = componentFilter;
        }

        public bool MatchesDefaultWatchedTypes(Type componentType)
        {
            for (int i = 0; i < _watchedTypes.Length; i++)
            {
                if (_watchedTypes[i] == componentType)
                {
                    return true;
                }
            }
            return false;
        }

        public void MarkDirty(Entity entity, Type componentType = null, ComponentChangedAction action = ComponentChangedAction.Update)
        {
            if (entity.IsNull) return;
            var hub = ReactiveDirtyHub.GetOrCreate(entity.Store);
            if (componentType != null)
            {
                hub.MarkDirty(entity.Id, componentType, action);
            }
            else
            {
                for (int i = 0; i < _watchedTypes.Length; i++)
                {
                    hub.MarkDirty(entity.Id, _watchedTypes[i], action);
                }
            }
        }

        public void Update(
            ArchetypeQuery query,
            bool triggerOnEntityEnter,
            Action<Entity> onEntityEnter,
            Action<Entity> onEntityExit,
            Action<Entity, Type, ComponentChangedAction> onReactiveUpdate)
        {
            var store = query.Store;
            if (!_storeStates.TryGetValue(store, out var state))
            {
                state = new StoreState(store);
                _storeStates[store] = state;
            }

            _dirtyDedupe.Clear();
            _dispatchBuffer.Clear();

            // 1. 首次接管该 Store：一次性收录当前已匹配实体（此后稳态帧不再全量扫描 Query.Entities）
            if (!state.Initialized)
            {
                state.Initialized = true;
                state.LastConsumedSequence = state.Hub.CurrentSequence;

                foreach (var entity in query.Entities)
                {
                    state.TrackedEntities[entity.Id] = entity;
                    onEntityEnter(entity);

                    if (triggerOnEntityEnter)
                    {
                        for (int i = 0; i < _watchedTypes.Length; i++)
                        {
                            EnqueueDispatch(entity.Id, _watchedTypes[i], ComponentChangedAction.Add);
                        }
                    }
                }

                FlushDispatch(store, query, state, onReactiveUpdate);
                return;
            }

            long lastSeq = state.LastConsumedSequence;
            long maxSeq = lastSeq;

            // 2. O(K) 处理退出/销毁候选（仅扫受监控组件的移除桶与 Store 删除桶）
            _exitCandidates.Clear();
            _exitIds.Clear();

            for (int t = 0; t < _watchedTypes.Length; t++)
            {
                if (!state.Hub.TryGetBucket(_watchedTypes[t], out var bucket)) continue;
                var removed = bucket.RemovedRecords;
                for (int i = removed.Count - 1; i >= 0; i--)
                {
                    var rec = removed[i];
                    if (rec.Sequence <= lastSeq) break;
                    if (rec.Sequence > maxSeq) maxSeq = rec.Sequence;
                    if (state.TrackedEntities.ContainsKey(rec.EntityId) && _exitCandidates.Add(rec.EntityId))
                    {
                        _exitIds.Add(rec.EntityId);
                    }
                }
            }

            var deleted = state.Hub.DeletedRecords;
            for (int i = deleted.Count - 1; i >= 0; i--)
            {
                var rec = deleted[i];
                if (rec.Sequence <= lastSeq) break;
                if (rec.Sequence > maxSeq) maxSeq = rec.Sequence;
                if (state.TrackedEntities.ContainsKey(rec.EntityId) && _exitCandidates.Add(rec.EntityId))
                {
                    _exitIds.Add(rec.EntityId);
                }
            }

            for (int i = 0; i < _exitIds.Count; i++)
            {
                int id = _exitIds[i];
                if (!state.TrackedEntities.TryGetValue(id, out var cachedEntity)) continue;

                bool stillMatched = store.TryGetEntityById(id, out var liveEntity) &&
                                    !liveEntity.IsNull &&
                                    query.IsMatch(liveEntity.Archetype.ComponentTypes, liveEntity.Tags);
                if (!stillMatched)
                {
                    state.TrackedEntities.Remove(id);
                    onEntityExit(cachedEntity);
                }
            }

            // 3. O(K) 仅读取受监控组件桶中的增量脏记录
            for (int t = 0; t < _watchedTypes.Length; t++)
            {
                var watchedType = _watchedTypes[t];
                if (!_componentFilter(watchedType)) continue;
                if (!state.Hub.TryGetBucket(watchedType, out var bucket)) continue;

                var changed = bucket.ChangedRecords;
                int startIdx = FindFirstIndexAfter(changed, lastSeq);
                for (int i = startIdx; i < changed.Count; i++)
                {
                    var rec = changed[i];
                    if (rec.Sequence > maxSeq) maxSeq = rec.Sequence;

                    if (!store.TryGetEntityById(rec.EntityId, out var liveEntity) || liveEntity.IsNull)
                    {
                        continue;
                    }

                    if (!query.IsMatch(liveEntity.Archetype.ComponentTypes, liveEntity.Tags))
                    {
                        continue;
                    }

                    bool isNewlyEntered = !state.TrackedEntities.ContainsKey(rec.EntityId);
                    if (isNewlyEntered)
                    {
                        state.TrackedEntities[rec.EntityId] = liveEntity;
                        onEntityEnter(liveEntity);

                        if (triggerOnEntityEnter)
                        {
                            for (int w = 0; w < _watchedTypes.Length; w++)
                            {
                                EnqueueDispatch(rec.EntityId, _watchedTypes[w], ComponentChangedAction.Add);
                            }
                        }
                    }
                    else
                    {
                        EnqueueDispatch(rec.EntityId, watchedType, rec.Action);
                    }
                }
            }

            state.LastConsumedSequence = maxSeq;

            // 4. 派发本帧脏记录
            FlushDispatch(store, query, state, onReactiveUpdate);
        }

        public void ClearSubscriptions()
        {
            _storeStates.Clear();
            _dirtyDedupe.Clear();
            _dispatchBuffer.Clear();
            _exitCandidates.Clear();
            _exitIds.Clear();
        }

        private void EnqueueDispatch(int entityId, Type componentType, ComponentChangedAction action)
        {
            if (_dirtyDedupe.Add(new DirtyKey(entityId, componentType)))
            {
                _dispatchBuffer.Add(new DirtyDispatchItem(entityId, componentType, action));
            }
        }

        private void FlushDispatch(
            EntityStore store,
            ArchetypeQuery query,
            StoreState state,
            Action<Entity, Type, ComponentChangedAction> onReactiveUpdate)
        {
            if (_dispatchBuffer.Count == 0) return;

            for (int i = 0; i < _dispatchBuffer.Count; i++)
            {
                var item = _dispatchBuffer[i];
                if (state.TrackedEntities.ContainsKey(item.EntityId) &&
                    store.TryGetEntityById(item.EntityId, out var liveEntity) &&
                    !liveEntity.IsNull &&
                    query.IsMatch(liveEntity.Archetype.ComponentTypes, liveEntity.Tags))
                {
                    onReactiveUpdate(liveEntity, item.ComponentType, item.Action);
                }
            }

            _dispatchBuffer.Clear();
            _dirtyDedupe.Clear();
        }

        private static int FindFirstIndexAfter(List<DirtyComponentRecord> list, long lastSeq)
        {
            int lo = 0;
            int hi = list.Count;
            while (lo < hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                if (list[mid].Sequence <= lastSeq)
                    lo = mid + 1;
                else
                    hi = mid;
            }
            return lo;
        }
    }

    /// <summary>
    /// 单组件响应式查询系统基类（基于 Store 级分桶脏中心，O(K) 零逐实体订阅与零 GC）。
    /// </summary>
    public abstract class ReactiveQuerySystem<T1> : QuerySystem<T1>
        where T1 : struct, IComponent
    {
        private readonly ReactiveQueryCore _core;
        private readonly Action<Entity> _onEntityEnter;
        private readonly Action<Entity> _onEntityExit;
        private readonly Action<Entity, Type, ComponentChangedAction> _onReactiveUpdate;

        protected ReactiveQuerySystem()
        {
            _core = new ReactiveQueryCore(new[] { typeof(T1) }, ShouldReactToComponent);
            _onEntityEnter = OnEntityEnter;
            _onEntityExit = OnEntityExit;
            _onReactiveUpdate = OnReactiveUpdateEntity;
        }

        protected virtual bool TriggerOnEntityEnter => true;
        public int SubscribedEntityCount => _core.TrackedCount;

        protected override void OnUpdate()
        {
            _core.Update(Query, TriggerOnEntityEnter, _onEntityEnter, _onEntityExit, _onReactiveUpdate);
            OnFrameUpdate();
        }

        protected virtual bool ShouldReactToComponent(Type componentType) => _core.MatchesDefaultWatchedTypes(componentType);
        protected virtual void OnEntityEnter(Entity entity) { }
        protected virtual void OnEntityExit(Entity entity) { }
        protected virtual void OnReactiveUpdateEntity(Entity entity, Type componentType) { }
        protected virtual void OnReactiveUpdateEntity(Entity entity, Type componentType, ComponentChangedAction action)
        {
            OnReactiveUpdateEntity(entity, componentType);
        }
        protected virtual void OnFrameUpdate() { }
        protected void MarkDirty(Entity entity, Type componentType = null) => _core.MarkDirty(entity, componentType);
        public void ClearSubscriptions() => _core.ClearSubscriptions();
    }

    /// <summary>双组件响应式查询系统基类。</summary>
    public abstract class ReactiveQuerySystem<T1, T2> : QuerySystem<T1, T2>
        where T1 : struct, IComponent
        where T2 : struct, IComponent
    {
        private readonly ReactiveQueryCore _core;
        private readonly Action<Entity> _onEntityEnter;
        private readonly Action<Entity> _onEntityExit;
        private readonly Action<Entity, Type, ComponentChangedAction> _onReactiveUpdate;

        protected ReactiveQuerySystem()
        {
            _core = new ReactiveQueryCore(new[] { typeof(T1), typeof(T2) }, ShouldReactToComponent);
            _onEntityEnter = OnEntityEnter;
            _onEntityExit = OnEntityExit;
            _onReactiveUpdate = OnReactiveUpdateEntity;
        }

        protected virtual bool TriggerOnEntityEnter => true;
        public int SubscribedEntityCount => _core.TrackedCount;

        protected override void OnUpdate()
        {
            _core.Update(Query, TriggerOnEntityEnter, _onEntityEnter, _onEntityExit, _onReactiveUpdate);
            OnFrameUpdate();
        }

        protected virtual bool ShouldReactToComponent(Type componentType) => _core.MatchesDefaultWatchedTypes(componentType);
        protected virtual void OnEntityEnter(Entity entity) { }
        protected virtual void OnEntityExit(Entity entity) { }
        protected virtual void OnReactiveUpdateEntity(Entity entity, Type componentType) { }
        protected virtual void OnReactiveUpdateEntity(Entity entity, Type componentType, ComponentChangedAction action)
        {
            OnReactiveUpdateEntity(entity, componentType);
        }
        protected virtual void OnFrameUpdate() { }
        protected void MarkDirty(Entity entity, Type componentType = null) => _core.MarkDirty(entity, componentType);
        public void ClearSubscriptions() => _core.ClearSubscriptions();
    }

    /// <summary>三组件响应式查询系统基类。</summary>
    public abstract class ReactiveQuerySystem<T1, T2, T3> : QuerySystem<T1, T2, T3>
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
    {
        private readonly ReactiveQueryCore _core;
        private readonly Action<Entity> _onEntityEnter;
        private readonly Action<Entity> _onEntityExit;
        private readonly Action<Entity, Type, ComponentChangedAction> _onReactiveUpdate;

        protected ReactiveQuerySystem()
        {
            _core = new ReactiveQueryCore(new[] { typeof(T1), typeof(T2), typeof(T3) }, ShouldReactToComponent);
            _onEntityEnter = OnEntityEnter;
            _onEntityExit = OnEntityExit;
            _onReactiveUpdate = OnReactiveUpdateEntity;
        }

        protected virtual bool TriggerOnEntityEnter => true;
        public int SubscribedEntityCount => _core.TrackedCount;

        protected override void OnUpdate()
        {
            _core.Update(Query, TriggerOnEntityEnter, _onEntityEnter, _onEntityExit, _onReactiveUpdate);
            OnFrameUpdate();
        }

        protected virtual bool ShouldReactToComponent(Type componentType) => _core.MatchesDefaultWatchedTypes(componentType);
        protected virtual void OnEntityEnter(Entity entity) { }
        protected virtual void OnEntityExit(Entity entity) { }
        protected virtual void OnReactiveUpdateEntity(Entity entity, Type componentType) { }
        protected virtual void OnReactiveUpdateEntity(Entity entity, Type componentType, ComponentChangedAction action)
        {
            OnReactiveUpdateEntity(entity, componentType);
        }
        protected virtual void OnFrameUpdate() { }
        protected void MarkDirty(Entity entity, Type componentType = null) => _core.MarkDirty(entity, componentType);
        public void ClearSubscriptions() => _core.ClearSubscriptions();
    }

    /// <summary>四组件响应式查询系统基类。</summary>
    public abstract class ReactiveQuerySystem<T1, T2, T3, T4> : QuerySystem<T1, T2, T3, T4>
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent
    {
        private readonly ReactiveQueryCore _core;
        private readonly Action<Entity> _onEntityEnter;
        private readonly Action<Entity> _onEntityExit;
        private readonly Action<Entity, Type, ComponentChangedAction> _onReactiveUpdate;

        protected ReactiveQuerySystem()
        {
            _core = new ReactiveQueryCore(new[] { typeof(T1), typeof(T2), typeof(T3), typeof(T4) }, ShouldReactToComponent);
            _onEntityEnter = OnEntityEnter;
            _onEntityExit = OnEntityExit;
            _onReactiveUpdate = OnReactiveUpdateEntity;
        }

        protected virtual bool TriggerOnEntityEnter => true;
        public int SubscribedEntityCount => _core.TrackedCount;

        protected override void OnUpdate()
        {
            _core.Update(Query, TriggerOnEntityEnter, _onEntityEnter, _onEntityExit, _onReactiveUpdate);
            OnFrameUpdate();
        }

        protected virtual bool ShouldReactToComponent(Type componentType) => _core.MatchesDefaultWatchedTypes(componentType);
        protected virtual void OnEntityEnter(Entity entity) { }
        protected virtual void OnEntityExit(Entity entity) { }
        protected virtual void OnReactiveUpdateEntity(Entity entity, Type componentType) { }
        protected virtual void OnReactiveUpdateEntity(Entity entity, Type componentType, ComponentChangedAction action)
        {
            OnReactiveUpdateEntity(entity, componentType);
        }
        protected virtual void OnFrameUpdate() { }
        protected void MarkDirty(Entity entity, Type componentType = null) => _core.MarkDirty(entity, componentType);
        public void ClearSubscriptions() => _core.ClearSubscriptions();
    }

    /// <summary>五组件响应式查询系统基类。</summary>
    public abstract class ReactiveQuerySystem<T1, T2, T3, T4, T5> : QuerySystem<T1, T2, T3, T4, T5>
        where T1 : struct, IComponent
        where T2 : struct, IComponent
        where T3 : struct, IComponent
        where T4 : struct, IComponent
        where T5 : struct, IComponent
    {
        private readonly ReactiveQueryCore _core;
        private readonly Action<Entity> _onEntityEnter;
        private readonly Action<Entity> _onEntityExit;
        private readonly Action<Entity, Type, ComponentChangedAction> _onReactiveUpdate;

        protected ReactiveQuerySystem()
        {
            _core = new ReactiveQueryCore(new[] { typeof(T1), typeof(T2), typeof(T3), typeof(T4), typeof(T5) }, ShouldReactToComponent);
            _onEntityEnter = OnEntityEnter;
            _onEntityExit = OnEntityExit;
            _onReactiveUpdate = OnReactiveUpdateEntity;
        }

        protected virtual bool TriggerOnEntityEnter => true;
        public int SubscribedEntityCount => _core.TrackedCount;

        protected override void OnUpdate()
        {
            _core.Update(Query, TriggerOnEntityEnter, _onEntityEnter, _onEntityExit, _onReactiveUpdate);
            OnFrameUpdate();
        }

        protected virtual bool ShouldReactToComponent(Type componentType) => _core.MatchesDefaultWatchedTypes(componentType);
        protected virtual void OnEntityEnter(Entity entity) { }
        protected virtual void OnEntityExit(Entity entity) { }
        protected virtual void OnReactiveUpdateEntity(Entity entity, Type componentType) { }
        protected virtual void OnReactiveUpdateEntity(Entity entity, Type componentType, ComponentChangedAction action)
        {
            OnReactiveUpdateEntity(entity, componentType);
        }
        protected virtual void OnFrameUpdate() { }
        protected void MarkDirty(Entity entity, Type componentType = null) => _core.MarkDirty(entity, componentType);
        public void ClearSubscriptions() => _core.ClearSubscriptions();
    }
}

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Friflo.Engine.ECS;

namespace HotUpdate.ECS
{
    public readonly struct DirtyComponentRecord
    {
        public readonly long Sequence;
        public readonly int EntityId;
        public readonly ComponentChangedAction Action;

        public DirtyComponentRecord(long sequence, int entityId, ComponentChangedAction action)
        {
            Sequence = sequence;
            EntityId = entityId;
            Action = action;
        }
    }

    public readonly struct StructuralExitRecord
    {
        public readonly long Sequence;
        public readonly int EntityId;

        public StructuralExitRecord(long sequence, int entityId)
        {
            Sequence = sequence;
            EntityId = entityId;
        }
    }

    /// <summary>
    /// Store 级按 ComponentType 分桶的全局脏标记中心（每 EntityStore 仅挂 1 组监听）。
    /// - 彻底省去逐实体订阅/退订和每帧全量扫描字典的开销；
    /// - 采用单调递增 Sequence 游标，支持多个 ReactiveQuerySystem 独立消费同一组件桶而不互相清空；
    /// - 使用 ConditionalWeakTable 与 EntityStore 生命周期自动绑定，无静态内存泄漏。
    /// </summary>
    public sealed class ReactiveDirtyHub
    {
        private static readonly ConditionalWeakTable<EntityStore, ReactiveDirtyHub> StoreHubs = new();

        public sealed class TypeBucket
        {
            public readonly List<DirtyComponentRecord> ChangedRecords = new(64);
            public readonly List<StructuralExitRecord> RemovedRecords = new(32);
        }

        private readonly EntityStore _store;
        private readonly Dictionary<Type, TypeBucket> _buckets = new();
        private readonly List<StructuralExitRecord> _deletedRecords = new(64);
        private long _sequence;

        public long CurrentSequence => _sequence;
        public List<StructuralExitRecord> DeletedRecords => _deletedRecords;

        private ReactiveDirtyHub(EntityStore store)
        {
            _store = store;
            _store.OnComponentAdded += OnComponentAddedOrUpdated;
            _store.OnComponentRemoved += OnComponentRemoved;
            _store.OnEntityDelete += OnEntityDelete;
        }

        public static ReactiveDirtyHub GetOrCreate(EntityStore store)
        {
            if (store == null) throw new ArgumentNullException(nameof(store));
            return StoreHubs.GetValue(store, static s => new ReactiveDirtyHub(s));
        }

        public TypeBucket GetOrCreateBucket(Type componentType)
        {
            if (!_buckets.TryGetValue(componentType, out var bucket))
            {
                bucket = new TypeBucket();
                _buckets[componentType] = bucket;
            }
            return bucket;
        }

        public bool TryGetBucket(Type componentType, out TypeBucket bucket)
        {
            return _buckets.TryGetValue(componentType, out bucket);
        }

        /// <summary>
        /// 手动向指定组件桶推入一条脏记录（适用于 ref 原地修改组件字段未触发 OnComponentAdded 的场景）。
        /// </summary>
        public void MarkDirty(int entityId, Type componentType, ComponentChangedAction action = ComponentChangedAction.Update)
        {
            var bucket = GetOrCreateBucket(componentType);
            bucket.ChangedRecords.Add(new DirtyComponentRecord(++_sequence, entityId, action));
        }

        /// <summary>
        /// 可在帧末或安全节点调用，按所有消费者的最小游标裁剪历史记录以控制内存上限。
        /// </summary>
        public void TrimBefore(long minConsumedSequence)
        {
            foreach (var kv in _buckets)
            {
                TrimChangedList(kv.Value.ChangedRecords, minConsumedSequence);
                TrimExitList(kv.Value.RemovedRecords, minConsumedSequence);
            }
            TrimExitList(_deletedRecords, minConsumedSequence);
        }

        /// <summary>清空全部历史脏记录（一般仅在测试或世界重置时调用）。</summary>
        public void ClearAllRecords()
        {
            foreach (var kv in _buckets)
            {
                kv.Value.ChangedRecords.Clear();
                kv.Value.RemovedRecords.Clear();
            }
            _deletedRecords.Clear();
        }

        private void OnComponentAddedOrUpdated(ComponentChanged changed)
        {
            if (changed.Action != ComponentChangedAction.Add &&
                changed.Action != ComponentChangedAction.Update)
            {
                return;
            }

            var bucket = GetOrCreateBucket(changed.Type);
            bucket.ChangedRecords.Add(new DirtyComponentRecord(++_sequence, changed.EntityId, changed.Action));
        }

        private void OnComponentRemoved(ComponentChanged changed)
        {
            var bucket = GetOrCreateBucket(changed.Type);
            bucket.RemovedRecords.Add(new StructuralExitRecord(++_sequence, changed.EntityId));
        }

        private void OnEntityDelete(EntityDelete delete)
        {
            _deletedRecords.Add(new StructuralExitRecord(++_sequence, delete.Entity.Id));
        }

        private static void TrimChangedList(List<DirtyComponentRecord> list, long minSeq)
        {
            int removeCount = 0;
            int n = list.Count;
            while (removeCount < n && list[removeCount].Sequence <= minSeq)
            {
                removeCount++;
            }
            if (removeCount > 0)
            {
                list.RemoveRange(0, removeCount);
            }
        }

        private static void TrimExitList(List<StructuralExitRecord> list, long minSeq)
        {
            int removeCount = 0;
            int n = list.Count;
            while (removeCount < n && list[removeCount].Sequence <= minSeq)
            {
                removeCount++;
            }
            if (removeCount > 0)
            {
                list.RemoveRange(0, removeCount);
            }
        }
    }
}

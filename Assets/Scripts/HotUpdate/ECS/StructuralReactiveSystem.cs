using System.Collections.Generic;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;

namespace HotUpdate.ECS
{
    /// <summary>
    /// 基于 Friflo 原生 <see cref="EventRecorder"/> + <see cref="EventFilter"/> 的结构变更响应基类。
    /// 零 C# 事件委托、零逐实体订阅，专用于“只关心组件/Tag 结构新增变化”的场景（例如模型装配、初始化配置等）。
    /// 注意：需要在每逻辑帧末尾统一调用 <c>store.EventRecorder.ClearEvents()</c> 清理已消费事件。
    /// </summary>
    public abstract class StructuralReactiveSystem<T1> : QuerySystem<T1>
        where T1 : struct, IComponent
    {
        private readonly Dictionary<EntityStore, EventFilter> _filters = new();

        protected override void OnUpdate()
        {
            var store = Query.Store;
            if (!store.EventRecorder.Enabled)
            {
                store.EventRecorder.Enabled = true;
            }

            if (!_filters.TryGetValue(store, out var eventFilter))
            {
                eventFilter = new EventFilter(store.EventRecorder);
                ConfigureEventFilter(eventFilter);
                _filters[store] = eventFilter;
            }

            Query.ForEachEntity((ref T1 c1, Entity entity) =>
            {
                if (eventFilter.HasEvent(entity.Id))
                {
                    OnStructuralEvent(entity, ref c1);
                }
            });
        }

        protected virtual void ConfigureEventFilter(EventFilter filter)
        {
            filter.ComponentAdded<T1>();
        }

        protected abstract void OnStructuralEvent(Entity entity, ref T1 c1);
    }

    /// <summary>双组件原生 EventRecorder 结构变更响应基类。</summary>
    public abstract class StructuralReactiveSystem<T1, T2> : QuerySystem<T1, T2>
        where T1 : struct, IComponent
        where T2 : struct, IComponent
    {
        private readonly Dictionary<EntityStore, EventFilter> _filters = new();

        protected override void OnUpdate()
        {
            var store = Query.Store;
            if (!store.EventRecorder.Enabled)
            {
                store.EventRecorder.Enabled = true;
            }

            if (!_filters.TryGetValue(store, out var eventFilter))
            {
                eventFilter = new EventFilter(store.EventRecorder);
                ConfigureEventFilter(eventFilter);
                _filters[store] = eventFilter;
            }

            Query.ForEachEntity((ref T1 c1, ref T2 c2, Entity entity) =>
            {
                if (eventFilter.HasEvent(entity.Id))
                {
                    OnStructuralEvent(entity, ref c1, ref c2);
                }
            });
        }

        protected virtual void ConfigureEventFilter(EventFilter filter)
        {
            filter.ComponentAdded<T1>();
            filter.ComponentAdded<T2>();
        }

        protected abstract void OnStructuralEvent(Entity entity, ref T1 c1, ref T2 c2);
    }
}

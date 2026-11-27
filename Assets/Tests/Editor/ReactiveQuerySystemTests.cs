using System;
using System.Collections.Generic;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using HotUpdate.ECS;
using NUnit.Framework;

namespace FrifloECSDemo.Tests
{
    public class ReactiveQuerySystemTests
    {
        private struct ModelAsset : IComponent
        {
            public int AssetId;
        }

        private struct AiConfig : IComponent
        {
            public int ProfileId;
        }

        private struct WaveState : IComponent
        {
            public int WaveIndex;
        }

        private struct UnwatchedComp : IComponent
        {
            public int Value;
        }

        private sealed class SingleReactiveSystem : ReactiveQuerySystem<ModelAsset>
        {
            public readonly List<(int EntityId, Type CompType, ComponentChangedAction Action)> Events = new();
            public readonly List<int> EnteredIds = new();
            public readonly List<int> ExitedIds = new();
            public int FrameUpdateCount;
            private readonly bool _triggerOnEnter;

            public SingleReactiveSystem(bool triggerOnEnter = true)
            {
                _triggerOnEnter = triggerOnEnter;
            }

            protected override bool TriggerOnEntityEnter => _triggerOnEnter;

            protected override void OnEntityEnter(Entity entity) => EnteredIds.Add(entity.Id);
            protected override void OnEntityExit(Entity entity) => ExitedIds.Add(entity.Id);

            protected override void OnReactiveUpdateEntity(Entity entity, Type componentType, ComponentChangedAction action)
            {
                Events.Add((entity.Id, componentType, action));
            }

            protected override void OnFrameUpdate() => FrameUpdateCount++;

            public void ForceDirty(Entity entity, Type compType = null) => MarkDirty(entity, compType);
        }

        private sealed class PairReactiveSystem : ReactiveQuerySystem<ModelAsset, AiConfig>
        {
            public readonly List<(int EntityId, Type CompType)> Events = new();
            public readonly List<int> ExitedIds = new();

            protected override void OnEntityExit(Entity entity) => ExitedIds.Add(entity.Id);

            protected override void OnReactiveUpdateEntity(Entity entity, Type componentType)
            {
                Events.Add((entity.Id, componentType));
            }
        }

        private sealed class TripleReactiveSystem : ReactiveQuerySystem<ModelAsset, AiConfig, WaveState>
        {
            public readonly List<(int EntityId, Type CompType)> Events = new();

            protected override void OnReactiveUpdateEntity(Entity entity, Type componentType)
            {
                Events.Add((entity.Id, componentType));
            }
        }

        [Test]
        public void SingleComponent_TriggersOnEnter_AndOnlyOnSubsequentUpdates()
        {
            var store = new EntityStore();
            var system = new SingleReactiveSystem(triggerOnEnter: true);
            var root = new SystemRoot(store) { system };

            var entity = store.CreateEntity(new ModelAsset { AssetId = 1 });

            // 首次进入触发 Enter 与 Add 响应
            root.Update(default);
            Assert.AreEqual(1, system.EnteredIds.Count);
            Assert.AreEqual(1, system.Events.Count);
            Assert.AreEqual((entity.Id, typeof(ModelAsset), ComponentChangedAction.Add), system.Events[0]);
            Assert.AreEqual(1, system.SubscribedEntityCount);

            // 无变更的下一帧不重复触发
            system.Events.Clear();
            root.Update(default);
            Assert.AreEqual(0, system.Events.Count);
            Assert.AreEqual(2, system.FrameUpdateCount);

            // 更新未监控组件不触发
            entity.AddComponent(new UnwatchedComp { Value = 42 });
            root.Update(default);
            Assert.AreEqual(0, system.Events.Count);

            // 同一帧连续更新同一受监控组件，合并为一次 Update 派发
            entity.AddComponent(new ModelAsset { AssetId = 2 });
            entity.AddComponent(new ModelAsset { AssetId = 3 });
            root.Update(default);
            Assert.AreEqual(1, system.Events.Count);
            Assert.AreEqual((entity.Id, typeof(ModelAsset), ComponentChangedAction.Update), system.Events[0]);

            // 再次无变更不触发
            system.Events.Clear();
            root.Update(default);
            Assert.AreEqual(0, system.Events.Count);
        }

        [Test]
        public void SingleComponent_TriggerOnEntityEnterFalse_SkipsInitialDispatch()
        {
            var store = new EntityStore();
            var system = new SingleReactiveSystem(triggerOnEnter: false);
            var root = new SystemRoot(store) { system };

            var entity = store.CreateEntity(new ModelAsset { AssetId = 10 });

            root.Update(default);
            Assert.AreEqual(1, system.EnteredIds.Count);
            Assert.AreEqual(0, system.Events.Count);

            entity.AddComponent(new ModelAsset { AssetId = 20 });
            root.Update(default);
            Assert.AreEqual(1, system.Events.Count);
            Assert.AreEqual(ComponentChangedAction.Update, system.Events[0].Action);
        }

        [Test]
        public void MultiComponent_DispatchesAllChangedComponentTypesInSameFrame()
        {
            var store = new EntityStore();
            var system = new PairReactiveSystem();
            var root = new SystemRoot(store) { system };

            var entity = store.CreateEntity(new ModelAsset { AssetId = 1 }, new AiConfig { ProfileId = 1 });

            // 首次进入两个受监控组件都派发
            root.Update(default);
            Assert.AreEqual(2, system.Events.Count);
            Assert.Contains((entity.Id, typeof(ModelAsset)), system.Events);
            Assert.Contains((entity.Id, typeof(AiConfig)), system.Events);

            // 同一帧同时更新 T1 和 T2，两者都不会被互相覆盖
            system.Events.Clear();
            entity.AddComponent(new ModelAsset { AssetId = 2 });
            entity.AddComponent(new AiConfig { ProfileId = 2 });
            root.Update(default);

            Assert.AreEqual(2, system.Events.Count);
            Assert.AreEqual((entity.Id, typeof(ModelAsset)), system.Events[0]);
            Assert.AreEqual((entity.Id, typeof(AiConfig)), system.Events[1]);
        }

        [Test]
        public void RemoveComponentAndDeleteEntity_UnsubscribesSafelyWithoutException()
        {
            var store = new EntityStore();
            var system = new PairReactiveSystem();
            var root = new SystemRoot(store) { system };

            var e1 = store.CreateEntity(new ModelAsset { AssetId = 1 }, new AiConfig { ProfileId = 1 });
            var e2 = store.CreateEntity(new ModelAsset { AssetId = 2 }, new AiConfig { ProfileId = 2 });

            root.Update(default);
            Assert.AreEqual(2, system.SubscribedEntityCount);
            system.Events.Clear();

            // e1 移除 T1 离开 Query；e2 直接销毁
            e1.RemoveComponent<ModelAsset>();
            e2.DeleteEntity();

            Assert.DoesNotThrow(() => root.Update(default));
            Assert.AreEqual(0, system.SubscribedEntityCount);
            Assert.AreEqual(2, system.ExitedIds.Count);
            Assert.AreEqual(0, system.Events.Count);

            // e1 退订后再更新 T2 不应触发监听
            e1.AddComponent(new AiConfig { ProfileId = 99 });
            root.Update(default);
            Assert.AreEqual(0, system.Events.Count);
        }

        [Test]
        public void MarkDirtyAndClearSubscriptions_WorkAsExpected()
        {
            var store = new EntityStore();
            var system = new SingleReactiveSystem(triggerOnEnter: false);
            var root = new SystemRoot(store) { system };

            var entity = store.CreateEntity(new ModelAsset { AssetId = 1 });
            root.Update(default);
            Assert.AreEqual(0, system.Events.Count);

            // 通过 ref 修改字段后手动 MarkDirty
            entity.GetComponent<ModelAsset>().AssetId = 777;
            system.ForceDirty(entity, typeof(ModelAsset));
            root.Update(default);
            Assert.AreEqual(1, system.Events.Count);
            Assert.AreEqual(typeof(ModelAsset), system.Events[0].CompType);

            // ClearSubscriptions 清理所有订阅
            system.ClearSubscriptions();
            Assert.AreEqual(0, system.SubscribedEntityCount);
        }

        [Test]
        public void TripleComponent_ReactsToThirdComponentUpdate()
        {
            var store = new EntityStore();
            var system = new TripleReactiveSystem();
            var root = new SystemRoot(store) { system };

            var entity = store.CreateEntity(
                new ModelAsset { AssetId = 1 },
                new AiConfig { ProfileId = 1 },
                new WaveState { WaveIndex = 1 });

            root.Update(default);
            system.Events.Clear();

            entity.AddComponent(new WaveState { WaveIndex = 2 });
            root.Update(default);

            Assert.AreEqual(1, system.Events.Count);
            Assert.AreEqual((entity.Id, typeof(WaveState)), system.Events[0]);
        }
    }
}

using System;
using System.Collections.Generic;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using HotUpdate.ECS;
using HotUpdate.ECS.FixedPoint;
using HotUpdate.ECS.State;
using NUnit.Framework;

namespace FrifloECSDemo.Tests
{
    public class FrameworkUpgradesTests
    {
        private struct SharedConfig : IComponent
        {
            public int Value;
        }

        private struct SpawnMarker : IComponent
        {
            public int TagId;
        }

        private sealed class SharedWatcherSystem : ReactiveQuerySystem<SharedConfig>
        {
            public readonly List<int> UpdatedIds = new();
            protected override bool TriggerOnEntityEnter => false;

            protected override void OnReactiveUpdateEntity(Entity entity, Type componentType)
            {
                UpdatedIds.Add(entity.Id);
            }
        }

        private sealed class SpawnStructuralSystem : StructuralReactiveSystem<SpawnMarker>
        {
            public readonly List<int> AddedIds = new();

            protected override void OnStructuralEvent(Entity entity, ref SpawnMarker c1)
            {
                AddedIds.Add(entity.Id);
            }
        }

        [Test]
        public void FixedPoint_LargeMultiplicationDoesNotOverflow_AndMathIsDeterministic()
        {
            // 验证 128 位安全乘法：500,000 * 500,000 = 2.5e11，传统 Q48.16 直接 long*long 会瞬间溢出
            FP bigA = FP.FromInt(500000);
            FP bigB = FP.FromInt(500000);
            FP prod = bigA * bigB;
            Assert.AreEqual(250000000000L, prod.RawValue >> FP.Shift);

            // 验证负大数乘法
            FP negProd = (-bigA) * bigB;
            Assert.AreEqual(-250000000000L, negProd.RawValue >> FP.Shift);

            // 验证 Sqrt 精度
            FP sqrt144 = FPMath.Sqrt(FP.FromInt(144));
            Assert.AreEqual(12f, sqrt144.ToFloat(), 0.001f);

            // 验证 Sin^2 + Cos^2 ≈ 1
            FP angle = FP.FromMilli(1234);
            FP s = FPMath.Sin(angle);
            FP c = FPMath.Cos(angle);
            Assert.AreEqual(1f, (s * s + c * c).ToFloat(), 0.015f);

            // 验证 FPRandom 同种子完全一致
            var r1 = new FPRandom(987654321UL);
            var r2 = new FPRandom(987654321UL);
            for (int i = 0; i < 64; i++)
            {
                Assert.AreEqual(r1.NextULong(), r2.NextULong());
            }
        }

        [Test]
        public void UnitState_MutexTable_EnforcesCoexistenceInterruptionAndImmunity()
        {
            UnitState st = UnitState.CreateDefault();
            Assert.IsTrue(st.Has(UnitStateType.Idle));

            // Moving 与 Attacking / HitFlash / Slowed 可同时并存（走 A 不断步）
            Assert.IsTrue(st.TryEnter(UnitStateType.Moving));
            Assert.IsFalse(st.Has(UnitStateType.Idle));
            Assert.IsTrue(st.TryEnter(UnitStateType.Attacking, FP.FromMilli(200)));
            Assert.IsTrue(st.TryEnter(UnitStateType.HitFlash, FP.FromMilli(100)));
            Assert.IsTrue(st.TryEnter(UnitStateType.Slowed, FP.FromInt(2)));
            Assert.IsTrue(st.Has(UnitStateType.Moving) && st.Has(UnitStateType.Attacking) && st.Has(UnitStateType.HitFlash) && st.Has(UnitStateType.Slowed));
            Assert.AreEqual(FP.Half, st.MoveSpeedMultiplier);

            // Stunned 打断 Moving 与 Attacking，保留 HitFlash/Slowed
            Assert.IsTrue(st.TryEnter(UnitStateType.Stunned, FP.One));
            Assert.IsFalse(st.Has(UnitStateType.Moving));
            Assert.IsFalse(st.Has(UnitStateType.Attacking));
            Assert.IsFalse(st.CanMove);
            Assert.IsFalse(st.CanAct);

            // 开启 SuperArmor 霸体：自动解除已有 Stunned，且阻挡后续 Stunned
            Assert.IsTrue(st.TryEnter(UnitStateType.SuperArmor, FP.FromInt(3)));
            Assert.IsFalse(st.Has(UnitStateType.Stunned));
            Assert.IsTrue(st.CanMove);
            Assert.IsFalse(st.TryEnter(UnitStateType.Stunned, FP.FromInt(2)), "霸体状态下必须免疫眩晕");

            // Dead 彻底清除全部状态并阻挡一切后续状态
            Assert.IsTrue(st.TryEnter(UnitStateType.Dead));
            Assert.AreEqual((uint)UnitStateFlags.Dead, st.Mask);
            Assert.IsFalse(st.TryEnter(UnitStateType.Moving));
            Assert.IsFalse(st.TryEnter(UnitStateType.SuperArmor, FP.One));
        }

        [Test]
        public void ReactiveDirtyHub_SupportsMultipleConsumersOnSameTypeInOKTime()
        {
            var store = new EntityStore();
            var sysA = new SharedWatcherSystem();
            var sysB = new SharedWatcherSystem();
            var root = new SystemRoot(store) { sysA, sysB };

            // 创建 1000 个实体
            var entities = new Entity[1000];
            for (int i = 0; i < 1000; i++)
            {
                entities[i] = store.CreateEntity(new SharedConfig { Value = i });
            }

            // 首帧初始化挂载游标
            root.Update(default);
            Assert.AreEqual(0, sysA.UpdatedIds.Count);
            Assert.AreEqual(0, sysB.UpdatedIds.Count);

            // 仅修改 2 个实体，验证两个共享同一组件桶的 System 都能独立收到这两条变更
            entities[17].AddComponent(new SharedConfig { Value = 999 });
            entities[888].AddComponent(new SharedConfig { Value = 1000 });

            root.Update(default);

            Assert.AreEqual(2, sysA.UpdatedIds.Count);
            Assert.AreEqual(2, sysB.UpdatedIds.Count);
            Assert.Contains(entities[17].Id, sysA.UpdatedIds);
            Assert.Contains(entities[888].Id, sysB.UpdatedIds);
        }

        [Test]
        public void StructuralReactiveSystem_UsesNativeEventRecorderWithoutDelegates()
        {
            var store = new EntityStore();
            store.EventRecorder.Enabled = true;

            var sys = new SpawnStructuralSystem();
            var root = new SystemRoot(store) { sys };

            var e1 = store.CreateEntity(new SpawnMarker { TagId = 1 });
            var e2 = store.CreateEntity(new SpawnMarker { TagId = 2 });

            root.Update(default);
            Assert.AreEqual(2, sys.AddedIds.Count);
            Assert.Contains(e1.Id, sys.AddedIds);
            Assert.Contains(e2.Id, sys.AddedIds);

            // 清理 EventRecorder 后下一帧不应重复触发
            store.EventRecorder.ClearEvents();
            sys.AddedIds.Clear();
            root.Update(default);
            Assert.AreEqual(0, sys.AddedIds.Count);
        }

        [Test]
        public void BenchmarkStages_OptimizedStage5IsSignificantlyFasterThanUnoptimizedStage1()
        {
            var stage1 = HotUpdate.ECS.Config.BenchmarkStageCatalog.Presets[0]; // 全关 O(N^2) + 旧轮询 + 旧清理
            var stage5 = HotUpdate.ECS.Config.BenchmarkStageCatalog.Presets[4]; // 全开 O(N) + O(K) + 无冗余清理
            stage1.InitialEnemyCount = 600;
            stage5.InitialEnemyCount = 600;

            FP dt = FP.FromMilli(50);

            double unoptTotalMs = 0;
            using (var w1 = new BattleWorld(stage1, enablePresentation: false))
            {
                w1.SpawnInitialBattle(stage1.InitialEnemyCount);
                w1.UpdateLogicTick(dt); // 预热
                for (int i = 0; i < 5; i++)
                {
                    w1.UpdateLogicTick(dt);
                    unoptTotalMs += w1.Snapshot.LogicMs;
                }
            }

            double optTotalMs = 0;
            using (var w5 = new BattleWorld(stage5, enablePresentation: false))
            {
                w5.SpawnInitialBattle(stage5.InitialEnemyCount);
                w5.UpdateLogicTick(dt); // 预热
                for (int i = 0; i < 5; i++)
                {
                    w5.UpdateLogicTick(dt);
                    optTotalMs += w5.Snapshot.LogicMs;
                }
            }

            UnityEngine.Debug.Log($"[Benchmark] 600 敌军 x 5 帧纯逻辑对比：关卡1(未优化)={unoptTotalMs:F2}ms vs 关卡5(全优化)={optTotalMs:F2}ms (加速比 {unoptTotalMs / Math.Max(0.01, optTotalMs):F1}x)");
            Assert.Less(optTotalMs, unoptTotalMs, "全优化关卡5 的逻辑耗时应显著低于未优化的关卡1");
        }

        [Test]
        public void PbdCollisionSolver_SeparatesTightClusterAndRespectsMassWeighting()
        {
            var solver = new HotUpdate.ECS.Spatial.PbdCollisionSolver(FP.FromInt(48), FP.FromInt(2));
            // 0 号：极重主控（invMass = 0.05）位于原点
            solver.AddBody(1, FP.Zero, FP.Zero, FP.FromMilli(700), FP.FromMilli(50));
            // 1~36 号：36 个普通小怪（invMass = 1.0）严重挤在原点附近 1m x 1m 内
            for (int i = 0; i < 36; i++)
            {
                FP x = FP.FromMilli(((i % 6) - 3) * 150);
                FP z = FP.FromMilli(((i / 6) - 3) * 150);
                solver.AddBody(i + 2, x, z, FP.FromMilli(500), FP.One);
            }

            // 连续求解 8 次模拟多帧收敛
            for (int step = 0; step < 8; step++)
            {
                solver.Solve(iterations: 2, relaxation: FP.FromMilli(780));
            }

            // 主控质量重，几乎不被挤飞
            ref readonly var playerBody = ref solver.GetBody(0);
            FP playerDrift = FPMath.Sqrt(playerBody.PosX * playerBody.PosX + playerBody.PosZ * playerBody.PosZ);
            Assert.Less(playerDrift.ToFloat(), 0.6f, "主控应凭借低 InvMass 几乎不被小怪群挤飞");

            // 检查所有小怪与主控之间的最小距离应已明显推开（无严重穿透）
            int severeOverlaps = 0;
            for (int i = 1; i < solver.BodyCount; i++)
            {
                ref readonly var b = ref solver.GetBody(i);
                FP dx = b.PosX - playerBody.PosX;
                FP dz = b.PosZ - playerBody.PosZ;
                FP dist = FPMath.Sqrt(dx * dx + dz * dz);
                if (dist < FP.FromMilli(950)) severeOverlaps++;
            }
            Assert.AreEqual(0, severeOverlaps, "PBD 硬碰撞求解后小怪不应与主控严重穿透重叠");
        }

        [Test]
        public void FrustumCullingSystem_CullsOutOfViewEntitiesAndKeepsVisibleOnes()
        {
            var camGo = new UnityEngine.GameObject("TestCullingCamera");
            try
            {
                camGo.transform.SetPositionAndRotation(new UnityEngine.Vector3(0f, 22f, -18f), UnityEngine.Quaternion.Euler(50f, 0f, 0f));
                var cam = camGo.AddComponent<UnityEngine.Camera>();
                cam.fieldOfView = 55f;
                cam.aspect = 16f / 9f;

                using var world = new BattleWorld(enablePresentation: false, randomSeed: 55UL);
                // 实体 A：就在相机正中心 (0, 0.5, 0)，必然可见
                var visibleEntity = world.Store.CreateEntity(
                    new HotUpdate.ECS.Components.UnitTransform(FPVector3.FromFloats(0f, 0.5f, 0f), FPVector3.One),
                    new HotUpdate.ECS.Components.CubeVisual(UnityEngine.Color.cyan));
                // 实体 B：在相机后方极远处 (0, 0.5, -100)，必然被视锥裁切剔除
                var culledEntity = world.Store.CreateEntity(
                    new HotUpdate.ECS.Components.UnitTransform(FPVector3.FromFloats(0f, 0.5f, -100f), FPVector3.One),
                    new HotUpdate.ECS.Components.CubeVisual(UnityEngine.Color.red));

                world.CullingSystem.OverrideCamera = cam;
                world.CullingRoot.Update(default);

                Assert.IsTrue(visibleEntity.GetComponent<HotUpdate.ECS.Components.CubeVisual>().IsVisible, "视野中心实体应为可见");
                Assert.IsFalse(culledEntity.GetComponent<HotUpdate.ECS.Components.CubeVisual>().IsVisible, "相机后方远处实体应被视锥裁切剔除");
                Assert.AreEqual(1, world.CullingSystem.VisibleCount);
                Assert.AreEqual(1, world.CullingSystem.CulledCount);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(camGo);
            }
        }
    }
}

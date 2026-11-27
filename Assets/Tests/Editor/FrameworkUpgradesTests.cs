using System;
using System.Collections.Generic;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using HotUpdate.ECS;
using HotUpdate.ECS.Buff;
using HotUpdate.ECS.Components;
using HotUpdate.ECS.Config;
using HotUpdate.ECS.FixedPoint;
using HotUpdate.ECS.Lockstep;
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

        [Test]
        public void DamagePipeline_ShieldBurnLifestealAndUndyingRescue_Follows8StepOrder()
        {
            using var world = new BattleWorld(BattleOptimizationConfig.CreateFullOptimized(0, 101UL), enablePresentation: false);
            world.SpawnInitialBattle(initialEnemies: 0);

            Entity player = world.Store.GetEntityById(world.PlayerEntityId);
            ref var pStats = ref player.GetComponent<CombatStats>();
            ref var pBuffs = ref player.GetComponent<UnitBuffs>();
            pBuffs.Shield = FP.FromInt(100);
            pBuffs.ShieldTimer = FP.FromInt(10);
            pBuffs.UndyingCharges = 1;
            pBuffs.LifestealRate = FP.FromMilli(200);
            pBuffs.CritRate = FP.Zero;
            pBuffs.ArmorResist = FP.Zero;

            Entity enemy = world.Store.CreateEntity(
                new UnitTransform(FPVector3.FromFloats(20f, 0.5f, 0f), FPVector3.One),
                new UnitVelocity(FP.FromInt(3), FP.Half),
                new CombatStats(
                    maxHp: FP.FromInt(1000),
                    atk: FP.FromInt(100),
                    attackRange: FP.FromInt(2),
                    aggroRange: FP.FromInt(2),
                    attackInterval: FP.FromInt(10)),
                new CombatTarget(),
                UnitState.CreateDefault(),
                UnitBuffs.CreateEnemyDefault(isElite: false),
                new CubeVisual(UnityEngine.Color.red),
                Tags.Get<EnemyTag>());

            FP initialHp = pStats.Hp;

            // Step 1: 护盾优先吸收 60 点伤害，血量无损
            world.EnqueueDamage(new DamageRequest(player.Id, FP.FromInt(60), attackerEntityId: enemy.Id));
            world.UpdateLogicTick(FP.FromMilli(50));
            Assert.AreEqual(initialHp, player.GetComponent<CombatStats>().Hp, "护盾足够时主控血量不应减少");
            Assert.AreEqual(40f, player.GetComponent<UnitBuffs>().Shield.ToFloat(), 0.5f, "护盾应精准扣除 60 点");

            // Step 2: 伤后节点 31（吸血 + 灼烧 DOT 叠层）
            player.GetComponent<CombatStats>().Hp = initialHp - FP.FromInt(100);
            FP loweredHp = player.GetComponent<CombatStats>().Hp;
            world.EnqueueDamage(new DamageRequest(enemy.Id, FP.FromInt(100), attackerEntityId: player.Id, applyBurnOnHit: true));
            world.UpdateLogicTick(FP.FromMilli(50));

            Assert.Greater(player.GetComponent<CombatStats>().Hp.ToFloat(), loweredHp.ToFloat(), "命中后应通过节点 31 吸血回复血量");
            Assert.GreaterOrEqual(enemy.GetComponent<UnitBuffs>().BurnStacks, 1, "命中应叠加灼烧 DOT 层数");

            FP enemyHpAfterHit = enemy.GetComponent<CombatStats>().Hp;
            for (int i = 0; i < 16; i++)
            {
                world.UpdateLogicTick(FP.FromMilli(50));
            }
            Assert.Less(enemy.GetComponent<CombatStats>().Hp.ToFloat(), enemyHpAfterHit.ToFloat(), "灼烧 DOT 应持续造成跳字扣血");

            // Step 3: 904 免死抢救（首次致命伤保留 35% 血量并进入 2 秒霸体）
            world.EnqueueDamage(new DamageRequest(player.Id, FP.FromInt(5000), attackerEntityId: enemy.Id));
            world.UpdateLogicTick(FP.FromMilli(50));

            Assert.IsFalse(player.Tags.Has<DeadTag>(), "拥有 904 免死次数时首次致命伤应被抢救存活");
            Assert.AreEqual(0, player.GetComponent<UnitBuffs>().UndyingCharges, "免死次数应消耗为 0");
            Assert.AreEqual(1, world.UndyingTriggerCount);
            Assert.IsTrue(player.GetComponent<UnitState>().Has(UnitStateType.SuperArmor), "免死抢救应立即赋予霸体");
            Assert.Greater(player.GetComponent<CombatStats>().Hp.ToFloat(), player.GetComponent<CombatStats>().MaxHp.ToFloat() * 0.30f);

            // Step 4: 免死耗尽后的第二次致命伤：主控由 DeathCleanupSystem 自动满血复活（不重触发免死），普通敌军则直接致死清理并计入击杀
            world.EnqueueDamage(new DamageRequest(player.Id, FP.FromInt(5000), attackerEntityId: enemy.Id));
            world.EnqueueDamage(new DamageRequest(enemy.Id, FP.FromInt(5000), attackerEntityId: player.Id));
            world.UpdateLogicTick(FP.FromMilli(50));

            Assert.AreEqual(1, world.UndyingTriggerCount, "免死耗尽后不应再次触发 904 免死");
            Assert.AreEqual(1, world.TotalKills, "普通敌军受致命伤后应正式死亡并计入 TotalKills");
            Assert.IsFalse(world.Store.TryGetEntityById(enemy.Id, out var deadCheck) && !deadCheck.IsNull, "死亡敌军应由 DeathCleanupSystem 清理销毁");
        }

        [Test]
        public void RoguelikeCards_UnlockHomingTornadoAndBounceProjectiles()
        {
            using var world = new BattleWorld(BattleOptimizationConfig.CreateFullOptimized(0, 202UL), enablePresentation: false);
            world.SpawnInitialBattle(initialEnemies: 0);

            // 验证确定性三选一抽卡不重复
            var drawn = new RoguelikeCardDef[3];
            world.DrawThreeRoguelikeCards(drawn);
            Assert.AreNotEqual(drawn[0].Id, drawn[1].Id);
            Assert.AreNotEqual(drawn[1].Id, drawn[2].Id);
            Assert.AreNotEqual(drawn[0].Id, drawn[2].Id);

            // 注入制导、龙卷风、弹射、灼烧四张词条卡
            world.ApplyRoguelikeCard(RoguelikeCardId.HomingMissiles);
            world.ApplyRoguelikeCard(RoguelikeCardId.TornadoStorm);
            world.ApplyRoguelikeCard(RoguelikeCardId.ChainBounce);
            world.ApplyRoguelikeCard(RoguelikeCardId.BurnEnchant);

            Assert.IsTrue(world.SkillModifiers.EnableHomingShots);
            Assert.IsTrue(world.SkillModifiers.EnableTornado);
            Assert.IsTrue(world.SkillModifiers.EnableBurnOnHit);
            Assert.AreEqual(2, world.SkillModifiers.ExtraBounceCount);
            Assert.AreEqual(4, world.AppliedCardCount);

            // 在主控旁生成 2 个高血量木桩敌军并推进逻辑帧，验证制导与龙卷风实体均生成并造成伤害
            Entity e1 = world.Store.CreateEntity(
                new UnitTransform(FPVector3.FromFloats(6f, 0.5f, 0f), FPVector3.One),
                new UnitVelocity(FP.One, FP.Half),
                new CombatStats(FP.FromInt(2500), FP.FromInt(5), FP.One, FP.FromInt(20), FP.One),
                new CombatTarget(),
                UnitState.CreateDefault(),
                UnitBuffs.CreateEnemyDefault(false),
                new CubeVisual(UnityEngine.Color.red),
                Tags.Get<EnemyTag>());
            world.Store.CreateEntity(
                new UnitTransform(FPVector3.FromFloats(7f, 0.5f, 1.5f), FPVector3.One),
                new UnitVelocity(FP.One, FP.Half),
                new CombatStats(FP.FromInt(2500), FP.FromInt(5), FP.One, FP.FromInt(20), FP.One),
                new CombatTarget(),
                UnitState.CreateDefault(),
                UnitBuffs.CreateEnemyDefault(false),
                new CubeVisual(UnityEngine.Color.red),
                Tags.Get<EnemyTag>());

            bool sawHoming = false;
            bool sawTornado = false;
            for (int i = 0; i < 40; i++)
            {
                world.UpdateLogicTick(FP.FromMilli(50));
                world.Store.Query<ProjectileData>().ForEachEntity((ref ProjectileData proj, Entity _) =>
                {
                    if (proj.Kind == ProjectileKind.Homing) sawHoming = true;
                    if (proj.Kind == ProjectileKind.Tornado) sawTornado = true;
                });
            }

            Assert.IsTrue(sawHoming, "选择精确制导词条后应生成 Homing 追踪弹道");
            Assert.IsTrue(sawTornado, "选择龙卷风暴词条后应生成 Tornado 持续旋风弹道");
            Assert.Less(e1.GetComponent<CombatStats>().Hp.ToFloat(), 2500f, "新形态弹道应对敌军造成有效伤害");
        }

        [Test]
        public void LockstepRunner_HeadlessCatchupMatchesNormalTicksBitForBit()
        {
            var cfg = BattleOptimizationConfig.CreateFullOptimized(300, 777UL);

            ulong catchupHash;
            double catchupMs;
            using (var worldA = new BattleWorld(cfg, enablePresentation: false))
            {
                worldA.SpawnInitialBattle(initialEnemies: 300);
                var runner = new LockstepRunner();
                catchupMs = runner.RunHeadlessCatchup(worldA, frames: 200);
                catchupHash = worldA.ComputeStateHash();
                Assert.AreEqual(200, runner.LogicFrameId);
                Assert.AreEqual(catchupHash, runner.LastStateHash);
            }

            ulong normalHash;
            using (var worldB = new BattleWorld(cfg, enablePresentation: false))
            {
                worldB.SpawnInitialBattle(initialEnemies: 300);
                for (int i = 0; i < 200; i++)
                {
                    worldB.UpdateLogicTick(LockstepRunner.FixedDeltaTimeFP);
                }
                normalHash = worldB.ComputeStateHash();
            }

            UnityEngine.Debug.Log($"[Lockstep Catchup] 300 敌军无头快进 200 帧 (10s) 耗时: {catchupMs:F2}ms | StateHash: 0x{catchupHash:X16}");
            Assert.AreEqual(normalHash, catchupHash, "无头高速追帧 200 帧后的 64 位 StateHash 必须与逐帧正常推演完全一致！");
        }
    }
}

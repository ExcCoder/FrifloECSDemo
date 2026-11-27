using Friflo.Engine.ECS;
using HotUpdate.ECS;
using HotUpdate.ECS.Components;
using HotUpdate.ECS.FixedPoint;
using HotUpdate.ECS.Spatial;
using HotUpdate.ECS.State;
using NUnit.Framework;
using UnityEngine;

namespace FrifloECSDemo.Tests
{
    public class BattleWorldTests
    {
        [Test]
        public void HeadlessBattleWorld_SpawnsSquadAndThousandEnemies_AndTicksFast()
        {
            using var world = new BattleWorld(enablePresentation: false, randomSeed: 42UL);
            world.OverrideMoveInput = (FP.One, FP.Zero);
            world.SpawnInitialBattle(initialEnemies: 1000);

            Assert.AreEqual(4, world.Snapshot.AliveAllies);
            Assert.AreEqual(1000, world.Snapshot.EnemyCount);
            Assert.AreEqual(1, world.Snapshot.WaveIndex);

            FP fixedDt = FP.FromMilli(50);
            for (int i = 0; i < 30; i++)
            {
                world.UpdateLogicTick(fixedDt);
            }

            Assert.Greater(world.PlayerPosition.X.ToFloat(), 5f);
            Assert.Greater(world.Grid.Count, 500);
            Assert.Less(world.Snapshot.LogicMs, 50.0, "1000 单位定点数纯逻辑帧耗时异常偏高");
        }

        [Test]
        public void Determinism_TwoWorldsWithSameSeed_HaveIdenticalStateHashAfter100Ticks()
        {
            const ulong seed = 20261127UL;
            FP fixedDt = FP.FromMilli(50);

            using var worldA = new BattleWorld(enablePresentation: false, randomSeed: seed);
            using var worldB = new BattleWorld(enablePresentation: false, randomSeed: seed);

            worldA.SpawnInitialBattle(initialEnemies: 500);
            worldB.SpawnInitialBattle(initialEnemies: 500);

            Assert.AreEqual(worldA.ComputeStateHash(), worldB.ComputeStateHash(), "初始状态哈希应完全一致");

            for (int tick = 0; tick < 100; tick++)
            {
                // 模拟确定性玩家输入切换（前 40 帧向右，中 30 帧斜上，后 30 帧向左）
                var input = tick < 40
                    ? (FP.One, FP.Zero)
                    : tick < 70
                        ? (FP.FromMilli(707), FP.FromMilli(707))
                        : (FP.NegOne, FP.Zero);

                worldA.OverrideMoveInput = input;
                worldB.OverrideMoveInput = input;

                worldA.UpdateLogicTick(fixedDt);
                worldB.UpdateLogicTick(fixedDt);
            }

            Assert.AreEqual(worldA.ComputeStateHash(), worldB.ComputeStateHash(), "双世界同种子跑 100 帧后 64 位状态哈希必须 100% 比特一致");
            Assert.AreEqual(worldA.TotalKills, worldB.TotalKills);
            Assert.AreEqual(worldA.PlayerPosition, worldB.PlayerPosition);
        }

        [Test]
        public void AutoCombatAndSkills_KillAndStunNearbyEnemies_WhileElitesResistStun()
        {
            using var world = new BattleWorld(enablePresentation: false, randomSeed: 7UL);
            world.OverrideMoveInput = (FP.Zero, FP.Zero);
            world.SpawnInitialBattle(initialEnemies: 0);

            // 放 16 个普通怪 + 4 个带 SuperArmor 的精英怪在主控周围 4 米内
            for (int i = 0; i < 20; i++)
            {
                FP angle = (FP.TwoPi * FP.FromInt(i)) / FP.FromInt(20);
                FPVector3 pos = new(FPMath.Cos(angle) * FP.FromInt(4), FP.FromMilli(400), FPMath.Sin(angle) * FP.FromInt(4));
                bool isElite = i < 4;
                UnitState st = UnitState.CreateDefault();
                if (isElite) st.TryEnter(UnitStateType.SuperArmor);

                world.Store.CreateEntity(
                    new UnitTransform(pos, FPVector3.FromFloats(0.8f, 0.8f, 0.8f)),
                    new UnitVelocity(FP.FromInt(3), FP.FromMilli(450)),
                    new CombatStats(isElite ? FP.FromInt(500) : FP.FromInt(40), FP.FromInt(2), FP.FromMilli(1400), FP.FromInt(20), FP.One),
                    new CombatTarget(),
                    st,
                    new CubeVisual(Color.red),
                    Tags.Get<EnemyTag>()
                );
            }

            FP fixedDt = FP.FromMilli(50);
            for (int i = 0; i < 60; i++)
            {
                world.UpdateLogicTick(fixedDt);
            }

            Assert.Greater(world.Snapshot.TotalKills, 0, "自动战斗与技能应击杀普通敌军");
            Assert.Less(world.Snapshot.EnemyCount, 20, "阵亡敌军实体应被自动清理");

            // 精英怪虽承受 Nova 伤害，但凭借 SuperArmor 不应处于 Stunned 状态
            var elites = world.Store.Query<UnitState>().AllTags(Tags.Get<EnemyTag>()).WithoutAllTags(Tags.Get<DeadTag>());
            elites.ForEachEntity((ref UnitState st, Entity _) =>
            {
                if (st.Has(UnitStateType.SuperArmor))
                {
                    Assert.IsFalse(st.Has(UnitStateType.Stunned), "带霸体的精英怪应通过互斥表免疫眩晕");
                }
            });
        }

        [Test]
        public void WaveDirector_TriggersReactiveSpawnOnNextWave()
        {
            using var world = new BattleWorld(enablePresentation: false, randomSeed: 100UL);
            world.OverrideMoveInput = (FP.Zero, FP.Zero);
            world.SpawnInitialBattle(initialEnemies: 0);

            Assert.AreEqual(1, world.Snapshot.WaveIndex);
            Assert.AreEqual(0, world.Snapshot.EnemyCount);

            FP fixedDt = FP.FromMilli(50);
            world.UpdateLogicTick(fixedDt);

            for (int i = 0; i < 210; i++)
            {
                world.UpdateLogicTick(fixedDt);
            }

            Assert.GreaterOrEqual(world.Snapshot.WaveIndex, 2);
            Assert.Greater(world.Snapshot.EnemyCount, 0, "波次切换后 WaveSpawnReactiveSystem 应通过 O(K) 脏桶自动刷怪");
        }

        [Test]
        public void SpatialGrid_FindsNearestEnemyAccuratelyAmongMany()
        {
            var grid = new SpatialGrid(worldHalfExtent: FP.FromInt(55), cellSize: FP.FromInt(3));
            for (int i = 1; i <= 1000; i++)
            {
                int x = ((i * 37) % 80) - 40;
                int z = ((i * 53) % 80) - 40;
                if (x * x + z * z < 9) x += 10;
                grid.Insert(i, new FPVector3(FP.FromInt(x), FP.Zero, FP.FromInt(z)), FP.Half, SpatialGrid.TeamEnemy);
            }

            grid.Insert(9999, new FPVector3(FP.FromMilli(1500), FP.Zero, FP.Zero), FP.Half, SpatialGrid.TeamEnemy);

            bool found = grid.FindNearest(FPVector3.Zero, FP.FromInt(20), SpatialGrid.TeamEnemy, -1, out var nearest, out FP sqrDist);
            Assert.IsTrue(found);
            Assert.AreEqual(9999, nearest.EntityId);
            Assert.LessOrEqual(sqrDist.ToFloat(), 2.26f);
        }
    }
}

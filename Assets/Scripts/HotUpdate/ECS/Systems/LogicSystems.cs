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
    /// 1. 每帧重建 Q48.16 定点数空间哈希网格。
    /// </summary>
    public sealed class SpatialGridBuildSystem : QuerySystem<UnitTransform, UnitVelocity>
    {
        private readonly BattleWorld _world;

        public SpatialGridBuildSystem(BattleWorld world)
        {
            _world = world;
            Filter.WithoutAllTags(Tags.Get<DeadTag>());
        }

        protected override void OnUpdate()
        {
            _world.SpatialClear();

            Query.ForEachEntity((ref UnitTransform tr, ref UnitVelocity vel, Entity entity) =>
            {
                byte team = entity.Tags.Has<EnemyTag>() ? SpatialGrid.TeamEnemy : SpatialGrid.TeamSquad;
                _world.SpatialInsert(entity.Id, tr.Position, vel.Radius, team);
            });
        }
    }

    /// <summary>
    /// 2. 推进所有实体的位掩码限时状态（受击闪白、减速、眩晕、霸体、攻击动画状态到期自动清除）。
    /// </summary>
    public sealed class StateUpdateSystem : QuerySystem<UnitState>
    {
        private readonly BattleWorld _world;

        public StateUpdateSystem(BattleWorld world)
        {
            _world = world;
        }

        protected override void OnUpdate()
        {
            FP dt = BattleWorld.LogicDeltaTimeFP;
            Query.ForEachEntity((ref UnitState state, Entity _) =>
            {
                state.TickTimers(dt);
            });
        }
    }

    /// <summary>
    /// 3. 伤害与附加控制状态统一结算（在 AI 与技能之前执行，严格遵守 StateMutexTable 互斥与霸体免疫规则）。
    /// </summary>
    public sealed class DamageResolveSystem : BaseSystem
    {
        private static readonly FP HitFlashDuration = FP.FromMilli(90);
        private static readonly FP NovaSlowDuration = FP.FromMilli(2500);
        private readonly BattleWorld _world;

        public DamageResolveSystem(BattleWorld world)
        {
            _world = world;
        }

        protected override void OnUpdateGroup()
        {
            var damages = _world.PendingDamages;
            if (damages.Count == 0) return;

            var store = _world.Store;
            for (int i = 0; i < damages.Count; i++)
            {
                var req = damages[i];
                if (!store.TryGetEntityById(req.TargetEntityId, out var target) || target.IsNull) continue;
                if (target.Tags.Has<DeadTag>() || !target.HasComponent<CombatStats>()) continue;

                ref var stats = ref target.GetComponent<CombatStats>();
                stats.Hp -= req.Amount;

                bool hasState = target.HasComponent<UnitState>();
                if (hasState)
                {
                    ref var st = ref target.GetComponent<UnitState>();
                    st.TryEnter(UnitStateType.HitFlash, HitFlashDuration);

                    if (req.ApplyState != UnitStateType.HitFlash && req.ApplyStateDuration.RawValue > 0L)
                    {
                        // 尝试施加附加状态（如 Stunned：若目标有 SuperArmor 霸体则按互斥表自动免疫！）
                        st.TryEnter(req.ApplyState, req.ApplyStateDuration);
                        if (req.ApplyState == UnitStateType.Stunned)
                        {
                            st.TryEnter(UnitStateType.Slowed, NovaSlowDuration);
                        }
                    }
                }

                if (stats.Hp.RawValue <= 0L)
                {
                    stats.Hp = FP.Zero;
                    if (hasState)
                    {
                        // Dead 状态通过互斥表一次性打断并清除全部其他状态
                        target.GetComponent<UnitState>().TryEnter(UnitStateType.Dead);
                    }
                    if (target.Tags.Has<EnemyTag>())
                    {
                        _world.TotalKills++;
                    }
                    target.AddTag<DeadTag>();
                }
            }

            damages.Clear();
        }
    }

    /// <summary>
    /// 4. 主控输入系统（输入采样在边界转为 Q48.16 定点数，并受 UnitState.CanMove / MoveSpeedMultiplier 约束）。
    /// </summary>
    public sealed class PlayerInputSystem : QuerySystem<UnitTransform, UnitVelocity, UnitState>
    {
        private readonly BattleWorld _world;

        public PlayerInputSystem(BattleWorld world)
        {
            _world = world;
            Filter.AllTags(Tags.Get<PlayerTag>()).WithoutAllTags(Tags.Get<DeadTag>());
        }

        protected override void OnUpdate()
        {
            FP inX, inZ;
            if (_world.OverrideMoveInput.HasValue)
            {
                inX = _world.OverrideMoveInput.Value.X;
                inZ = _world.OverrideMoveInput.Value.Y;
            }
            else
            {
                inX = FP.FromFloat(Input.GetAxisRaw("Horizontal"));
                inZ = FP.FromFloat(Input.GetAxisRaw("Vertical"));
            }

            FPVector3 dir = new FPVector3(inX, FP.Zero, inZ);
            if (dir.SqrMagnitudeXZ() > FP.One)
            {
                dir = dir.NormalizedXZ();
            }

            Query.ForEachEntity((ref UnitTransform tr, ref UnitVelocity vel, ref UnitState state, Entity _) =>
            {
                if (!state.CanMove)
                {
                    vel.Value = FPVector3.Zero;
                }
                else if (dir.SqrMagnitudeXZ().RawValue > 0L)
                {
                    state.TryEnter(UnitStateType.Moving);
                    vel.Value = dir * (vel.MoveSpeed * state.MoveSpeedMultiplier);
                }
                else
                {
                    state.TryEnter(UnitStateType.Idle);
                    vel.Value = FPVector3.Zero;
                }

                _world.PlayerPosition = tr.Position;
            });
        }
    }

    /// <summary>
    /// 5. 4 个自动跟随小弟 AI（纯定点数编队跟随 + 自动索敌 + 邻近分离 + 状态约束）。
    /// </summary>
    public sealed class AllyAiSystem : QuerySystem<UnitTransform, UnitVelocity, AllyFormation, CombatStats, UnitState>
    {
        private static readonly FP ComfortRatio = FP.FromMilli(850);
        private static readonly FP MinFollowDist = FP.FromMilli(600);
        private static readonly FP SmoothRadius = FP.FromInt(3);
        private static readonly FP SepDist = FP.FromMilli(1500);
        private static readonly FP SepWeight = FP.FromMilli(3500);

        private readonly BattleWorld _world;

        public AllyAiSystem(BattleWorld world)
        {
            _world = world;
            Filter.AllTags(Tags.Get<AllyTag>())
                  .AllComponents(ComponentTypes.Get<CombatTarget>())
                  .WithoutAllTags(Tags.Get<DeadTag>());
        }

        protected override void OnUpdate()
        {
            FPVector3 playerPos = _world.PlayerPosition;

            Query.ForEachEntity((ref UnitTransform tr, ref UnitVelocity vel, ref AllyFormation form, ref CombatStats stats, ref UnitState state, Entity entity) =>
            {
                if (!state.CanMove)
                {
                    vel.Value = FPVector3.Zero;
                    return;
                }

                ref var target = ref entity.GetComponent<CombatTarget>();

                FPVector3 anchorPos = new(playerPos.X + form.LocalOffset.X, tr.Position.Y, playerPos.Z + form.LocalOffset.Z);
                FPVector3 toAnchor = new(anchorPos.X - tr.Position.X, FP.Zero, anchorPos.Z - tr.Position.Z);
                FP distToAnchor = toAnchor.MagnitudeXZ();

                bool hasEnemy = _world.SpatialFindNearest(tr.Position, stats.AggroRange, SpatialGrid.TeamEnemy, entity.Id, out var nearestEnemy, out FP enemySqrDist);
                target.TargetEntityId = hasEnemy ? nearestEnemy.EntityId : 0;

                FPVector3 desiredDir = FPVector3.Zero;
                FP speedScale = FP.One;
                FP comfortRange = stats.AttackRange * ComfortRatio;

                if (distToAnchor > form.LeashDistance && distToAnchor.RawValue > 0L)
                {
                    desiredDir = toAnchor / distToAnchor;
                    speedScale = FP.FromMilli(1250);
                }
                else if (hasEnemy && enemySqrDist > comfortRange * comfortRange)
                {
                    FPVector3 toEnemy = new FPVector3(nearestEnemy.Position.X - tr.Position.X, FP.Zero, nearestEnemy.Position.Z - tr.Position.Z).NormalizedXZ();
                    FPVector3 anchorBias = distToAnchor > SepDist ? (toAnchor / distToAnchor) * FP.FromMilli(350) : FPVector3.Zero;
                    desiredDir = toEnemy * FP.FromMilli(750) + anchorBias;
                }
                else if (distToAnchor > MinFollowDist)
                {
                    desiredDir = toAnchor / distToAnchor;
                    speedScale = FPMath.Clamp01(distToAnchor / SmoothRadius);
                }

                FPVector3 sep = _world.SpatialComputeSeparation(entity.Id, tr.Position, SepDist, SpatialGrid.TeamSquad, maxNeighbors: 6);
                FP effSpeed = vel.MoveSpeed * state.MoveSpeedMultiplier;
                FPVector3 finalMove = desiredDir * (effSpeed * speedScale) + sep * SepWeight;
                finalMove.Y = FP.Zero;

                FP maxSpd = effSpeed * FP.FromMilli(1250);
                if (finalMove.SqrMagnitudeXZ() > maxSpd * maxSpd)
                {
                    finalMove = finalMove.NormalizedXZ() * maxSpd;
                }

                vel.Value = finalMove;
                state.TryEnter(finalMove.SqrMagnitudeXZ().RawValue > 16L ? UnitStateType.Moving : UnitStateType.Idle);
            });
        }
    }

    /// <summary>
    /// 6. 千级敌军集群 AI（纯定点数追击 + 空间网格邻近分离 + 眩晕/减速状态响应）。
    /// </summary>
    public sealed class EnemySwarmSystem : QuerySystem<UnitTransform, UnitVelocity, CombatStats, CombatTarget, UnitState>
    {
        private static readonly FP SeekRange = FP.FromInt(18);
        private static readonly FP StopRatio = FP.FromMilli(780);
        private static readonly FP SepRadiusFactor = FP.FromMilli(2150);
        private static readonly FP SepSpeedFactor = FP.FromMilli(1350);
        private static readonly FP MaxSpeedFactor = FP.FromMilli(1150);

        private readonly BattleWorld _world;

        public EnemySwarmSystem(BattleWorld world)
        {
            _world = world;
            Filter.AllTags(Tags.Get<EnemyTag>()).WithoutAllTags(Tags.Get<DeadTag>());
        }

        protected override void OnUpdate()
        {
            FPVector3 playerPos = _world.PlayerPosition;

            Query.ForEachEntity((ref UnitTransform tr, ref UnitVelocity vel, ref CombatStats stats, ref CombatTarget target, ref UnitState state, Entity entity) =>
            {
                if (!state.CanMove)
                {
                    vel.Value = FPVector3.Zero;
                    return;
                }

                FPVector3 chasePos = playerPos;
                FP sqrDistToTarget;

                if (_world.SpatialFindNearest(tr.Position, SeekRange, SpatialGrid.TeamSquad, entity.Id, out var nearestSquad, out FP sqrDist))
                {
                    target.TargetEntityId = nearestSquad.EntityId;
                    chasePos = nearestSquad.Position;
                    sqrDistToTarget = sqrDist;
                }
                else
                {
                    target.TargetEntityId = _world.PlayerEntityId;
                    FP dx = playerPos.X - tr.Position.X;
                    FP dz = playerPos.Z - tr.Position.Z;
                    sqrDistToTarget = dx * dx + dz * dz;
                }

                FP effSpeed = vel.MoveSpeed * state.MoveSpeedMultiplier;
                FPVector3 toTarget = new(chasePos.X - tr.Position.X, FP.Zero, chasePos.Z - tr.Position.Z);
                FP stopDist = stats.AttackRange * StopRatio;

                FPVector3 seekDir = FPVector3.Zero;
                FPVector3 seekVel = FPVector3.Zero;
                if (sqrDistToTarget > stopDist * stopDist && toTarget.SqrMagnitudeXZ().RawValue > 8L)
                {
                    seekDir = toTarget.NormalizedXZ();
                    seekVel = seekDir * effSpeed;
                }

                FPVector3 separation = _world.SpatialComputeSeparation(entity.Id, tr.Position, vel.Radius * SepRadiusFactor, SpatialGrid.TeamEnemy, maxNeighbors: 8);
                FPVector3 combined;

                if (_world.Config.UseHardCollision)
                {
                    // 开启 PBD 硬碰撞时：降低软排斥防抖动，并在被前方友军挡路时叠加确定性切向包抄绕行速度（防同心圆叠罗汉）
                    FPVector3 flankVel = FPVector3.Zero;
                    if (seekDir.SqrMagnitudeXZ().RawValue > 0L && separation.SqrMagnitudeXZ().RawValue > 256L)
                    {
                        FP dot = seekDir.X * separation.X + seekDir.Z * separation.Z;
                        if (dot.RawValue < 0L)
                        {
                            FP sign = (entity.Id & 1) == 0 ? FP.One : FP.NegOne;
                            FPVector3 tangent = new(-seekDir.Z * sign, FP.Zero, seekDir.X * sign);
                            flankVel = tangent * (effSpeed * FP.FromMilli(650));
                        }
                    }
                    combined = seekVel + flankVel + separation * (effSpeed * FP.FromMilli(350));
                }
                else
                {
                    combined = seekVel + separation * (effSpeed * SepSpeedFactor);
                }
                combined.Y = FP.Zero;

                FP maxSpd = effSpeed * MaxSpeedFactor;
                if (combined.SqrMagnitudeXZ() > maxSpd * maxSpd)
                {
                    combined = combined.NormalizedXZ() * maxSpd;
                }

                vel.Value = combined;
                state.TryEnter(combined.SqrMagnitudeXZ().RawValue > 16L ? UnitStateType.Moving : UnitStateType.Idle);
            });
        }
    }

    /// <summary>
    /// 7. 纯定点数运动积分与场地边界限制。
    /// </summary>
    public sealed class MovementSystem : QuerySystem<UnitTransform, UnitVelocity>
    {
        private readonly BattleWorld _world;

        public MovementSystem(BattleWorld world)
        {
            _world = world;
            Filter.WithoutAllTags(Tags.Get<DeadTag>());
        }

        protected override void OnUpdate()
        {
            FP dt = BattleWorld.LogicDeltaTimeFP;
            FP bound = BattleWorld.ArenaHalfSize;
            FP negBound = -bound;

            Query.ForEachEntity((ref UnitTransform tr, ref UnitVelocity vel, Entity entity) =>
            {
                if (vel.Value.SqrMagnitudeXZ().RawValue > 8L)
                {
                    tr.Position.X = FPMath.Clamp(tr.Position.X + vel.Value.X * dt, negBound, bound);
                    tr.Position.Z = FPMath.Clamp(tr.Position.Z + vel.Value.Z * dt, negBound, bound);
                    tr.Yaw = FPMath.Atan2Deg(vel.Value.X, vel.Value.Z);
                }

                if (entity.Id == _world.PlayerEntityId)
                {
                    _world.PlayerPosition = tr.Position;
                }
            });
        }
    }

    /// <summary>
    /// 7b. PBD 敌军圆形硬碰撞解穿透系统（仅作用于 EnemyTag，玩家与小弟无碰撞阻挡；对称半邻域 2.0m 空间网格 + 精英/普通质量权重 + 2 轮位置投影）。
    /// </summary>
    public sealed class PbdCollisionSystem : QuerySystem<UnitTransform, UnitVelocity>
    {
        private static readonly FP Relaxation = FP.FromMilli(780);
        private readonly BattleWorld _world;
        private readonly List<Entity> _bodyEntities = new(4096);

        public PbdCollisionSystem(BattleWorld world)
        {
            _world = world;
            Filter.AllTags(Tags.Get<EnemyTag>()).WithoutAllTags(Tags.Get<DeadTag>());
        }

        protected override void OnUpdate()
        {
            var solver = _world.CollisionSolver;
            solver.Clear();
            _bodyEntities.Clear();

            Query.ForEachEntity((ref UnitTransform tr, ref UnitVelocity vel, Entity entity) =>
            {
                solver.AddBody(entity.Id, tr.Position.X, tr.Position.Z, vel.Radius, vel.InvMass);
                _bodyEntities.Add(entity);
            });

            solver.Solve(iterations: 2, relaxation: Relaxation);

            int count = _bodyEntities.Count;
            for (int i = 0; i < count; i++)
            {
                var ent = _bodyEntities[i];
                ref readonly var body = ref solver.GetBody(i);
                ref var tr = ref ent.GetComponent<UnitTransform>();
                tr.Position.X = body.PosX;
                tr.Position.Z = body.PosZ;
            }
            _bodyEntities.Clear();
        }
    }

    /// <summary>
    /// 8. 自动普攻系统（受 UnitState.CanAct 约束；攻击时叠加 Attacking 状态而不打断 Moving）。
    /// </summary>
    public sealed class AutoAttackSystem : QuerySystem<UnitTransform, CombatStats, CombatTarget, UnitState>
    {
        private readonly struct PendingShot
        {
            public readonly FPVector3 Origin;
            public readonly FPVector3 Direction;
            public readonly FP Damage;
            public readonly bool IsPlayer;

            public PendingShot(FPVector3 origin, FPVector3 direction, FP damage, bool isPlayer)
            {
                Origin = origin;
                Direction = direction;
                Damage = damage;
                IsPlayer = isPlayer;
            }
        }

        private static readonly FP AttackStateDuration = FP.FromMilli(180);
        private static readonly FP ProjectileSpeed = FP.FromInt(24);
        private static readonly FP ProjectileLife = FP.FromMilli(750);
        private static readonly FP ProjectileHitRadius = FP.FromMilli(550);

        private readonly BattleWorld _world;
        private readonly List<PendingShot> _pendingShots = new(32);

        public AutoAttackSystem(BattleWorld world)
        {
            _world = world;
            Filter.WithoutAllTags(Tags.Get<DeadTag>());
        }

        protected override void OnUpdate()
        {
            FP dt = BattleWorld.LogicDeltaTimeFP;
            var damages = _world.PendingDamages;
            _pendingShots.Clear();

            Query.ForEachEntity((ref UnitTransform tr, ref CombatStats stats, ref CombatTarget target, ref UnitState state, Entity entity) =>
            {
                if (stats.AttackCooldown.RawValue > 0L)
                {
                    stats.AttackCooldown -= dt;
                }
                if (stats.AttackCooldown.RawValue > 0L || !state.CanAct)
                {
                    return;
                }

                bool isEnemy = entity.Tags.Has<EnemyTag>();
                if (!isEnemy)
                {
                    if (_world.SpatialFindNearest(tr.Position, stats.AttackRange, SpatialGrid.TeamEnemy, entity.Id, out var enemyEntry, out _))
                    {
                        target.TargetEntityId = enemyEntry.EntityId;
                        FPVector3 dir = new FPVector3(enemyEntry.Position.X - tr.Position.X, FP.Zero, enemyEntry.Position.Z - tr.Position.Z).NormalizedXZ();
                        if (dir.SqrMagnitudeXZ().RawValue > 0L)
                        {
                            stats.AttackCooldown = stats.AttackInterval;
                            state.TryEnter(UnitStateType.Attacking, AttackStateDuration);
                            _pendingShots.Add(new PendingShot(tr.Position, dir, stats.Atk, entity.Tags.Has<PlayerTag>()));
                        }
                    }
                }
                else
                {
                    if (_world.SpatialFindNearest(tr.Position, stats.AttackRange, SpatialGrid.TeamSquad, entity.Id, out var squadEntry, out _))
                    {
                        stats.AttackCooldown = stats.AttackInterval;
                        state.TryEnter(UnitStateType.Attacking, AttackStateDuration);
                        damages.Add(new DamageRequest(squadEntry.EntityId, stats.Atk));
                    }
                }
            });

            for (int i = 0; i < _pendingShots.Count; i++)
            {
                var s = _pendingShots[i];
                Color boltColor = s.IsPlayer ? new Color(0.99f, 0.88f, 0.28f) : new Color(0.40f, 0.90f, 1.0f);
                _world.SpawnProjectile(s.Origin, s.Direction, ProjectileSpeed, s.Damage, ProjectileLife, ProjectileHitRadius, SpatialGrid.TeamSquad, boltColor, pierceLeft: 0);
            }
            _pendingShots.Clear();
        }
    }

    /// <summary>
    /// 9. 自动技能释放系统：
    /// - 技能1：扇形穿透弹幕；
    /// - 技能2：脉冲冲击波 AOE + 附加 1.2s Stunned（对带 SuperArmor 的精英怪按互斥表自动被免疫！）+ 2.5s Slowed；
    /// - 技能3：小队治疗光环 + 赋予主控与 4 小弟 3.0s SuperArmor 霸体。
    /// </summary>
    public sealed class AutoSkillSystem : QuerySystem<UnitTransform, SkillCaster, UnitState>
    {
        private readonly struct PendingVolley
        {
            public readonly FPVector3 Origin;
            public readonly FPVector3 CenterDir;
            public readonly int Count;
            public readonly FP Damage;
            public readonly bool IsPlayer;

            public PendingVolley(FPVector3 origin, FPVector3 centerDir, int count, FP damage, bool isPlayer)
            {
                Origin = origin;
                CenterDir = centerDir;
                Count = count;
                Damage = damage;
                IsPlayer = isPlayer;
            }
        }

        private readonly struct PendingPulse
        {
            public readonly FPVector3 Origin;
            public readonly FP Radius;
            public readonly Color Color;

            public PendingPulse(FPVector3 origin, FP radius, Color color)
            {
                Origin = origin;
                Radius = radius;
                Color = color;
            }
        }

        private static readonly FP VolleyDetectRange = FP.FromInt(15);
        private static readonly FP NovaStunDuration = FP.FromMilli(1200);
        private static readonly FP SquadArmorDuration = FP.FromInt(3);

        private readonly BattleWorld _world;
        private readonly List<PendingVolley> _volleys = new(16);
        private readonly List<PendingPulse> _pulses = new(8);
        private FP _pendingSquadHeal;

        public AutoSkillSystem(BattleWorld world)
        {
            _world = world;
            Filter.WithoutAllTags(Tags.Get<DeadTag>());
        }

        protected override void OnUpdate()
        {
            FP dt = BattleWorld.LogicDeltaTimeFP;
            var scratch = _world.QueryScratch;
            var damages = _world.PendingDamages;

            _volleys.Clear();
            _pulses.Clear();
            _pendingSquadHeal = FP.Zero;

            Query.ForEachEntity((ref UnitTransform tr, ref SkillCaster caster, ref UnitState state, Entity entity) =>
            {
                bool isPlayer = entity.Tags.Has<PlayerTag>();

                if (caster.VolleyCooldown.RawValue > 0L)
                {
                    caster.VolleyTimer -= dt;
                    if (caster.VolleyTimer.RawValue <= 0L && state.CanAct)
                    {
                        if (_world.SpatialFindNearest(tr.Position, VolleyDetectRange, SpatialGrid.TeamEnemy, entity.Id, out var enemy, out _))
                        {
                            caster.VolleyTimer = caster.VolleyCooldown;
                            FPVector3 dir = new FPVector3(enemy.Position.X - tr.Position.X, FP.Zero, enemy.Position.Z - tr.Position.Z).NormalizedXZ();
                            if (dir.SqrMagnitudeXZ().RawValue > 0L)
                            {
                                _volleys.Add(new PendingVolley(tr.Position, dir, caster.VolleyCount, caster.VolleyDamage, isPlayer));
                            }
                        }
                        else
                        {
                            caster.VolleyTimer = FP.Zero;
                        }
                    }
                }

                if (caster.NovaCooldown.RawValue > 0L)
                {
                    caster.NovaTimer -= dt;
                    if (caster.NovaTimer.RawValue <= 0L && state.CanAct)
                    {
                        _world.SpatialQueryTeamInRadius(tr.Position, caster.NovaRadius, SpatialGrid.TeamEnemy, scratch);
                        if (scratch.Count > 0)
                        {
                            caster.NovaTimer = caster.NovaCooldown;
                            for (int i = 0; i < scratch.Count; i++)
                            {
                                damages.Add(new DamageRequest(scratch[i].EntityId, caster.NovaDamage, UnitStateType.Stunned, NovaStunDuration));
                            }
                            _pulses.Add(new PendingPulse(tr.Position, caster.NovaRadius, new Color(0.99f, 0.75f, 0.14f)));
                        }
                        else
                        {
                            caster.NovaTimer = FP.Zero;
                        }
                    }
                }

                if (caster.HealCooldown.RawValue > 0L)
                {
                    caster.HealTimer -= dt;
                    if (caster.HealTimer.RawValue <= 0L && state.CanAct)
                    {
                        caster.HealTimer = caster.HealCooldown;
                        _pendingSquadHeal += caster.HealAmount;
                        _pulses.Add(new PendingPulse(tr.Position, FP.FromMilli(5500), new Color(0.20f, 0.85f, 0.45f)));
                    }
                }
            });

            if (_pendingSquadHeal.RawValue > 0L)
            {
                var squadQuery = _world.Store.Query<CombatStats, UnitState>().AnyTags(Tags.Get<PlayerTag, AllyTag>()).WithoutAllTags(Tags.Get<DeadTag>());
                squadQuery.ForEachEntity((ref CombatStats stats, ref UnitState st, Entity _) =>
                {
                    stats.Hp = FPMath.Min(stats.MaxHp, stats.Hp + _pendingSquadHeal);
                    st.TryEnter(UnitStateType.SuperArmor, SquadArmorDuration);
                });
            }

            for (int i = 0; i < _volleys.Count; i++)
            {
                var v = _volleys[i];
                int n = Math.Max(1, v.Count);
                FP totalSpreadDeg = n > 1 ? FP.FromInt(36) : FP.Zero;
                FP startDeg = -(totalSpreadDeg * FP.Half);
                FP stepDeg = n > 1 ? totalSpreadDeg / FP.FromInt(n - 1) : FP.Zero;
                Color col = v.IsPlayer ? new Color(1.0f, 0.55f, 0.15f) : new Color(0.65f, 0.85f, 1.0f);

                for (int k = 0; k < n; k++)
                {
                    FP deg = startDeg + stepDeg * FP.FromInt(k);
                    FPVector3 shotDir = v.CenterDir.RotateYDeg(deg);
                    _world.SpawnProjectile(v.Origin, shotDir, FP.FromInt(22), v.Damage, FP.FromMilli(950), FP.FromMilli(650), SpatialGrid.TeamSquad, col, pierceLeft: v.IsPlayer ? 2 : 0);
                }
            }

            for (int i = 0; i < _pulses.Count; i++)
            {
                var p = _pulses[i];
                _world.SpawnPulseEffect(p.Origin, FP.FromMilli(800), p.Radius, FP.FromMilli(350), p.Color);
            }
        }
    }

    /// <summary>
    /// 10. 纯定点数弹道飞行与空间网格命中检测。
    /// </summary>
    public sealed class ProjectileSystem : QuerySystem<UnitTransform, ProjectileData>
    {
        private static readonly FP ExtraHitMargin = FP.FromMilli(550);
        private static readonly FP BoundMargin = FP.FromInt(2);
        private readonly BattleWorld _world;

        public ProjectileSystem(BattleWorld world)
        {
            _world = world;
        }

        protected override void OnUpdate()
        {
            FP dt = BattleWorld.LogicDeltaTimeFP;
            FP limit = BattleWorld.ArenaHalfSize + BoundMargin;
            var damages = _world.PendingDamages;
            var expired = _world.EntityIdScratch;
            expired.Clear();

            Query.ForEachEntity((ref UnitTransform tr, ref ProjectileData proj, Entity entity) =>
            {
                tr.Position += proj.Direction * (proj.Speed * dt);
                proj.LifeTime -= dt;

                if (proj.LifeTime.RawValue <= 0L ||
                    FPMath.Abs(tr.Position.X) > limit ||
                    FPMath.Abs(tr.Position.Z) > limit)
                {
                    expired.Add(entity.Id);
                    return;
                }

                byte targetTeam = proj.Team == SpatialGrid.TeamSquad ? SpatialGrid.TeamEnemy : SpatialGrid.TeamSquad;
                if (_world.SpatialFindNearest(tr.Position, proj.HitRadius + ExtraHitMargin, targetTeam, -1, out var hit, out _))
                {
                    damages.Add(new DamageRequest(hit.EntityId, proj.Damage));
                    if (proj.PierceLeft > 0)
                    {
                        proj.PierceLeft--;
                    }
                    else
                    {
                        expired.Add(entity.Id);
                    }
                }
            });

            var store = _world.Store;
            for (int i = 0; i < expired.Count; i++)
            {
                if (store.TryGetEntityById(expired[i], out var ent) && !ent.IsNull)
                {
                    ent.DeleteEntity();
                }
            }
            expired.Clear();
        }
    }

    /// <summary>
    /// 11. 脉冲特效纯定点数缩放推进与销毁。
    /// </summary>
    public sealed class PulseEffectSystem : QuerySystem<UnitTransform, PulseEffect, CubeVisual>
    {
        private readonly BattleWorld _world;

        public PulseEffectSystem(BattleWorld world)
        {
            _world = world;
        }

        protected override void OnUpdate()
        {
            FP dt = BattleWorld.LogicDeltaTimeFP;
            var expired = _world.EntityIdScratch;
            expired.Clear();

            Query.ForEachEntity((ref UnitTransform tr, ref PulseEffect fx, ref CubeVisual vis, Entity entity) =>
            {
                fx.Elapsed += dt;
                if (fx.Elapsed >= fx.Duration)
                {
                    expired.Add(entity.Id);
                    return;
                }

                FP t = FPMath.Clamp01(fx.Elapsed / fx.Duration);
                FP inv = FP.One - t;
                FP easeOut = FP.One - inv * inv;
                FP radius = FPMath.Lerp(fx.StartRadius, fx.EndRadius, easeOut);
                FP height = FPMath.Lerp(fx.Height, FP.FromMilli(20), t);
                FP diameter = radius * FP.FromInt(2);

                tr.Scale = new FPVector3(diameter, height, diameter);
                tr.Position.Y = height * FP.Half + FP.FromMilli(20);
                vis.BaseColor = Color.Lerp(fx.Color, fx.Color * 0.25f, t.ToFloat());
            });

            var store = _world.Store;
            for (int i = 0; i < expired.Count; i++)
            {
                if (store.TryGetEntityById(expired[i], out var ent) && !ent.IsNull)
                {
                    ent.DeleteEntity();
                }
            }
            expired.Clear();
        }
    }

    /// <summary>
    /// 12a. 波次计时推进（触发下游 O(K) 分桶 ReactiveQuerySystem）。
    /// </summary>
    public sealed class WaveDirectorSystem : QuerySystem<WaveDirector>
    {
        private readonly BattleWorld _world;
        private readonly List<(Entity Entity, WaveDirector NextWave)> _pendingWaveUps = new(2);

        public WaveDirectorSystem(BattleWorld world)
        {
            _world = world;
        }

        protected override void OnUpdate()
        {
            FP dt = BattleWorld.LogicDeltaTimeFP;
            _pendingWaveUps.Clear();

            Query.ForEachEntity((ref WaveDirector wave, Entity entity) =>
            {
                wave.WaveTimer -= dt;
                if (wave.WaveTimer.RawValue <= 0L)
                {
                    var next = wave;
                    next.WaveIndex++;
                    next.WaveTimer = next.WaveInterval;
                    next.SpawnPerWave = Math.Min(400, next.SpawnPerWave + 30);
                    _pendingWaveUps.Add((entity, next));
                }
            });

            for (int i = 0; i < _pendingWaveUps.Count; i++)
            {
                var (ent, nextWave) = _pendingWaveUps[i];
                ent.AddComponent(nextWave);
            }
        }
    }

    /// <summary>
    /// 12b. 响应式波次刷怪系统（基于 Store 分桶脏中心 O(K) 触发）。
    /// </summary>
    public sealed class WaveSpawnReactiveSystem : ReactiveQuerySystem<WaveDirector>
    {
        private readonly BattleWorld _world;

        public WaveSpawnReactiveSystem(BattleWorld world)
        {
            _world = world;
        }

        protected override bool TriggerOnEntityEnter => false;

        protected override void OnReactiveUpdateEntity(Entity entity, Type componentType, ComponentChangedAction action)
        {
            if (action != ComponentChangedAction.Update) return;
            ref var wave = ref entity.GetComponent<WaveDirector>();
            _world.SpawnEnemyBatch(wave.SpawnPerWave, wave.MaxEnemyCap);
        }
    }

    /// <summary>
    /// 13. 死亡实体批量清理（主控/小弟阵亡原地复活并重置位掩码状态，保证压测不断档）。
    /// </summary>
    public sealed class DeathCleanupSystem : BaseSystem
    {
        private readonly BattleWorld _world;

        public DeathCleanupSystem(BattleWorld world)
        {
            _world = world;
        }

        protected override void OnUpdateGroup()
        {
            var store = _world.Store;
            var deadIds = _world.EntityIdScratch;
            deadIds.Clear();

            var deadQuery = store.Query().AllTags(Tags.Get<DeadTag>());
            foreach (var ent in deadQuery.Entities)
            {
                deadIds.Add(ent.Id);
            }

            for (int i = 0; i < deadIds.Count; i++)
            {
                if (!store.TryGetEntityById(deadIds[i], out var ent) || ent.IsNull) continue;

                if (ent.Tags.Has<PlayerTag>() || ent.Tags.Has<AllyTag>())
                {
                    ent.RemoveTag<DeadTag>();
                    ref var stats = ref ent.GetComponent<CombatStats>();
                    stats.Hp = stats.MaxHp;
                    if (ent.HasComponent<UnitState>())
                    {
                        ref var st = ref ent.GetComponent<UnitState>();
                        st.ResetAlive();
                        st.TryEnter(UnitStateType.SuperArmor, FP.FromInt(2));
                    }
                    if (ent.HasComponent<UnitTransform>())
                    {
                        _world.SpawnPulseEffect(ent.GetComponent<UnitTransform>().Position, FP.Half, FP.FromInt(4), FP.FromMilli(350), new Color(0.25f, 0.95f, 0.65f));
                    }
                }
                else
                {
                    ent.DeleteEntity();
                }
            }
            deadIds.Clear();
        }
    }
}

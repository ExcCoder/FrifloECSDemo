using System;
using System.Collections.Generic;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using HotUpdate.ECS.Buff;
using HotUpdate.ECS.Components;
using HotUpdate.ECS.FixedPoint;
using HotUpdate.ECS.Spatial;
using HotUpdate.ECS.State;
using UnityEngine;

namespace HotUpdate.ECS.Systems
{
    /// <summary>1. 每帧重建 Q48.16 定点数空间索引。</summary>
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
    /// 2. 零 GC Buff 计时推进系统（对应笔记第 4 篇 300 DOT 灼烧周期扣血与 500 护盾过期回收）。
    /// </summary>
    public sealed class BuffTickSystem : QuerySystem<UnitBuffs>
    {
        private static readonly FP BurnTickInterval = FP.FromMilli(500);
        private readonly BattleWorld _world;

        public BuffTickSystem(BattleWorld world)
        {
            _world = world;
            Filter.WithoutAllTags(Tags.Get<DeadTag>());
        }

        protected override void OnUpdate()
        {
            FP dt = BattleWorld.LogicDeltaTimeFP;
            var damages = _world.PendingDamages;

            Query.ForEachEntity((ref UnitBuffs buffs, Entity entity) =>
            {
                if (buffs.Shield.RawValue > 0L && buffs.ShieldTimer.RawValue > 0L)
                {
                    buffs.ShieldTimer -= dt;
                    if (buffs.ShieldTimer.RawValue <= 0L)
                    {
                        buffs.Shield = FP.Zero;
                        buffs.ShieldTimer = FP.Zero;
                    }
                }

                if (buffs.BurnStacks > 0 && buffs.BurnTimer.RawValue > 0L)
                {
                    buffs.BurnTimer -= dt;
                    buffs.BurnTickTimer -= dt;

                    if (buffs.BurnTickTimer.RawValue <= 0L)
                    {
                        buffs.BurnTickTimer += BurnTickInterval;
                        FP dotDmg = buffs.BurnDamagePerTick * FP.FromInt(buffs.BurnStacks);
                        damages.Add(new DamageRequest(entity.Id, dotDmg, isTrueOrDotDamage: true));
                    }

                    if (buffs.BurnTimer.RawValue <= 0L)
                    {
                        buffs.BurnTimer = FP.Zero;
                        buffs.BurnTickTimer = FP.Zero;
                        buffs.BurnStacks = 0;
                    }
                }
            });
        }
    }

    /// <summary>3. 推进位掩码限时状态。</summary>
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
    /// 4. 确定性 8 步伤害结算管线（严格对照笔记《确定性顺序的伤害管线实现详解》）：
    /// 1) 死亡短路 → 2) 多乘区计算（技能倍率 × 确定性 FPRandom 暴击 × 抗性穿透）→
    /// 3) 500 护盾优先吸收 → 4) 700 受击闪白与控制/霸体互斥 → 5) 血量扣减钳制 →
    /// 6) 节点 31 伤后衍生（吸血 + 300 灼烧叠层）→ 7) 904 免死抢救 → 8) 致死打 DeadTag 与经验升级。
    /// </summary>
    public sealed class DamageResolveSystem : BaseSystem
    {
        private static readonly FP HitFlashDuration = FP.FromMilli(90);
        private static readonly FP NovaSlowDuration = FP.FromMilli(2500);
        private static readonly FP BurnDuration = FP.FromInt(4);
        private static readonly FP BurnBaseDps = FP.FromInt(12);
        private static readonly FP UndyingRestoreRatio = FP.FromMilli(350);
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

                // 阶段 1：多乘区伤害计算（非 DOT 真伤才走暴击与抗性乘区）
                FP finalDmg = req.BaseAmount;
                Entity attacker = default;
                bool hasAttacker = req.AttackerEntityId > 0 &&
                                   store.TryGetEntityById(req.AttackerEntityId, out attacker) &&
                                   !attacker.IsNull;

                bool hasTargetBuffs = target.HasComponent<UnitBuffs>();

                if (!req.IsTrueOrDotDamage)
                {
                    if (hasAttacker && !attacker.Tags.Has<EnemyTag>())
                    {
                        finalDmg *= _world.SkillModifiers.SkillDmgMultiplier;
                    }

                    FP critRate = FP.Zero;
                    FP critMul = FP.One;
                    FP resistPen = FP.Zero;

                    if (hasAttacker && attacker.HasComponent<UnitBuffs>())
                    {
                        ref var atkBuff = ref attacker.GetComponent<UnitBuffs>();
                        critRate = atkBuff.CritRate;
                        critMul = atkBuff.CritDmgMul;
                        resistPen = atkBuff.ResistPen;
                    }

                    if (critRate.RawValue > 0L && _world.RollCrit(critRate))
                    {
                        finalDmg *= critMul;
                        _world.CritHitCount++;
                    }

                    if (hasTargetBuffs)
                    {
                        FP rawRes = target.GetComponent<UnitBuffs>().ArmorResist;
                        if (rawRes.RawValue > 0L)
                        {
                            FP effRes = rawRes * (FP.One - FPMath.Clamp01(resistPen));
                            FP immuneRate = FPMath.Clamp01(FP.One - effRes);
                            finalDmg *= immuneRate;
                        }
                    }
                }

                // 阶段 2（500）：护盾优先吸收
                FP actualHpDamage = finalDmg;
                if (actualHpDamage.RawValue > 0L && hasTargetBuffs)
                {
                    ref var targetBuff = ref target.GetComponent<UnitBuffs>();
                    if (targetBuff.Shield.RawValue > 0L)
                    {
                        if (targetBuff.Shield >= actualHpDamage)
                        {
                            targetBuff.Shield -= actualHpDamage;
                            actualHpDamage = FP.Zero;
                        }
                        else
                        {
                            actualHpDamage -= targetBuff.Shield;
                            targetBuff.Shield = FP.Zero;
                            targetBuff.ShieldTimer = FP.Zero;
                        }
                    }
                }

                // 阶段 3（700）：受击闪白与附加控制（遵守互斥表与霸体免疫）
                bool hasState = target.HasComponent<UnitState>();
                if (hasState)
                {
                    ref var st = ref target.GetComponent<UnitState>();
                    st.TryEnter(UnitStateType.HitFlash, HitFlashDuration);

                    if (req.ApplyState != UnitStateType.HitFlash && req.ApplyStateDuration.RawValue > 0L)
                    {
                        st.TryEnter(req.ApplyState, req.ApplyStateDuration);
                        if (req.ApplyState == UnitStateType.Stunned)
                        {
                            st.TryEnter(UnitStateType.Slowed, NovaSlowDuration);
                        }
                    }
                }

                // 阶段 4：扣血与钳制
                ref var stats = ref target.GetComponent<CombatStats>();
                stats.Hp = FPMath.Clamp(stats.Hp - actualHpDamage, FP.Zero, stats.MaxHp);

                // 阶段 5（节点 31）：实际扣血 > 0 时触发吸血与灼烧 DOT
                if (actualHpDamage.RawValue > 0L)
                {
                    if (hasAttacker && !attacker.Tags.Has<DeadTag>() &&
                        attacker.HasComponent<UnitBuffs>() && attacker.HasComponent<CombatStats>())
                    {
                        FP vamp = attacker.GetComponent<UnitBuffs>().LifestealRate;
                        if (vamp.RawValue > 0L)
                        {
                            ref var atkStats = ref attacker.GetComponent<CombatStats>();
                            atkStats.Hp = FPMath.Min(atkStats.MaxHp, atkStats.Hp + actualHpDamage * vamp);
                        }
                    }

                    if (req.ApplyBurnOnHit && hasTargetBuffs && stats.Hp.RawValue > 0L)
                    {
                        target.GetComponent<UnitBuffs>().ApplyBurn(BurnBaseDps, BurnDuration);
                    }
                }

                // 阶段 6（904）：致死前免死抢救判定
                if (stats.Hp.RawValue <= 0L)
                {
                    if (hasTargetBuffs)
                    {
                        ref var targetBuff = ref target.GetComponent<UnitBuffs>();
                        if (targetBuff.UndyingCharges > 0)
                        {
                            targetBuff.UndyingCharges--;
                            stats.Hp = FPMath.Max(FP.One, stats.MaxHp * UndyingRestoreRatio);
                            _world.UndyingTriggerCount++;

                            if (hasState)
                            {
                                target.GetComponent<UnitState>().TryEnter(UnitStateType.SuperArmor, FP.FromInt(2));
                            }
                            if (target.HasComponent<UnitTransform>())
                            {
                                _world.SpawnPulseEffect(target.GetComponent<UnitTransform>().Position, FP.Half, FP.FromInt(4), FP.FromMilli(400), new Color(1.0f, 0.92f, 0.35f));
                            }
                            continue;
                        }
                    }

                    // 阶段 7：正式致死 + 经验发放
                    stats.Hp = FP.Zero;
                    if (hasState)
                    {
                        target.GetComponent<UnitState>().TryEnter(UnitStateType.Dead);
                    }
                    if (target.Tags.Has<EnemyTag>())
                    {
                        _world.TotalKills++;
                        _world.GrantSquadExp(1);
                    }
                    target.AddTag<DeadTag>();
                }
            }

            damages.Clear();
        }
    }

    /// <summary>5. 主控输入系统。</summary>
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

    /// <summary>6. 4 个自动跟随小弟 AI。</summary>
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

    /// <summary>7. 千级敌军集群 AI（支持切向包抄绕行）。</summary>
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

    /// <summary>8a. 纯定点数运动积分。</summary>
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

    /// <summary>8b. PBD 敌军圆形硬碰撞解穿透（仅作用于 EnemyTag，玩家与小弟无阻挡）。</summary>
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

    /// <summary>9. 自动普攻系统（支持词条改造为追踪弹、弹射弹与灼烧附魔）。</summary>
    public sealed class AutoAttackSystem : QuerySystem<UnitTransform, CombatStats, CombatTarget, UnitState>
    {
        private readonly struct PendingShot
        {
            public readonly int AttackerId;
            public readonly FPVector3 Origin;
            public readonly FPVector3 Direction;
            public readonly FP Damage;
            public readonly bool IsPlayer;
            public readonly int TargetId;
            public readonly FPVector3 TargetPos;

            public PendingShot(int attackerId, FPVector3 origin, FPVector3 direction, FP damage, bool isPlayer, int targetId, FPVector3 targetPos)
            {
                AttackerId = attackerId;
                Origin = origin;
                Direction = direction;
                Damage = damage;
                IsPlayer = isPlayer;
                TargetId = targetId;
                TargetPos = targetPos;
            }
        }

        private static readonly FP AttackStateDuration = FP.FromMilli(180);
        private static readonly FP ProjectileSpeed = FP.FromInt(24);
        private static readonly FP ProjectileLife = FP.FromMilli(950);
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
            var mod = _world.SkillModifiers;
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
                            stats.AttackCooldown = stats.AttackInterval * mod.CooldownScale;
                            state.TryEnter(UnitStateType.Attacking, AttackStateDuration);
                            _pendingShots.Add(new PendingShot(entity.Id, tr.Position, dir, stats.Atk, entity.Tags.Has<PlayerTag>(), enemyEntry.EntityId, enemyEntry.Position));
                        }
                    }
                }
                else
                {
                    if (_world.SpatialFindNearest(tr.Position, stats.AttackRange, SpatialGrid.TeamSquad, entity.Id, out var squadEntry, out _))
                    {
                        stats.AttackCooldown = stats.AttackInterval;
                        state.TryEnter(UnitStateType.Attacking, AttackStateDuration);
                        damages.Add(new DamageRequest(squadEntry.EntityId, stats.Atk, attackerEntityId: entity.Id));
                    }
                }
            });

            ProjectileKind kind = mod.EnableHomingShots ? ProjectileKind.Homing : ProjectileKind.Linear;
            for (int i = 0; i < _pendingShots.Count; i++)
            {
                var s = _pendingShots[i];
                Color boltColor = kind == ProjectileKind.Homing
                    ? new Color(0.95f, 0.35f, 0.95f)
                    : (s.IsPlayer ? new Color(0.99f, 0.88f, 0.28f) : new Color(0.40f, 0.90f, 1.0f));

                _world.SpawnProjectile(
                    s.Origin, s.Direction, ProjectileSpeed, s.Damage, ProjectileLife, ProjectileHitRadius,
                    SpatialGrid.TeamSquad, boltColor,
                    attackerEntityId: s.AttackerId,
                    pierceLeft: mod.ExtraPierceCount,
                    bounceLeft: mod.ExtraBounceCount,
                    kind: kind,
                    targetEntityId: s.TargetId,
                    targetPos: s.TargetPos,
                    applyBurn: mod.EnableBurnOnHit);
            }
            _pendingShots.Clear();
        }
    }

    /// <summary>
    /// 10. 自动技能释放系统（自动应用 200~209 号词条加成：连发 / 弹射 / 穿透 / CD 缩减 / 龙卷风解锁）。
    /// </summary>
    public sealed class AutoSkillSystem : QuerySystem<UnitTransform, SkillCaster, UnitState>
    {
        private readonly struct PendingVolley
        {
            public readonly int AttackerId;
            public readonly FPVector3 Origin;
            public readonly FPVector3 CenterDir;
            public readonly int Count;
            public readonly FP Damage;
            public readonly bool IsPlayer;

            public PendingVolley(int attackerId, FPVector3 origin, FPVector3 centerDir, int count, FP damage, bool isPlayer)
            {
                AttackerId = attackerId;
                Origin = origin;
                CenterDir = centerDir;
                Count = count;
                Damage = damage;
                IsPlayer = isPlayer;
            }
        }

        private readonly struct PendingTornado
        {
            public readonly int AttackerId;
            public readonly FPVector3 Origin;
            public readonly FPVector3 Dir;
            public readonly FP Damage;

            public PendingTornado(int attackerId, FPVector3 origin, FPVector3 dir, FP damage)
            {
                AttackerId = attackerId;
                Origin = origin;
                Dir = dir;
                Damage = damage;
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
        private readonly List<PendingTornado> _tornadoes = new(4);
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
            var mod = _world.SkillModifiers;

            _volleys.Clear();
            _tornadoes.Clear();
            _pulses.Clear();
            _pendingSquadHeal = FP.Zero;

            Query.ForEachEntity((ref UnitTransform tr, ref SkillCaster caster, ref UnitState state, Entity entity) =>
            {
                bool isPlayer = entity.Tags.Has<PlayerTag>();

                // 技能 1：扇形弹幕（叠加 201 连发与 206 CD 缩减）
                if (caster.VolleyCooldown.RawValue > 0L)
                {
                    caster.VolleyTimer -= dt;
                    if (caster.VolleyTimer.RawValue <= 0L && state.CanAct)
                    {
                        if (_world.SpatialFindNearest(tr.Position, VolleyDetectRange, SpatialGrid.TeamEnemy, entity.Id, out var enemy, out _))
                        {
                            caster.VolleyTimer = caster.VolleyCooldown * mod.CooldownScale;
                            FPVector3 dir = new FPVector3(enemy.Position.X - tr.Position.X, FP.Zero, enemy.Position.Z - tr.Position.Z).NormalizedXZ();
                            if (dir.SqrMagnitudeXZ().RawValue > 0L)
                            {
                                int totalCount = caster.VolleyCount + (isPlayer ? mod.ExtraVolleyCount : Math.Min(2, mod.ExtraVolleyCount));
                                _volleys.Add(new PendingVolley(entity.Id, tr.Position, dir, totalCount, caster.VolleyDamage, isPlayer));
                            }
                        }
                        else
                        {
                            caster.VolleyTimer = FP.Zero;
                        }
                    }
                }

                // 技能 2：脉冲冲击波 AOE
                if (caster.NovaCooldown.RawValue > 0L)
                {
                    caster.NovaTimer -= dt;
                    if (caster.NovaTimer.RawValue <= 0L && state.CanAct)
                    {
                        _world.SpatialQueryTeamInRadius(tr.Position, caster.NovaRadius, SpatialGrid.TeamEnemy, scratch);
                        if (scratch.Count > 0)
                        {
                            caster.NovaTimer = caster.NovaCooldown * mod.CooldownScale;
                            for (int i = 0; i < scratch.Count; i++)
                            {
                                damages.Add(new DamageRequest(
                                    scratch[i].EntityId,
                                    caster.NovaDamage,
                                    UnitStateType.Stunned,
                                    NovaStunDuration,
                                    attackerEntityId: entity.Id,
                                    applyBurnOnHit: mod.EnableBurnOnHit));
                            }
                            _pulses.Add(new PendingPulse(tr.Position, caster.NovaRadius, new Color(0.99f, 0.75f, 0.14f)));
                        }
                        else
                        {
                            caster.NovaTimer = FP.Zero;
                        }
                    }
                }

                // 技能 3：小队治疗光环 + 护盾 + 霸体
                if (caster.HealCooldown.RawValue > 0L)
                {
                    caster.HealTimer -= dt;
                    if (caster.HealTimer.RawValue <= 0L && state.CanAct)
                    {
                        caster.HealTimer = caster.HealCooldown * mod.CooldownScale;
                        _pendingSquadHeal += caster.HealAmount;
                        _pulses.Add(new PendingPulse(tr.Position, FP.FromMilli(5500), new Color(0.20f, 0.85f, 0.45f)));
                    }
                }

                // 技能 4：龙卷风暴（选卡解锁后自动释放 Tornado 持续多段弹道）
                if (mod.EnableTornado && caster.TornadoCooldown.RawValue > 0L)
                {
                    caster.TornadoTimer -= dt;
                    if (caster.TornadoTimer.RawValue <= 0L && state.CanAct)
                    {
                        if (_world.SpatialFindNearest(tr.Position, VolleyDetectRange, SpatialGrid.TeamEnemy, entity.Id, out var enemy, out _))
                        {
                            caster.TornadoTimer = caster.TornadoCooldown * mod.CooldownScale;
                            FPVector3 dir = new FPVector3(enemy.Position.X - tr.Position.X, FP.Zero, enemy.Position.Z - tr.Position.Z).NormalizedXZ();
                            if (dir.SqrMagnitudeXZ().RawValue > 0L)
                            {
                                _tornadoes.Add(new PendingTornado(entity.Id, tr.Position, dir, caster.TornadoDamage));
                            }
                        }
                        else
                        {
                            caster.TornadoTimer = FP.Zero;
                        }
                    }
                }
            });

            if (_pendingSquadHeal.RawValue > 0L)
            {
                var squadQuery = _world.Store.Query<CombatStats, UnitState, UnitBuffs>().AnyTags(Tags.Get<PlayerTag, AllyTag>()).WithoutAllTags(Tags.Get<DeadTag>());
                squadQuery.ForEachEntity((ref CombatStats stats, ref UnitState st, ref UnitBuffs bf, Entity _) =>
                {
                    stats.Hp = FPMath.Min(stats.MaxHp, stats.Hp + _pendingSquadHeal);
                    bf.AddShield(FP.FromInt(60), FP.FromInt(8));
                    st.TryEnter(UnitStateType.SuperArmor, SquadArmorDuration);
                });
            }

            for (int i = 0; i < _volleys.Count; i++)
            {
                var v = _volleys[i];
                int n = Math.Max(1, v.Count);
                FP totalSpreadDeg = n > 1 ? FP.FromInt(Math.Min(70, 28 + n * 4)) : FP.Zero;
                FP startDeg = -(totalSpreadDeg * FP.Half);
                FP stepDeg = n > 1 ? totalSpreadDeg / FP.FromInt(n - 1) : FP.Zero;
                Color col = v.IsPlayer ? new Color(1.0f, 0.55f, 0.15f) : new Color(0.65f, 0.85f, 1.0f);

                for (int k = 0; k < n; k++)
                {
                    FP deg = startDeg + stepDeg * FP.FromInt(k);
                    FPVector3 shotDir = v.CenterDir.RotateYDeg(deg);
                    _world.SpawnProjectile(
                        v.Origin, shotDir, FP.FromInt(22), v.Damage, FP.FromMilli(950), FP.FromMilli(650),
                        SpatialGrid.TeamSquad, col,
                        attackerEntityId: v.AttackerId,
                        pierceLeft: (v.IsPlayer ? 2 : 0) + mod.ExtraPierceCount,
                        bounceLeft: mod.ExtraBounceCount,
                        kind: ProjectileKind.Linear,
                        applyBurn: mod.EnableBurnOnHit);
                }
            }

            for (int i = 0; i < _tornadoes.Count; i++)
            {
                var t = _tornadoes[i];
                _world.SpawnProjectile(
                    t.Origin, t.Dir, FP.FromMilli(4500), t.Damage, FP.FromInt(5), FP.FromMilli(2600),
                    SpatialGrid.TeamSquad, new Color(0.55f, 0.35f, 0.98f),
                    attackerEntityId: t.AttackerId,
                    kind: ProjectileKind.Tornado,
                    applyBurn: mod.EnableBurnOnHit);
            }

            for (int i = 0; i < _pulses.Count; i++)
            {
                var p = _pulses[i];
                _world.SpawnPulseEffect(p.Origin, FP.FromMilli(800), p.Radius, FP.FromMilli(350), p.Color);
            }
        }
    }

    /// <summary>
    /// 11. 三形态确定性弹道系统（对应笔记第 3 篇）：
    /// - Linear（Free）：直线飞行 + 穿透 + 命中弹射；
    /// - Homing（Precise）：实时追踪目标（目标死亡则飞向其最后存活坐标）；
    /// - Tornado（Tornado）：慢速旋转柱体，每 0.3s 周期对周围 2.6m 敌军造成多段 AOE 伤害！
    /// </summary>
    public sealed class ProjectileSystem : QuerySystem<UnitTransform, ProjectileData>
    {
        private static readonly FP ExtraHitMargin = FP.FromMilli(550);
        private static readonly FP BounceSearchRange = FP.FromMilli(8500);
        private static readonly FP TornadoTickInterval = FP.FromMilli(300);
        private static readonly FP TornadoSpinSpeed = FP.FromInt(360);
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
            var scratch = _world.QueryScratch;
            var expired = _world.EntityIdScratch;
            var store = _world.Store;
            expired.Clear();

            Query.ForEachEntity((ref UnitTransform tr, ref ProjectileData proj, Entity entity) =>
            {
                proj.LifeTime -= dt;

                // 精确制导追踪弹：更新目标方向
                if (proj.Kind == ProjectileKind.Homing)
                {
                    FPVector3 aimPos = proj.LastTargetPos;
                    if (proj.TargetEntityId > 0 &&
                        store.TryGetEntityById(proj.TargetEntityId, out var tgt) &&
                        !tgt.IsNull && !tgt.Tags.Has<DeadTag>() &&
                        tgt.HasComponent<UnitTransform>())
                    {
                        aimPos = tgt.GetComponent<UnitTransform>().Position;
                        proj.LastTargetPos = aimPos;
                    }

                    FPVector3 toAim = new(aimPos.X - tr.Position.X, FP.Zero, aimPos.Z - tr.Position.Z);
                    if (toAim.SqrMagnitudeXZ().RawValue > 64L)
                    {
                        proj.Direction = toAim.NormalizedXZ();
                        tr.Yaw = FPMath.Atan2Deg(proj.Direction.X, proj.Direction.Z);
                    }
                }

                tr.Position += proj.Direction * (proj.Speed * dt);

                if (proj.LifeTime.RawValue <= 0L ||
                    FPMath.Abs(tr.Position.X) > limit ||
                    FPMath.Abs(tr.Position.Z) > limit)
                {
                    expired.Add(entity.Id);
                    return;
                }

                byte targetTeam = proj.Team == SpatialGrid.TeamSquad ? SpatialGrid.TeamEnemy : SpatialGrid.TeamSquad;

                // 龙卷风持续多段判定弹
                if (proj.Kind == ProjectileKind.Tornado)
                {
                    tr.Yaw += TornadoSpinSpeed * dt;
                    proj.TickTimer -= dt;
                    if (proj.TickTimer.RawValue <= 0L)
                    {
                        proj.TickTimer = TornadoTickInterval;
                        _world.SpatialQueryTeamInRadius(tr.Position, proj.HitRadius, targetTeam, scratch);
                        for (int i = 0; i < scratch.Count; i++)
                        {
                            damages.Add(new DamageRequest(
                                scratch[i].EntityId,
                                proj.Damage,
                                attackerEntityId: proj.AttackerEntityId,
                                applyBurnOnHit: proj.ApplyBurn));
                        }
                    }
                    return;
                }

                // 直线 / 追踪弹单点命中 + 弹射 + 穿透
                if (_world.SpatialFindNearest(tr.Position, proj.HitRadius + ExtraHitMargin, targetTeam, proj.LastHitEntityId, out var hit, out _))
                {
                    damages.Add(new DamageRequest(
                        hit.EntityId,
                        proj.Damage,
                        attackerEntityId: proj.AttackerEntityId,
                        applyBurnOnHit: proj.ApplyBurn));
                    proj.LastHitEntityId = hit.EntityId;

                    if (proj.BounceLeft > 0 &&
                        _world.SpatialFindNearest(hit.Position, BounceSearchRange, targetTeam, hit.EntityId, out var nextTarget, out _))
                    {
                        proj.BounceLeft--;
                        FPVector3 bounceDir = new FPVector3(nextTarget.Position.X - tr.Position.X, FP.Zero, nextTarget.Position.Z - tr.Position.Z).NormalizedXZ();
                        if (bounceDir.SqrMagnitudeXZ().RawValue > 0L)
                        {
                            proj.Direction = bounceDir;
                            proj.TargetEntityId = nextTarget.EntityId;
                            proj.LastTargetPos = nextTarget.Position;
                            tr.Yaw = FPMath.Atan2Deg(bounceDir.X, bounceDir.Z);
                        }
                    }
                    else if (proj.PierceLeft > 0)
                    {
                        proj.PierceLeft--;
                    }
                    else
                    {
                        expired.Add(entity.Id);
                    }
                }
            });

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

    /// <summary>12. 脉冲特效推进。</summary>
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

    /// <summary>13a. 波次计时推进。</summary>
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

    /// <summary>13b. O(K) 响应式波次刷怪。</summary>
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

    /// <summary>14. 死亡实体批量清理。</summary>
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

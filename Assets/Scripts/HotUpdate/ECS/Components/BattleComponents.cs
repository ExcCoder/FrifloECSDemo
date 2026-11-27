using Friflo.Engine.ECS;
using HotUpdate.ECS.FixedPoint;
using UnityEngine;

namespace HotUpdate.ECS.Components
{
    // ================= 阵营与状态标签（零体积，用于高速 Archetype 过滤） =================

    public struct PlayerTag : ITag { }
    public struct AllyTag : ITag { }
    public struct EnemyTag : ITag { }
    public struct DeadTag : ITag { }

    // ================= 空间与移动纯数据组件（全 Q48.16 定点数） =================

    public struct UnitTransform : IComponent
    {
        public FPVector3 Position;
        public FP Yaw;
        public FPVector3 Scale;

        public UnitTransform(FPVector3 position, FPVector3 scale, FP yaw = default)
        {
            Position = position;
            Scale = scale;
            Yaw = yaw;
        }
    }

    public struct UnitVelocity : IComponent
    {
        public FPVector3 Value;
        public FP MoveSpeed;
        public FP Radius;
        /// <summary>反质量（越小越重，主控=0.08 几乎推不动，精英=0.35，普通怪=1.0）。</summary>
        public FP InvMass;

        public UnitVelocity(FP moveSpeed, FP radius, FP invMass = default)
        {
            Value = FPVector3.Zero;
            MoveSpeed = moveSpeed;
            Radius = radius;
            InvMass = invMass.RawValue > 0L ? invMass : FP.One;
        }
    }

    public struct AllyFormation : IComponent
    {
        public int SlotIndex;
        public FPVector3 LocalOffset;
        public FP LeashDistance;

        public AllyFormation(int slotIndex, FPVector3 localOffset, FP leashDistance)
        {
            SlotIndex = slotIndex;
            LocalOffset = localOffset;
            LeashDistance = leashDistance;
        }
    }

    // ================= 战斗属性与索敌组件（全 Q48.16 定点数） =================

    public struct CombatStats : IComponent
    {
        public FP Hp;
        public FP MaxHp;
        public FP Atk;
        public FP AttackRange;
        public FP AggroRange;
        public FP AttackInterval;
        public FP AttackCooldown;

        public CombatStats(FP maxHp, FP atk, FP attackRange, FP aggroRange, FP attackInterval)
        {
            Hp = maxHp;
            MaxHp = maxHp;
            Atk = atk;
            AttackRange = attackRange;
            AggroRange = aggroRange;
            AttackInterval = attackInterval;
            AttackCooldown = FP.Zero;
        }
    }

    public struct CombatTarget : IComponent
    {
        public int TargetEntityId;
    }

    // ================= 自动技能与弹道/特效组件 =================

    public struct SkillCaster : IComponent
    {
        // 技能1：扇形多发弹幕
        public FP VolleyCooldown;
        public FP VolleyTimer;
        public FP VolleyDamage;
        public int VolleyCount;

        // 技能2：范围脉冲冲击波 AOE（附带眩晕 + 减速）
        public FP NovaCooldown;
        public FP NovaTimer;
        public FP NovaRadius;
        public FP NovaDamage;

        // 技能3：小队治疗光环（恢复血量 + 赋予霸体 SuperArmor）
        public FP HealCooldown;
        public FP HealTimer;
        public FP HealAmount;
    }

    public struct ProjectileData : IComponent
    {
        public FPVector3 Direction;
        public FP Speed;
        public FP Damage;
        public FP LifeTime;
        public FP HitRadius;
        public byte Team; // 0 = 我方小队, 1 = 敌方
        public int PierceLeft;
    }

    public struct PulseEffect : IComponent
    {
        public FP Elapsed;
        public FP Duration;
        public FP StartRadius;
        public FP EndRadius;
        public FP Height;
        public Color Color;
    }

    // ================= 表现层纯数据组件（无 GameObject 引用） =================

    public struct CubeVisual : IComponent
    {
        public Color BaseColor;
        /// <summary>由 CullingRoot 中的 FrustumCullingSystem 更新；未开启视锥裁切时恒为 true。</summary>
        public bool IsVisible;

        public CubeVisual(Color baseColor)
        {
            BaseColor = baseColor;
            IsVisible = true;
        }
    }

    // ================= 波次控制器组件（由 ReactiveQuerySystem 监听响应） =================

    public struct WaveDirector : IComponent
    {
        public int WaveIndex;
        public FP WaveTimer;
        public FP WaveInterval;
        public int SpawnPerWave;
        public int MaxEnemyCap;
    }
}

using Friflo.Engine.ECS;
using HotUpdate.ECS.FixedPoint;
using UnityEngine;

namespace HotUpdate.ECS.Components
{
    public struct PlayerTag : ITag { }
    public struct AllyTag : ITag { }
    public struct EnemyTag : ITag { }
    public struct DeadTag : ITag { }

    public enum ProjectileKind : byte
    {
        Linear = 0,   // Free：直线（支持穿透 + 弹射）
        Homing = 1,   // Precise：精确制导追踪弹
        Tornado = 2,  // Tornado：慢速移动持续多段 AOE 龙卷风
    }

    public struct UnitTransform : IComponent
    {
        public FPVector3 Position;
        public FPVector3 LastPosition; // 上一逻辑帧坐标，用于 20Hz -> 60fps 渲染插值与短暂外推
        public FP Yaw;
        public FP LastYaw;
        public FPVector3 Scale;

        public UnitTransform(FPVector3 position, FPVector3 scale, FP yaw = default)
        {
            Position = position;
            LastPosition = position;
            Scale = scale;
            Yaw = yaw;
            LastYaw = yaw;
        }
    }

    public struct UnitVelocity : IComponent
    {
        public FPVector3 Value;
        public FP MoveSpeed;
        public FP Radius;
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

        // 技能3：小队治疗光环（恢复血量 + 护盾 + 霸体）
        public FP HealCooldown;
        public FP HealTimer;
        public FP HealAmount;

        // 技能4：龙卷风暴（选卡解锁后自动释放）
        public FP TornadoCooldown;
        public FP TornadoTimer;
        public FP TornadoDamage;
    }

    public struct ProjectileData : IComponent
    {
        public ProjectileKind Kind;
        public FPVector3 Direction;
        public FP Speed;
        public FP Damage;
        public FP LifeTime;
        public FP HitRadius;
        public byte Team; // 0 = 我方小队, 1 = 敌方
        public int AttackerEntityId;
        public int PierceLeft;
        public int BounceLeft;
        public int LastHitEntityId;
        public int TargetEntityId;
        public FPVector3 LastTargetPos;
        public FP TickTimer;
        public bool ApplyBurn;
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

    public struct CubeVisual : IComponent
    {
        public Color BaseColor;
        public bool IsVisible;

        public CubeVisual(Color baseColor)
        {
            BaseColor = baseColor;
            IsVisible = true;
        }
    }

    public struct WaveDirector : IComponent
    {
        public int WaveIndex;
        public FP WaveTimer;
        public FP WaveInterval;
        public int SpawnPerWave;
        public int MaxEnemyCap;
    }
}

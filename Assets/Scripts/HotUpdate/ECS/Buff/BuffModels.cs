using Friflo.Engine.ECS;
using HotUpdate.ECS.FixedPoint;

namespace HotUpdate.ECS.Buff
{
    /// <summary>
    /// 纯值类型零 GC Buff 运行态组件（覆盖笔记第 4 篇核心号段）：
    /// - 300：DOT 灼烧持续伤害（支持叠层与周期 Tick）；
    /// - 500：护盾优先吸收伤害；
    /// - 904：免死抢救次数；
    /// - 多乘区：暴击率 / 暴击伤害 / 抗性 / 抗性穿透 / 伤后吸血（节点 31）。
    /// </summary>
    public struct UnitBuffs : IComponent
    {
        // 500 护盾
        public FP Shield;
        public FP ShieldTimer;

        // 300 灼烧 DOT
        public FP BurnTimer;
        public FP BurnTickTimer;
        public FP BurnDamagePerTick;
        public int BurnStacks;

        // 904 免死次数
        public int UndyingCharges;

        // 多乘区与节点 31 吸血
        public FP CritRate;         // 0 ~ 1
        public FP CritDmgMul;       // 例如 1.75
        public FP ArmorResist;      // 0 ~ 0.8
        public FP ResistPen;        // 0 ~ 1
        public FP LifestealRate;    // 0 ~ 1

        public static UnitBuffs CreateSquadDefault() => new()
        {
            Shield = FP.FromInt(80),
            ShieldTimer = FP.FromInt(20),
            UndyingCharges = 1,
            CritRate = FP.FromMilli(150),
            CritDmgMul = FP.FromMilli(1750),
            ArmorResist = FP.FromMilli(100),
            ResistPen = FP.FromMilli(150),
            LifestealRate = FP.FromMilli(50),
        };

        public static UnitBuffs CreateEnemyDefault(bool isElite) => new()
        {
            Shield = isElite ? FP.FromInt(40) : FP.Zero,
            ShieldTimer = isElite ? FP.FromInt(30) : FP.Zero,
            UndyingCharges = 0,
            CritRate = FP.FromMilli(50),
            CritDmgMul = FP.FromMilli(1500),
            ArmorResist = isElite ? FP.FromMilli(200) : FP.Zero,
            ResistPen = FP.Zero,
            LifestealRate = FP.Zero,
        };

        public void AddShield(FP amount, FP duration)
        {
            Shield += amount;
            if (duration > ShieldTimer) ShieldTimer = duration;
        }

        public void ApplyBurn(FP damagePerTick, FP duration, int maxStacks = 5)
        {
            if (BurnStacks < maxStacks) BurnStacks++;
            if (damagePerTick > BurnDamagePerTick) BurnDamagePerTick = damagePerTick;
            if (duration > BurnTimer) BurnTimer = duration;
            if (BurnTickTimer.RawValue <= 0L) BurnTickTimer = FP.FromMilli(500);
        }
    }

    /// <summary>
    /// 对应笔记第 4 篇 200~209 号词条的全局小队技能修正器（不改技能核心代码即可动态改造弹道与伤害形态）。
    /// </summary>
    public struct SquadSkillModifiers
    {
        public int ExtraVolleyCount;   // 201 额外连发数
        public int ExtraBounceCount;   // 202 弹射次数
        public int ExtraPierceCount;   // 203 穿透次数
        public FP SkillDmgMultiplier;  // 204 技能伤害倍率
        public FP CooldownScale;       // 206 冷却缩减倍率（越小越快）
        public bool EnableBurnOnHit;   // 200 命中附加 300 灼烧 DOT
        public bool EnableHomingShots; // 解锁追踪弹（Precise）
        public bool EnableTornado;     // 解锁龙卷风持续多段弹（Tornado）

        public static SquadSkillModifiers CreateDefault() => new()
        {
            ExtraVolleyCount = 0,
            ExtraBounceCount = 0,
            ExtraPierceCount = 0,
            SkillDmgMultiplier = FP.One,
            CooldownScale = FP.One,
            EnableBurnOnHit = false,
            EnableHomingShots = false,
            EnableTornado = false,
        };
    }
}

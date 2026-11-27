using System;
using Friflo.Engine.ECS;
using HotUpdate.ECS.Components;
using HotUpdate.ECS.FixedPoint;

namespace HotUpdate.ECS.Buff
{
    public enum RoguelikeCardId
    {
        ExtraVolley = 1,
        ChainBounce = 2,
        BurnEnchant = 3,
        HomingMissiles = 4,
        TornadoStorm = 5,
        CritOverload = 6,
        ShieldAndVamp = 7,
        UndyingAndHaste = 8,
    }

    public readonly struct RoguelikeCardDef
    {
        public readonly RoguelikeCardId Id;
        public readonly string Title;
        public readonly string Tag;
        public readonly string Description;

        public RoguelikeCardDef(RoguelikeCardId id, string title, string tag, string description)
        {
            Id = id;
            Title = title;
            Tag = tag;
            Description = description;
        }
    }

    /// <summary>
    /// 对应笔记第 4 & 5 篇的 Roguelike 词条卡池（选卡后通过 Buff/Modifier 立即动态强化技能与伤害管线）。
    /// </summary>
    public static class RoguelikeCardCatalog
    {
        public static readonly RoguelikeCardDef[] AllCards =
        {
            new(RoguelikeCardId.ExtraVolley,    "多重齐射 [201]", "连发+", "扇形弹幕额外 +2 发，弹道穿透 +1"),
            new(RoguelikeCardId.ChainBounce,    "雷链弹射 [202]", "弹射+", "普攻与弹幕命中后自动弹射 +2 次，伤害倍率 +15%"),
            new(RoguelikeCardId.BurnEnchant,    "烈焰附魔 [200+300]", "DOT", "所有命中附加可叠层灼烧 DOT（最高 5 层持续掉血）"),
            new(RoguelikeCardId.HomingMissiles, "精确制导 [Precise]", "追踪", "普攻升级为自动追踪制导飞弹，攻击范围 +2m"),
            new(RoguelikeCardId.TornadoStorm,   "龙卷风暴 [Tornado]", "持续AOE", "主控自动召唤移动龙卷风，持续多段席卷周围敌军"),
            new(RoguelikeCardId.CritOverload,   "会心过载 [乘区]", "暴击+", "全队暴击率 +20%、暴击伤害 +45%、无视抗性 +25%"),
            new(RoguelikeCardId.ShieldAndVamp,  "圣盾吸血 [500+31]", "生存", "全队立即获得 +150 护盾（优先吸伤），伤后吸血 +12%"),
            new(RoguelikeCardId.UndyingAndHaste,"不死疾行 [904+206]", "免死", "全队免死抢救次数 +1，全技能冷却缩短 20%"),
        };

        /// <summary>使用确定性 FPRandom 抽取 3 张不重复词条卡。</summary>
        public static void DrawThree(ref FPRandom rng, RoguelikeCardDef[] output)
        {
            int total = AllCards.Length;
            int i0 = rng.RangeInt(0, total);
            int i1 = (i0 + 1 + rng.RangeInt(0, total - 1)) % total;
            int i2 = (i1 + 1 + rng.RangeInt(0, total - 2)) % total;
            if (i2 == i0) i2 = (i2 + 1) % total;

            output[0] = AllCards[i0];
            output[1] = AllCards[i1];
            output[2] = AllCards[i2];
        }

        public static void ApplyCard(BattleWorld world, RoguelikeCardId cardId)
        {
            ref var mod = ref world.SkillModifiers;
            var squadQuery = world.Store.Query<CombatStats, UnitBuffs>().AnyTags(Tags.Get<PlayerTag, AllyTag>()).WithoutAllTags(Tags.Get<DeadTag>());

            switch (cardId)
            {
                case RoguelikeCardId.ExtraVolley:
                    mod.ExtraVolleyCount += 2;
                    mod.ExtraPierceCount += 1;
                    break;

                case RoguelikeCardId.ChainBounce:
                    mod.ExtraBounceCount += 2;
                    mod.SkillDmgMultiplier += FP.FromMilli(150);
                    break;

                case RoguelikeCardId.BurnEnchant:
                    mod.EnableBurnOnHit = true;
                    break;

                case RoguelikeCardId.HomingMissiles:
                    mod.EnableHomingShots = true;
                    squadQuery.ForEachEntity((ref CombatStats stats, ref UnitBuffs _, Entity _) =>
                    {
                        stats.AttackRange += FP.FromInt(2);
                    });
                    break;

                case RoguelikeCardId.TornadoStorm:
                    mod.EnableTornado = true;
                    world.Store.Query<SkillCaster>().ForEachEntity((ref SkillCaster caster, Entity _) =>
                    {
                        if (caster.TornadoCooldown.RawValue > 0L && caster.TornadoTimer > FP.FromMilli(150))
                        {
                            caster.TornadoTimer = FP.FromMilli(150);
                        }
                    });
                    break;

                case RoguelikeCardId.CritOverload:
                    squadQuery.ForEachEntity((ref CombatStats _, ref UnitBuffs buffs, Entity _) =>
                    {
                        buffs.CritRate = FPMath.Min(FP.One, buffs.CritRate + FP.FromMilli(200));
                        buffs.CritDmgMul += FP.FromMilli(450);
                        buffs.ResistPen = FPMath.Min(FP.One, buffs.ResistPen + FP.FromMilli(250));
                    });
                    break;

                case RoguelikeCardId.ShieldAndVamp:
                    squadQuery.ForEachEntity((ref CombatStats _, ref UnitBuffs buffs, Entity _) =>
                    {
                        buffs.AddShield(FP.FromInt(150), FP.FromInt(25));
                        buffs.LifestealRate = FPMath.Min(FP.FromMilli(500), buffs.LifestealRate + FP.FromMilli(120));
                    });
                    break;

                case RoguelikeCardId.UndyingAndHaste:
                    mod.CooldownScale = FPMath.Max(FP.FromMilli(400), mod.CooldownScale * FP.FromMilli(800));
                    squadQuery.ForEachEntity((ref CombatStats _, ref UnitBuffs buffs, Entity _) =>
                    {
                        buffs.UndyingCharges += 1;
                    });
                    break;
            }

            world.AppliedCardCount++;
        }
    }
}

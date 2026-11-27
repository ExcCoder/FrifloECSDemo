using System;
using System.Runtime.CompilerServices;
using Friflo.Engine.ECS;
using HotUpdate.ECS.FixedPoint;

namespace HotUpdate.ECS.State
{
    public enum UnitStateType : byte
    {
        Idle = 0,
        Moving = 1,
        Attacking = 2,
        Casting = 3,
        HitFlash = 4,
        Slowed = 5,
        Stunned = 6,
        SuperArmor = 7,
        Dead = 8,
    }

    [Flags]
    public enum UnitStateFlags : uint
    {
        None       = 0u,
        Idle       = 1u << UnitStateType.Idle,
        Moving     = 1u << UnitStateType.Moving,
        Attacking  = 1u << UnitStateType.Attacking,
        Casting    = 1u << UnitStateType.Casting,
        HitFlash   = 1u << UnitStateType.HitFlash,
        Slowed     = 1u << UnitStateType.Slowed,
        Stunned    = 1u << UnitStateType.Stunned,
        SuperArmor = 1u << UnitStateType.SuperArmor,
        Dead       = 1u << UnitStateType.Dead,
        All        = 0xFFFFFFFFu,
    }

    /// <summary>
    /// 状态互斥与阻挡配置表（纯位运算，零 GC，O(1) 判定）：
    /// - MutexMask[s]：成功进入状态 s 时，自动强制打断并清除的冲突状态掩码；
    /// - BlockedByMask[s]：若实体当前已包含该掩码中的任意状态，则禁止进入状态 s（例如 Dead 阻挡一切，SuperArmor 阻挡 Stunned）。
    /// </summary>
    public static class StateMutexTable
    {
        private const int StateCount = 9;

        public static readonly uint[] MutexMask = new uint[StateCount];
        public static readonly uint[] BlockedByMask = new uint[StateCount];

        static StateMutexTable()
        {
            // Idle：互斥 Moving
            MutexMask[(int)UnitStateType.Idle] = (uint)UnitStateFlags.Moving;
            BlockedByMask[(int)UnitStateType.Idle] = (uint)(UnitStateFlags.Dead | UnitStateFlags.Stunned);

            // Moving：互斥 Idle；允许与 Attacking / HitFlash / Slowed / SuperArmor 并存（走 A 不断步）
            MutexMask[(int)UnitStateType.Moving] = (uint)UnitStateFlags.Idle;
            BlockedByMask[(int)UnitStateType.Moving] = (uint)(UnitStateFlags.Dead | UnitStateFlags.Stunned);

            // Attacking：允许与 Moving / HitFlash / Slowed / SuperArmor 并存
            MutexMask[(int)UnitStateType.Attacking] = 0u;
            BlockedByMask[(int)UnitStateType.Attacking] = (uint)(UnitStateFlags.Dead | UnitStateFlags.Stunned);

            // Casting：瞬发或引导施法，允许与 HitFlash / SuperArmor 并存
            MutexMask[(int)UnitStateType.Casting] = 0u;
            BlockedByMask[(int)UnitStateType.Casting] = (uint)(UnitStateFlags.Dead | UnitStateFlags.Stunned);

            // HitFlash：纯受击叠加态，不打断移动/攻击，仅被 Dead 阻挡
            MutexMask[(int)UnitStateType.HitFlash] = 0u;
            BlockedByMask[(int)UnitStateType.HitFlash] = (uint)UnitStateFlags.Dead;

            // Slowed：减速叠加态，不打断移动/攻击，被 Dead 阻挡
            MutexMask[(int)UnitStateType.Slowed] = 0u;
            BlockedByMask[(int)UnitStateType.Slowed] = (uint)UnitStateFlags.Dead;

            // Stunned：强控眩晕，打断 Idle / Moving / Attacking / Casting；但会被 SuperArmor（霸体）与 Dead 免疫！
            MutexMask[(int)UnitStateType.Stunned] = (uint)(UnitStateFlags.Idle | UnitStateFlags.Moving | UnitStateFlags.Attacking | UnitStateFlags.Casting);
            BlockedByMask[(int)UnitStateType.Stunned] = (uint)(UnitStateFlags.Dead | UnitStateFlags.SuperArmor);

            // SuperArmor：霸体状态，开启时顺手解除已有的 Stunned
            MutexMask[(int)UnitStateType.SuperArmor] = (uint)UnitStateFlags.Stunned;
            BlockedByMask[(int)UnitStateType.SuperArmor] = (uint)UnitStateFlags.Dead;

            // Dead：终结态，彻底打断清除一切其他状态
            MutexMask[(int)UnitStateType.Dead] = (uint)(UnitStateFlags.All & ~UnitStateFlags.Dead);
            BlockedByMask[(int)UnitStateType.Dead] = 0u;
        }
    }

    /// <summary>
    /// 零 GC 位掩码状态集合组件（纯值类型 struct，替代 OOP 单枚举状态机与 HashSet 状态集合）。
    /// </summary>
    public struct UnitState : IComponent
    {
        public uint Mask;
        public FP HitFlashTimer;
        public FP SlowTimer;
        public FP StunTimer;
        public FP SuperArmorTimer;
        public FP AttackTimer;

        public static UnitState CreateDefault() => new() { Mask = (uint)UnitStateFlags.Idle };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Has(UnitStateType state) => (Mask & (1u << (int)state)) != 0u;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool HasAny(UnitStateFlags flags) => (Mask & (uint)flags) != 0u;

        public bool CanMove => !HasAny(UnitStateFlags.Dead | UnitStateFlags.Stunned);
        public bool CanAct => !HasAny(UnitStateFlags.Dead | UnitStateFlags.Stunned);

        public FP MoveSpeedMultiplier => Has(UnitStateType.Slowed) ? FP.Half : FP.One;

        /// <summary>
        /// 按互斥表尝试进入目标状态；若被当前高优先级/免疫状态阻挡（如霸体免疫眩晕），返回 false。
        /// </summary>
        public bool TryEnter(UnitStateType state, FP duration = default)
        {
            int idx = (int)state;
            if ((Mask & StateMutexTable.BlockedByMask[idx]) != 0u)
            {
                return false;
            }

            // 1 条位运算同时打断所有互斥状态并置入目标状态
            uint cleared = Mask & StateMutexTable.MutexMask[idx];
            Mask = (Mask & ~StateMutexTable.MutexMask[idx]) | (1u << idx);

            // 若互斥表清除了带计时器的状态，同步归零对应计时器
            if ((cleared & (uint)UnitStateFlags.Stunned) != 0u) StunTimer = FP.Zero;
            if ((cleared & (uint)UnitStateFlags.Slowed) != 0u) SlowTimer = FP.Zero;
            if ((cleared & (uint)UnitStateFlags.Attacking) != 0u) AttackTimer = FP.Zero;
            if ((cleared & (uint)UnitStateFlags.HitFlash) != 0u) HitFlashTimer = FP.Zero;
            if ((cleared & (uint)UnitStateFlags.SuperArmor) != 0u) SuperArmorTimer = FP.Zero;

            if (duration.RawValue > 0L)
            {
                switch (state)
                {
                    case UnitStateType.HitFlash:
                        HitFlashTimer = FPMath.Max(HitFlashTimer, duration);
                        break;
                    case UnitStateType.Slowed:
                        SlowTimer = FPMath.Max(SlowTimer, duration);
                        break;
                    case UnitStateType.Stunned:
                        StunTimer = FPMath.Max(StunTimer, duration);
                        break;
                    case UnitStateType.SuperArmor:
                        SuperArmorTimer = FPMath.Max(SuperArmorTimer, duration);
                        break;
                    case UnitStateType.Attacking:
                        AttackTimer = FPMath.Max(AttackTimer, duration);
                        break;
                }
            }

            return true;
        }

        public void Exit(UnitStateType state)
        {
            Mask &= ~(1u << (int)state);
            switch (state)
            {
                case UnitStateType.HitFlash: HitFlashTimer = FP.Zero; break;
                case UnitStateType.Slowed: SlowTimer = FP.Zero; break;
                case UnitStateType.Stunned: StunTimer = FP.Zero; break;
                case UnitStateType.SuperArmor: SuperArmorTimer = FP.Zero; break;
                case UnitStateType.Attacking: AttackTimer = FP.Zero; break;
            }
        }

        /// <summary>推进所有限时状态，到期自动清除对应位掩码。</summary>
        public void TickTimers(FP dt)
        {
            if (HitFlashTimer.RawValue > 0L)
            {
                HitFlashTimer -= dt;
                if (HitFlashTimer.RawValue <= 0L) Exit(UnitStateType.HitFlash);
            }
            if (SlowTimer.RawValue > 0L)
            {
                SlowTimer -= dt;
                if (SlowTimer.RawValue <= 0L) Exit(UnitStateType.Slowed);
            }
            if (StunTimer.RawValue > 0L)
            {
                StunTimer -= dt;
                if (StunTimer.RawValue <= 0L)
                {
                    Exit(UnitStateType.Stunned);
                    // 眩晕结束后若未死亡，恢复为 Idle 等待下一帧移动接管
                    if (!Has(UnitStateType.Dead) && !Has(UnitStateType.Moving))
                    {
                        Mask |= (uint)UnitStateFlags.Idle;
                    }
                }
            }
            if (SuperArmorTimer.RawValue > 0L)
            {
                SuperArmorTimer -= dt;
                if (SuperArmorTimer.RawValue <= 0L) Exit(UnitStateType.SuperArmor);
            }
            if (AttackTimer.RawValue > 0L)
            {
                AttackTimer -= dt;
                if (AttackTimer.RawValue <= 0L) Exit(UnitStateType.Attacking);
            }
        }

        public void ResetAlive()
        {
            Mask = (uint)UnitStateFlags.Idle;
            HitFlashTimer = FP.Zero;
            SlowTimer = FP.Zero;
            StunTimer = FP.Zero;
            SuperArmorTimer = FP.Zero;
            AttackTimer = FP.Zero;
        }
    }
}

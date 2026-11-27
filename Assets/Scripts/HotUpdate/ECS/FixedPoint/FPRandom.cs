using System.Runtime.CompilerServices;

namespace HotUpdate.ECS.FixedPoint
{
    /// <summary>
    /// 纯 64 位位运算确定性伪随机数生成器（XorShift64*）。
    /// 相同种子在任何平台产生的随机序列 100% 比特一致，不依赖 UnityEngine.Random 或 System.Random。
    /// </summary>
    public struct FPRandom
    {
        private ulong _state;

        public ulong State => _state;

        public FPRandom(ulong seed)
        {
            _state = seed == 0UL ? 0x9E3779B97F4A7C15UL : seed;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong NextULong()
        {
            ulong x = _state;
            x ^= x >> 12;
            x ^= x << 25;
            x ^= x >> 27;
            _state = x;
            return x * 0x2545F4914F6CDD1DUL;
        }

        /// <summary>返回 [0, 1) 区间的定点数。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FP NextFP01()
        {
            // 取高 16 位作为 [0, 65535] 的 Q48.16 小数部分
            long raw = (long)(NextULong() >> 48);
            return FP.FromRaw(raw);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FP Range(FP min, FP max)
        {
            if (max <= min) return min;
            return min + (max - min) * NextFP01();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int RangeInt(int minInclusive, int maxExclusive)
        {
            if (maxExclusive <= minInclusive) return minInclusive;
            uint span = (uint)(maxExclusive - minInclusive);
            return minInclusive + (int)(NextULong() % span);
        }

        /// <summary>返回 [0, 2*Pi) 的确定性弧度。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FP NextAngleRad() => FP.TwoPi * NextFP01();
    }
}

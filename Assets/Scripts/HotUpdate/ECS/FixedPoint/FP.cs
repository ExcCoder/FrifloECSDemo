using System;
using System.Runtime.CompilerServices;

namespace HotUpdate.ECS.FixedPoint
{
    /// <summary>
    /// 面向帧同步与确定性战斗的 Q48.16 二进制位移定点数（16 位小数，1.0 = 65536L）。
    /// - 乘除优先使用位运算（>> 16 / << 16）并内置防 64 位中间溢出的 128 位分段乘法；
    /// - 全程纯整数运算，无浮点舍入漂移，保证 PC / iOS / Android 跨平台 100% 比特一致。
    /// </summary>
    public readonly struct FP : IEquatable<FP>, IComparable<FP>
    {
        public const int Shift = 16;
        public const long OneRaw = 1L << Shift;          // 65536
        public const long HalfRaw = OneRaw >> 1;         // 32768
        public const long PiRaw = 205887L;               // round(pi * 65536)
        public const long TwoPiRaw = 411775L;            // round(2*pi * 65536)
        public const long HalfPiRaw = 102944L;           // round(pi/2 * 65536)
        public const long Deg2RadRaw = 1144L;            // round((pi/180) * 65536)
        public const long Rad2DegRaw = 3754936L;         // round((180/pi) * 65536)

        public static readonly FP Zero = new(0L);
        public static readonly FP One = new(OneRaw);
        public static readonly FP Half = new(HalfRaw);
        public static readonly FP NegOne = new(-OneRaw);
        public static readonly FP Epsilon = new(1L);
        public static readonly FP Pi = new(PiRaw);
        public static readonly FP TwoPi = new(TwoPiRaw);
        public static readonly FP HalfPi = new(HalfPiRaw);
        public static readonly FP Deg2Rad = new(Deg2RadRaw);
        public static readonly FP Rad2Deg = new(Rad2DegRaw);
        public static readonly FP MaxValue = new(long.MaxValue);
        public static readonly FP MinValue = new(long.MinValue);

        public readonly long RawValue;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FP(long rawValue) => RawValue = rawValue;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FP FromRaw(long rawValue) => new(rawValue);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FP FromInt(int value) => new((long)value << Shift);

        /// <summary>按千分比构造（例如 FromMilli(50) = 0.05，适合确定性配置与固定步长）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FP FromMilli(int milli) => new(((long)milli << Shift) / 1000L);

        /// <summary>仅建议在初始化/输入边缘从 float 转入，禁止在核心逻辑循环中往返转 float。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FP FromFloat(float value) => new((long)Math.Round(value * OneRaw));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float ToFloat() => RawValue * (1f / OneRaw);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ToIntFloor() => (int)(RawValue >> Shift);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator FP(int value) => FromInt(value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FP operator +(FP a, FP b) => new(a.RawValue + b.RawValue);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FP operator -(FP a, FP b) => new(a.RawValue - b.RawValue);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FP operator -(FP a) => new(-a.RawValue);

        /// <summary>
        /// 安全 Q48.16 乘法：拆分为高/低 32 位运算，彻底避免常规 (a * b) >> 16 在大数时的 long 中间溢出。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FP operator *(FP a, FP b)
        {
            long x = a.RawValue;
            long y = b.RawValue;

            // 常见小范围快速路径（|x|, |y| < 2^30 约 ±16384，乘积严格 < 2^60 绝不溢出）
            if (((x + (1L << 30)) | (y + (1L << 30))) >> 31 == 0)
            {
                return new FP((x * y) >> Shift);
            }

            // 通用 128 位安全乘法路径
            bool negative = (x < 0) ^ (y < 0);
            ulong ux = x < 0 ? (ulong)(-x) : (ulong)x;
            ulong uy = y < 0 ? (ulong)(-y) : (ulong)y;

            ulong xLo = ux & 0xFFFFFFFFUL;
            ulong xHi = ux >> 32;
            ulong yLo = uy & 0xFFFFFFFFUL;
            ulong yHi = uy >> 32;

            ulong loLo = xLo * yLo;
            ulong hiLo = xHi * yLo;
            ulong loHi = xLo * yHi;
            ulong hiHi = xHi * yHi;

            ulong mid = (loLo >> 32) + (hiLo & 0xFFFFFFFFUL) + (loHi & 0xFFFFFFFFUL);
            ulong low64 = (loLo & 0xFFFFFFFFUL) | (mid << 32);
            ulong high64 = hiHi + (hiLo >> 32) + (loHi >> 32) + (mid >> 32);

            ulong result = (high64 << (64 - Shift)) | (low64 >> Shift);
            return new FP(negative ? -(long)result : (long)result);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FP operator /(FP a, FP b)
        {
            long divisor = b.RawValue;
            if (divisor == 0L)
            {
                return a.RawValue >= 0L ? MaxValue : MinValue;
            }

            long dividend = a.RawValue;
            // 小范围快速路径：|dividend| < 2^46 时左移 16 位不会溢出
            if ((dividend > -(1L << 46)) && (dividend < (1L << 46)))
            {
                return new FP((dividend << Shift) / divisor);
            }

            // 大数安全路径：先做高位商余拆分
            long q = dividend / divisor;
            long r = dividend % divisor;
            return new FP((q << Shift) + ((r << Shift) / divisor));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator ==(FP a, FP b) => a.RawValue == b.RawValue;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator !=(FP a, FP b) => a.RawValue != b.RawValue;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator <(FP a, FP b) => a.RawValue < b.RawValue;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator >(FP a, FP b) => a.RawValue > b.RawValue;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator <=(FP a, FP b) => a.RawValue <= b.RawValue;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator >=(FP a, FP b) => a.RawValue >= b.RawValue;

        public bool Equals(FP other) => RawValue == other.RawValue;
        public override bool Equals(object obj) => obj is FP other && RawValue == other.RawValue;
        public override int GetHashCode() => RawValue.GetHashCode();
        public int CompareTo(FP other) => RawValue.CompareTo(other.RawValue);
        public override string ToString() => ToFloat().ToString("F4");
    }

    /// <summary>
    /// 全纯整数实现的确定性定点数数学库（Sqrt / Sin / Cos / Atan2Deg / Clamp / Lerp）。
    /// </summary>
    public static class FPMath
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FP Abs(FP x) => new(x.RawValue < 0L ? -x.RawValue : x.RawValue);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FP Min(FP a, FP b) => a.RawValue <= b.RawValue ? a : b;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FP Max(FP a, FP b) => a.RawValue >= b.RawValue ? a : b;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FP Clamp(FP val, FP min, FP max)
        {
            if (val.RawValue < min.RawValue) return min;
            if (val.RawValue > max.RawValue) return max;
            return val;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FP Clamp01(FP val) => Clamp(val, FP.Zero, FP.One);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FP Lerp(FP a, FP b, FP t) => a + (b - a) * Clamp01(t);

        /// <summary>
        /// 无浮点纯整数开方：返回 sqrt(x)（Q48.16），对大数自动退化缩放避免左移溢出。
        /// </summary>
        public static FP Sqrt(FP x)
        {
            long raw = x.RawValue;
            if (raw <= 0L) return FP.Zero;

            // 若 raw < 2^47，则 (ulong)raw << 16 严格在 ulong 范围内，开方结果直接就是 Q48.16
            if (raw < (1L << 47))
            {
                ulong n = (ulong)raw << FP.Shift;
                return new FP((long)ISqrtU64(n));
            }

            // 超大值：先开方再左移 8 位
            return new FP((long)(ISqrtU64((ulong)raw) << (FP.Shift >> 1)));
        }

        /// <summary>
        /// 确定性正弦函数（输入弧度 FP），使用 Bhaskara / 5 阶奇多项式逼近，全纯整数运算。
        /// </summary>
        public static FP Sin(FP rad)
        {
            long r = rad.RawValue % FP.TwoPiRaw;
            if (r > FP.PiRaw) r -= FP.TwoPiRaw;
            else if (r < -FP.PiRaw) r += FP.TwoPiRaw;

            // 折叠到 [-pi/2, pi/2]
            if (r > FP.HalfPiRaw) r = FP.PiRaw - r;
            else if (r < -FP.HalfPiRaw) r = -FP.PiRaw - r;

            // 泰勒多项式：sin(x) ≈ x - x^3/6 + x^5/120（|x| <= pi/2 最大误差 < 0.0045，纯确定性）
            FP x = new(r);
            FP x2 = x * x;
            FP x3 = x2 * x;
            FP x5 = x3 * x2;
            FP term3 = new(x3.RawValue / 6L);
            FP term5 = new(x5.RawValue / 120L);
            return Clamp(x - term3 + term5, FP.NegOne, FP.One);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FP Cos(FP rad) => Sin(new FP(rad.RawValue + FP.HalfPiRaw));

        /// <summary>确定性 Atan2（返回角度制 FP，范围 [-180, 180]），纯整数运算。</summary>
        public static FP Atan2Deg(FP y, FP x)
        {
            if (x.RawValue == 0L)
            {
                if (y.RawValue > 0L) return FP.FromInt(90);
                if (y.RawValue < 0L) return FP.FromInt(-90);
                return FP.Zero;
            }

            FP absY = Abs(y) + FP.Epsilon;
            FP angleRad;
            if (x.RawValue >= 0L)
            {
                FP r = (x - absY) / (x + absY);
                angleRad = FP.FromRaw(51472L) - FP.FromRaw(51472L) * r; // pi/4 - pi/4 * r
            }
            else
            {
                FP r = (x + absY) / (absY - x);
                angleRad = FP.FromRaw(154416L) - FP.FromRaw(51472L) * r; // 3*pi/4 - pi/4 * r
            }

            if (y.RawValue < 0L) angleRad = -angleRad;
            return angleRad * FP.Rad2Deg;
        }

        private static ulong ISqrtU64(ulong n)
        {
            ulong res = 0UL;
            ulong bit = 1UL << 62;
            while (bit > n) bit >>= 2;

            while (bit != 0UL)
            {
                if (n >= res + bit)
                {
                    n -= res + bit;
                    res = (res >> 1) + bit;
                }
                else
                {
                    res >>= 1;
                }
                bit >>= 2;
            }
            return res;
        }
    }
}

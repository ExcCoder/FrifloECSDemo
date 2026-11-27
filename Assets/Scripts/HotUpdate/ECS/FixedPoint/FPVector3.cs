using System;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace HotUpdate.ECS.FixedPoint
{
    /// <summary>
    /// Q48.16 三维定点数向量（逻辑层专用，仅在表现层边界调用 ToVector3 转换）。
    /// </summary>
    public struct FPVector3 : IEquatable<FPVector3>
    {
        public static readonly FPVector3 Zero = new(FP.Zero, FP.Zero, FP.Zero);
        public static readonly FPVector3 One = new(FP.One, FP.One, FP.One);
        public static readonly FPVector3 Forward = new(FP.Zero, FP.Zero, FP.One);

        public FP X;
        public FP Y;
        public FP Z;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FPVector3(FP x, FP y, FP z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FPVector3 FromFloats(float x, float y, float z) =>
            new(FP.FromFloat(x), FP.FromFloat(y), FP.FromFloat(z));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FPVector3 FromVector3(in Vector3 v) =>
            new(FP.FromFloat(v.x), FP.FromFloat(v.y), FP.FromFloat(v.z));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector3 ToVector3() => new(X.ToFloat(), Y.ToFloat(), Z.ToFloat());

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FP SqrMagnitudeXZ() => X * X + Z * Z;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FP MagnitudeXZ() => FPMath.Sqrt(X * X + Z * Z);

        public FPVector3 NormalizedXZ()
        {
            FP sqr = X * X + Z * Z;
            if (sqr.RawValue <= 4L) return Zero;
            FP mag = FPMath.Sqrt(sqr);
            if (mag.RawValue == 0L) return Zero;
            return new FPVector3(X / mag, FP.Zero, Z / mag);
        }

        /// <summary>绕 Y 轴按确定性定点数角度旋转 XZ 向量。</summary>
        public FPVector3 RotateYDeg(FP degrees)
        {
            FP rad = degrees * FP.Deg2Rad;
            FP c = FPMath.Cos(rad);
            FP s = FPMath.Sin(rad);
            return new FPVector3(X * c + Z * s, Y, -X * s + Z * c);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FPVector3 operator +(FPVector3 a, FPVector3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FPVector3 operator -(FPVector3 a, FPVector3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FPVector3 operator *(FPVector3 v, FP s) => new(v.X * s, v.Y * s, v.Z * s);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static FPVector3 operator /(FPVector3 v, FP s) => new(v.X / s, v.Y / s, v.Z / s);

        public bool Equals(FPVector3 other) => X == other.X && Y == other.Y && Z == other.Z;
        public override bool Equals(object obj) => obj is FPVector3 other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(X.RawValue, Y.RawValue, Z.RawValue);
        public override string ToString() => $"({X}, {Y}, {Z})";
    }
}

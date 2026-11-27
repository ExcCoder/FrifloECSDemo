using System;
using System.Collections.Generic;
using HotUpdate.ECS.FixedPoint;

namespace HotUpdate.ECS.Spatial
{
    public readonly struct SpatialEntry
    {
        public readonly int EntityId;
        public readonly FPVector3 Position;
        public readonly FP Radius;
        public readonly byte Team; // 0 = Squad (Player + Allies), 1 = Enemy

        public SpatialEntry(int entityId, FPVector3 position, FP radius, byte team)
        {
            EntityId = entityId;
            Position = position;
            Radius = radius;
            Team = team;
        }
    }

    /// <summary>
    /// 全 Q48.16 定点数 XZ 平面均匀空间哈希网格（平铺数组 + 链式索引，零 GC 且 100% 跨平台确定性）。
    /// </summary>
    public sealed class SpatialGrid
    {
        public const byte TeamSquad = 0;
        public const byte TeamEnemy = 1;

        private readonly FP _cellSize;
        private readonly FP _invCellSize;
        private readonly FP _minX;
        private readonly FP _minZ;
        private readonly int _gridWidth;
        private readonly int _gridHeight;

        private readonly int[] _cellHead;
        private SpatialEntry[] _entries;
        private int[] _nextIndex;
        private int _count;

        public int Count => _count;

        public SpatialGrid(FP worldHalfExtent, FP cellSize, int initialCapacity = 4096)
        {
            _cellSize = cellSize < FP.Half ? FP.Half : cellSize;
            _invCellSize = FP.One / _cellSize;
            _minX = -worldHalfExtent;
            _minZ = -worldHalfExtent;

            int cells = ((worldHalfExtent * FP.FromInt(2)) / _cellSize).ToIntFloor() + 2;
            _gridWidth = Math.Max(4, cells);
            _gridHeight = _gridWidth;

            _cellHead = new int[_gridWidth * _gridHeight];
            _entries = new SpatialEntry[initialCapacity];
            _nextIndex = new int[initialCapacity];
            Clear();
        }

        public void Clear()
        {
            Array.Fill(_cellHead, -1);
            _count = 0;
        }

        public void Insert(int entityId, in FPVector3 position, FP radius, byte team)
        {
            if (_count >= _entries.Length)
            {
                int newCap = _entries.Length * 2;
                Array.Resize(ref _entries, newCap);
                Array.Resize(ref _nextIndex, newCap);
            }

            int cellIdx = GetCellIndex(position.X, position.Z);
            int idx = _count++;
            _entries[idx] = new SpatialEntry(entityId, position, radius, team);
            _nextIndex[idx] = _cellHead[cellIdx];
            _cellHead[cellIdx] = idx;
        }

        /// <summary>
        /// 查找指定半径内距离最近的指定阵营单位（忽略 excludeEntityId）；距离相等时按 EntityId 较小者确定性打破平局。
        /// </summary>
        public bool FindNearest(in FPVector3 origin, FP maxRadius, byte targetTeam, int excludeEntityId, out SpatialEntry nearest, out FP nearestSqrDist)
        {
            nearest = default;
            nearestSqrDist = maxRadius * maxRadius;
            bool found = false;

            GetCellBounds(origin.X, origin.Z, maxRadius, out int minCx, out int maxCx, out int minCz, out int maxCz);

            for (int cz = minCz; cz <= maxCz; cz++)
            {
                int rowOffset = cz * _gridWidth;
                for (int cx = minCx; cx <= maxCx; cx++)
                {
                    int idx = _cellHead[rowOffset + cx];
                    while (idx != -1)
                    {
                        ref readonly var entry = ref _entries[idx];
                        if (entry.Team == targetTeam && entry.EntityId != excludeEntityId)
                        {
                            FP dx = entry.Position.X - origin.X;
                            FP dz = entry.Position.Z - origin.Z;
                            FP sqr = dx * dx + dz * dz;
                            if (sqr < nearestSqrDist || (found && sqr == nearestSqrDist && entry.EntityId < nearest.EntityId))
                            {
                                nearestSqrDist = sqr;
                                nearest = entry;
                                found = true;
                            }
                        }
                        idx = _nextIndex[idx];
                    }
                }
            }

            return found;
        }

        /// <summary>
        /// 收集指定半径内某个阵营的所有单位（调用方复用 results 列表保持零 GC）。
        /// </summary>
        public void QueryTeamInRadius(in FPVector3 origin, FP radius, byte targetTeam, List<SpatialEntry> results)
        {
            results.Clear();
            GetCellBounds(origin.X, origin.Z, radius, out int minCx, out int maxCx, out int minCz, out int maxCz);

            for (int cz = minCz; cz <= maxCz; cz++)
            {
                int rowOffset = cz * _gridWidth;
                for (int cx = minCx; cx <= maxCx; cx++)
                {
                    int idx = _cellHead[rowOffset + cx];
                    while (idx != -1)
                    {
                        ref readonly var entry = ref _entries[idx];
                        if (entry.Team == targetTeam)
                        {
                            FP dx = entry.Position.X - origin.X;
                            FP dz = entry.Position.Z - origin.Z;
                            FP effectiveRadius = radius + entry.Radius;
                            if (dx * dx + dz * dz <= effectiveRadius * effectiveRadius)
                            {
                                results.Add(entry);
                            }
                        }
                        idx = _nextIndex[idx];
                    }
                }
            }
        }

        /// <summary>
        /// 确定性邻近同阵营排斥向量计算（纯 Q48.16 运算，无浮点）。
        /// </summary>
        public FPVector3 ComputeSeparation(int selfEntityId, in FPVector3 origin, FP desiredSeparation, byte team, int maxNeighbors = 12)
        {
            FP sqrLimit = desiredSeparation * desiredSeparation;
            GetCellBounds(origin.X, origin.Z, desiredSeparation, out int minCx, out int maxCx, out int minCz, out int maxCz);

            FP pushX = FP.Zero;
            FP pushZ = FP.Zero;
            int checkedCount = 0;

            for (int cz = minCz; cz <= maxCz; cz++)
            {
                int rowOffset = cz * _gridWidth;
                for (int cx = minCx; cx <= maxCx; cx++)
                {
                    int idx = _cellHead[rowOffset + cx];
                    while (idx != -1)
                    {
                        ref readonly var entry = ref _entries[idx];
                        if (entry.Team == team && entry.EntityId != selfEntityId)
                        {
                            FP dx = origin.X - entry.Position.X;
                            FP dz = origin.Z - entry.Position.Z;
                            FP sqr = dx * dx + dz * dz;
                            if (sqr < sqrLimit)
                            {
                                if (sqr.RawValue <= 8L)
                                {
                                    // 极端完全重合时用实体 ID 确定性推开
                                    FP angle = FP.FromRaw((selfEntityId * 106039L) % FP.TwoPiRaw);
                                    pushX += FPMath.Cos(angle);
                                    pushZ += FPMath.Sin(angle);
                                }
                                else
                                {
                                    FP dist = FPMath.Sqrt(sqr);
                                    FP weight = (desiredSeparation - dist) / desiredSeparation;
                                    pushX += (dx / dist) * weight;
                                    pushZ += (dz / dist) * weight;
                                }

                                if (++checkedCount >= maxNeighbors)
                                {
                                    return new FPVector3(pushX, FP.Zero, pushZ);
                                }
                            }
                        }
                        idx = _nextIndex[idx];
                    }
                }
            }

            return new FPVector3(pushX, FP.Zero, pushZ);
        }

        private int GetCellIndex(FP x, FP z)
        {
            int cx = ClampInt(((x - _minX) * _invCellSize).ToIntFloor(), 0, _gridWidth - 1);
            int cz = ClampInt(((z - _minZ) * _invCellSize).ToIntFloor(), 0, _gridHeight - 1);
            return cz * _gridWidth + cx;
        }

        private void GetCellBounds(FP x, FP z, FP radius, out int minCx, out int maxCx, out int minCz, out int maxCz)
        {
            minCx = ClampInt((((x - radius) - _minX) * _invCellSize).ToIntFloor(), 0, _gridWidth - 1);
            maxCx = ClampInt((((x + radius) - _minX) * _invCellSize).ToIntFloor(), 0, _gridWidth - 1);
            minCz = ClampInt((((z - radius) - _minZ) * _invCellSize).ToIntFloor(), 0, _gridHeight - 1);
            maxCz = ClampInt((((z + radius) - _minZ) * _invCellSize).ToIntFloor(), 0, _gridHeight - 1);
        }

        private static int ClampInt(int v, int min, int max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }
    }
}

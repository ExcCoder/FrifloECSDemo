using System;

namespace HotUpdate.ECS.Config
{
    /// <summary>
    /// 可独立开关的四大核心教学优化配置。
    /// 既可通过 6 个教学预设关卡一键切换，也可以在 Game/Fight 界面单独勾选任意组合。
    /// </summary>
    [Serializable]
    public struct BattleOptimizationConfig
    {
        public int StageId;
        public string StageName;
        public string TeachingNote;

        /// <summary>优化 1：true = GPU Instancing (0 GameObject)；false = 每实体传统 GameObject Cube。</summary>
        public bool UseGpuInstancing;

        /// <summary>优化 2：true = XZ 均匀空间哈希网格 O(N)；false = 全量暴力 O(N^2) 查询。</summary>
        public bool UseSpatialGrid;

        /// <summary>优化 3：true = Store 级按类型分桶脏中心 O(K) 零 GC；false = 旧版逐实体闭包 + 每帧 new HashSet/List 全量轮询。</summary>
        public bool UseBucketedReactive;

        /// <summary>优化 4：true = 原生 DeadTag 批量清理（无冗余列表）；false = 旧版维护冗余 List 逐帧 TryGetEntityById + RemoveAt(i)。</summary>
        public bool UseOptimizedCleanup;

        /// <summary>优化 5：true = PBD 细粒度空间网格硬碰撞解穿透 + 质量权重 + 切向包抄（不重叠）；false = 仅软排斥（高压下会重叠聚集）。</summary>
        public bool UseHardCollision;

        /// <summary>优化 6：true = 独立 CullingRoot 3D 视锥体裁切（视野外跳过渲染提交）；false = 全量提交渲染。</summary>
        public bool UseFrustumCulling;

        /// <summary>初始刷出的敌军数量。</summary>
        public int InitialEnemyCount;

        /// <summary>固定随机种子，确保不同优化组合下刷怪分布完全一致，对比公平。</summary>
        public ulong RandomSeed;

        public static BattleOptimizationConfig CreateFullOptimized(int initialEnemies = 300, ulong seed = 2026UL)
        {
            return new BattleOptimizationConfig
            {
                StageId = 5,
                StageName = "关卡5 · 全优化拉满",
                TeachingNote = "六大优化全开：Instancing + 空间网格 + 分桶脏中心 + 无冗余清理 + PBD 硬碰撞 + 视锥裁切",
                UseGpuInstancing = true,
                UseSpatialGrid = true,
                UseBucketedReactive = true,
                UseOptimizedCleanup = true,
                UseHardCollision = true,
                UseFrustumCulling = true,
                InitialEnemyCount = initialEnemies,
                RandomSeed = seed,
            };
        }

        public string BuildShortTagSummary()
        {
            return
                $"Instancing:{(UseGpuInstancing ? "ON" : "OFF")} | " +
                $"网格:{(UseSpatialGrid ? "ON" : "OFF")} | " +
                $"脏桶:{(UseBucketedReactive ? "ON" : "OFF")} | " +
                $"清理:{(UseOptimizedCleanup ? "ON" : "OFF")} | " +
                $"硬碰撞:{(UseHardCollision ? "ON" : "OFF")} | " +
                $"视锥裁剪:{(UseFrustumCulling ? "ON" : "OFF")}";
        }
    }

    /// <summary>
    /// 教学预设关卡库与当前选中配置中心。
    /// </summary>
    public static class BenchmarkStageCatalog
    {
        public static readonly BattleOptimizationConfig[] Presets =
        {
            new()
            {
                StageId = 1,
                StageName = "关卡1 · 原始基准（全关）",
                TeachingNote = "反面教材：1000 个真实 GameObject + O(N^2) 暴力查询 + 每帧 new 集合逐实体监听 + 冗余 List.RemoveAt 清理。",
                UseGpuInstancing = false,
                UseSpatialGrid = false,
                UseBucketedReactive = false,
                UseOptimizedCleanup = false,
                InitialEnemyCount = 1000,
                RandomSeed = 2026UL,
            },
            new()
            {
                StageId = 2,
                StageName = "关卡2 · 仅开 GPU Instancing",
                TeachingNote = "对比渲染优化：0 个单位 GameObject，Render 耗时骤降至 ~0.3ms，但 Logic 仍被 O(N^2) 拖垮（定位主瓶颈）。",
                UseGpuInstancing = true,
                UseSpatialGrid = false,
                UseBucketedReactive = false,
                UseOptimizedCleanup = false,
                InitialEnemyCount = 1000,
                RandomSeed = 2026UL,
            },
            new()
            {
                StageId = 3,
                StageName = "关卡3 · 仅开空间哈希网格",
                TeachingNote = "对比逻辑优化：Logic 从几十 ms 暴跌至 ~3ms，但 1000 个 GameObject 依然拖慢主线程渲染与 Transform 同步。",
                UseGpuInstancing = false,
                UseSpatialGrid = true,
                UseBucketedReactive = false,
                UseOptimizedCleanup = false,
                InitialEnemyCount = 1000,
                RandomSeed = 2026UL,
            },
            new()
            {
                StageId = 4,
                StageName = "关卡4 · 双核心优化（渲染+网格）",
                TeachingNote = "Instancing + 空间网格齐开帧率起飞，但旧版响应式轮询与冗余列表仍会造成明显 GC Alloc 抖动。",
                UseGpuInstancing = true,
                UseSpatialGrid = true,
                UseBucketedReactive = false,
                UseOptimizedCleanup = false,
                InitialEnemyCount = 1000,
                RandomSeed = 2026UL,
            },
            new()
            {
                StageId = 5,
                StageName = "关卡5 · 六大优化全开（1000 敌）",
                TeachingNote = "六大优化全开：GPU Instancing + 空间网格 + 分桶脏中心 + 无冗余清理 + PBD 敌军硬碰撞 + 3D 视锥体裁切。",
                UseGpuInstancing = true,
                UseSpatialGrid = true,
                UseBucketedReactive = true,
                UseOptimizedCleanup = true,
                UseHardCollision = true,
                UseFrustumCulling = true,
                InitialEnemyCount = 1000,
                RandomSeed = 2026UL,
            },
            new()
            {
                StageId = 6,
                StageName = "关卡6 · 极限压测（2500 敌）",
                TeachingNote = "六大优化全开下同屏 2,500 敌军，视锥外自动剔除渲染提交，视野内千军整齐包抄不重叠。",
                UseGpuInstancing = true,
                UseSpatialGrid = true,
                UseBucketedReactive = true,
                UseOptimizedCleanup = true,
                UseHardCollision = true,
                UseFrustumCulling = true,
                InitialEnemyCount = 2500,
                RandomSeed = 2026UL,
            },
        };

        /// <summary>当前选中的教学关卡 / 自定义优化配置（默认指向关卡 5 全优化）。</summary>
        public static BattleOptimizationConfig CurrentConfig { get; set; } = Presets[4];

        public static void SelectPresetByIndex(int index)
        {
            if (index < 0 || index >= Presets.Length) return;
            CurrentConfig = Presets[index];
        }

        public static int GetCurrentPresetIndex()
        {
            for (int i = 0; i < Presets.Length; i++)
            {
                if (Presets[i].StageId == CurrentConfig.StageId) return i;
            }
            return 0;
        }

        public static void SelectNextPreset()
        {
            int next = (GetCurrentPresetIndex() + 1) % Presets.Length;
            CurrentConfig = Presets[next];
        }

        public static void SelectPrevPreset()
        {
            int prev = (GetCurrentPresetIndex() - 1 + Presets.Length) % Presets.Length;
            CurrentConfig = Presets[prev];
        }
    }
}

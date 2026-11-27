using System;
using System.Collections.Generic;
using System.Diagnostics;
using Friflo.Engine.ECS;
using Friflo.Engine.ECS.Systems;
using HotUpdate.ECS.Components;
using HotUpdate.ECS.Config;
using HotUpdate.ECS.FixedPoint;
using HotUpdate.ECS.Spatial;
using HotUpdate.ECS.State;
using HotUpdate.ECS.Systems;
using UnityEngine;

namespace HotUpdate.ECS
{
    public readonly struct DamageRequest
    {
        public readonly int TargetEntityId;
        public readonly FP Amount;
        public readonly UnitStateType ApplyState;
        public readonly FP ApplyStateDuration;

        public DamageRequest(int targetEntityId, FP amount, UnitStateType applyState = UnitStateType.HitFlash, FP applyStateDuration = default)
        {
            TargetEntityId = targetEntityId;
            Amount = amount;
            ApplyState = applyState;
            ApplyStateDuration = applyStateDuration;
        }
    }

    public struct BattleSnapshot
    {
        public string StageName;
        public string OptimizationSummary;
        public float PlayerHp;
        public float PlayerMaxHp;
        public int AliveAllies;
        public int TotalAllies;
        public int EnemyCount;
        public int StunnedEnemyCount;
        public int ProjectileCount;
        public int ActiveUnitGameObjects;
        public int CollisionResolvedPairs;
        public int VisibleRenderCount;
        public int CulledRenderCount;
        public int WaveIndex;
        public float WaveCountdown;
        public int TotalKills;
        public double LogicMs;
        public double RenderMs;
        public double GcAllocKbPerFrame;
        public bool PlayerHasSuperArmor;

        // 技能冷却进度（0 = 就绪，0~1 = 剩余冷却比例）
        public float BasicCdRatio;
        public float VolleyCdRatio;
        public float NovaCdRatio;
        public float HealCdRatio;
    }

    /// <summary>
    /// Friflo ECS 确定性战斗世界底座：
    /// - 严格分离 LogicRoot 与 PresentationRoot；
    /// - 支持通过 BattleOptimizationConfig 灵活开关四大教学优化（GPU Instancing / 空间哈希网格 / O(K) 分桶脏中心 / 无冗余列表清理）；
    /// - 提供 ComputeStateHash() 用于帧同步确定性校验。
    /// </summary>
    public sealed class BattleWorld : IDisposable
    {
        public static readonly FP ArenaHalfSize = FP.FromInt(48);
        public const int SquadAllyCount = 4;

        /// <summary>全局确定性逻辑步长（Q48.16 定点数）。</summary>
        public static FP LogicDeltaTimeFP { get; set; } = FP.FromMilli(50);

        public static float LogicDeltaTime => LogicDeltaTimeFP.ToFloat();

        public BattleOptimizationConfig Config { get; }
        public EntityStore Store { get; }
        public SystemRoot LogicRoot { get; }
        public SystemRoot CullingRoot { get; }
        public SystemRoot PresentationRoot { get; }
        public FrustumCullingSystem CullingSystem { get; }
        public SpatialGrid Grid { get; }
        public BruteForceSpatialIndex BruteForceIndex { get; }
        public PbdCollisionSolver CollisionSolver { get; }
        public ReactiveDirtyHub DirtyHub { get; }

        internal readonly List<DamageRequest> PendingDamages = new(512);
        internal readonly List<SpatialEntry> QueryScratch = new(256);
        internal readonly List<int> EntityIdScratch = new(256);

        public int PlayerEntityId { get; private set; }
        public FPVector3 PlayerPosition { get; internal set; }
        public int TotalKills { get; internal set; }

        /// <summary>测试或帧同步输入帧可注入定点移动输入；为 null 时自动从键盘采样转入定点。</summary>
        public (FP X, FP Y)? OverrideMoveInput { get; set; }

        public BattleSnapshot Snapshot => _snapshot;

        private readonly Stopwatch _stopwatch = new();
        private FPRandom _rng;
        private readonly CubeInstancedRenderSystem _instancedRenderSystem;
        private readonly GameObjectCubeRenderSystem _gameObjectRenderSystem;
        private readonly WaveSpawnReactiveSystem _waveReactiveSystem;
        private readonly BucketedCombatReactiveSystem _bucketedCombatReactive;
        private readonly LegacyPerEntityReactiveSystem _legacyCombatReactive;

        private BattleSnapshot _snapshot;
        private int _tickCount;
        private double _smoothedGcKb;
        private bool _disposed;

        public BattleWorld(bool enablePresentation = true, ulong randomSeed = 2026UL)
            : this(BattleOptimizationConfig.CreateFullOptimized(300, randomSeed), enablePresentation)
        {
        }

        public BattleWorld(BattleOptimizationConfig config, bool enablePresentation = true)
        {
            Config = config;
            _rng = new FPRandom(config.RandomSeed == 0UL ? 2026UL : config.RandomSeed);
            Store = new EntityStore(PidType.UsePidAsId);
            Store.EventRecorder.Enabled = true;
            DirtyHub = ReactiveDirtyHub.GetOrCreate(Store);
            Grid = new SpatialGrid(worldHalfExtent: ArenaHalfSize + FP.FromInt(6), cellSize: FP.FromInt(3), initialCapacity: 4096);
            BruteForceIndex = new BruteForceSpatialIndex();
            CollisionSolver = new PbdCollisionSolver(arenaHalfSize: ArenaHalfSize, cellSize: FP.FromInt(2), initialCapacity: 4096);

            _waveReactiveSystem = new WaveSpawnReactiveSystem(this);

            // 1. 纯逻辑层：按开关动态挂载 优化版 vs 旧版对照组 System
            LogicRoot = new SystemRoot(Store)
            {
                new SpatialGridBuildSystem(this),
                new StateUpdateSystem(this),
                new DamageResolveSystem(this),
                new PlayerInputSystem(this),
                new AllyAiSystem(this),
                new EnemySwarmSystem(this),
                new MovementSystem(this),
            };

            // 教学开关 5：PBD 细粒度空间网格硬碰撞解穿透（置入 MovementSystem 之后，防止千军聚集重叠）
            if (config.UseHardCollision)
            {
                LogicRoot.Add(new PbdCollisionSystem(this));
            }

            LogicRoot.Add(new AutoAttackSystem(this));
            LogicRoot.Add(new AutoSkillSystem(this));
            LogicRoot.Add(new ProjectileSystem(this));
            LogicRoot.Add(new PulseEffectSystem(this));
            LogicRoot.Add(new WaveDirectorSystem(this));
            LogicRoot.Add(_waveReactiveSystem);
            LogicRoot.Add(new DeathCleanupSystem(this));

            // 教学开关 3：O(K) 分桶脏中心 vs 旧版逐实体闭包 + 每帧 new HashSet/List
            if (config.UseBucketedReactive)
            {
                _bucketedCombatReactive = new BucketedCombatReactiveSystem();
                LogicRoot.Add(_bucketedCombatReactive);
            }
            else
            {
                _legacyCombatReactive = new LegacyPerEntityReactiveSystem();
                LogicRoot.Add(_legacyCombatReactive);
            }

            // 教学开关 4：无冗余列表清理 vs 旧版维护冗余 List 逐帧 TryGetEntityById + RemoveAt(i)
            if (!config.UseOptimizedCleanup)
            {
                LogicRoot.Add(new LegacyEntityListCleanupSystem(this));
            }

            LogicRoot.SetMonitorPerf(true);

            // 2. 视锥裁切层（第 3 棵 SystemRoot：独立驱动 3D 视锥体剔除，对齐三层 SystemRoot 架构）
            CullingSystem = new FrustumCullingSystem(this, cullPadding: 1.5f);
            CullingRoot = new SystemRoot(Store)
            {
                CullingSystem,
            };
            CullingRoot.SetMonitorPerf(true);

            // 3. 纯表现层：教学开关 1 —— GPU Instancing (0 GameObject) vs 每实体真实 GameObject Cube
            if (enablePresentation)
            {
                PresentationRoot = new SystemRoot(Store);
                // 相机跟随置于渲染前，确保同帧视锥裁切使用的是最新相机位姿
                PresentationRoot.Add(new CameraFollowSystem(this));
                if (config.UseGpuInstancing)
                {
                    _instancedRenderSystem = new CubeInstancedRenderSystem(this);
                    PresentationRoot.Add(_instancedRenderSystem);
                }
                else
                {
                    _gameObjectRenderSystem = new GameObjectCubeRenderSystem(this);
                    PresentationRoot.Add(_gameObjectRenderSystem);
                }
                PresentationRoot.SetMonitorPerf(true);
            }
        }

        // ================= 空间查询统一路由（教学开关 2：SpatialGrid O(N) vs BruteForce O(N^2)） =================

        public int SpatialEntryCount => Config.UseSpatialGrid ? Grid.Count : BruteForceIndex.Count;

        public void SpatialClear()
        {
            if (Config.UseSpatialGrid) Grid.Clear();
            else BruteForceIndex.Clear();
        }

        public void SpatialInsert(int entityId, in FPVector3 position, FP radius, byte team)
        {
            if (Config.UseSpatialGrid) Grid.Insert(entityId, position, radius, team);
            else BruteForceIndex.Insert(entityId, position, radius, team);
        }

        public bool SpatialFindNearest(in FPVector3 origin, FP maxRadius, byte targetTeam, int excludeEntityId, out SpatialEntry nearest, out FP nearestSqrDist)
        {
            if (Config.UseSpatialGrid)
                return Grid.FindNearest(origin, maxRadius, targetTeam, excludeEntityId, out nearest, out nearestSqrDist);
            return BruteForceIndex.FindNearest(origin, maxRadius, targetTeam, excludeEntityId, out nearest, out nearestSqrDist);
        }

        public void SpatialQueryTeamInRadius(in FPVector3 origin, FP radius, byte targetTeam, List<SpatialEntry> results)
        {
            if (Config.UseSpatialGrid) Grid.QueryTeamInRadius(origin, radius, targetTeam, results);
            else BruteForceIndex.QueryTeamInRadius(origin, radius, targetTeam, results);
        }

        public FPVector3 SpatialComputeSeparation(int selfEntityId, in FPVector3 origin, FP desiredSeparation, byte team, int maxNeighbors = 12)
        {
            if (Config.UseSpatialGrid)
                return Grid.ComputeSeparation(selfEntityId, origin, desiredSeparation, team, maxNeighbors);
            return BruteForceIndex.ComputeSeparation(selfEntityId, origin, desiredSeparation, team, maxNeighbors);
        }

        // ================= 战场生成与主循环 =================

        public void SpawnInitialBattle(int initialEnemies = 300)
        {
            Store.CreateEntity(new WaveDirector
            {
                WaveIndex = 1,
                WaveTimer = FP.FromInt(10),
                WaveInterval = FP.FromInt(10),
                SpawnPerWave = 120,
                MaxEnemyCap = 4000,
            });

            SpawnSquad();

            if (initialEnemies > 0)
            {
                SpawnEnemyBatch(initialEnemies);
            }

            RefreshSnapshot();
        }

        private void SpawnSquad()
        {
            Entity player = Store.CreateEntity(
                new UnitTransform(
                    new FPVector3(FP.Zero, FP.FromMilli(600), FP.Zero),
                    new FPVector3(FP.FromMilli(1200), FP.FromMilli(1200), FP.FromMilli(1200))),
                new UnitVelocity(moveSpeed: FP.FromMilli(9500), radius: FP.FromMilli(700), invMass: FP.FromMilli(80)),
                new CombatStats(
                    maxHp: FP.FromInt(500),
                    atk: FP.FromInt(36),
                    attackRange: FP.FromInt(11),
                    aggroRange: FP.FromInt(14),
                    attackInterval: FP.FromMilli(350)),
                new CombatTarget(),
                UnitState.CreateDefault(),
                new SkillCaster
                {
                    VolleyCooldown = FP.FromInt(3),
                    VolleyTimer = FP.FromMilli(800),
                    VolleyDamage = FP.FromInt(48),
                    VolleyCount = 5,
                    NovaCooldown = FP.FromInt(5),
                    NovaTimer = FP.FromInt(2),
                    NovaRadius = FP.FromInt(8),
                    NovaDamage = FP.FromInt(95),
                    HealCooldown = FP.FromMilli(7500),
                    HealTimer = FP.FromInt(4),
                    HealAmount = FP.FromInt(90),
                },
                new CubeVisual(new Color(0.13f, 0.83f, 0.93f)),
                Tags.Get<PlayerTag>()
            );
            PlayerEntityId = player.Id;
            PlayerPosition = FPVector3.Zero;

            FPVector3[] offsets =
            {
                new(FP.FromMilli(-2400), FP.Zero, FP.FromMilli(-1500)),
                new(FP.FromMilli(2400),  FP.Zero, FP.FromMilli(-1500)),
                new(FP.FromMilli(-4000), FP.Zero, FP.FromMilli(-3400)),
                new(FP.FromMilli(4000),  FP.Zero, FP.FromMilli(-3400)),
            };

            for (int i = 0; i < SquadAllyCount; i++)
            {
                FPVector3 spawnPos = new(offsets[i].X, FP.FromMilli(480), offsets[i].Z);
                Store.CreateEntity(
                    new UnitTransform(spawnPos, new FPVector3(FP.FromMilli(950), FP.FromMilli(950), FP.FromMilli(950))),
                    new UnitVelocity(moveSpeed: FP.FromMilli(10500), radius: FP.FromMilli(550), invMass: FP.FromMilli(350)),
                    new CombatStats(
                        maxHp: FP.FromInt(260),
                        atk: FP.FromInt(24),
                        attackRange: FP.FromMilli(9500),
                        aggroRange: FP.FromInt(13),
                        attackInterval: FP.FromMilli(500)),
                    new CombatTarget(),
                    UnitState.CreateDefault(),
                    new AllyFormation(i, offsets[i], leashDistance: FP.FromInt(12)),
                    new SkillCaster
                    {
                        VolleyCooldown = FP.FromMilli(4200),
                        VolleyTimer = FP.FromMilli(1200 + i * 500),
                        VolleyDamage = FP.FromInt(32),
                        VolleyCount = 3,
                        NovaCooldown = FP.Zero,
                        HealCooldown = FP.Zero,
                    },
                    new CubeVisual(new Color(0.23f, 0.51f, 0.96f)),
                    Tags.Get<AllyTag>()
                );
            }
        }

        public int SpawnEnemyBatch(int count, int hardCap = 4000)
        {
            if (count <= 0) return 0;

            int currentEnemies = Store.Query().AllTags(Tags.Get<EnemyTag>()).WithoutAllTags(Tags.Get<DeadTag>()).Count;
            int allowed = Math.Min(count, hardCap - currentEnemies);
            if (allowed <= 0) return 0;

            FPVector3 center = PlayerPosition;
            FP minBound = -ArenaHalfSize + FP.One;
            FP maxBound = ArenaHalfSize - FP.One;

            for (int i = 0; i < allowed; i++)
            {
                FP angle = _rng.NextAngleRad();
                FP dist = _rng.Range(FP.FromInt(14), FP.FromInt(38));

                FP x = FPMath.Clamp(center.X + FPMath.Cos(angle) * dist, minBound, maxBound);
                FP z = FPMath.Clamp(center.Z + FPMath.Sin(angle) * dist, minBound, maxBound);

                bool isElite = (i % 10) == 0;
                FP scale = isElite ? FP.FromMilli(1050) : FP.FromMilli(800);
                FP maxHp = isElite ? FP.FromInt(140) : FP.FromInt(65);
                FP speed = isElite ? FP.FromMilli(4200) : _rng.Range(FP.FromMilli(4600), FP.FromMilli(5700));
                Color color = isElite ? new Color(0.98f, 0.45f, 0.09f) : new Color(0.90f, 0.22f, 0.27f);

                UnitState initState = UnitState.CreateDefault();
                if (isElite)
                {
                    initState.TryEnter(UnitStateType.SuperArmor);
                }

                Store.CreateEntity(
                    new UnitTransform(new FPVector3(x, scale * FP.Half, z), new FPVector3(scale, scale, scale)),
                    new UnitVelocity(moveSpeed: speed, radius: scale * FP.FromMilli(550), invMass: isElite ? FP.FromMilli(350) : FP.One),
                    new CombatStats(
                        maxHp: maxHp,
                        atk: FP.FromInt(isElite ? 12 : 6),
                        attackRange: FP.FromMilli(1450),
                        aggroRange: FP.FromInt(60),
                        attackInterval: FP.FromMilli(900)),
                    new CombatTarget(),
                    initState,
                    new CubeVisual(color),
                    Tags.Get<EnemyTag>()
                );
            }

            return allowed;
        }

        public void ClearAllEnemies()
        {
            EntityIdScratch.Clear();
            var query = Store.Query().AllTags(Tags.Get<EnemyTag>());
            foreach (var entity in query.Entities)
            {
                EntityIdScratch.Add(entity.Id);
            }
            for (int i = 0; i < EntityIdScratch.Count; i++)
            {
                if (Store.TryGetEntityById(EntityIdScratch[i], out var ent) && !ent.IsNull)
                {
                    ent.DeleteEntity();
                }
            }
            EntityIdScratch.Clear();
            RefreshSnapshot();
        }

        public void Update(float deltaTime)
        {
            if (_disposed) return;

            float clamped = Mathf.Clamp(deltaTime, 0f, 0.05f);
            UpdateLogicTick(FP.FromFloat(clamped));
            UpdatePresentation();
        }

        public void UpdateLogicTick(FP fixedDeltaTime)
        {
            if (_disposed) return;

            LogicDeltaTimeFP = fixedDeltaTime;
            long memBefore = GC.GetTotalMemory(false);
            _stopwatch.Restart();
            LogicRoot.Update(default);
            _stopwatch.Stop();
            long memAfter = GC.GetTotalMemory(false);

            _snapshot.LogicMs = _stopwatch.Elapsed.TotalMilliseconds;
            if (memAfter >= memBefore)
            {
                double kb = (memAfter - memBefore) / 1024.0;
                _smoothedGcKb = _tickCount == 0 ? kb : (_smoothedGcKb * 0.85 + kb * 0.15);
            }
            _snapshot.GcAllocKbPerFrame = _smoothedGcKb;

            if (Store.EventRecorder.Enabled && Store.EventRecorder.AllEventsCount > 0)
            {
                Store.EventRecorder.ClearEvents();
            }

            if ((++_tickCount & 63) == 0)
            {
                DirtyHub.TrimBefore(DirtyHub.CurrentSequence);
            }

            RefreshSnapshot();
        }

        public void UpdateLogicTick(float fixedDeltaTime) => UpdateLogicTick(FP.FromFloat(fixedDeltaTime));

        public void UpdatePresentation()
        {
            if (_disposed || PresentationRoot == null) return;

            _stopwatch.Restart();
            // 先更新视锥裁切（教学开关 6），再提交表现层渲染
            if (Config.UseFrustumCulling)
            {
                CullingRoot.Update(default);
            }
            PresentationRoot.Update(default);
            _stopwatch.Stop();

            _snapshot.RenderMs = _stopwatch.Elapsed.TotalMilliseconds;
            _snapshot.ActiveUnitGameObjects = _gameObjectRenderSystem != null ? _gameObjectRenderSystem.ActiveGameObjectCount : 0;
            if (Config.UseFrustumCulling)
            {
                _snapshot.VisibleRenderCount = CullingSystem.VisibleCount;
                _snapshot.CulledRenderCount = CullingSystem.CulledCount;
            }
            else
            {
                _snapshot.VisibleRenderCount = _instancedRenderSystem != null
                    ? _instancedRenderSystem.SubmittedInstanceCount
                    : _snapshot.ActiveUnitGameObjects;
                _snapshot.CulledRenderCount = 0;
            }
        }

        public void SpawnProjectile(FPVector3 origin, FPVector3 direction, FP speed, FP damage, FP lifeTime, FP hitRadius, byte team, Color color, int pierceLeft = 0)
        {
            FPVector3 flatDir = direction.NormalizedXZ();
            if (flatDir.SqrMagnitudeXZ().RawValue == 0L) flatDir = FPVector3.Forward;

            FP yaw = FPMath.Atan2Deg(flatDir.X, flatDir.Z);
            Store.CreateEntity(
                new UnitTransform(
                    new FPVector3(origin.X, FP.FromMilli(650), origin.Z),
                    new FPVector3(FP.FromMilli(280), FP.FromMilli(280), FP.FromMilli(850)),
                    yaw),
                new ProjectileData
                {
                    Direction = flatDir,
                    Speed = speed,
                    Damage = damage,
                    LifeTime = lifeTime,
                    HitRadius = hitRadius,
                    Team = team,
                    PierceLeft = pierceLeft,
                },
                new CubeVisual(color)
            );
        }

        public void SpawnPulseEffect(FPVector3 origin, FP startRadius, FP endRadius, FP duration, Color color, FP height = default)
        {
            if (height.RawValue <= 0L) height = FP.FromMilli(140);
            FP diameter = startRadius * FP.FromInt(2);
            Store.CreateEntity(
                new UnitTransform(
                    new FPVector3(origin.X, height * FP.Half + FP.FromMilli(30), origin.Z),
                    new FPVector3(diameter, height, diameter)),
                new PulseEffect
                {
                    Elapsed = FP.Zero,
                    Duration = duration,
                    StartRadius = startRadius,
                    EndRadius = endRadius,
                    Height = height,
                    Color = color,
                },
                new CubeVisual(color)
            );
        }

        public ulong ComputeStateHash()
        {
            const ulong fnvOffset = 14695981039346656037UL;
            const ulong fnvPrime = 1099511628211UL;
            ulong hash = fnvOffset;

            static ulong Mix(ulong h, ulong val) => (h ^ val) * fnvPrime;

            hash = Mix(hash, _rng.State);
            hash = Mix(hash, (ulong)TotalKills);
            hash = Mix(hash, (ulong)PlayerPosition.X.RawValue);
            hash = Mix(hash, (ulong)PlayerPosition.Z.RawValue);

            var unitQuery = Store.Query<UnitTransform, CombatStats, UnitState>();
            unitQuery.ForEachEntity((ref UnitTransform tr, ref CombatStats stats, ref UnitState st, Entity e) =>
            {
                hash = Mix(hash, (ulong)e.Id);
                hash = Mix(hash, (ulong)tr.Position.X.RawValue);
                hash = Mix(hash, (ulong)tr.Position.Y.RawValue);
                hash = Mix(hash, (ulong)tr.Position.Z.RawValue);
                hash = Mix(hash, (ulong)stats.Hp.RawValue);
                hash = Mix(hash, st.Mask);
            });

            var projQuery = Store.Query<UnitTransform, ProjectileData>();
            projQuery.ForEachEntity((ref UnitTransform tr, ref ProjectileData p, Entity e) =>
            {
                hash = Mix(hash, (ulong)e.Id);
                hash = Mix(hash, (ulong)tr.Position.X.RawValue);
                hash = Mix(hash, (ulong)tr.Position.Z.RawValue);
                hash = Mix(hash, (ulong)p.LifeTime.RawValue);
            });

            return hash;
        }

        private void RefreshSnapshot()
        {
            _snapshot.StageName = string.IsNullOrEmpty(Config.StageName) ? "自定义组合" : Config.StageName;
            _snapshot.OptimizationSummary = Config.BuildShortTagSummary();
            _snapshot.TotalAllies = SquadAllyCount;
            _snapshot.TotalKills = TotalKills;
            _snapshot.ActiveUnitGameObjects = _gameObjectRenderSystem != null ? _gameObjectRenderSystem.ActiveGameObjectCount : 0;
            _snapshot.CollisionResolvedPairs = Config.UseHardCollision ? CollisionSolver.LastResolvedPairs : 0;

            if (Store.TryGetEntityById(PlayerEntityId, out var player) && !player.IsNull)
            {
                ref var stats = ref player.GetComponent<CombatStats>();
                _snapshot.PlayerHp = stats.Hp.ToFloat();
                _snapshot.PlayerMaxHp = stats.MaxHp.ToFloat();
                _snapshot.BasicCdRatio = stats.AttackInterval.RawValue > 0L
                    ? Mathf.Clamp01((stats.AttackCooldown / stats.AttackInterval).ToFloat())
                    : 0f;

                if (player.TryGetComponent<UnitState>(out var st))
                {
                    _snapshot.PlayerHasSuperArmor = st.Has(UnitStateType.SuperArmor);
                }

                if (player.TryGetComponent<SkillCaster>(out var caster))
                {
                    _snapshot.VolleyCdRatio = caster.VolleyCooldown.RawValue > 0L ? Mathf.Clamp01((caster.VolleyTimer / caster.VolleyCooldown).ToFloat()) : 0f;
                    _snapshot.NovaCdRatio = caster.NovaCooldown.RawValue > 0L ? Mathf.Clamp01((caster.NovaTimer / caster.NovaCooldown).ToFloat()) : 0f;
                    _snapshot.HealCdRatio = caster.HealCooldown.RawValue > 0L ? Mathf.Clamp01((caster.HealTimer / caster.HealCooldown).ToFloat()) : 0f;
                }
            }

            _snapshot.AliveAllies = Store.Query().AllTags(Tags.Get<AllyTag>()).WithoutAllTags(Tags.Get<DeadTag>()).Count;
            _snapshot.EnemyCount = Store.Query().AllTags(Tags.Get<EnemyTag>()).WithoutAllTags(Tags.Get<DeadTag>()).Count;
            _snapshot.ProjectileCount = Store.Query<ProjectileData>().Count;

            int stunned = 0;
            var enemyStateQuery = Store.Query<UnitState>().AllTags(Tags.Get<EnemyTag>()).WithoutAllTags(Tags.Get<DeadTag>());
            enemyStateQuery.ForEachEntity((ref UnitState st, Entity _) =>
            {
                if (st.Has(UnitStateType.Stunned)) stunned++;
            });
            _snapshot.StunnedEnemyCount = stunned;

            var waveQuery = Store.Query<WaveDirector>();
            foreach (var entity in waveQuery.Entities)
            {
                ref var wave = ref entity.GetComponent<WaveDirector>();
                _snapshot.WaveIndex = wave.WaveIndex;
                _snapshot.WaveCountdown = Mathf.Max(0f, wave.WaveTimer.ToFloat());
                break;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _waveReactiveSystem?.ClearSubscriptions();
            _bucketedCombatReactive?.ClearSubscriptions();
            _legacyCombatReactive?.Dispose();
            _instancedRenderSystem?.Dispose();
            _gameObjectRenderSystem?.Dispose();
            DirtyHub.ClearAllRecords();
            PendingDamages.Clear();
            Grid.Clear();
            BruteForceIndex.Clear();
        }
    }
}

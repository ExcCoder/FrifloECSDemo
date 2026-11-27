using F8Framework.Core;
using F8Framework.Launcher;
using HotUpdate.Core;
using HotUpdate.ECS;
using HotUpdate.ECS.Buff;
using HotUpdate.ECS.Config;
using HotUpdate.ECS.Lockstep;
using HotUpdate.UI;
using UnityEngine;

namespace HotUpdate.Procedure
{
    /// <summary>
    /// 战斗流程：按 BenchmarkStageCatalog.CurrentConfig 创建 BattleWorld，支持：
    /// - 战斗中零重载热切换关卡或六大优化开关；
    /// - 20Hz 帧同步回环（MockRelay）、60fps 渲染插值开关、一键无头高速追帧（200帧/10秒）；
    /// - 肉鸽升级自动/手动弹出三选一词条（F8 LayerType.PopUp）。
    /// </summary>
    public class FightProcedure : ProcedureNode
    {
        public bool IsPaused { get; private set; }
        public BattleWorld World { get; private set; }
        public LockstepRunner Lockstep { get; } = new LockstepRunner();

        public override void OnInit(ProcedureProcessor processor) { }

        public override void OnEnter(ProcedureProcessor processor)
        {
            IsPaused = false;
            Time.timeScale = 1f;

            SceneFlowManager.EnsureSceneLoaded(SceneNames.Fight, () =>
            {
                StartWorldWithConfig(BenchmarkStageCatalog.CurrentConfig);
                FF8.UI.Open(UIID.UIFight);
            });
        }

        public override void OnUpdate(ProcedureProcessor processor)
        {
            if (World == null) return;

            if (IsPaused)
            {
                World.UpdatePresentation();
                return;
            }

            Lockstep.TickRenderFrame(World, Time.deltaTime);

            // 升级触发肉鸽三选一弹窗（不与系统暂停菜单冲突）
            if (World.PendingCardPicks > 0
                && !FF8.UI.Has(UIID.UIRoguelikeCard)
                && !FF8.UI.Has(UIID.UIGameMenu))
            {
                OpenCardSelectionPopup();
            }
        }

        public void SetPaused(bool paused)
        {
            IsPaused = paused;
            Time.timeScale = paused ? 0f : 1f;
        }

        /// <summary>
        /// 热切换关卡或优化开关并立即用相同固定种子重启当前战局（无需重载场景，便于课堂/演示实时对比帧率）。
        /// </summary>
        public void RestartWithConfig(BattleOptimizationConfig newConfig)
        {
            BenchmarkStageCatalog.CurrentConfig = newConfig;
            if (FF8.UI.Has(UIID.UIRoguelikeCard))
            {
                FF8.UI.Close(UIID.UIRoguelikeCard, false);
            }
            IsPaused = false;
            Time.timeScale = 1f;
            StartWorldWithConfig(newConfig);
        }

        public void SwitchPrevStage()
        {
            BenchmarkStageCatalog.SelectPrevPreset();
            RestartWithConfig(BenchmarkStageCatalog.CurrentConfig);
        }

        public void SwitchNextStage()
        {
            BenchmarkStageCatalog.SelectNextPreset();
            RestartWithConfig(BenchmarkStageCatalog.CurrentConfig);
        }

        public void ToggleOptimizationAndRestart(int flagIndex)
        {
            var cfg = BenchmarkStageCatalog.CurrentConfig;
            cfg.StageId = 0;
            cfg.StageName = "自定义优化组合";
            switch (flagIndex)
            {
                case 0: cfg.UseGpuInstancing = !cfg.UseGpuInstancing; break;
                case 1: cfg.UseSpatialGrid = !cfg.UseSpatialGrid; break;
                case 2: cfg.UseBucketedReactive = !cfg.UseBucketedReactive; break;
                case 3: cfg.UseOptimizedCleanup = !cfg.UseOptimizedCleanup; break;
                case 4: cfg.UseHardCollision = !cfg.UseHardCollision; break;
                case 5: cfg.UseFrustumCulling = !cfg.UseFrustumCulling; break;
            }
            RestartWithConfig(cfg);
        }

        public void ToggleLockstep20Hz()
        {
            Lockstep.UseLockstep20Hz = !Lockstep.UseLockstep20Hz;
        }

        public void ToggleInterpolation()
        {
            Lockstep.EnableInterpolation = !Lockstep.EnableInterpolation;
        }

        public double TriggerFastForwardCatchup(int frames = 200)
        {
            if (World == null) return 0;
            double elapsedMs = Lockstep.RunHeadlessCatchup(World, frames);
            World.UpdatePresentation();
            return elapsedMs;
        }

        public void OpenCardSelectionPopup()
        {
            if (World == null) return;
            SetPaused(true);
            if (!FF8.UI.Has(UIID.UIRoguelikeCard))
            {
                FF8.UI.Open(UIID.UIRoguelikeCard);
            }
        }

        public void ApplySelectedCard(RoguelikeCardId cardId)
        {
            World?.ApplyRoguelikeCard(cardId);
            SetPaused(false);
        }

        public void DismissCardSelectionPopup()
        {
            if (World != null && World.PendingCardPicks > 0)
            {
                World.PendingCardPicks--;
            }
            SetPaused(false);
        }

        public void SpawnExtraEnemies(int count) => World?.SpawnEnemyBatch(count);
        public void ClearEnemies() => World?.ClearAllEnemies();

        public override void OnExit(ProcedureProcessor processor)
        {
            IsPaused = false;
            Time.timeScale = 1f;
            DisposeWorld();

            if (FF8.UI.Has(UIID.UIRoguelikeCard))
            {
                FF8.UI.Close(UIID.UIRoguelikeCard, false);
            }
            if (FF8.UI.Has(UIID.UIGameMenu))
            {
                FF8.UI.Close(UIID.UIGameMenu, false);
            }
            FF8.UI.Close(UIID.UIFight, false);
        }

        public override void OnDestroy(ProcedureProcessor processor) => DisposeWorld();

        private void StartWorldWithConfig(BattleOptimizationConfig config)
        {
            DisposeWorld();
            Lockstep.Reset();
            World = new BattleWorld(config, enablePresentation: true);
            World.SpawnInitialBattle(initialEnemies: config.InitialEnemyCount);
        }

        private void DisposeWorld()
        {
            if (World != null)
            {
                World.Dispose();
                World = null;
            }
        }
    }
}

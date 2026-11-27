using F8Framework.Core;
using F8Framework.Launcher;
using HotUpdate.Core;
using HotUpdate.ECS;
using HotUpdate.ECS.Config;
using HotUpdate.Procedure;
using UnityEngine;
using UnityEngine.UI;

namespace HotUpdate.UI
{
    /// <summary>
    /// 战斗 HUD 界面（Fight 场景）：
    /// - 左上：实时教学性能仪表盘（当前关卡、FPS、Logic ms、Render ms、单帧 GC KB、单位 GameObject 数、敌人数与眩晕数）；
    /// - 右侧：实时热切换关卡（上一关/下一关）与四大优化开关，无需切场景即可实时对比帧率差异！
    /// </summary>
    public class UIFightView : BaseView
    {
        private static readonly Color OnColor = new(0.06f, 0.58f, 0.40f, 0.95f);
        private static readonly Color OffColor = new(0.58f, 0.16f, 0.20f, 0.95f);

        [SerializeField] private Text txtStats;
        [SerializeField] private Text txtWave;
        [SerializeField] private Button btnPauseMenu;
        [SerializeField] private Button btnBackGame;
        [SerializeField] private Button btnSpawn200;
        [SerializeField] private Button btnSpawn1000;
        [SerializeField] private Button btnClearEnemies;
        [SerializeField] private Button btnPrevStage;
        [SerializeField] private Button btnNextStage;
        [SerializeField] private Button[] liveToggleButtons;
        [SerializeField] private Text[] skillSlotLabels;

        private float _fpsTimer;
        private int _fpsFrames;
        private float _currentFps;
        private float _hudRefreshTimer;

        protected override void OnAwake()
        {
            if (txtStats == null) txtStats = transform.Find("TopLeftStats/TxtStats")?.GetComponent<Text>();
            if (txtWave == null) txtWave = transform.Find("TopCenterWave/TxtWave")?.GetComponent<Text>();
            if (btnPauseMenu == null) btnPauseMenu = transform.Find("TopRightActions/BtnPauseMenu")?.GetComponent<Button>();
            if (btnBackGame == null) btnBackGame = transform.Find("TopRightActions/BtnBackGame")?.GetComponent<Button>();
            if (btnSpawn200 == null) btnSpawn200 = transform.Find("StressBar/BtnSpawn200")?.GetComponent<Button>();
            if (btnSpawn1000 == null) btnSpawn1000 = transform.Find("StressBar/BtnSpawn1000")?.GetComponent<Button>();
            if (btnClearEnemies == null) btnClearEnemies = transform.Find("StressBar/BtnClearEnemies")?.GetComponent<Button>();
            if (btnPrevStage == null) btnPrevStage = transform.Find("LiveBenchPanel/BtnPrevStage")?.GetComponent<Button>();
            if (btnNextStage == null) btnNextStage = transform.Find("LiveBenchPanel/BtnNextStage")?.GetComponent<Button>();

            if (liveToggleButtons == null || liveToggleButtons.Length == 0)
            {
                liveToggleButtons = new Button[6];
                for (int i = 0; i < 6; i++)
                {
                    liveToggleButtons[i] = transform.Find($"LiveBenchPanel/BtnLiveToggle_{i}")?.GetComponent<Button>();
                }
            }

            if (skillSlotLabels == null || skillSlotLabels.Length == 0)
            {
                skillSlotLabels = new Text[4];
                for (int i = 0; i < 4; i++)
                {
                    skillSlotLabels[i] = transform.Find($"BottomSkillBar/SkillSlot_{i + 1}/Label")?.GetComponent<Text>();
                }
            }
        }

        protected override void OnAdded(int uiId, object[] args = null)
        {
            _fpsTimer = 0f;
            _fpsFrames = 0;
            _currentFps = 0f;
            _hudRefreshTimer = 0f;

            BindButton(btnPauseMenu, OnClickPauseMenu);
            BindButton(btnBackGame, OnClickBackGame);
            BindButton(btnSpawn200, OnClickSpawn200);
            BindButton(btnSpawn1000, OnClickSpawn1000);
            BindButton(btnClearEnemies, OnClickClearEnemies);
            BindButton(btnPrevStage, OnClickPrevStage);
            BindButton(btnNextStage, OnClickNextStage);

            if (liveToggleButtons != null)
            {
                for (int i = 0; i < liveToggleButtons.Length; i++)
                {
                    int flagIdx = i;
                    if (liveToggleButtons[i] == null) continue;
                    liveToggleButtons[i].onClick.RemoveAllListeners();
                    liveToggleButtons[i].onClick.AddListener(() => OnClickLiveToggle(flagIdx));
                }
            }

            RefreshHud();
        }

        protected override void OnBeforeRemove()
        {
            UnbindButton(btnPauseMenu, OnClickPauseMenu);
            UnbindButton(btnBackGame, OnClickBackGame);
            UnbindButton(btnSpawn200, OnClickSpawn200);
            UnbindButton(btnSpawn1000, OnClickSpawn1000);
            UnbindButton(btnClearEnemies, OnClickClearEnemies);
            UnbindButton(btnPrevStage, OnClickPrevStage);
            UnbindButton(btnNextStage, OnClickNextStage);
        }

        private void Update()
        {
            _fpsFrames++;
            _fpsTimer += Time.unscaledDeltaTime;
            if (_fpsTimer >= 0.4f)
            {
                _currentFps = _fpsFrames / _fpsTimer;
                _fpsFrames = 0;
                _fpsTimer = 0f;
            }

            _hudRefreshTimer += Time.unscaledDeltaTime;
            if (_hudRefreshTimer >= 0.1f)
            {
                _hudRefreshTimer = 0f;
                RefreshHud();
            }
        }

        private void RefreshHud()
        {
            if (!FF8.Procedure.PeekProcedureNode(out FightProcedure fightProc) || fightProc.World == null)
            {
                return;
            }

            BattleSnapshot snap = fightProc.World.Snapshot;
            BattleOptimizationConfig cfg = fightProc.World.Config;

            if (txtStats != null)
            {
                string armorTag = snap.PlayerHasSuperArmor ? " [霸体]" : "";
                txtStats.text =
                    $"FPS: {_currentFps:F0}  |  Logic: {snap.LogicMs:F2}ms  Render: {snap.RenderMs:F2}ms\n" +
                    $"渲染: {snap.VisibleRenderCount} / 视锥剔除: {snap.CulledRenderCount}  |  单位 GO: {snap.ActiveUnitGameObjects}\n" +
                    $"单帧 GC: ~{snap.GcAllocKbPerFrame:F1} KB  |  硬碰撞解穿透: {snap.CollisionResolvedPairs}对\n" +
                    $"主控 HP: {snap.PlayerHp:F0}/{snap.PlayerMaxHp:F0}{armorTag}  |  小弟: {snap.AliveAllies}/{snap.TotalAllies}  |  敌人: {snap.EnemyCount}  |  击杀: {snap.TotalKills}";
            }

            if (txtWave != null)
            {
                txtWave.text = $"{snap.StageName} · 第 {snap.WaveIndex} 波（WASD 移动）";
            }

            UpdateLiveToggleButton(0, "Instancing", cfg.UseGpuInstancing);
            UpdateLiveToggleButton(1, "空间网格", cfg.UseSpatialGrid);
            UpdateLiveToggleButton(2, "O(K)脏桶", cfg.UseBucketedReactive);
            UpdateLiveToggleButton(3, "清理优化", cfg.UseOptimizedCleanup);
            UpdateLiveToggleButton(4, "PBD硬碰撞", cfg.UseHardCollision);
            UpdateLiveToggleButton(5, "视锥裁剪", cfg.UseFrustumCulling);

            if (skillSlotLabels != null && skillSlotLabels.Length >= 4)
            {
                SetSkillLabel(skillSlotLabels[0], "自动普攻", snap.BasicCdRatio);
                SetSkillLabel(skillSlotLabels[1], "扇形弹幕", snap.VolleyCdRatio);
                SetSkillLabel(skillSlotLabels[2], "脉冲冲击", snap.NovaCdRatio);
                SetSkillLabel(skillSlotLabels[3], "小队治疗", snap.HealCdRatio);
            }
        }

        private void UpdateLiveToggleButton(int idx, string name, bool enabled)
        {
            if (liveToggleButtons == null || idx >= liveToggleButtons.Length || liveToggleButtons[idx] == null) return;
            var img = liveToggleButtons[idx].GetComponent<Image>();
            if (img != null) img.color = enabled ? OnColor : OffColor;
            var txt = liveToggleButtons[idx].GetComponentInChildren<Text>();
            if (txt != null) txt.text = $"{name}: {(enabled ? "ON" : "OFF")}";
        }

        private static void SetSkillLabel(Text label, string title, float cdRatio)
        {
            if (label == null) return;
            label.text = cdRatio <= 0.01f
                ? $"{title}\n[就绪]"
                : $"{title}\n[{(1f - cdRatio) * 100f:F0}%]";
        }

        private static void BindButton(Button btn, UnityEngine.Events.UnityAction action)
        {
            if (btn == null) return;
            btn.onClick.RemoveListener(action);
            btn.onClick.AddListener(action);
        }

        private static void UnbindButton(Button btn, UnityEngine.Events.UnityAction action)
        {
            if (btn == null) return;
            btn.onClick.RemoveListener(action);
        }

        private void OnClickPauseMenu()
        {
            if (FF8.Procedure.PeekProcedureNode(out FightProcedure fightProc))
            {
                fightProc.SetPaused(true);
            }
            FF8.UI.Open(UIID.UIGameMenu, new object[] { "Fight" });
        }

        private void OnClickBackGame() => SceneFlowManager.EnterGame();

        private void OnClickSpawn200()
        {
            if (FF8.Procedure.PeekProcedureNode(out FightProcedure fp))
            {
                fp.SpawnExtraEnemies(200);
                RefreshHud();
            }
        }

        private void OnClickSpawn1000()
        {
            if (FF8.Procedure.PeekProcedureNode(out FightProcedure fp))
            {
                fp.SpawnExtraEnemies(1000);
                RefreshHud();
            }
        }

        private void OnClickClearEnemies()
        {
            if (FF8.Procedure.PeekProcedureNode(out FightProcedure fp))
            {
                fp.ClearEnemies();
                RefreshHud();
            }
        }

        private void OnClickPrevStage()
        {
            if (FF8.Procedure.PeekProcedureNode(out FightProcedure fp))
            {
                fp.SwitchPrevStage();
                RefreshHud();
            }
        }

        private void OnClickNextStage()
        {
            if (FF8.Procedure.PeekProcedureNode(out FightProcedure fp))
            {
                fp.SwitchNextStage();
                RefreshHud();
            }
        }

        private void OnClickLiveToggle(int flagIndex)
        {
            if (FF8.Procedure.PeekProcedureNode(out FightProcedure fp))
            {
                fp.ToggleOptimizationAndRestart(flagIndex);
                RefreshHud();
            }
        }
    }
}

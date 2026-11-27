using F8Framework.Core;
using F8Framework.Launcher;
using HotUpdate.Core;
using HotUpdate.ECS.Config;
using UnityEngine;
using UnityEngine.UI;

namespace HotUpdate.UI
{
    /// <summary>
    /// 游戏主界面（Game 场景）：
    /// - 左侧提供 6 个渐进式教学对比关卡预设按钮；
    /// - 右侧/下方提供 4 个独立优化开关与 3 档初始敌军数量，支持任意混搭后直接进入 Fight 场景压测。
    /// </summary>
    public class UIGameView : BaseView
    {
        private static readonly Color ActiveColor = new(0.15f, 0.39f, 0.92f, 1f);
        private static readonly Color NormalColor = new(0.18f, 0.23f, 0.31f, 0.96f);
        private static readonly Color OnColor = new(0.06f, 0.62f, 0.43f, 1f);
        private static readonly Color OffColor = new(0.55f, 0.18f, 0.22f, 0.96f);

        [SerializeField] private Button btnStartFight;
        [SerializeField] private Button btnOpenMenu;
        [SerializeField] private Button btnBackLobby;
        [SerializeField] private Text txtSquadInfo;

        [SerializeField] private Button[] stageButtons;
        [SerializeField] private Button[] toggleButtons;
        [SerializeField] private Button[] enemyCountButtons;

        private readonly int[] _enemyCountOptions = { 300, 1000, 2000 };

        protected override void OnAwake()
        {
            if (btnStartFight == null) btnStartFight = transform.Find("BottomBar/BtnStartFight")?.GetComponent<Button>();
            if (btnOpenMenu == null) btnOpenMenu = transform.Find("TopBar/BtnOpenMenu")?.GetComponent<Button>();
            if (btnBackLobby == null) btnBackLobby = transform.Find("TopBar/BtnBackLobby")?.GetComponent<Button>();
            if (txtSquadInfo == null) txtSquadInfo = transform.Find("StageInfoPanel/TxtSquadInfo")?.GetComponent<Text>();

            if (stageButtons == null || stageButtons.Length == 0)
            {
                stageButtons = new Button[BenchmarkStageCatalog.Presets.Length];
                for (int i = 0; i < stageButtons.Length; i++)
                {
                    stageButtons[i] = transform.Find($"StageSelectPanel/BtnStage_{i + 1}")?.GetComponent<Button>();
                }
            }

            if (toggleButtons == null || toggleButtons.Length == 0)
            {
                toggleButtons = new Button[6];
                for (int i = 0; i < 6; i++)
                {
                    toggleButtons[i] = transform.Find($"StageInfoPanel/BtnToggle_{i}")?.GetComponent<Button>();
                }
            }

            if (enemyCountButtons == null || enemyCountButtons.Length == 0)
            {
                enemyCountButtons = new Button[3];
                for (int i = 0; i < 3; i++)
                {
                    enemyCountButtons[i] = transform.Find($"StageInfoPanel/BtnCount_{i}")?.GetComponent<Button>();
                }
            }
        }

        protected override void OnAdded(int uiId, object[] args = null)
        {
            BindSimple(btnStartFight, OnClickStartFight);
            BindSimple(btnOpenMenu, OnClickOpenMenu);
            BindSimple(btnBackLobby, OnClickBackLobby);

            if (stageButtons != null)
            {
                for (int i = 0; i < stageButtons.Length; i++)
                {
                    int idx = i;
                    if (stageButtons[i] == null) continue;
                    stageButtons[i].onClick.RemoveAllListeners();
                    stageButtons[i].onClick.AddListener(() => OnSelectStage(idx));
                }
            }

            if (toggleButtons != null)
            {
                for (int i = 0; i < toggleButtons.Length; i++)
                {
                    int flagIdx = i;
                    if (toggleButtons[i] == null) continue;
                    toggleButtons[i].onClick.RemoveAllListeners();
                    toggleButtons[i].onClick.AddListener(() => OnToggleFlag(flagIdx));
                }
            }

            if (enemyCountButtons != null)
            {
                for (int i = 0; i < enemyCountButtons.Length; i++)
                {
                    int countVal = _enemyCountOptions[i];
                    if (enemyCountButtons[i] == null) continue;
                    enemyCountButtons[i].onClick.RemoveAllListeners();
                    enemyCountButtons[i].onClick.AddListener(() => OnSelectEnemyCount(countVal));
                }
            }

            RefreshView();
        }

        protected override void OnBeforeRemove()
        {
            if (btnStartFight != null) btnStartFight.onClick.RemoveListener(OnClickStartFight);
            if (btnOpenMenu != null) btnOpenMenu.onClick.RemoveListener(OnClickOpenMenu);
            if (btnBackLobby != null) btnBackLobby.onClick.RemoveListener(OnClickBackLobby);
        }

        private void OnSelectStage(int index)
        {
            BenchmarkStageCatalog.SelectPresetByIndex(index);
            RefreshView();
        }

        private void OnToggleFlag(int flagIndex)
        {
            var cfg = BenchmarkStageCatalog.CurrentConfig;
            cfg.StageId = 0;
            cfg.StageName = "自定义优化组合";
            cfg.TeachingNote = "自由组合四大优化开关，进入 Fight 场景后还可实时热切换对比帧率与耗时！";
            switch (flagIndex)
            {
                case 0: cfg.UseGpuInstancing = !cfg.UseGpuInstancing; break;
                case 1: cfg.UseSpatialGrid = !cfg.UseSpatialGrid; break;
                case 2: cfg.UseBucketedReactive = !cfg.UseBucketedReactive; break;
                case 3: cfg.UseOptimizedCleanup = !cfg.UseOptimizedCleanup; break;
                case 4: cfg.UseHardCollision = !cfg.UseHardCollision; break;
                case 5: cfg.UseFrustumCulling = !cfg.UseFrustumCulling; break;
            }
            BenchmarkStageCatalog.CurrentConfig = cfg;
            RefreshView();
        }

        private void OnSelectEnemyCount(int count)
        {
            var cfg = BenchmarkStageCatalog.CurrentConfig;
            cfg.InitialEnemyCount = count;
            BenchmarkStageCatalog.CurrentConfig = cfg;
            RefreshView();
        }

        private void RefreshView()
        {
            var cfg = BenchmarkStageCatalog.CurrentConfig;

            if (txtSquadInfo != null)
            {
                txtSquadInfo.text =
                    $"【{cfg.StageName}】  初始敌军: {cfg.InitialEnemyCount}  |  我方: 1 主控 + 4 小弟\n" +
                    $"{cfg.TeachingNote}";
            }

            if (stageButtons != null)
            {
                for (int i = 0; i < stageButtons.Length && i < BenchmarkStageCatalog.Presets.Length; i++)
                {
                    if (stageButtons[i] == null) continue;
                    bool selected = cfg.StageId == BenchmarkStageCatalog.Presets[i].StageId;
                    var img = stageButtons[i].GetComponent<Image>();
                    if (img != null) img.color = selected ? ActiveColor : NormalColor;
                }
            }

            UpdateToggleButton(0, "GPU Instancing 渲染", cfg.UseGpuInstancing);
            UpdateToggleButton(1, "XZ 空间哈希网格 O(N)", cfg.UseSpatialGrid);
            UpdateToggleButton(2, "O(K) 分桶脏中心", cfg.UseBucketedReactive);
            UpdateToggleButton(3, "DeadTag 无冗余清理", cfg.UseOptimizedCleanup);
            UpdateToggleButton(4, "PBD 敌军硬碰撞+绕行", cfg.UseHardCollision);
            UpdateToggleButton(5, "3D 视锥体裁切", cfg.UseFrustumCulling);

            if (enemyCountButtons != null)
            {
                for (int i = 0; i < enemyCountButtons.Length && i < _enemyCountOptions.Length; i++)
                {
                    if (enemyCountButtons[i] == null) continue;
                    bool selected = cfg.InitialEnemyCount == _enemyCountOptions[i];
                    var img = enemyCountButtons[i].GetComponent<Image>();
                    if (img != null) img.color = selected ? ActiveColor : NormalColor;
                }
            }
        }

        private void UpdateToggleButton(int idx, string title, bool enabled)
        {
            if (toggleButtons == null || idx >= toggleButtons.Length || toggleButtons[idx] == null) return;
            var img = toggleButtons[idx].GetComponent<Image>();
            if (img != null) img.color = enabled ? OnColor : OffColor;
            var txt = toggleButtons[idx].GetComponentInChildren<Text>();
            if (txt != null) txt.text = $"{(enabled ? "[ON]  " : "[OFF] ")}{title}";
        }

        private static void BindSimple(Button btn, UnityEngine.Events.UnityAction action)
        {
            if (btn == null) return;
            btn.onClick.RemoveListener(action);
            btn.onClick.AddListener(action);
        }

        private void OnClickStartFight() => SceneFlowManager.EnterFight();
        private void OnClickOpenMenu() => FF8.UI.Open(UIID.UIGameMenu, new object[] { "Game" });
        private void OnClickBackLobby() => SceneFlowManager.EnterLobby();
    }
}

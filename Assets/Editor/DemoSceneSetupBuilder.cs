using System.IO;
using HotUpdate.Core;
using HotUpdate.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace FrifloECSDemo.Editor
{
    public static class DemoSceneSetupBuilder
    {
        private const string UiPrefabDir = "Assets/Resources/UI";
        private const string MaterialDir = "Assets/Materials";
        private const string SceneDir = "Assets/Scenes";
        private const string AssetBundlesDir = "Assets/AssetBundles";

        [MenuItem("开发工具/Demo/一键生成 Lobby-Game-Fight 场景与 UI 预制体")]
        public static void BuildAll()
        {
            EnsureDir(UiPrefabDir);
            EnsureDir(MaterialDir);
            EnsureDir(SceneDir);
            EnsureDir(AssetBundlesDir);

            Font font = GetDefaultFont();

            // 1. 生成 5 个 UI 预制体
            BuildUILobbyPrefab(font);
            BuildUIGamePrefab(font);
            BuildUIGameMenuPrefab(font);
            BuildUIFightPrefab(font);
            BuildUIRoguelikeCardPrefab(font);

            // 2. 生成基础材质
            Material matLobbyGround = CreateOrUpdateMaterial($"{MaterialDir}/MatLobbyGround.mat", new Color(0.11f, 0.16f, 0.23f));
            Material matGameGround = CreateOrUpdateMaterial($"{MaterialDir}/MatGameGround.mat", new Color(0.10f, 0.14f, 0.25f));
            Material matFightGround = CreateOrUpdateMaterial($"{MaterialDir}/MatFightGround.mat", new Color(0.15f, 0.17f, 0.21f));
            Material matPedestal = CreateOrUpdateMaterial($"{MaterialDir}/MatPedestal.mat", new Color(0.20f, 0.25f, 0.33f));
            Material matPlayer = CreateOrUpdateMaterial($"{MaterialDir}/MatPlayerCube.mat", new Color(0.13f, 0.83f, 0.93f));
            Material matAlly = CreateOrUpdateMaterial($"{MaterialDir}/MatAllyCube.mat", new Color(0.23f, 0.51f, 0.96f));
            Material matBorder = CreateOrUpdateMaterial($"{MaterialDir}/MatArenaBorder.mat", new Color(0.30f, 0.36f, 0.45f));

            // 3. 生成三个 3D 场景
            BuildLobbyScene(matLobbyGround, matPedestal, matPlayer, matAlly);
            BuildGameScene(matGameGround, matPedestal, matPlayer, matAlly);
            BuildFightScene(matFightGround, matBorder);

            // 4. 注册 Build Settings（Lobby -> Game -> Fight）
            EditorBuildSettings.scenes = new[]
            {
                new EditorBuildSettingsScene($"{SceneDir}/Lobby.unity", true),
                new EditorBuildSettingsScene($"{SceneDir}/Game.unity", true),
                new EditorBuildSettingsScene($"{SceneDir}/Fight.unity", true),
            };

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            // 4.5 生成 F8 AssetBundleMap / ResourceMap 索引，确保 AssetManager.OnInit 正常读取
            F8Framework.Core.Editor.ABBuildTool.GenerateAssetNames(true);
            F8Framework.Core.Editor.ABBuildTool.GenerateResourceNames(true);
            AssetDatabase.Refresh();

            // 5. 默认打开 Lobby 场景
            EditorSceneManager.OpenScene($"{SceneDir}/Lobby.unity", OpenSceneMode.Single);
            Debug.Log("[DemoSceneSetupBuilder] Lobby / Game / Fight 场景与 4 个 UI 预制体已全部生成并写入 Build Settings！");
        }

        private static void BuildUILobbyPrefab(Font font)
        {
            GameObject root = CreateFullScreenRoot("UILobby");
            var view = root.AddComponent<UILobbyView>();

            GameObject card = CreatePanel(root.transform, "CenterCard", new Vector2(760, 420), new Color(0.08f, 0.11f, 0.17f, 0.88f));
            SetAnchorCenter(card.GetComponent<RectTransform>(), new Vector2(0f, 30f));

            GameObject accent = CreatePanel(card.transform, "TopAccent", new Vector2(760, 6), new Color(0.22f, 0.74f, 0.97f, 1f));
            SetAnchorTopStretch(accent.GetComponent<RectTransform>(), 0f, 6f);

            Text title = CreateText(card.transform, "TxtTitle", "FRIFLO ECS 3D DEMO", font, 44, FontStyle.Bold, Color.white, TextAnchor.MiddleCenter);
            SetRect(title.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -68f), new Vector2(680f, 64f));

            Text subtitle = CreateText(card.transform, "TxtSubtitle", "1 主控 + 4 小弟 vs 千级敌军", font, 22, FontStyle.Normal, new Color(0.80f, 0.85f, 0.92f), TextAnchor.MiddleCenter);
            SetRect(subtitle.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -132f), new Vector2(680f, 44f));

            Button btnEnterGame = CreateButton(card.transform, "BtnEnterGame", "进入游戏主界面 (Game)", font, 24, new Vector2(360, 68), new Color(0.15f, 0.39f, 0.92f, 1f));
            SetAnchorCenter(btnEnterGame.GetComponent<RectTransform>(), new Vector2(0f, -26f));

            Button btnQuickFight = CreateButton(card.transform, "BtnQuickFight", "快速进入战斗 (Fight)", font, 24, new Vector2(360, 68), new Color(0.02f, 0.59f, 0.41f, 1f));
            SetAnchorCenter(btnQuickFight.GetComponent<RectTransform>(), new Vector2(0f, -118f));

            var so = new SerializedObject(view);
            so.FindProperty("btnEnterGame").objectReferenceValue = btnEnterGame;
            so.FindProperty("btnQuickFight").objectReferenceValue = btnQuickFight;
            so.FindProperty("txtSubtitle").objectReferenceValue = subtitle;
            so.ApplyModifiedPropertiesWithoutUndo();

            SaveAndDestroyPrefab(root, $"{UiPrefabDir}/UILobby.prefab");
        }

        private static void BuildUIGamePrefab(Font font)
        {
            GameObject root = CreateFullScreenRoot("UIGame");
            var view = root.AddComponent<UIGameView>();

            // 顶部栏
            GameObject topBar = CreatePanel(root.transform, "TopBar", Vector2.zero, new Color(0.06f, 0.09f, 0.16f, 0.90f));
            SetAnchorTopStretch(topBar.GetComponent<RectTransform>(), 0f, 84f);

            Text title = CreateText(topBar.transform, "TxtHeader", "GAME · 教学优化对比关卡选择", font, 28, FontStyle.Bold, Color.white, TextAnchor.MiddleLeft);
            SetRect(title.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(280f, 0f), new Vector2(520f, 56f));

            Button btnOpenMenu = CreateButton(topBar.transform, "BtnOpenMenu", "系统菜单", font, 21, new Vector2(150, 50), new Color(0.20f, 0.25f, 0.33f, 1f));
            SetRect(btnOpenMenu.GetComponent<RectTransform>(), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-265f, 0f), new Vector2(150f, 50f));

            Button btnBackLobby = CreateButton(topBar.transform, "BtnBackLobby", "返回大厅", font, 21, new Vector2(150, 50), new Color(0.28f, 0.33f, 0.41f, 1f));
            SetRect(btnBackLobby.GetComponent<RectTransform>(), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-95f, 0f), new Vector2(150f, 50f));

            // 左侧：6 个教学预设关卡按钮列表
            GameObject stagePanel = CreatePanel(root.transform, "StageSelectPanel", new Vector2(460, 520), new Color(0.08f, 0.11f, 0.17f, 0.88f));
            SetRect(stagePanel.GetComponent<RectTransform>(), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(265f, 10f), new Vector2(460f, 520f));

            Text stageHeader = CreateText(stagePanel.transform, "TxtStageHeader", "选择教学对比关卡（预设方案）", font, 22, FontStyle.Bold, new Color(0.22f, 0.74f, 0.97f), TextAnchor.MiddleLeft);
            SetRect(stageHeader.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -34f), new Vector2(416f, 38f));

            var presets = HotUpdate.ECS.Config.BenchmarkStageCatalog.Presets;
            Button[] stageBtns = new Button[presets.Length];
            for (int i = 0; i < presets.Length; i++)
            {
                stageBtns[i] = CreateButton(stagePanel.transform, $"BtnStage_{i + 1}", presets[i].StageName, font, 20, new Vector2(416, 62), new Color(0.18f, 0.23f, 0.31f, 0.96f));
                SetRect(stageBtns[i].GetComponent<RectTransform>(), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -94f - i * 74f), new Vector2(416f, 62f));
            }

            // 右侧：当前关卡说明 + 4 个独立优化开关 + 初始敌军数档位
            GameObject infoPanel = CreatePanel(root.transform, "StageInfoPanel", new Vector2(680, 520), new Color(0.08f, 0.11f, 0.17f, 0.88f));
            SetRect(infoPanel.GetComponent<RectTransform>(), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-380f, 10f), new Vector2(680f, 520f));

            Text txtSquadInfo = CreateText(infoPanel.transform, "TxtSquadInfo", "关卡说明", font, 20, FontStyle.Normal, new Color(0.92f, 0.95f, 0.98f), TextAnchor.UpperLeft);
            txtSquadInfo.lineSpacing = 1.35f;
            SetRect(txtSquadInfo.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -72f), new Vector2(624f, 104f));

            Text toggleHeader = CreateText(infoPanel.transform, "TxtToggleHeader", "六大独立优化开关（2×3 矩阵，可自由混搭对比）", font, 19, FontStyle.Bold, new Color(0.99f, 0.88f, 0.28f), TextAnchor.MiddleLeft);
            SetRect(toggleHeader.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -142f), new Vector2(624f, 32f));

            string[] toggleNames = { "GPU Instancing 渲染", "XZ 空间哈希网格 O(N)", "O(K) 分桶脏中心", "DeadTag 无冗余清理", "PBD 敌军硬碰撞+绕行", "3D 视锥体裁切" };
            Button[] toggleBtns = new Button[6];
            for (int i = 0; i < 6; i++)
            {
                int col = i % 2;
                int row = i / 2;
                float x = col == 0 ? -158f : 158f;
                float y = -194f - row * 62f;
                toggleBtns[i] = CreateButton(infoPanel.transform, $"BtnToggle_{i}", toggleNames[i], font, 18, new Vector2(300, 50), new Color(0.06f, 0.62f, 0.43f, 1f));
                SetRect(toggleBtns[i].GetComponent<RectTransform>(), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(x, y), new Vector2(300f, 50f));
            }

            Text countHeader = CreateText(infoPanel.transform, "TxtCountHeader", "初始敌军规模（固定种子，对比公平）", font, 19, FontStyle.Bold, new Color(0.99f, 0.88f, 0.28f), TextAnchor.MiddleLeft);
            SetRect(countHeader.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -384f), new Vector2(624f, 32f));

            string[] countLabels = { "300 敌军", "1000 敌军", "2000 敌军" };
            Button[] countBtns = new Button[3];
            for (int i = 0; i < 3; i++)
            {
                float x = -212f + i * 212f;
                countBtns[i] = CreateButton(infoPanel.transform, $"BtnCount_{i}", countLabels[i], font, 19, new Vector2(194, 52), new Color(0.18f, 0.23f, 0.31f, 0.96f));
                SetRect(countBtns[i].GetComponent<RectTransform>(), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(x, -440f), new Vector2(194f, 52f));
            }

            // 底部开始战斗栏
            GameObject bottomBar = CreateEmptyRect(root.transform, "BottomBar");
            SetAnchorBottomStretch(bottomBar.GetComponent<RectTransform>(), 0f, 112f);

            Button btnStartFight = CreateButton(bottomBar.transform, "BtnStartFight", "开始教学压测战斗 (进入 Fight)", font, 25, new Vector2(420, 72), new Color(0.06f, 0.72f, 0.51f, 1f));
            SetAnchorCenter(btnStartFight.GetComponent<RectTransform>(), new Vector2(0f, 10f));

            var so = new SerializedObject(view);
            so.FindProperty("btnStartFight").objectReferenceValue = btnStartFight;
            so.FindProperty("btnOpenMenu").objectReferenceValue = btnOpenMenu;
            so.FindProperty("btnBackLobby").objectReferenceValue = btnBackLobby;
            so.FindProperty("txtSquadInfo").objectReferenceValue = txtSquadInfo;

            AssignButtonArray(so, "stageButtons", stageBtns);
            AssignButtonArray(so, "toggleButtons", toggleBtns);
            AssignButtonArray(so, "enemyCountButtons", countBtns);
            so.ApplyModifiedPropertiesWithoutUndo();

            SaveAndDestroyPrefab(root, $"{UiPrefabDir}/UIGame.prefab");
        }

        private static void BuildUIGameMenuPrefab(Font font)
        {
            GameObject root = CreateFullScreenRoot("UIGameMenu");
            Image mask = root.AddComponent<Image>();
            mask.color = new Color(0f, 0f, 0f, 0.62f);

            var view = root.AddComponent<UIGameMenuView>();

            GameObject panel = CreatePanel(root.transform, "Panel", new Vector2(460, 400), new Color(0.12f, 0.16f, 0.23f, 0.96f));
            SetAnchorCenter(panel.GetComponent<RectTransform>(), Vector2.zero);

            Text txtTitle = CreateText(panel.transform, "TxtTitle", "系统菜单", font, 32, FontStyle.Bold, Color.white, TextAnchor.MiddleCenter);
            SetRect(txtTitle.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -54f), new Vector2(400f, 52f));

            Button btnResume = CreateButton(panel.transform, "BtnResume", "继续", font, 24, new Vector2(300, 60), new Color(0.15f, 0.39f, 0.92f, 1f));
            SetAnchorCenter(btnResume.GetComponent<RectTransform>(), new Vector2(0f, 44f));

            Button btnToGame = CreateButton(panel.transform, "BtnToGame", "返回 Game 菜单", font, 24, new Vector2(300, 60), new Color(0.28f, 0.33f, 0.41f, 1f));
            SetAnchorCenter(btnToGame.GetComponent<RectTransform>(), new Vector2(0f, -34f));

            Button btnToLobby = CreateButton(panel.transform, "BtnToLobby", "返回 Lobby 大厅", font, 24, new Vector2(300, 60), new Color(0.86f, 0.15f, 0.15f, 1f));
            SetAnchorCenter(btnToLobby.GetComponent<RectTransform>(), new Vector2(0f, -112f));

            var so = new SerializedObject(view);
            so.FindProperty("btnResume").objectReferenceValue = btnResume;
            so.FindProperty("btnToGame").objectReferenceValue = btnToGame;
            so.FindProperty("btnToLobby").objectReferenceValue = btnToLobby;
            so.FindProperty("txtMenuTitle").objectReferenceValue = txtTitle;
            so.ApplyModifiedPropertiesWithoutUndo();

            SaveAndDestroyPrefab(root, $"{UiPrefabDir}/UIGameMenu.prefab");
        }

        private static void BuildUIFightPrefab(Font font)
        {
            GameObject root = CreateFullScreenRoot("UIFight");
            var view = root.AddComponent<UIFightView>();

            // 左上状态统计面板（含性能、护盾/免死/等级、帧同步 FrameId 与 64 位 StateHash）
            GameObject statsBox = CreatePanel(root.transform, "TopLeftStats", new Vector2(560, 192), new Color(0.06f, 0.09f, 0.16f, 0.85f));
            SetRect(statsBox.GetComponent<RectTransform>(), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(296f, -110f), new Vector2(560f, 192f));

            Text txtStats = CreateText(statsBox.transform, "TxtStats", "FPS: -- | Logic: --ms Render: --ms | GC: --KB\n渲染: 0 / 视锥剔除: 0 | GO: 0 | 硬碰撞: 0对\n主控 HP: 500/500 护盾:0 免死:1 小弟:4/4\n小队 Lv.1 (0/25) 词条:0 暴击:0 灼烧敌:0\n敌军: 0 | 击杀: 0 | 弹道: 0\n锁步帧: #0 | Hash: 0x00000000", font, 16, FontStyle.Normal, new Color(0.89f, 0.91f, 0.94f), TextAnchor.MiddleLeft);
            txtStats.lineSpacing = 1.24f;
            SetRect(txtStats.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(532f, 176f));

            // 左上压测刷怪按钮栏
            GameObject stressBar = CreateEmptyRect(root.transform, "StressBar");
            SetRect(stressBar.GetComponent<RectTransform>(), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(296f, -236f), new Vector2(560f, 44f));

            Button btnSpawn200 = CreateButton(stressBar.transform, "BtnSpawn200", "+200 敌人", font, 17, new Vector2(176, 40), new Color(0.85f, 0.47f, 0.02f, 0.95f));
            SetAnchorCenter(btnSpawn200.GetComponent<RectTransform>(), new Vector2(-190f, 0f));

            Button btnSpawn1000 = CreateButton(stressBar.transform, "BtnSpawn1000", "+1000 敌人", font, 17, new Vector2(180, 40), new Color(0.86f, 0.15f, 0.15f, 0.95f));
            SetAnchorCenter(btnSpawn1000.GetComponent<RectTransform>(), new Vector2(0f, 0f));

            Button btnClearEnemies = CreateButton(stressBar.transform, "BtnClearEnemies", "清空敌人", font, 17, new Vector2(176, 40), new Color(0.28f, 0.33f, 0.41f, 0.95f));
            SetAnchorCenter(btnClearEnemies.GetComponent<RectTransform>(), new Vector2(190f, 0f));

            // 左中：帧同步与肉鸽词条教学控制栏（2×2 按钮）
            GameObject lockstepBar = CreateEmptyRect(root.transform, "LockstepCardBar");
            SetRect(lockstepBar.GetComponent<RectTransform>(), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(296f, -312f), new Vector2(560f, 92f));

            Button btnToggleLockstep = CreateButton(lockstepBar.transform, "BtnToggleLockstep", "20Hz帧同步: ON", font, 16, new Vector2(272, 40), new Color(0.06f, 0.58f, 0.40f, 0.95f));
            SetAnchorCenter(btnToggleLockstep.GetComponent<RectTransform>(), new Vector2(-142f, 23f));

            Button btnToggleInterp = CreateButton(lockstepBar.transform, "BtnToggleInterp", "60fps插值: ON", font, 16, new Vector2(272, 40), new Color(0.06f, 0.58f, 0.40f, 0.95f));
            SetAnchorCenter(btnToggleInterp.GetComponent<RectTransform>(), new Vector2(142f, 23f));

            Button btnFastForward = CreateButton(lockstepBar.transform, "BtnFastForward", "快进10s(200帧)", font, 15, new Vector2(180, 40), new Color(0.15f, 0.39f, 0.92f, 0.96f));
            SetAnchorCenter(btnFastForward.GetComponent<RectTransform>(), new Vector2(-190f, -23f));

            Button btnToggleAutoLevelUp = CreateButton(lockstepBar.transform, "BtnToggleAutoLevelUp", "自动升级: OFF", font, 15, new Vector2(180, 40), new Color(0.58f, 0.16f, 0.20f, 0.95f));
            SetAnchorCenter(btnToggleAutoLevelUp.GetComponent<RectTransform>(), new Vector2(0f, -23f));

            Button btnOpenCards = CreateButton(lockstepBar.transform, "BtnOpenCards", "手动三选一抽卡", font, 15, new Vector2(180, 40), new Color(0.58f, 0.20f, 0.92f, 0.96f));
            SetAnchorCenter(btnOpenCards.GetComponent<RectTransform>(), new Vector2(190f, -23f));

            // 顶部中央波次横幅
            GameObject waveBox = CreatePanel(root.transform, "TopCenterWave", new Vector2(560, 56), new Color(0.06f, 0.09f, 0.16f, 0.80f));
            SetRect(waveBox.GetComponent<RectTransform>(), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -46f), new Vector2(560f, 56f));

            Text txtWave = CreateText(waveBox.transform, "TxtWave", "FIGHT ARENA · 第 1 波", font, 20, FontStyle.Bold, new Color(0.99f, 0.88f, 0.28f), TextAnchor.MiddleCenter);
            SetRect(txtWave.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(540f, 44f));

            // 右上操作按钮
            GameObject actionsBox = CreateEmptyRect(root.transform, "TopRightActions");
            SetRect(actionsBox.GetComponent<RectTransform>(), new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-190f, -46f), new Vector2(340f, 60f));

            Button btnPauseMenu = CreateButton(actionsBox.transform, "BtnPauseMenu", "暂停 / 菜单", font, 20, new Vector2(156, 52), new Color(0.20f, 0.25f, 0.33f, 0.95f));
            SetAnchorCenter(btnPauseMenu.GetComponent<RectTransform>(), new Vector2(-86f, 0f));

            Button btnBackGame = CreateButton(actionsBox.transform, "BtnBackGame", "选关 / 退出", font, 20, new Vector2(156, 52), new Color(0.73f, 0.11f, 0.11f, 0.95f));
            SetAnchorCenter(btnBackGame.GetComponent<RectTransform>(), new Vector2(86f, 0f));

            // 右侧：战斗中实时热切换关卡与六大优化开关面板
            GameObject livePanel = CreatePanel(root.transform, "LiveBenchPanel", new Vector2(260, 420), new Color(0.06f, 0.09f, 0.16f, 0.82f));
            SetRect(livePanel.GetComponent<RectTransform>(), new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-150f, -310f), new Vector2(260f, 420f));

            Text liveHeader = CreateText(livePanel.transform, "TxtLiveHeader", "教学实时热切换", font, 18, FontStyle.Bold, new Color(0.22f, 0.74f, 0.97f), TextAnchor.MiddleCenter);
            SetRect(liveHeader.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -24f), new Vector2(240f, 30f));

            Button btnPrevStage = CreateButton(livePanel.transform, "BtnPrevStage", "◀ 上一关", font, 17, new Vector2(114, 40), new Color(0.18f, 0.25f, 0.36f, 0.96f));
            SetRect(btnPrevStage.GetComponent<RectTransform>(), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(-61f, -66f), new Vector2(114f, 40f));

            Button btnNextStage = CreateButton(livePanel.transform, "BtnNextStage", "下一关 ▶", font, 17, new Vector2(114, 40), new Color(0.15f, 0.39f, 0.92f, 0.96f));
            SetRect(btnNextStage.GetComponent<RectTransform>(), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(61f, -66f), new Vector2(114f, 40f));

            string[] liveNames = { "Instancing: ON", "空间网格: ON", "O(K)脏桶: ON", "清理优化: ON", "PBD硬碰撞: ON", "视锥裁剪: ON" };
            Button[] liveToggleBtns = new Button[6];
            for (int i = 0; i < 6; i++)
            {
                liveToggleBtns[i] = CreateButton(livePanel.transform, $"BtnLiveToggle_{i}", liveNames[i], font, 17, new Vector2(234, 42), new Color(0.06f, 0.58f, 0.40f, 0.95f));
                SetRect(liveToggleBtns[i].GetComponent<RectTransform>(), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -122f - i * 50f), new Vector2(234f, 42f));
            }

            // 底部自动技能栏
            GameObject skillBar = CreatePanel(root.transform, "BottomSkillBar", new Vector2(540, 92), new Color(0.06f, 0.09f, 0.16f, 0.80f));
            SetRect(skillBar.GetComponent<RectTransform>(), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 64f), new Vector2(540f, 92f));

            string[] slotLabels = { "自动普攻\n[就绪]", "扇形弹幕\n[就绪]", "脉冲冲击\n[就绪]", "小队治疗\n[就绪]" };
            Text[] labelRefs = new Text[slotLabels.Length];
            for (int i = 0; i < slotLabels.Length; i++)
            {
                GameObject slot = CreatePanel(skillBar.transform, $"SkillSlot_{i + 1}", new Vector2(118, 72), new Color(0.15f, 0.20f, 0.29f, 0.95f));
                float x = -195f + i * 130f;
                SetAnchorCenter(slot.GetComponent<RectTransform>(), new Vector2(x, 0f));
                Text label = CreateText(slot.transform, "Label", slotLabels[i], font, 16, FontStyle.Normal, new Color(0.80f, 0.86f, 0.94f), TextAnchor.MiddleCenter);
                SetRect(label.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(110f, 64f));
                labelRefs[i] = label;
            }

            var so = new SerializedObject(view);
            so.FindProperty("txtStats").objectReferenceValue = txtStats;
            so.FindProperty("txtWave").objectReferenceValue = txtWave;
            so.FindProperty("btnPauseMenu").objectReferenceValue = btnPauseMenu;
            so.FindProperty("btnBackGame").objectReferenceValue = btnBackGame;
            so.FindProperty("btnSpawn200").objectReferenceValue = btnSpawn200;
            so.FindProperty("btnSpawn1000").objectReferenceValue = btnSpawn1000;
            so.FindProperty("btnClearEnemies").objectReferenceValue = btnClearEnemies;
            so.FindProperty("btnToggleLockstep").objectReferenceValue = btnToggleLockstep;
            so.FindProperty("btnToggleInterp").objectReferenceValue = btnToggleInterp;
            so.FindProperty("btnFastForward").objectReferenceValue = btnFastForward;
            so.FindProperty("btnToggleAutoLevelUp").objectReferenceValue = btnToggleAutoLevelUp;
            so.FindProperty("btnOpenCards").objectReferenceValue = btnOpenCards;
            so.FindProperty("btnPrevStage").objectReferenceValue = btnPrevStage;
            so.FindProperty("btnNextStage").objectReferenceValue = btnNextStage;
            AssignButtonArray(so, "liveToggleButtons", liveToggleBtns);
            var slotsProp = so.FindProperty("skillSlotLabels");
            slotsProp.arraySize = labelRefs.Length;
            for (int i = 0; i < labelRefs.Length; i++)
            {
                slotsProp.GetArrayElementAtIndex(i).objectReferenceValue = labelRefs[i];
            }
            so.ApplyModifiedPropertiesWithoutUndo();

            SaveAndDestroyPrefab(root, $"{UiPrefabDir}/UIFight.prefab");
        }

        private static void BuildUIRoguelikeCardPrefab(Font font)
        {
            GameObject root = CreateFullScreenRoot("UIRoguelikeCard");
            Image mask = root.AddComponent<Image>();
            mask.color = new Color(0f, 0f, 0f, 0.72f);

            var view = root.AddComponent<UIRoguelikeCardView>();

            GameObject modal = CreatePanel(root.transform, "CardModal", new Vector2(1000, 520), new Color(0.08f, 0.11f, 0.18f, 0.96f));
            SetAnchorCenter(modal.GetComponent<RectTransform>(), Vector2.zero);

            Text txtHeader = CreateText(modal.transform, "TxtModalTitle", "小队升级 · 三选一肉鸽词条", font, 28, FontStyle.Bold, new Color(0.99f, 0.88f, 0.28f), TextAnchor.MiddleCenter);
            SetRect(txtHeader.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -40f), new Vector2(920f, 44f));

            Text txtSub = CreateText(modal.transform, "TxtModalHint", "选择词条立即热注入 UnitBuffs / SquadSkillModifiers，实时改变 8 步伤害管线与多形态弹道", font, 16, FontStyle.Normal, new Color(0.75f, 0.82f, 0.92f), TextAnchor.MiddleCenter);
            SetRect(txtSub.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -76f), new Vector2(920f, 28f));

            Color[] stripeColors =
            {
                new Color(0.22f, 0.74f, 0.97f, 1f),
                new Color(0.96f, 0.62f, 0.04f, 1f),
                new Color(0.66f, 0.33f, 0.97f, 1f),
            };

            Button[] cardButtons = new Button[3];
            Text[] tagTexts = new Text[3];
            Text[] titleTexts = new Text[3];
            Text[] descTexts = new Text[3];

            for (int i = 0; i < 3; i++)
            {
                float x = -316f + i * 316f;
                GameObject cardGo = CreatePanel(modal.transform, $"Card_{i}", new Vector2(292, 316), new Color(0.13f, 0.18f, 0.28f, 0.98f));
                SetAnchorCenter(cardGo.GetComponent<RectTransform>(), new Vector2(x, -12f));

                var btn = cardGo.AddComponent<Button>();
                var colors = btn.colors;
                colors.normalColor = Color.white;
                colors.highlightedColor = new Color(1.14f, 1.14f, 1.14f, 1f);
                colors.pressedColor = new Color(0.86f, 0.86f, 0.86f, 1f);
                btn.colors = colors;
                cardButtons[i] = btn;

                GameObject stripe = CreatePanel(cardGo.transform, "TopStripe", new Vector2(292, 8), stripeColors[i]);
                SetAnchorTopStretch(stripe.GetComponent<RectTransform>(), 0f, 8f);

                tagTexts[i] = CreateText(cardGo.transform, "TxtTag", "[词条]", font, 18, FontStyle.Bold, stripeColors[i], TextAnchor.MiddleCenter);
                SetRect(tagTexts[i].rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -38f), new Vector2(256f, 30f));

                titleTexts[i] = CreateText(cardGo.transform, "TxtTitle", "词条名称", font, 22, FontStyle.Bold, Color.white, TextAnchor.MiddleCenter);
                SetRect(titleTexts[i].rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -86f), new Vector2(264f, 44f));

                descTexts[i] = CreateText(cardGo.transform, "TxtDesc", "词条效果描述", font, 17, FontStyle.Normal, new Color(0.85f, 0.90f, 0.96f), TextAnchor.UpperCenter);
                descTexts[i].lineSpacing = 1.35f;
                SetRect(descTexts[i].rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, -18f), new Vector2(254f, 132f));

                Text pickHint = CreateText(cardGo.transform, "TxtPickHint", "点击选择此词条", font, 16, FontStyle.Bold, new Color(0.20f, 0.83f, 0.60f), TextAnchor.MiddleCenter);
                SetRect(pickHint.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 28f), new Vector2(254f, 30f));
            }

            Button btnSkip = CreateButton(modal.transform, "BtnSkip", "暂不选择 / 关闭", font, 18, new Vector2(220, 44), new Color(0.28f, 0.33f, 0.41f, 0.95f));
            SetRect(btnSkip.GetComponent<RectTransform>(), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 34f), new Vector2(220f, 44f));

            var so = new SerializedObject(view);
            so.FindProperty("txtHeader").objectReferenceValue = txtHeader;
            so.FindProperty("btnSkip").objectReferenceValue = btnSkip;
            AssignButtonArray(so, "cardButtons", cardButtons);
            AssignTextArray(so, "cardTagTexts", tagTexts);
            AssignTextArray(so, "cardTitleTexts", titleTexts);
            AssignTextArray(so, "cardDescTexts", descTexts);
            so.ApplyModifiedPropertiesWithoutUndo();

            SaveAndDestroyPrefab(root, $"{UiPrefabDir}/UIRoguelikeCard.prefab");
        }

        private static void BuildLobbyScene(Material matGround, Material matPedestal, Material matPlayer, Material matAlly)
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            CreateMainCamera(new Vector3(0f, 5.5f, -10.5f), Quaternion.Euler(20f, 0f, 0f), new Color(0.04f, 0.07f, 0.13f));
            CreateDirectionalLight(Quaternion.Euler(48f, -30f, 0f), 1.1f);
            CreateLauncherObject();

            var stage = new GameObject("LobbyStage");
            CreatePrimitive("Ground", PrimitiveType.Plane, stage.transform, Vector3.zero, new Vector3(4f, 1f, 4f), matGround);
            CreatePrimitive("Pedestal", PrimitiveType.Cylinder, stage.transform, new Vector3(0f, 0.15f, 1.5f), new Vector3(6f, 0.15f, 6f), matPedestal);

            // 1 主控 + 4 小弟 展示阵型
            CreatePrimitive("PreviewPlayer", PrimitiveType.Cube, stage.transform, new Vector3(0f, 0.9f, 1.5f), new Vector3(1.2f, 1.2f, 1.2f), matPlayer);
            Vector3[] allyOffsets =
            {
                new Vector3(-1.8f, 0.7f, 0.2f),
                new Vector3(1.8f, 0.7f, 0.2f),
                new Vector3(-2.6f, 0.7f, -1.0f),
                new Vector3(2.6f, 0.7f, -1.0f),
            };
            for (int i = 0; i < allyOffsets.Length; i++)
            {
                CreatePrimitive($"PreviewAlly_{i + 1}", PrimitiveType.Cube, stage.transform, allyOffsets[i], new Vector3(0.85f, 0.85f, 0.85f), matAlly);
            }

            EditorSceneManager.SaveScene(scene, $"{SceneDir}/Lobby.unity");
        }

        private static void BuildGameScene(Material matGround, Material matPedestal, Material matPlayer, Material matAlly)
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            CreateMainCamera(new Vector3(3.2f, 6.2f, -9.2f), Quaternion.Euler(26f, -16f, 0f), new Color(0.06f, 0.09f, 0.17f));
            CreateDirectionalLight(Quaternion.Euler(50f, -40f, 0f), 1.1f);
            CreateLauncherObject();

            var stage = new GameObject("GameStage");
            CreatePrimitive("Ground", PrimitiveType.Plane, stage.transform, Vector3.zero, new Vector3(4f, 1f, 4f), matGround);
            CreatePrimitive("SquadBase", PrimitiveType.Cube, stage.transform, new Vector3(2f, 0.1f, 0.5f), new Vector3(7f, 0.2f, 5f), matPedestal);

            // 偏右展示 1 主控 + 4 小弟，与左侧 UI 面板错开
            CreatePrimitive("SquadPlayer", PrimitiveType.Cube, stage.transform, new Vector3(2f, 0.85f, 1.2f), new Vector3(1.2f, 1.2f, 1.2f), matPlayer);
            Vector3[] allyPositions =
            {
                new Vector3(0.4f, 0.65f, 0.0f),
                new Vector3(3.6f, 0.65f, 0.0f),
                new Vector3(-0.3f, 0.65f, -1.0f),
                new Vector3(4.3f, 0.65f, -1.0f),
            };
            for (int i = 0; i < allyPositions.Length; i++)
            {
                CreatePrimitive($"SquadAlly_{i + 1}", PrimitiveType.Cube, stage.transform, allyPositions[i], new Vector3(0.85f, 0.85f, 0.85f), matAlly);
            }

            EditorSceneManager.SaveScene(scene, $"{SceneDir}/Game.unity");
        }

        private static void BuildFightScene(Material matGround, Material matBorder)
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // 俯视角战斗相机（适合 3D 割草/小队战斗）
            CreateMainCamera(new Vector3(0f, 22f, -18f), Quaternion.Euler(50f, 0f, 0f), new Color(0.07f, 0.09f, 0.14f));
            CreateDirectionalLight(Quaternion.Euler(55f, -35f, 0f), 1.15f);
            CreateLauncherObject();

            var arena = new GameObject("FightArena");
            // 100m x 100m 战斗场地
            CreatePrimitive("ArenaFloor", PrimitiveType.Plane, arena.transform, Vector3.zero, new Vector3(10f, 1f, 10f), matGround);

            // 场地边界围栏
            CreatePrimitive("BorderNorth", PrimitiveType.Cube, arena.transform, new Vector3(0f, 0.5f, 50f), new Vector3(101f, 1f, 1f), matBorder);
            CreatePrimitive("BorderSouth", PrimitiveType.Cube, arena.transform, new Vector3(0f, 0.5f, -50f), new Vector3(101f, 1f, 1f), matBorder);
            CreatePrimitive("BorderEast", PrimitiveType.Cube, arena.transform, new Vector3(50f, 0.5f, 0f), new Vector3(1f, 1f, 101f), matBorder);
            CreatePrimitive("BorderWest", PrimitiveType.Cube, arena.transform, new Vector3(-50f, 0.5f, 0f), new Vector3(1f, 1f, 101f), matBorder);

            // 预留 ECS BattleWorld 宿主节点
            new GameObject("BattleRoot");

            EditorSceneManager.SaveScene(scene, $"{SceneDir}/Fight.unity");
        }

        private static void CreateLauncherObject()
        {
            var go = new GameObject("DemoGameLauncher");
            go.AddComponent<DemoGameLauncher>();
        }

        private static void CreateMainCamera(Vector3 pos, Quaternion rot, Color bgColor)
        {
            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            camGo.transform.SetPositionAndRotation(pos, rot);
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = bgColor;
            cam.fieldOfView = 55f;
            camGo.AddComponent<AudioListener>();
        }

        private static void CreateDirectionalLight(Quaternion rot, float intensity)
        {
            var lightGo = new GameObject("Directional Light");
            lightGo.transform.rotation = rot;
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = intensity;
            light.shadows = LightShadows.Soft;
        }

        private static GameObject CreatePrimitive(string name, PrimitiveType type, Transform parent, Vector3 pos, Vector3 scale, Material mat)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            var renderer = go.GetComponent<Renderer>();
            if (renderer != null && mat != null)
            {
                renderer.sharedMaterial = mat;
            }
            return go;
        }

        private static Material CreateOrUpdateMaterial(string assetPath, Color color)
        {
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Standard"));
                mat.color = color;
                mat.SetFloat("_Glossiness", 0.25f);
                AssetDatabase.CreateAsset(mat, assetPath);
            }
            else
            {
                mat.color = color;
                EditorUtility.SetDirty(mat);
            }
            return mat;
        }

        private static GameObject CreateFullScreenRoot(string name)
        {
            var root = new GameObject(name, typeof(RectTransform));
            root.layer = LayerMask.NameToLayer("UI");
            var rt = root.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            return root;
        }

        private static GameObject CreateEmptyRect(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = LayerMask.NameToLayer("UI");
            go.transform.SetParent(parent, false);
            return go;
        }

        private static GameObject CreatePanel(Transform parent, string name, Vector2 size, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.layer = LayerMask.NameToLayer("UI");
            go.transform.SetParent(parent, false);
            go.GetComponent<RectTransform>().sizeDelta = size;
            go.GetComponent<Image>().color = color;
            return go;
        }

        private static Text CreateText(Transform parent, string name, string content, Font font, int fontSize, FontStyle style, Color color, TextAnchor alignment)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            go.layer = LayerMask.NameToLayer("UI");
            go.transform.SetParent(parent, false);
            var text = go.GetComponent<Text>();
            text.text = content;
            text.font = font;
            text.fontSize = fontSize;
            text.fontStyle = style;
            text.color = color;
            text.alignment = alignment;
            text.raycastTarget = false;
            return text;
        }

        private static Button CreateButton(Transform parent, string name, string label, Font font, int fontSize, Vector2 size, Color bgColor)
        {
            GameObject go = CreatePanel(parent, name, size, bgColor);
            var btn = go.AddComponent<Button>();
            var colors = btn.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.12f, 1.12f, 1.12f, 1f);
            colors.pressedColor = new Color(0.85f, 0.85f, 0.85f, 1f);
            btn.colors = colors;

            Text txt = CreateText(go.transform, "Text", label, font, fontSize, FontStyle.Bold, Color.white, TextAnchor.MiddleCenter);
            var rt = txt.rectTransform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            return btn;
        }

        private static void SetAnchorCenter(RectTransform rt, Vector2 anchoredPos)
        {
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = anchoredPos;
        }

        private static void SetAnchorTopStretch(RectTransform rt, float topOffset, float height)
        {
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.offsetMin = new Vector2(0f, -(topOffset + height));
            rt.offsetMax = new Vector2(0f, -topOffset);
        }

        private static void SetAnchorBottomStretch(RectTransform rt, float bottomOffset, float height)
        {
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(1f, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.offsetMin = new Vector2(0f, bottomOffset);
            rt.offsetMax = new Vector2(0f, bottomOffset + height);
        }

        private static void SetRect(RectTransform rt, Vector2 anchorMin, Vector2 anchorMax, Vector2 anchoredPos, Vector2 size)
        {
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = anchoredPos;
            rt.sizeDelta = size;
        }

        private static void AssignButtonArray(SerializedObject so, string propName, Button[] buttons)
        {
            var prop = so.FindProperty(propName);
            prop.arraySize = buttons.Length;
            for (int i = 0; i < buttons.Length; i++)
            {
                prop.GetArrayElementAtIndex(i).objectReferenceValue = buttons[i];
            }
        }

        private static void AssignTextArray(SerializedObject so, string propName, Text[] texts)
        {
            var prop = so.FindProperty(propName);
            prop.arraySize = texts.Length;
            for (int i = 0; i < texts.Length; i++)
            {
                prop.GetArrayElementAtIndex(i).objectReferenceValue = texts[i];
            }
        }

        private static void SaveAndDestroyPrefab(GameObject root, string prefabPath)
        {
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            Object.DestroyImmediate(root);
        }

        private static Font GetDefaultFont()
        {
            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font == null)
            {
                font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            }
            return font;
        }

        private static void EnsureDir(string assetDir)
        {
            string fullPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", assetDir));
            if (!Directory.Exists(fullPath))
            {
                Directory.CreateDirectory(fullPath);
            }
        }
    }
}

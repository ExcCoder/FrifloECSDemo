using System.Collections;
using F8Framework.Core;
using F8Framework.Launcher;
using HotUpdate.Procedure;
using HotUpdate.UI;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace HotUpdate.Core
{
    /// <summary>
    /// Demo 精简持久化入口（基于 F8Framework ModuleCenter）。
    /// 挂在 Lobby 场景即可；也支持从任意场景直接运行 Play，自动初始化框架并进入对应 Procedure。
    /// </summary>
    public class DemoGameLauncher : MonoBehaviour
    {
        public static DemoGameLauncher Instance { get; private set; }

        private bool _initialized;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);
            EnsurePersistentEventSystem();
        }

        private IEnumerator Start()
        {
            if (_initialized)
            {
                yield break;
            }

            InitializeF8Modules();
            InitializeUI();
            RegisterProcedures();

            _initialized = true;
            yield return null;

            EnterInitialProcedureByActiveScene();
        }

        private void InitializeF8Modules()
        {
            ModuleCenter.Initialize(this);

            FF8.Message = ModuleCenter.CreateModule<MessageManager>();
            FF8.Input = ModuleCenter.CreateModule<InputManager>(new DefaultInputHelper());
            FF8.Storage = ModuleCenter.CreateModule<StorageManager>();
            FF8.Timer = ModuleCenter.CreateModule<TimerManager>();
            FF8.Procedure = ModuleCenter.CreateModule<ProcedureManager>();
            FF8.GameObjectPool = ModuleCenter.CreateModule<GameObjectPool>();
            FF8.Asset = ModuleCenter.CreateModule<AssetManager>();
#if UNITY_EDITOR
            // Editor 下直接从 Resources / AssetBundles 目录加载，无需先打 AB 包
            FF8.Asset.IsEditorMode = true;
#endif
            FF8.Audio = ModuleCenter.CreateModule<AudioManager>();
            FF8.Tween = ModuleCenter.CreateModule<Tween>();
            FF8.UI = ModuleCenter.CreateModule<UIManager>();
        }

        private void InitializeUI()
        {
            FF8.UI.Initialize(UIRegistry.CreateConfigs());
            FF8.UI.SetCanvasScaler(
                null,
                CanvasScaler.ScaleMode.ScaleWithScreenSize,
                new Vector2(1920f, 1080f),
                CanvasScaler.ScreenMatchMode.MatchWidthOrHeight,
                0.5f,
                100f);
        }

        private void RegisterProcedures()
        {
            EnsureProcedureNode(new LobbyProcedure());
            EnsureProcedureNode(new GameProcedure());
            EnsureProcedureNode(new FightProcedure());
        }

        private static void EnsureProcedureNode<T>(T node) where T : ProcedureNode
        {
            if (!FF8.Procedure.HasProcedureNode<T>())
            {
                FF8.Procedure.AddProcedureNodes(node);
            }
        }

        private void EnterInitialProcedureByActiveScene()
        {
            string activeScene = SceneManager.GetActiveScene().name;
            switch (activeScene)
            {
                case SceneNames.Game:
                    SceneFlowManager.EnterGame();
                    break;
                case SceneNames.Fight:
                    SceneFlowManager.EnterFight();
                    break;
                default:
                    SceneFlowManager.EnterLobby();
                    break;
            }
        }

        private void EnsurePersistentEventSystem()
        {
            if (FindObjectOfType<EventSystem>() != null)
            {
                return;
            }

            var esGo = new GameObject("EventSystem");
            esGo.transform.SetParent(transform, false);
            esGo.AddComponent<EventSystem>();
            esGo.AddComponent<StandaloneInputModule>();
        }

        private void Update()
        {
            if (_initialized)
            {
                ModuleCenter.Update();
            }
        }

        private void LateUpdate()
        {
            if (_initialized)
            {
                ModuleCenter.LateUpdate();
            }
        }

        private void FixedUpdate()
        {
            if (_initialized)
            {
                ModuleCenter.FixedUpdate();
            }
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }
    }
}

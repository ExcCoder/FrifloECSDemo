using System;
using System.Collections;
using F8Framework.Launcher;
using HotUpdate.Procedure;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HotUpdate.Core
{
    /// <summary>
    /// 统一管理场景异步切换与流程跳转，防止重复触发切场景。
    /// </summary>
    public static class SceneFlowManager
    {
        private static bool _isLoadingScene;

        public static bool IsLoadingScene => _isLoadingScene;
        public static string CurrentSceneName => SceneManager.GetActiveScene().name;

        public static void EnterLobby()
        {
            if (_isLoadingScene) return;
            FF8.Procedure.RunProcedureNode<LobbyProcedure>();
        }

        public static void EnterGame()
        {
            if (_isLoadingScene) return;
            FF8.Procedure.RunProcedureNode<GameProcedure>();
        }

        public static void EnterFight()
        {
            if (_isLoadingScene) return;
            FF8.Procedure.RunProcedureNode<FightProcedure>();
        }

        /// <summary>
        /// 若当前不在目标场景，则通过协程异步加载目标场景后再回调；已在目标场景则直接回调。
        /// </summary>
        public static void EnsureSceneLoaded(string targetSceneName, Action onLoaded)
        {
            if (string.Equals(CurrentSceneName, targetSceneName, StringComparison.Ordinal))
            {
                onLoaded?.Invoke();
                return;
            }

            if (DemoGameLauncher.Instance == null)
            {
                Debug.LogError($"[SceneFlowManager] DemoGameLauncher 尚未初始化，无法加载场景: {targetSceneName}");
                return;
            }

            DemoGameLauncher.Instance.StartCoroutine(LoadSceneRoutine(targetSceneName, onLoaded));
        }

        private static IEnumerator LoadSceneRoutine(string targetSceneName, Action onLoaded)
        {
            _isLoadingScene = true;
            AsyncOperation op = SceneManager.LoadSceneAsync(targetSceneName, LoadSceneMode.Single);
            if (op == null)
            {
                _isLoadingScene = false;
                Debug.LogError($"[SceneFlowManager] 加载场景失败，请确认场景已加入 Build Settings: {targetSceneName}");
                yield break;
            }

            while (!op.isDone)
            {
                yield return null;
            }

            _isLoadingScene = false;
            onLoaded?.Invoke();
        }
    }
}

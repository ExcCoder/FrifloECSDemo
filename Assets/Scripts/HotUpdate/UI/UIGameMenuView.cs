using F8Framework.Core;
using F8Framework.Launcher;
using HotUpdate.Core;
using HotUpdate.Procedure;
using UnityEngine;
using UnityEngine.UI;

namespace HotUpdate.UI
{
    /// <summary>
    /// 通用系统菜单弹窗（可在 Game 场景或 Fight 战斗场景中弹出）。
    /// </summary>
    public class UIGameMenuView : BaseView
    {
        [SerializeField] private Button btnResume;
        [SerializeField] private Button btnToGame;
        [SerializeField] private Button btnToLobby;
        [SerializeField] private Text txtMenuTitle;

        private bool _openedFromFight;

        protected override void OnAwake()
        {
            if (btnResume == null) btnResume = transform.Find("Panel/BtnResume")?.GetComponent<Button>();
            if (btnToGame == null) btnToGame = transform.Find("Panel/BtnToGame")?.GetComponent<Button>();
            if (btnToLobby == null) btnToLobby = transform.Find("Panel/BtnToLobby")?.GetComponent<Button>();
            if (txtMenuTitle == null) txtMenuTitle = transform.Find("Panel/TxtTitle")?.GetComponent<Text>();
        }

        protected override void OnAdded(int uiId, object[] args = null)
        {
            _openedFromFight = args != null && args.Length > 0 && args[0] is string src && src == "Fight";

            if (txtMenuTitle != null)
            {
                txtMenuTitle.text = _openedFromFight ? "战斗暂停菜单" : "系统菜单";
            }

            if (btnToGame != null)
            {
                btnToGame.gameObject.SetActive(_openedFromFight);
                btnToGame.onClick.RemoveListener(OnClickToGame);
                btnToGame.onClick.AddListener(OnClickToGame);
            }

            if (btnResume != null)
            {
                btnResume.onClick.RemoveListener(OnClickResume);
                btnResume.onClick.AddListener(OnClickResume);
            }

            if (btnToLobby != null)
            {
                btnToLobby.onClick.RemoveListener(OnClickToLobby);
                btnToLobby.onClick.AddListener(OnClickToLobby);
            }
        }

        protected override void OnBeforeRemove()
        {
            if (btnResume != null) btnResume.onClick.RemoveListener(OnClickResume);
            if (btnToGame != null) btnToGame.onClick.RemoveListener(OnClickToGame);
            if (btnToLobby != null) btnToLobby.onClick.RemoveListener(OnClickToLobby);
        }

        private void OnClickResume()
        {
            if (_openedFromFight && FF8.Procedure.PeekProcedureNode(out FightProcedure fightProc))
            {
                fightProc.SetPaused(false);
            }
            Close(false);
        }

        private void OnClickToGame()
        {
            Close(false);
            SceneFlowManager.EnterGame();
        }

        private void OnClickToLobby()
        {
            Close(false);
            SceneFlowManager.EnterLobby();
        }
    }
}

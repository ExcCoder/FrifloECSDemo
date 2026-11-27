using F8Framework.Core;
using HotUpdate.Core;
using UnityEngine;
using UnityEngine.UI;

namespace HotUpdate.UI
{
    /// <summary>
    /// 大厅主界面（Lobby 场景）：提供进入 Game 主菜单、直接进入 Fight 战斗、退出等入口。
    /// </summary>
    public class UILobbyView : BaseView
    {
        [SerializeField] private Button btnEnterGame;
        [SerializeField] private Button btnQuickFight;
        [SerializeField] private Text txtSubtitle;

        protected override void OnAwake()
        {
            if (btnEnterGame == null) btnEnterGame = transform.Find("CenterCard/BtnEnterGame")?.GetComponent<Button>();
            if (btnQuickFight == null) btnQuickFight = transform.Find("CenterCard/BtnQuickFight")?.GetComponent<Button>();
            if (txtSubtitle == null) txtSubtitle = transform.Find("CenterCard/TxtSubtitle")?.GetComponent<Text>();
        }

        protected override void OnAdded(int uiId, object[] args = null)
        {
            if (txtSubtitle != null)
            {
                txtSubtitle.text = "Friflo ECS 3D 割草小队 Demo · 1 主控 + 4 小弟 vs 千级敌军";
            }

            if (btnEnterGame != null)
            {
                btnEnterGame.onClick.RemoveListener(OnClickEnterGame);
                btnEnterGame.onClick.AddListener(OnClickEnterGame);
            }

            if (btnQuickFight != null)
            {
                btnQuickFight.onClick.RemoveListener(OnClickQuickFight);
                btnQuickFight.onClick.AddListener(OnClickQuickFight);
            }
        }

        protected override void OnBeforeRemove()
        {
            if (btnEnterGame != null) btnEnterGame.onClick.RemoveListener(OnClickEnterGame);
            if (btnQuickFight != null) btnQuickFight.onClick.RemoveListener(OnClickQuickFight);
        }

        private void OnClickEnterGame()
        {
            SceneFlowManager.EnterGame();
        }

        private void OnClickQuickFight()
        {
            SceneFlowManager.EnterFight();
        }
    }
}

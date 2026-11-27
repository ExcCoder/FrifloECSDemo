using F8Framework.Core;
using F8Framework.Launcher;
using HotUpdate.Core;
using HotUpdate.UI;

namespace HotUpdate.Procedure
{
    /// <summary>
    /// 大厅流程：加载 Lobby 场景并打开 UILobby。
    /// </summary>
    public class LobbyProcedure : ProcedureNode
    {
        public override void OnInit(ProcedureProcessor processor) { }

        public override void OnEnter(ProcedureProcessor processor)
        {
            SceneFlowManager.EnsureSceneLoaded(SceneNames.Lobby, () =>
            {
                FF8.UI.Open(UIID.UILobby);
            });
        }

        public override void OnUpdate(ProcedureProcessor processor) { }

        public override void OnExit(ProcedureProcessor processor)
        {
            FF8.UI.Close(UIID.UILobby, false);
        }

        public override void OnDestroy(ProcedureProcessor processor) { }
    }
}

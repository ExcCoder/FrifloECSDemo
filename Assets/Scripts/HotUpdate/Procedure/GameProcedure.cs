using F8Framework.Core;
using F8Framework.Launcher;
using HotUpdate.Core;
using HotUpdate.UI;

namespace HotUpdate.Procedure
{
    /// <summary>
    /// 游戏主界面/整备菜单流程：加载 Game 场景并打开 UIGame。
    /// </summary>
    public class GameProcedure : ProcedureNode
    {
        public override void OnInit(ProcedureProcessor processor) { }

        public override void OnEnter(ProcedureProcessor processor)
        {
            SceneFlowManager.EnsureSceneLoaded(SceneNames.Game, () =>
            {
                FF8.UI.Open(UIID.UIGame);
            });
        }

        public override void OnUpdate(ProcedureProcessor processor) { }

        public override void OnExit(ProcedureProcessor processor)
        {
            if (FF8.UI.Has(UIID.UIGameMenu))
            {
                FF8.UI.Close(UIID.UIGameMenu, false);
            }
            FF8.UI.Close(UIID.UIGame, false);
        }

        public override void OnDestroy(ProcedureProcessor processor) { }
    }
}

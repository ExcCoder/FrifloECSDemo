using System.Collections.Generic;
using F8Framework.Core;

namespace HotUpdate.UI
{
    /// <summary>
    /// Demo 全局 UI 标识。
    /// </summary>
    public enum UIID
    {
        UILobby = 1,
        UIGame = 2,
        UIGameMenu = 3,
        UIFight = 4,
    }

    /// <summary>
    /// 集中维护 UI 配置映射表（路径对应 Assets/Resources/UI 下的预制体名）。
    /// </summary>
    public static class UIRegistry
    {
        public static Dictionary<UIID, UIConfig> CreateConfigs()
        {
            return new Dictionary<UIID, UIConfig>
            {
                { UIID.UILobby, new UIConfig(LayerType.UI, "UILobby") },
                { UIID.UIGame, new UIConfig(LayerType.UI, "UIGame") },
                { UIID.UIGameMenu, new UIConfig(LayerType.PopUp, "UIGameMenu") },
                { UIID.UIFight, new UIConfig(LayerType.UI, "UIFight") },
            };
        }
    }
}

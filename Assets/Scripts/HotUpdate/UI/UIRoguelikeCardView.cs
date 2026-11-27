using F8Framework.Core;
using F8Framework.Launcher;
using HotUpdate.ECS.Buff;
using HotUpdate.Procedure;
using UnityEngine;
using UnityEngine.UI;

namespace HotUpdate.UI
{
    /// <summary>
    /// 肉鸽升级“三选一”词条弹窗（LayerType.PopUp）：
    /// 对应笔记第 4 & 5 篇的 Buff / SkillModifier 实时热注入教学演示。
    /// </summary>
    public class UIRoguelikeCardView : BaseView
    {
        [SerializeField] private Text txtHeader;
        [SerializeField] private Button[] cardButtons;
        [SerializeField] private Text[] cardTagTexts;
        [SerializeField] private Text[] cardTitleTexts;
        [SerializeField] private Text[] cardDescTexts;
        [SerializeField] private Button btnSkip;

        private readonly RoguelikeCardDef[] _drawnCards = new RoguelikeCardDef[3];

        protected override void OnAwake()
        {
            if (txtHeader == null) txtHeader = transform.Find("CardModal/TxtModalTitle")?.GetComponent<Text>();
            if (btnSkip == null) btnSkip = transform.Find("CardModal/BtnSkip")?.GetComponent<Button>();

            if (cardButtons == null || cardButtons.Length < 3)
            {
                cardButtons = new Button[3];
                cardTagTexts = new Text[3];
                cardTitleTexts = new Text[3];
                cardDescTexts = new Text[3];
                for (int i = 0; i < 3; i++)
                {
                    Transform card = transform.Find($"CardModal/Card_{i}");
                    if (card == null) continue;
                    cardButtons[i] = card.GetComponent<Button>();
                    cardTagTexts[i] = card.Find("TxtTag")?.GetComponent<Text>();
                    cardTitleTexts[i] = card.Find("TxtTitle")?.GetComponent<Text>();
                    cardDescTexts[i] = card.Find("TxtDesc")?.GetComponent<Text>();
                }
            }
        }

        protected override void OnAdded(int uiId, object[] args = null)
        {
            if (FF8.Procedure.PeekProcedureNode(out FightProcedure fightProc) && fightProc.World != null)
            {
                fightProc.World.DrawThreeRoguelikeCards(_drawnCards);
                if (txtHeader != null)
                {
                    txtHeader.text = $"小队升级 Lv.{fightProc.World.SquadLevel} · 三选一肉鸽词条 (已选 {fightProc.World.AppliedCardCount} 张)";
                }
            }

            for (int i = 0; i < 3; i++)
            {
                int idx = i;
                RoguelikeCardDef card = _drawnCards[i];
                if (cardTagTexts != null && i < cardTagTexts.Length && cardTagTexts[i] != null)
                {
                    cardTagTexts[i].text = $"[{card.Tag}]";
                }
                if (cardTitleTexts != null && i < cardTitleTexts.Length && cardTitleTexts[i] != null)
                {
                    cardTitleTexts[i].text = card.Title;
                }
                if (cardDescTexts != null && i < cardDescTexts.Length && cardDescTexts[i] != null)
                {
                    cardDescTexts[i].text = card.Description;
                }

                if (cardButtons != null && i < cardButtons.Length && cardButtons[i] != null)
                {
                    cardButtons[i].onClick.RemoveAllListeners();
                    cardButtons[i].onClick.AddListener(() => OnClickCard(idx));
                }
            }

            if (btnSkip != null)
            {
                btnSkip.onClick.RemoveListener(OnClickSkip);
                btnSkip.onClick.AddListener(OnClickSkip);
            }
        }

        protected override void OnBeforeRemove()
        {
            if (btnSkip != null)
            {
                btnSkip.onClick.RemoveListener(OnClickSkip);
            }
            if (cardButtons != null)
            {
                for (int i = 0; i < cardButtons.Length; i++)
                {
                    if (cardButtons[i] != null) cardButtons[i].onClick.RemoveAllListeners();
                }
            }
        }

        private void OnClickCard(int index)
        {
            if (index < 0 || index >= _drawnCards.Length) return;
            RoguelikeCardId selectedId = _drawnCards[index].Id;

            if (FF8.Procedure.PeekProcedureNode(out FightProcedure fightProc))
            {
                fightProc.ApplySelectedCard(selectedId);
            }

            Close(false);
        }

        private void OnClickSkip()
        {
            if (FF8.Procedure.PeekProcedureNode(out FightProcedure fightProc))
            {
                fightProc.DismissCardSelectionPopup();
            }

            Close(false);
        }
    }
}

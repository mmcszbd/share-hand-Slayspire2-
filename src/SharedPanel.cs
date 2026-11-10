using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
namespace SharedHandPrototype;
internal static class SharedPanel
{
    internal static bool Dirty = true;
    private static Label? _status;
    internal static void MarkDirty() => Dirty = true;
    internal static void Hide()
    {
        NativeSharedHand.Clear();
        if (GodotObject.IsInstanceValid(_status)) _status!.QueueFree();
        _status = null;
    }
    internal static void Refresh(CombatState state)
    {
        Dirty = false;
        NativeSharedHand.Refresh(state);
        if (!GodotObject.IsInstanceValid(_status))
        {
            _status = new Label { Name = "SharedHandStatus", Position = new Vector2(440, 82),
                MouseFilter = Control.MouseFilterEnum.Ignore, ZIndex = 0 };
            NCombatRoom.Instance.AddChild(_status);
        }
        _status!.Visible = ActiveScreenContext.Instance.GetCurrentScreen() == NCombatRoom.Instance;
        _status.Text = HandSharingMode.Private ? $"共享牌组 {Entry.Version} · 手牌不共享 · 个人上限 10 · 每人基础抽 {RuleSession.Current.DrawPerTurn} · "+(RuleSession.Current.Advanced?.Enabled==true?"高级自定义牌堆":"抽牌堆、弃牌堆、消耗堆共享") : Entry.Ready ? $"共享手牌 {Entry.Version} · 公共上限 {RuleSession.Current.HandLimit} · 每人基础抽 {RuleSession.Current.DrawPerTurn} · " +
            (RuleSession.Current.Advanced?.Enabled==true ? "高级自定义牌堆" : RuleSession.Current.Piles == DrawPileMode.Common ? "共享战斗牌组" : "独立牌堆") +
            (RuleSession.Current.TeammateCardLimit > 0 ? $" · 队友牌上限 {RuleSession.Current.TeammateCardLimit}/回合" : "") : $"共享手牌 {Entry.Version} · " + Entry.Status;
        _status.TooltipText = Entry.Status;
    }
}











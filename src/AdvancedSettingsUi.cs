using System;
using Godot;

namespace SharedHandPrototype;

internal static class AdvancedSettingsUi
{
    internal static Control Create()
    {
        var panel=new VBoxContainer{Name="AdvancedSettings",SizeFlagsHorizontal=Control.SizeFlags.ExpandFill};
        void Choice(string name,string[] values,Func<int> read,Action<int> write)
        {
            var dropdown=new OptionButton{CustomMinimumSize=new Vector2(300,52),SizeFlagsHorizontal=Control.SizeFlags.ShrinkEnd};
            foreach(var value in values)dropdown.AddItem(value);
            void Refresh()=>dropdown.Select(Math.Clamp(read(),0,values.Length-1));
            Refresh();
            SharedHandConfig.Instance.OnConfigReloaded+=Refresh;
            dropdown.TreeExiting+=()=>SharedHandConfig.Instance.OnConfigReloaded-=Refresh;
            dropdown.ItemSelected+=id=>{write((int)id);SharedHandConfig.Instance.Changed();};
            var text=NativeConfigStore.CreateRawLabelControl(name,24);
            panel.AddChild(new NConfigOptionRow("","Advanced_"+name,text,dropdown));
        }
        Choice("抽牌堆",new[]{"独立","合并"},()=>SharedHandConfig.AdvancedSharedDraw?1:0,v=>SharedHandConfig.AdvancedSharedDraw=v==1);
        Choice("弃牌堆",new[]{"独立","合并"},()=>SharedHandConfig.AdvancedSharedDiscard?1:0,v=>SharedHandConfig.AdvancedSharedDiscard=v==1);
        Choice("消耗堆",new[]{"独立","合并"},()=>SharedHandConfig.AdvancedSharedExhaust?1:0,v=>SharedHandConfig.AdvancedSharedExhaust=v==1);
        Choice("战斗合并规则",new[]{"所有人的牌都汇入","同步产生的对应副本只保留一份"},
            ()=>SharedHandConfig.AdvancedUniqueCards?1:0,v=>SharedHandConfig.AdvancedUniqueCards=v==1);
        Choice("手牌使用规则",new[]{"共享战斗牌堆模式","独立模式","不共享手牌模式"},
            ()=>SharedHandConfig.AdvancedPrivateHands?2:SharedHandConfig.AdvancedPersonalHandEffects?1:0,
            v=>{SharedHandConfig.AdvancedPrivateHands=v==2;SharedHandConfig.AdvancedPersonalHandEffects=v==1;});
        Choice("卡组同步规则",new[]{"不同步","完全一致","获得牌追加给其他人"},
            ()=>(int)SharedHandConfig.AdvancedDeckSyncMode,v=>SharedHandConfig.AdvancedDeckSyncMode=(AdvancedDeckSync)v);
        Choice("卡牌归属规则",new[]{"印牌归出牌的人","印牌归原先的持有者"},
            ()=>SharedHandConfig.AdvancedGeneratedForHolder?1:0,v=>SharedHandConfig.AdvancedGeneratedForHolder=v==1);
        var note=NativeConfigStore.CreateRawLabelControl("高级自定义模式覆盖上方的牌堆与同步设置，由房主决定；战斗开始时按以上设置建立牌堆。对应副本去重只在公共抽牌堆开战时生效，不影响消耗堆。\n弃牌堆合并、抽牌堆独立时，谁洗牌，公共弃牌堆里的牌就进入谁的抽牌堆。\n手牌使用规则的“独立模式”仍可手动打队友牌，只让卡牌效果处理自己的手牌；“不共享手牌模式”则只能看到和使用自己的手牌。",21);
        note.FitContent=true;note.AutowrapMode=TextServer.AutowrapMode.WordSmart;panel.AddChild(note);
        return panel;
    }
}

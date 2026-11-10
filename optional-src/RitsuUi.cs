using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using STS2RitsuLib.Settings;
using STS2RitsuLib.Utils.Persistence;

namespace SharedHandPrototype.Optional;

public static class SettingsRegistration
{
    public static void Register()
    {
        ModSettingsRegistry.Register(new ModSettingsPageBuilder(Entry.ModId)
        .WithTitle(ModSettingsText.Literal("共享牌组（SharedHandPrototype）"))
        .WithModDisplayName(ModSettingsText.Literal("共享牌组（SharedHandPrototype）"))
        .WithSourceAssemblyManifestLookup(false)
        .AddSection("options",section=>section.AddCustom("full",ModSettingsText.Literal(""),_=>SharedHandSettingsUi.CreateOptions()))
        .Build());
        try
        {
            var method=AccessTools.Method(typeof(ModSettingsRegistry),nameof(ModSettingsRegistry.Register),new[]{typeof(ModSettingsPage)});
            new Harmony("SharedHandPrototype.youjin-settings").Patch(method,prefix:new HarmonyMethod(typeof(SettingsRegistration),nameof(ExtendYoujinPage)));
            var existing=ModSettingsRegistry.GetPages().FirstOrDefault(p=>p.ModId=="youjin"&&p.Id=="settings");
            if(existing!=null)ModSettingsRegistry.Register(existing);
        }
        catch(Exception ex){Godot.GD.PushWarning("[SharedHand] YouJin settings integration unavailable: "+ex.GetBaseException().Message);}
    }

    private static bool CanShow()
    {
        var type=AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("YouJin.Scripts.YouJinSettings")).FirstOrDefault(t=>t!=null);
        return type?.GetProperty("PvpStyleSetting")?.GetValue(null)?.ToString() is "Speed" or "TurnBased";
    }
    private static ModSettingsText Text(string value)=>ModSettingsText.Literal(value);
    private static void ExtendYoujinPage(ref ModSettingsPage page)
    {
        if(page.ModId!="youjin"||page.Id!="settings"||page.Sections.Any(s=>s.Id=="sharedhand_board_duel"))return;
        try
        {
            var extra=new ModSettingsPageBuilder("sharedhand_board_ui").AddSection("sharedhand_board_toggle",s=>
                s.WithVisibleWhen(CanShow).AddToggle("sharedhand_board_enabled",Text("棋盘决斗"),
                    ModSettingsBindings.Callback<bool>(Entry.ModId,"board_duel_enabled",()=>SharedHandConfig.BoardDuelEnabled,v=>SharedHandConfig.BoardDuelEnabled=v,SharedHandConfig.SaveBoardDuelSettings,SaveScope.Global),Text("*该选项由共享牌组提供。")))
                .AddSection("sharedhand_board_duel",s=>s.WithTitle(Text("棋盘决斗")).WithVisibleWhen(()=>CanShow()&&SharedHandConfig.BoardDuelEnabled)
                    .AddIntSlider("sharedhand_board_cards",Text("轮流出牌数"),ModSettingsBindings.Callback<int>(Entry.ModId,"board_duel_cards",()=>SharedHandConfig.BoardDuelCards,v=>SharedHandConfig.BoardDuelCards=Math.Clamp(v,1,10),SharedHandConfig.SaveBoardDuelSettings,SaveScope.Global),1,10)
                    .AddIntSlider("sharedhand_board_turn_seconds",Text("轮流出牌时间"),ModSettingsBindings.Callback<int>(Entry.ModId,"board_duel_turn_seconds",()=>SharedHandConfig.BoardDuelTurnSeconds,v=>SharedHandConfig.BoardDuelTurnSeconds=Math.Clamp(v,1,60),SharedHandConfig.SaveBoardDuelSettings,SaveScope.Global),1,60,1,v=>$"{v} 秒")
                    .AddIntSlider("sharedhand_board_rps_seconds",Text("猜拳时间"),ModSettingsBindings.Callback<int>(Entry.ModId,"board_duel_rps_seconds",()=>SharedHandConfig.BoardDuelRpsSeconds,v=>SharedHandConfig.BoardDuelRpsSeconds=Math.Clamp(v,1,60),SharedHandConfig.SaveBoardDuelSettings,SaveScope.Global),1,60,1,v=>$"{v} 秒"))
                .Build().Sections;
            var sections=page.Sections.ToList();int i=sections.FindIndex(s=>s.Id=="pvp");
            if(i<0)return;
            sections.InsertRange(i+1,extra);
            var ctor=typeof(ModSettingsPage).GetConstructors(BindingFlags.Instance|BindingFlags.NonPublic).Single();
            page=(ModSettingsPage)ctor.Invoke(new object?[]{page.ModId,page.Id,page.ParentPageId,page.Title,page.Description,page.SortOrder,sections,page.VisibleWhen,page.EnabledWhen,page.MenuCapabilities,page.VisibleOnHostSurfaces,page.ReadOnlyOnHostSurfaces,page.SidebarVisibleOnlyWhenActive,page.HideDescription,page.SourceAssembly,page.UseSourceAssemblyManifestLookup});
        }
        catch(Exception ex){Godot.GD.PushWarning("[SharedHand] Could not extend YouJin settings: "+ex.GetBaseException().Message);}
    }
}

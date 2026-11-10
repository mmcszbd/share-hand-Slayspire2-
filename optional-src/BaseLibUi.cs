using BaseLib.Config;
using Godot;
using MegaCrit.Sts2.Core.Localization;

namespace SharedHandPrototype.Optional;

public static class SettingsRegistration
{
    private sealed class Config:ModConfig
    {
        public Config():base("SharedHandPrototype.optional-ui.cfg"){}
        public override bool VisibleInModList()=>true;
        public override void SetupConfigUI(Control container)=>container.AddChild(SharedHandSettingsUi.CreateOptions());
    }
    public static void Register()
    {
        var config=new Config();
        LocManager.Instance.GetTable("settings_ui").MergeWith(new(){[config.ModPrefix[..^1]+".mod_title"]="共享牌组（SharedHandPrototype）"});
        ModConfigRegistry.Register(Entry.ModId,config);
    }
}

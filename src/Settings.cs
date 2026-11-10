using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;


using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;

namespace SharedHandPrototype;

public enum DrawPileMode { Independent, Common }

public sealed record GameplaySettings(int HandLimit = 15, int DrawPerTurn = 5,
    bool ShareDeck = false, DrawPileMode Piles = DrawPileMode.Independent,
    bool SmallDeck = false, bool SmallerDeck = false, bool Harmony = false, bool LockSelections = false, bool IndependentCopies = true, int TeammateCardLimit = 0, bool PrivateHands = false, bool UnifiedForge = false, bool BoardDuel = false, int BoardCards = 1, int BoardTurnSeconds = 15, int BoardRpsSeconds = 15, bool OnlyHands = false, AdvancedRules? Advanced = null)
{
    public GameplaySettings Normalize() => this with
    {
        HandLimit = Math.Clamp(HandLimit, 1, 99), DrawPerTurn = Math.Clamp(DrawPerTurn, 1, 99),
        ShareDeck = Advanced?.Enabled==true ? Advanced.DeckSync != AdvancedDeckSync.None : ShareDeck,
        Piles = Advanced?.Enabled==true ? (Advanced.SharedDraw && Advanced.SharedDiscard ? DrawPileMode.Common : DrawPileMode.Independent) :
            !OnlyHands && !PrivateHands && Piles == DrawPileMode.Common ? DrawPileMode.Common : DrawPileMode.Independent,
        PrivateHands = Advanced?.Enabled==true ? Advanced.PrivateHands : !OnlyHands && PrivateHands,
        OnlyHands = Advanced?.Enabled==true ? false : OnlyHands,
        IndependentCopies = Advanced?.Enabled==true ? !Advanced.UniqueMirroredCards : IndependentCopies && !OnlyHands,
        TeammateCardLimit = TeammateCardLimit == 0 ? 0 : Math.Clamp(TeammateCardLimit, 1, 20),
        BoardCards = Math.Clamp(BoardCards, 1, 10), BoardTurnSeconds = Math.Clamp(BoardTurnSeconds, 1, 60), BoardRpsSeconds = Math.Clamp(BoardRpsSeconds, 1, 60),
        SmallerDeck = (Advanced?.Enabled==true ? Advanced.DeckSync != AdvancedDeckSync.None : ShareDeck) && SmallerDeck,
        SmallDeck = SmallDeck && !((Advanced?.Enabled==true ? Advanced.DeckSync != AdvancedDeckSync.None : ShareDeck) && SmallerDeck)
    };
}

public sealed class SharedHandConfig : NativeConfigStore
{
    internal static SharedHandConfig Instance = null!;
    private static int _handLimit = 15, _draw = 5;
    private static int _observedHandLimit=15;
    private static bool _shareDeck;
    private static bool _smallerDeck, _smallDeck;
    private static DrawPileMode _piles = DrawPileMode.Common;
    private static bool _independent = false, _independentConfigured;
    private static bool _privateHands, _onlyHands;
    private static Dictionary<string, string> _colors = new();
    [ConfigSection("Gameplay"), ConfigHoverTip, ConfigSlider(1, 99, 1)]
    public static int PublicHandLimit { get => _handLimit; set => _handLimit = Math.Clamp(value, 1, 99); }
    // Null migrates older configs: preserve a non-default cap as customized.
    // Explicit false distinguishes an automatically chosen 27/35 from an edit.
    [ConfigHideInUI]
    public static bool? HandLimitManuallyChanged { get; set; }
    [ConfigIgnore, ConfigSlider(1, 99, 1)]
    public static int EditedHandLimit
    {
        get => PublicHandLimit;
        set
        {
            int limit=Math.Clamp(value,1,99);
            if(limit!=PublicHandLimit)HandLimitManuallyChanged=true;
            PublicHandLimit=limit;
        }
    }
    internal static void SetAutomaticHandLimit(int players)
    {
        if(players is < 2 or > PartyLimits.MaxPlayers || HandLimitManuallyChanged==true ||
            HandLimitManuallyChanged==null&&PublicHandLimit!=15)return;
        int limit=players==2?15:players==3?27:35;
        bool changed=HandLimitManuallyChanged!=false||PublicHandLimit!=limit;
        HandLimitManuallyChanged=false;PublicHandLimit=limit;_observedHandLimit=limit;
        if(changed)Instance?.SaveDebounced();
    }
    [ConfigHoverTip, ConfigSlider(1, 99, 1)]
    public static int BaseDraw { get => _draw; set => _draw = Math.Clamp(value, 1, 99); }
    [ConfigHoverTip]
    public static bool SharedDeck
    {
        get => _shareDeck;
        set { _shareDeck = value; if (!value) _smallerDeck = false; }
    }
    [ConfigHideInUI]
    public static DrawPileMode PileMode
    {
        get => _piles;
        set
        {
            _piles = value == DrawPileMode.Common ? value : DrawPileMode.Independent;
            if (_piles == DrawPileMode.Common) { _privateHands = false; _onlyHands = false; }
            // Old config files only stored this enum. Preserve their single
            // common copy; an explicit new checkbox value always wins on load.
            if (!_independentConfigured) _independent = _piles != DrawPileMode.Common;
        }
    }
    [ConfigHoverTip]
    public static bool Harmony { get; set; } = true;
    [ConfigIgnore] public static bool FriendlySynergyAlwaysEnabled { get => true; set { } }
    internal const string PrivateHandsHelp="仅抽牌堆、弃牌堆、消耗堆共享。";
    internal const string IndependentPileHelp="神化，活雾，只影响你自己的牌。若与“同步卡组”配合将会导致抽牌堆变得很大。该选项也会让你抓牌比较独立。";
    internal const string OnlyHandsHelp="你可以把队友的刀舞打出来，以此污染对方的牌堆。";
    internal const string HarmonyHelp="你可以把所有的技能牌都用在队友身上了。你应该会帮队友起甲，而不是把祭品用在队友身上把队友弄死的...对吧？\n你的能力牌同样起效。";
    [ConfigSection("Display"), ConfigHoverTip]
    public static bool StackBasics { get=>_stackBasics; set{_stackBasics=value;if(!value)_stackSameName=false;} }
    private static bool _stackBasics=true,_stackSameName;
    [ConfigHoverTip]
    public static bool StackSameName { get=>_stackSameName; set{_stackSameName=value;if(value)_stackBasics=true;} }
    [ConfigHoverTip]
    public static bool SelectionChains { get; set; } = true;
    [ConfigHoverTip]
    public static bool LockSelections { get; set; } = false;
    [ConfigHoverTip]
    public static bool TeammateGlow { get; set; } = true;
    public static bool OwnCardGlow { get; set; } = false;
    [ConfigHoverTip]
    public static bool LimitTeammateCards { get; set; } = false;
    private static int _teammateCardLimit = 5;
    [ConfigHoverTip, ConfigSlider(1, 20, 1)]
    public static int TeammateCardsPerTurn { get => _teammateCardLimit; set => _teammateCardLimit = Math.Clamp(value, 1, 20); }
    internal const string LimitHelp = "默认关闭，由房主决定。启用后，每名玩家每回合最多打出所设数量的队友手牌。仅抽牌赋予临时归属；进入弃牌堆后清除，直接回收、检索或生成的牌不占额度。只计算实际打出的牌；卡牌效果造成的消耗、变化、强化不计入，重复结算同一张牌也只计算一次。战斗中修改会在离开战斗后生效。";
    internal const string SharedPileHelp = "战斗中合并所有人的抽、弃牌堆和手牌，允许了散射炮的发挥。消耗堆不合并。";
    [ConfigHideInUI]
    public static bool AutoCharacterColors { get; set; } = false;
    [ConfigHoverTip]
    public static bool TopHandInverted { get; set; } = true;
    [ConfigHoverTip]
    public static bool TopHandOccluded { get; set; } = true;
    [ConfigHoverTip]
    public static SharedHandLayout HandLayout { get; set; } = SharedHandLayout.BalancedRows;
    [ConfigHideInUI]
    public static string CustomLayoutData { get; set; } = "";
    [ConfigHideInUI]
    public static string SavedLayoutPresets { get; set; } = "";
    [ConfigHideInUI]
    public static int LayoutPresetTarget { get; set; } = 2;
    internal static void EditorChanged() { Instance.Changed(); Instance.SaveDebounced(); SharedPanel.MarkDirty(); }
    [ConfigIgnore]
    public static bool LayoutCustom { get => HandLayout == SharedHandLayout.Custom; set { if(value) HandLayout=SharedHandLayout.Custom; } }
    [ConfigIgnore]
    public static bool LayoutTopBottom { get => HandLayout == SharedHandLayout.TopAndBottom; set { if (value) HandLayout = SharedHandLayout.TopAndBottom; } }
    [ConfigIgnore]
    public static bool LayoutQuadrants { get => HandLayout == SharedHandLayout.FourQuadrants; set { if (value) HandLayout = SharedHandLayout.FourQuadrants; } }
    [ConfigIgnore]
    public static bool LayoutBalanced { get => HandLayout == SharedHandLayout.BalancedRows; set { if (value) HandLayout = SharedHandLayout.BalancedRows; } }
    [ConfigSection("StarterDeck"), ConfigHoverTip]
    public static bool SmallDeck { get => _smallDeck; set { _smallDeck = value; if (value) _smallerDeck = false; } }
    [ConfigHoverTip]
    public static bool SmallerDeck { get => _smallerDeck; set { _smallerDeck = SharedDeck && value; if (_smallerDeck) _smallDeck = false; } }
    [ConfigHoverTip]
    public static bool IndependentPiles { get => _independent; set { _independent = value; _independentConfigured = true; if(value)_onlyHands=false; } }
    [ConfigHoverTip]
    public static bool SharedPiles
    {
        get => PileMode == DrawPileMode.Common;
        set { _piles = value ? DrawPileMode.Common : DrawPileMode.Independent; if(value) { _privateHands=false; _onlyHands=false; } }
    }
    [ConfigHideInUI]
    public static string CharacterColors
    {
        get => JsonSerializer.Serialize(_colors);
        set
        {
            try { _colors = JsonSerializer.Deserialize<Dictionary<string, string>>(value) ?? new(); }
            catch (JsonException) { _colors = new(); }
        }
    }
    internal static readonly Color DefaultGreen = new(.12f, 1f, .25f, .98f);
    internal static GameplaySettings Requested => new(PublicHandLimit, BaseDraw, SharedDeck, PileMode, SmallDeck, SmallerDeck, Harmony, LockSelections, IndependentPiles, LimitTeammateCards ? TeammateCardsPerTurn : 0, PrivateHands, UnifiedForge, BoardDuelEnabled, BoardDuelCards, BoardDuelTurnSeconds, BoardDuelRpsSeconds, OnlyHands, AdvancedRequested);
    internal const string LockHelp = "默认关闭，由房主决定。队友正在拖动、瞄准或选中的手牌，其他玩家暂时不能打出；取消选择后解锁。锁链显示仍可单独开关。战斗中修改会在离开战斗后生效。";
    internal static Color ColorFor(CharacterModel character) => _colors.TryGetValue(character.Id.ToString(), out var html) &&
        Color.HtmlIsValid(html) ? Color.FromHtml(html) : AutoCharacterColors ? character.MapDrawingColor : DefaultGreen;
    internal static void SetColor(CharacterModel character, Color color) => _colors[character.Id.ToString()] = color.ToHtml();
    internal static void ResetColors() => _colors.Clear();
    internal static void SetVanillaColors()
    {
        AutoCharacterColors = true;
        foreach (var character in Characters())
            SetColor(character, character.MapDrawingColor);
    }
    [ConfigSection("Actions"), ConfigButton("DefaultCharacterColors"), ConfigHoverTip]
    private void DefaultCharacterColors()
    {
        SetVanillaColors();
        Save(); ConfigReloaded(); Changed();
    }
    [ConfigButton("ExportDiagnostics"), ConfigHoverTip]
    private void ExportDiagnostics() => MegaCrit.Sts2.Core.Helpers.TaskHelper.RunSafely(Diagnostics.Prompt());
    [ConfigSection("Actions"), ConfigHoverTip]
    public static bool PrivateHands
    {
        get => _privateHands;
        set { _privateHands=value; if(value) { _onlyHands=false; _piles=DrawPileMode.Independent; } }
    }
    [ConfigSection("Experimental"), ConfigHoverTip]
    public static bool UnifiedForge { get; set; } = false;
    [ConfigSection("Experimental"), ConfigHoverTip]
    public static bool HandExpansion { get; set; } = false;
    [ConfigHideInUI] public static bool BoardDuelEnabled { get; set; }
    [ConfigHideInUI] public static bool AdvancedEnabled { get; set; }
    [ConfigHideInUI] public static bool AdvancedSharedDraw { get; set; }
    [ConfigHideInUI] public static bool AdvancedSharedDiscard { get; set; }
    [ConfigHideInUI] public static bool AdvancedSharedExhaust { get; set; }
    [ConfigHideInUI] public static bool AdvancedUniqueCards { get; set; }
    [ConfigHideInUI] public static bool AdvancedPersonalHandEffects { get; set; }
    [ConfigHideInUI] public static bool AdvancedPrivateHands { get; set; }
    [ConfigHideInUI] public static AdvancedDeckSync AdvancedDeckSyncMode { get; set; }
    [ConfigHideInUI] public static bool AdvancedGeneratedForHolder { get; set; }
    internal static AdvancedRules AdvancedRequested => new(AdvancedEnabled,AdvancedSharedDraw,AdvancedSharedDiscard,
        AdvancedSharedExhaust,AdvancedUniqueCards,AdvancedPersonalHandEffects,AdvancedDeckSyncMode,AdvancedGeneratedForHolder,AdvancedPrivateHands);
    [ConfigHideInUI] public static bool OnlyHands
    {
        get => _onlyHands;
        set { _onlyHands=value; if(value) { _privateHands=false; _piles=DrawPileMode.Independent; _independent=false; } }
    }
    [ConfigHideInUI] public static int BoardDuelCards { get; set; } = 1;
    [ConfigHideInUI] public static int BoardDuelTurnSeconds { get; set; } = 15;
    [ConfigHideInUI] public static int BoardDuelRpsSeconds { get; set; } = 15;
    public static void SaveBoardDuelSettings() { Instance?.SaveDebounced(); }
    internal static IEnumerable<CharacterModel> Characters() => ModelDb.AllCharacters
        .Concat(ModelDb.All.OfType<CharacterModel>()).Where(c => c.IsPlayable).DistinctBy(c => c.Id).ToArray();
    internal void ResetAll()
    {
        HandLimitManuallyChanged=false;
        PrivateHands=false; OnlyHands=false; UnifiedForge=false; HandExpansion=false; BoardDuelEnabled=false; BoardDuelCards=1; BoardDuelTurnSeconds=15; BoardDuelRpsSeconds=15;
        AdvancedEnabled=false;AdvancedSharedDraw=false;AdvancedSharedDiscard=false;AdvancedSharedExhaust=false;
        AdvancedUniqueCards=false;AdvancedPersonalHandEffects=false;AdvancedPrivateHands=false;AdvancedDeckSyncMode=AdvancedDeckSync.None;AdvancedGeneratedForHolder=false;
        Harmony=true; LockSelections=false; LimitTeammateCards=false; TeammateCardsPerTurn=5; IndependentPiles=false; PublicHandLimit = 15; BaseDraw = 5; SharedDeck = false; PileMode = DrawPileMode.Common; StackBasics=true; StackSameName=false;
        SmallDeck = false; SmallerDeck = false; SelectionChains = true; TeammateGlow = true; OwnCardGlow = false; HandLayout = SharedHandLayout.BalancedRows; TopHandInverted = true; TopHandOccluded = true; ResetColors(); CustomLayoutData="";
        SavedLayoutPresets=""; LayoutPresetTarget=2; AutoCharacterColors=false;
        _observedHandLimit=PublicHandLimit;
        Save(); ConfigReloaded(); Changed();
    }
    protected override void RestoreDefaultsNoConfirm() => ResetAll();
    public SharedHandConfig() : base("SharedHandPrototype") { }
    public override bool VisibleInModList() => true;
    internal static void Register()
    {
        Instance = new SharedHandConfig();
        Instance.Load();
        _observedHandLimit=PublicHandLimit;
        Instance.OnConfigReloaded+=()=>_observedHandLimit=PublicHandLimit;
        Instance.ConfigChanged += (_, _) =>
        {
            // RitsuLib mirrors the persisted PublicHandLimit property directly.
            // Both configuration UIs notify Changed after a real edit.
            if(_observedHandLimit!=PublicHandLimit)HandLimitManuallyChanged=true;
            _observedHandLimit=PublicHandLimit;
            Instance.SaveDebounced();SharedPanel.MarkDirty();
        };
    }
    private static PropertyInfo Property(string name) => typeof(SharedHandConfig).GetProperty(name)!;
    private static readonly FieldInfo Hover = AccessTools.Field(typeof(NConfigOptionRow), "_hoverTip");
    private static HoverTip Tip(string title, string help)
    {
        var key = "SharedHandPrototype." + title;
        LocManager.Instance.GetTable("settings_ui").MergeWith(new() { [key] = title });
        return new HoverTip(new LocString("settings_ui", key), help);
    }
    private NConfigOptionRow Row(Control parent, string title, Control input, string help)
    {
        var row = new NConfigOptionRow("", title, CreateRawLabelControl(title, 26), input);
        Hover.SetValue(row, Tip(title, help));
        parent.AddChild(row);
        return row;
    }
    public override void SetupConfigUI(Control optionContainer)
    {
        var note = CreateRawLabelControl($"共享手牌 · {Entry.Version}（测试版）\n规则由房主决定；颜色和锁链提示仅影响本机。\n模组兼容《友尽尖塔》，【共享模式】正常可用，已适配【贪婪模式】常规内容，【任务模式】不可用。\n同步卡组与牌堆模式支持局中修改；战斗中修改会在离开战斗后生效。\n手牌上限和基础抽牌数在重新载入存档后采用房主当前设置。", 22);
        note.CustomMinimumSize = new Vector2(0, 185);
        optionContainer.AddChild(note);
        var stack=CreateRawTickboxControl(Property(nameof(StackBasics)));
        Row(optionContainer,"打防堆叠",stack,"就是打击和防御堆一块而已。");
        var sameName=CreateRawTickboxControl(Property(nameof(StackSameName)));
        var sameRow=Row(optionContainer,"　同名堆叠",sameName,"将所有手牌中的同名卡堆叠，若附魔、强化不一致则无法堆叠。");
        void UpdateStacks(){stack.IsTicked=StackBasics;sameName.IsTicked=StackSameName;}
        EventHandler stacksChanged=(_,_)=>UpdateStacks();ConfigChanged+=stacksChanged;OnConfigReloaded+=UpdateStacks;
        sameRow.TreeExiting+=()=>{ConfigChanged-=stacksChanged;OnConfigReloaded-=UpdateStacks;};UpdateStacks();
        var layouts = new VBoxContainer { Name = "SharedHandLayouts", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        optionContainer.AddChild(layouts);
        var topBottom = CreateRawTickboxControl(Property(nameof(LayoutTopBottom)));
        var quadrants = CreateRawTickboxControl(Property(nameof(LayoutQuadrants)));
        var balanced = CreateRawTickboxControl(Property(nameof(LayoutBalanced)));
        var custom = CreateRawTickboxControl(Property(nameof(LayoutCustom)));
        Row(layouts, "布局一：上下对称扇形", topBottom, "下方为 P1/P2 共用的扇形手牌，上方为 P3/P4 共用的镜像扇形。扇形沿用原版曲线和悬停放大、相邻牌让位动画。");
        Row(layouts, "布局二：下方四区", quadrants, "下方 2×2 固定牌区，增大卡牌至 0.64 倍，不再随区域牌数自动缩小。悬停显示独立大图。");
        Row(layouts, "布局三：底部扇形与左右竖排", balanced, "P1/P2 在底部共用原有扇形区域；P3 在左侧能量上方，P4 在右侧结束回合按钮上方。竖排露出每张牌名，可以覆盖卡图。向战场拖动出牌。");
        Row(layouts, "自定义布局", custom, "使用下方截图编辑器中的位置、缩放、间距和排列。拖动整组牌区；可合并 P1/P2 或 P3/P4。仅影响本机，自动保存。");
        Row(layouts, "上方手牌受顶部 UI 遮挡", CreateRawTickboxControl(Property(nameof(TopHandOccluded))), "默认开启。上方手牌被顶部状态栏遮挡。关闭仅取消顶部遮挡；打开地图、卡组或设置时，手牌始终隐藏。仅影响本机。");
        layouts.AddChild(LayoutEditor.Create());
        void UpdateLayouts() { topBottom.IsTicked = LayoutTopBottom; quadrants.IsTicked = LayoutQuadrants; balanced.IsTicked = LayoutBalanced; custom.IsTicked=LayoutCustom; }
        EventHandler updateLayouts = (_, _) => UpdateLayouts();
        ConfigChanged += updateLayouts; OnConfigReloaded += UpdateLayouts;
        layouts.TreeExiting += () => { ConfigChanged -= updateLayouts; OnConfigReloaded -= UpdateLayouts; };
        UpdateLayouts();
        Row(optionContainer,"和谐友爱",CreateRawTickboxControl(Property(nameof(Harmony))),HarmonyHelp);
        var cap = CreateRawSliderControl(Property(nameof(EditedHandLimit)));
        cap.SetRange(1, 99, 1);
        Row(optionContainer, "公共手牌数量上限", cap, "1～99，默认 15。所有玩家手牌合计使用此上限。已满时停止抽牌，生成的新牌遵循原版满手牌处理。在 2～4 人共享手牌中生效。新冒险和重新载入存档时采用房主当前配置；进行中的战斗不会即时改变。");
        var draw = CreateRawSliderControl(Property(nameof(BaseDraw)));
        draw.SetRange(1, 99, 1);
        Row(optionContainer, "每人每回合基础抽牌", draw, "1～99，默认 5。分别设置每名玩家回合开始时的基础抽牌数；保留遗物、能力和其他效果提供的额外抽牌，仍受公共手牌上限限制。新冒险和重新载入存档时采用房主当前配置；进行中的战斗不会即时改变。");
        var deck = CreateRawTickboxControl(Property(nameof(SharedDeck)));
        Row(optionContainer, "同步卡组", deck, "所有玩家卡组合并。任意玩家对卡组的改变都生效。\n由房主决定；已开始的冒险也可开启，合并现有卡组，之后获得、删除和变化的牌继续同步。战斗中修改会在离开战斗后应用。关闭后保留已有卡牌，不拆分卡组。\n小卡组模式和万物一心仅在新冒险开始时调整打防。控制台删除会尝试在下次保存前校验同步。");
        var pilePair = new HBoxContainer { Name = "PileChoices", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        optionContainer.AddChild(pilePair);
        var independent = CreateRawTickboxControl(Property(nameof(IndependentPiles)));
        var shared = CreateRawTickboxControl(Property(nameof(SharedPiles)));
        Row(pilePair, "独立牌堆", independent, IndependentPileHelp).SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var sharedRow = Row(pilePair, "共享战斗牌组", shared, SharedPileHelp);
        sharedRow.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var handPair = new HBoxContainer { Name = "HandSharingChoices", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        optionContainer.AddChild(handPair);
        var personal = CreateRawTickboxControl(Property(nameof(PrivateHands)));
        var onlyHands = CreateRawTickboxControl(Property(nameof(OnlyHands)));
        Row(handPair, "手牌不共享", personal, PrivateHandsHelp).SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        Row(handPair, "仅手牌共享", onlyHands, OnlyHandsHelp).SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        Row(optionContainer, "队友选牌锁链提示", CreateRawTickboxControl(Property(nameof(SelectionChains))),
            "默认开启。队友正在拖动、瞄准或选择弃置／升级的手牌，附加女王“束缚”使用的锁链外观。只是提示，不会施加减益、不锁定卡牌，你仍可以正常打出它。关闭后仅隐藏本机的提示。");
        Row(optionContainer, "真的锁住了", CreateRawTickboxControl(Property(nameof(LockSelections))), LockHelp);
        var smallPair = new HBoxContainer { Name = "SmallDeckChoices", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        optionContainer.AddChild(smallPair);
        var small = CreateRawTickboxControl(Property(nameof(SmallDeck)));
        Row(smallPair, "小卡组模式", small,
            "打开后安东尼会弄你。减少打防数量。\n游戏开始时将所有玩家打防数量减少至原数量除以2后除以人数。采用四舍五入。").SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var smaller = CreateRawTickboxControl(Property(nameof(SmallerDeck)));
        var smallerRow = Row(smallPair, "万物一心", smaller,
            "必须开启“同步卡组”。\n与小卡组模式冲突。\n开始游戏时，会以队伍中打防数量最高的一名角色为准，构建打防数量。");
        smallerRow.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        void UpdateChoices()
        {
            independent.IsTicked = IndependentPiles; shared.IsTicked = SharedPiles;
            personal.IsTicked = PrivateHands; onlyHands.IsTicked = OnlyHands;
            small.IsTicked = SmallDeck; smaller.IsTicked = SmallerDeck;
            if (SharedDeck) smaller.Enable(); else smaller.Disable();
            smallerRow.Modulate = SharedDeck ? Colors.White : new Color(.5f, .5f, .5f);
        }
        EventHandler updateChoices = (_, _) => UpdateChoices();
        ConfigChanged += updateChoices; OnConfigReloaded += UpdateChoices;
        smallPair.TreeExiting += () => { ConfigChanged -= updateChoices; OnConfigReloaded -= UpdateChoices; };
        UpdateChoices();
        var heading = CreateRawLabelControl("队友卡牌发光边框颜色", 30);
        heading.CustomMinimumSize = new Vector2(0, 80);
        optionContainer.AddChild(heading);
        Row(optionContainer, "队友卡牌发光", CreateRawTickboxControl(Property(nameof(TeammateGlow))),
            "默认开启。用下方角色颜色标记队友持有的手牌。关闭后不再添加队友发光，恢复卡牌原本的可打出、选中等高亮效果；仅影响本机显示。");
        optionContainer.AddChild(new NConfigOptionRow("", "OwnCardGlow", CreateRawLabelControl("自己卡牌也发光", 26), CreateRawTickboxControl(Property(nameof(OwnCardGlow)))) { Name="OwnCardGlowRow" });
        var limited = CreateRawTickboxControl(Property(nameof(LimitTeammateCards)));
        Row(optionContainer, "限制每回合可打出队友牌的数量", limited, LimitHelp);
        var teammateLimit = CreateRawSliderControl(Property(nameof(TeammateCardsPerTurn)));
        teammateLimit.SetRange(1, 20, 1);
        var limitRow = Row(optionContainer, "　每回合队友牌数量", teammateLimit, LimitHelp);
        void UpdateLimit() { limited.IsTicked = LimitTeammateCards; limitRow.Visible=LimitTeammateCards; }
        EventHandler limitChanged = (_,_) => UpdateLimit();
        ConfigChanged += limitChanged; OnConfigReloaded += UpdateLimit;
        limitRow.TreeExiting += () => { ConfigChanged -= limitChanged; OnConfigReloaded -= UpdateLimit; };
        UpdateLimit();
        var pickers = new Dictionary<CharacterModel, ColorPickerButton>();
        foreach (var character in Characters())
        {
            var name = character.Title.GetFormattedText();
            var iconAndName = new HBoxContainer { CustomMinimumSize = new Vector2(0, 64), MouseFilter = Control.MouseFilterEnum.Ignore };
            try
            {
                iconAndName.AddChild(new TextureRect { Texture = character.IconTexture, CustomMinimumSize = new Vector2(56, 56),
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                    MouseFilter = Control.MouseFilterEnum.Ignore });
            }
            catch { iconAndName.AddChild(CreateRawLabelControl("?", 24)); }
            var title = CreateRawLabelControl(name, 26);
            title.CustomMinimumSize = new Vector2(240, 64);
            iconAndName.AddChild(title);
            var picker = new ColorPickerButton { Color = ColorFor(character), EditAlpha = false, CustomMinimumSize = new Vector2(64, 48),
                SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            picker.GetPicker().DeferredMode = true;
            picker.ColorChanged += color => { SetColor(character, color); Changed(); };
            var row = new NConfigOptionRow("", "Color_" + character.Id.Entry, iconAndName, picker);
            Hover.SetValue(row, Tip(name + "的队友牌", "点击颜色方块展开调色板，可输入颜色值。按当前持牌玩家的角色区分，只改变发光边缘，不改变卡面或名字。模组角色自动列出。"));
            optionContainer.AddChild(row);
            pickers[character] = picker;
        }
        void UpdateColors() { foreach (var item in pickers) item.Value.Color = ColorFor(item.Key); }
        void RefreshColors() { UpdateColors(); Changed(); }
        OnConfigReloaded += UpdateColors;
        heading.TreeExiting += () => OnConfigReloaded -= UpdateColors;
        var vanilla = CreateRawButtonControl("默认角色颜色", () => { SetVanillaColors(); RefreshColors(); });
        Row(optionContainer, "角色默认配色", vanilla, "使用各角色的地图绘画画笔颜色，包含模组角色。");
        optionContainer.AddChild(CreateRawButtonControl("恢复默认", ResetAll));
        optionContainer.AddChild(CreateRawButtonControl("导出联机诊断包", ExportDiagnostics));
        optionContainer.AddChild(new HSeparator{CustomMinimumSize=new Vector2(0,24)});
        var experimental=CreateRawLabelControl("趣味实验性调整",28);
        experimental.Name="SharedHandExperimentalHeading";
        experimental.CustomMinimumSize=new Vector2(0,64);
        optionContainer.AddChild(experimental);
        Row(optionContainer,"全队锻造统一",CreateRawTickboxControl(Property(nameof(UnifiedForge))),"所有人都只会锻造同一把君王之剑，不影响复制。");
        Row(optionContainer,"手牌展开",CreateRawTickboxControl(Property(nameof(HandExpansion))),"开启后，可在布局编辑器中为合并的牌区启用展开模式并调整展开程度。仅影响本机显示。");
        var synergyTick=CreateRawTickboxControl(Property(nameof(FriendlySynergyAlwaysEnabled)));
        synergyTick.MouseFilter=Control.MouseFilterEnum.Ignore;synergyTick.FocusMode=Control.FocusModeEnum.None;
        synergyTick.Toggled+=tick=>tick.IsTicked=true;
        var synergy=new NConfigOptionRow("","FriendlySynergy",CreateRawLabelControl("友尽联动",26),synergyTick);
        synergy.Modulate=new Color(.6f,.6f,.6f,1f);
        Hover.SetValue(synergy,Tip("友尽联动","解锁棋盘决斗模式，为RitsuLib的友尽尖塔配置添加一项新功能。\n【棋盘决斗】在互殴战进行时，规则变为每人每次出一张牌，轮流进行，回合开始时进行猜拳。"));
        optionContainer.AddChild(synergy);
        var advancedToggle=CreateRawTickboxControl(Property(nameof(AdvancedEnabled)));
        Row(optionContainer,"高级自定义模式",advancedToggle,"覆盖上方玩法开关，逐项设置牌堆、同步、手牌效果和生成牌归属。由房主决定，战斗中修改会在离开战斗后生效。");
        optionContainer.AddChild(new Control { CustomMinimumSize = new Vector2(0, 24), MouseFilter = Control.MouseFilterEnum.Ignore });
        var descriptions=new VBoxContainer{Name="ModeDescriptionPanel",SizeFlagsHorizontal=Control.SizeFlags.ExpandFill};
        optionContainer.AddChild(descriptions);ModeDescriptionPreview.AddTo(descriptions);
        var advanced=AdvancedSettingsUi.Create();optionContainer.AddChild(advanced);
        void UpdateAdvanced(){descriptions.Visible=!AdvancedEnabled;advanced.Visible=AdvancedEnabled;}
        EventHandler advancedChanged=(_,_)=>UpdateAdvanced();ConfigChanged+=advancedChanged;OnConfigReloaded+=UpdateAdvanced;
        advanced.TreeExiting+=()=>{ConfigChanged-=advancedChanged;OnConfigReloaded-=UpdateAdvanced;};
        UpdateAdvanced();
    }
}













using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;

namespace SharedHandPrototype;

internal static class LayoutEditor
{
    internal readonly record struct PreviewCard(HandLayouts.Slot Slot,int Owner);

    // The editor renders ten sample cards per player. Use the same geometry,
    // layer ordering and displacement as the live hand, so every participant
    // in a joined zone has a distinct row rather than sharing one rear row.
    internal static List<PreviewCard> PreviewCards(HandDesign design,int root,bool expansionEnabled)
    {
        var members=LayoutDesign.Members(design,root);
        var zone=design.Zones[root];
        var previews=new List<PreviewCard>(members.Length*10);
        if(expansionEnabled&&zone.Expand&&members.Length>1)
        {
            var positions=LayoutDesign.Geometry(root,10,new Vector2(1920,1080),zone);
            var direction=zone.Shape=="Column"?new Vector2(zone.X<.5f?1:-1,0):new Vector2(0,zone.Mirror?1:-1);
            for(int layer=0;layer<members.Length;layer++)
                for(int i=0;i<10;i++)
                    previews.Add(new(positions[i] with { Layer=layer,Center=positions[i].Center+direction*(zone.ExpandDistance*layer) },members[layer]));
        }
        else
        {
            var positions=LayoutDesign.Geometry(root,10*members.Length,new Vector2(1920,1080),zone);
            for(int i=0;i<positions.Count;i++)previews.Add(new(positions[i],members[i/10]));
        }
        return previews;
    }

    internal static bool PreviewContains(HandDesign design,int root,bool expansionEnabled,Vector2 point)
    {
        // Mouse position is in the native 1920x1080 coordinate space. Undo
        // each preview card's rotation to match its drawn hit rectangle.
        return PreviewCards(design,root,expansionEnabled).Any(preview=>
        {
            var slot=preview.Slot;
            var local=(point-slot.Center).Rotated(-Mathf.DegToRad(slot.Angle));
            return new Rect2(new Vector2(-150,-211)*slot.Scale,new Vector2(300,422)*slot.Scale).HasPoint(local);
        });
    }

    internal static Texture2D? Asset(string name)
    {
        var directory=Path.GetDirectoryName(typeof(Entry).Assembly.Location);
        var path=directory==null?null:Path.Combine(directory,name);
        if(path!=null&&File.Exists(path))
        {
            var external=Image.LoadFromFile(path);
            if(external!=null&&!external.IsEmpty())return ImageTexture.CreateFromImage(external);
        }
        return EmbeddedAsset(name);
    }
    internal static Texture2D? EmbeddedAsset(string name)
    {
        using var input=typeof(Entry).Assembly.GetManifestResourceStream("SharedHandPrototype.Assets."+name);
        if(input==null)return null;
        using var data=new MemoryStream();input.CopyTo(data);
        var image=new Image();
        return image.LoadPngFromBuffer(data.ToArray())==Error.Ok&&!image.IsEmpty()?ImageTexture.CreateFromImage(image):null;
    }
    internal static Control Create()
    {
        var panel=new VBoxContainer{Name="SharedHandLayoutEditor",SizeFlagsHorizontal=Control.SizeFlags.ExpandFill};
        var caption=new Label{Text="点击下方玩家标签选择牌区，再拖动该牌区调整位置；可调整缩放、间距和排列方式。\n“从布局开始”会切换到对应布局并载入编辑器；编辑后的调整需勾选“使用自定义布局”或保存预设后才会生效。修改自动保存，仅影响本机。",AutowrapMode=TextServer.AutowrapMode.WordSmart};
        panel.AddChild(caption);
        var canvas=new Control{Name="LayoutCanvas",CustomMinimumSize=new Vector2(640,420),MouseFilter=Control.MouseFilterEnum.Stop,SizeFlagsHorizontal=Control.SizeFlags.ExpandFill,ClipContents=true};
        panel.AddChild(canvas);
        var background=Asset("layout-background.png");
        var art=Enumerable.Range(0,4).Select(i=>Asset("layout-card-"+i+".png")).ToArray();
        int selected=0,source=Math.Clamp(SharedHandConfig.LayoutPresetTarget,0,2);bool dragging=false,updating=false;Vector2 delta=default;
        HandDesign design=LayoutDesign.Custom;
        var active=new CheckButton{Text="使用自定义布局",ButtonPressed=SharedHandConfig.HandLayout==SharedHandLayout.Custom};panel.AddChild(active);
        var title=new Label();panel.AddChild(title);
        var groupBar=new HFlowContainer{Name="LayoutPlayerGroups",SizeFlagsHorizontal=Control.SizeFlags.ExpandFill};panel.AddChild(groupBar);
        var groupHelp=new Label{Text="拖动玩家标签到另一名玩家上以合并；点击组合右上角的 × 可拆分。",AutowrapMode=TextServer.AutowrapMode.WordSmart};panel.AddChild(groupHelp);
        var scaleLabel=new Label();panel.AddChild(scaleLabel);
        var scale=new HSlider{Name="LayoutScale",MinValue=.3,MaxValue=1.2,Step=.01,CustomMinimumSize=new Vector2(0,34)};panel.AddChild(scale);
        var gapLabel=new Label();panel.AddChild(gapLabel);
        var gap=new HSlider{Name="LayoutSpacing",MinValue=24,MaxValue=180,Step=1,CustomMinimumSize=new Vector2(0,34)};panel.AddChild(gap);
        var shapes=new HBoxContainer();panel.AddChild(shapes);
        var flip=new CheckButton{Name="LayoutFlip180",Text="翻转 180°"};shapes.AddChild(flip);
        var expansion=new HBoxContainer{Name="LayoutExpansionOption",Visible=SharedHandConfig.HandExpansion};
        var expansionTitleArea=new Control{CustomMinimumSize=new Vector2(112,40),MouseFilter=Control.MouseFilterEnum.Ignore};
        var expansionTitle=new Label{Name="LayoutExpansionTitle",Text="展开模式",Position=new Vector2(0,8),MouseFilter=Control.MouseFilterEnum.Ignore};
        expansionTitle.AddThemeColorOverride("font_color",new Color(1,.76f,.2f));expansionTitleArea.AddChild(expansionTitle);expansion.AddChild(expansionTitleArea);
        var expansionToggle=new CheckButton{Name="LayoutExpansionToggle",TooltipText="将合并牌区中的玩家手牌分层展开，仅影响本机显示。"};expansion.AddChild(expansionToggle);
        var expansionDistanceRow=new VBoxContainer{Name="LayoutExpansionDistanceRow",Visible=false};panel.AddChild(expansionDistanceRow);
        var expansionDistanceText=new Label();expansionDistanceRow.AddChild(expansionDistanceText);
        var expansionDistance=new HSlider{Name="LayoutExpansionDistance",MinValue=40,MaxValue=320,Step=5,CustomMinimumSize=new Vector2(0,34)};expansionDistanceRow.AddChild(expansionDistance);
        var sourceButtons=new HBoxContainer();panel.AddChild(sourceButtons);
        var colors=new[]{new Color(.8f,.25f,.2f),new Color(.2f,.8f,.4f),new Color(.95f,.65f,.2f),new Color(.2f,.65f,1)};
        var dragLabel=new PanelContainer{Name="LayoutGroupDrag",Visible=false,MouseFilter=Control.MouseFilterEnum.Ignore,ZIndex=100,TopLevel=true};
        dragLabel.AddThemeStyleboxOverride("panel",new StyleBoxFlat{BgColor=new Color(.18f,.24f,.3f,.96f),
            BorderColor=new Color(.7f,.85f,.95f),BorderWidthLeft=2,BorderWidthRight=2,BorderWidthTop=2,BorderWidthBottom=2,
            CornerRadiusTopLeft=7,CornerRadiusTopRight=7,CornerRadiusBottomLeft=7,CornerRadiusBottomRight=7});
        canvas.AddChild(dragLabel);
        void PrepareDrag(int player)
        {
            foreach(var child in dragLabel.GetChildren()){dragLabel.RemoveChild(child);child.QueueFree();}
            var members=LayoutDesign.Members(design,player);
            var row=new HBoxContainer{MouseFilter=Control.MouseFilterEnum.Ignore};dragLabel.AddChild(row);
            for(int i=0;i<members.Length;i++)
            {
                if(i>0)row.AddChild(new Label{Text="🔗",VerticalAlignment=VerticalAlignment.Center,MouseFilter=Control.MouseFilterEnum.Ignore});
                row.AddChild(new Label{Text="P"+(members[i]+1),CustomMinimumSize=new Vector2(48,40),
                    HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,MouseFilter=Control.MouseFilterEnum.Ignore});
            }
            dragLabel.Size=new Vector2(48*members.Length+20*Math.Max(0,members.Length-1)+8,44);
        }
        void MoveDrag(Vector2 pointer)
        {
            var viewport=canvas.GetViewport().GetVisibleRect();
            var wanted=pointer+new Vector2(28,-55);
            dragLabel.GlobalPosition=new Vector2(Math.Clamp(wanted.X,0,Math.Max(0,viewport.Size.X-dragLabel.Size.X)),
                Math.Clamp(wanted.Y,0,Math.Max(0,viewport.Size.Y-dragLabel.Size.Y)));
        }
        var chips=new System.Collections.Generic.Dictionary<int,Button>();
        int dragPlayer=-1;bool groupDragging=false;Vector2 dragStart=default;
        Action rebuildGroups=()=>{};
        Rect2 Area()=>new(new Vector2(0,(canvas.Size.Y-canvas.Size.X*9/16)/2),new Vector2(canvas.Size.X,canvas.Size.X*9/16));
        void Save() { LayoutDesign.Custom=design;SharedHandConfig.EditorChanged();canvas.QueueRedraw(); }
        void Refresh()
        {
            updating=true;
            selected=Math.Clamp(selected,0,design.Zones.Length-1);int root=LayoutDesign.Root(design,selected);
            title.Text="当前生效："+(SharedHandConfig.HandLayout==SharedHandLayout.Custom?"自定义布局":"布局 "+((int)SharedHandConfig.HandLayout+1))+"　当前牌区："+string.Join(" / ",LayoutDesign.Members(design,root).Select(i=>"P"+(i+1)))+"　保存目标：布局 "+(source+1);
            var z=design.Zones[root];scale.Value=z.Scale;gap.Value=z.Spacing;
            scaleLabel.Text="卡牌缩放："+z.Scale.ToString("0.00");gapLabel.Text="卡牌间距："+z.Spacing.ToString("0")+" 像素（竖排保留完整牌名）";
            flip.ButtonPressed=z.Flip180??(z.Mirror&&SharedHandConfig.TopHandInverted);
            expansion.Visible=SharedHandConfig.HandExpansion;
            expansionToggle.ButtonPressed=z.Expand;
            expansionDistanceRow.Visible=SharedHandConfig.HandExpansion&&z.Expand;
            expansionDistance.Value=z.ExpandDistance;
            expansionDistanceText.Text="展开程度："+z.ExpandDistance.ToString("0")+" 像素";
            active.ButtonPressed=SharedHandConfig.HandLayout==SharedHandLayout.Custom;
            updating=false;canvas.QueueRedraw();
        }
        void Change(Func<HandZone,HandZone> change) { int root=LayoutDesign.Root(design,selected);design.Zones[root]=change(design.Zones[root]);Save();Refresh(); }
        active.Toggled+=v=>{if(updating)return;SharedHandConfig.HandLayout=v?SharedHandLayout.Custom:(SharedHandLayout)source;SharedHandConfig.EditorChanged();Refresh();};
        flip.Toggled+=v=>{if(!updating)Change(z=>z with{Flip180=v});};
        expansionToggle.Toggled+=v=>{if(!updating)Change(z=>z with{Expand=v});};
        expansionDistance.ValueChanged+=v=>{if(!updating)Change(z=>z with{ExpandDistance=(float)v});};
        scale.ValueChanged+=v=>{if(!updating)Change(z=>z with{Scale=(float)v});};
        gap.ValueChanged+=v=>{if(!updating)Change(z=>z with{Spacing=(float)v});};
        foreach(var (name,shape) in new[]{("扇形","Fan"),("横排","Row"),("竖排","Column")})
        {
            var button=new Button{Text=name};shapes.AddChild(button);button.Pressed+=()=>Change(z=>z with{Shape=shape});
        }
        shapes.MoveChild(flip,shapes.GetChildCount()-1);
        shapes.AddChild(expansion);
        rebuildGroups=()=>
        {
            foreach(var child in groupBar.GetChildren()){groupBar.RemoveChild(child);child.QueueFree();}chips.Clear();
            foreach(var root in Enumerable.Range(0,design.Zones.Length).Where(i=>LayoutDesign.Root(design,i)==i))
            {
                var members=LayoutDesign.Members(design,root);
                var frame=new PanelContainer{Name="PlayerGroup_"+root};
                var style=new StyleBoxFlat{BgColor=members.Length>1?new Color(.18f,.24f,.3f,.85f):new Color(.12f,.13f,.16f,.8f),
                    BorderColor=members.Length>1?new Color(.65f,.8f,.9f):new Color(.35f,.38f,.4f),BorderWidthLeft=1,BorderWidthRight=1,BorderWidthTop=1,BorderWidthBottom=1,
                    CornerRadiusTopLeft=7,CornerRadiusTopRight=7,CornerRadiusBottomLeft=7,CornerRadiusBottomRight=7};
                frame.AddThemeStyleboxOverride("panel",style);groupBar.AddChild(frame);
                var row=new HBoxContainer();frame.AddChild(row);
                for(int memberIndex=0;memberIndex<members.Length;memberIndex++)
                {
                    int index=members[memberIndex];
                    if(memberIndex>0)row.AddChild(new Label{Name="LayoutGroupLink",Text="🔗",VerticalAlignment=VerticalAlignment.Center,MouseFilter=Control.MouseFilterEnum.Ignore});
                    var chip=new Button{Name="LayoutPlayer_"+index,Text="P"+(index+1),CustomMinimumSize=new Vector2(48,40),TooltipText="拖到另一玩家上合并牌区"};
                    chips[index]=chip;row.AddChild(chip);
                    chip.GuiInput+=ev=>
                    {
                        if(ev is InputEventMouseButton {ButtonIndex:MouseButton.Left} mouse)
                        {
                            if(mouse.Pressed){dragPlayer=index;dragStart=mouse.GlobalPosition;groupDragging=false;selected=index;Refresh();}
                            else if(dragPlayer==index)
                            {
                                if(groupDragging)
                                {
                                    var target=chips.Where(p=>p.Key!=index&&GodotObject.IsInstanceValid(p.Value)&&p.Value.GetGlobalRect().HasPoint(mouse.GlobalPosition)).Select(p=>(int?)p.Key).FirstOrDefault();
                                    if(target is int other){design=LayoutDesign.Join(design,index,other);selected=index;Save();rebuildGroups();Refresh();}
                                }
                                dragPlayer=-1;groupDragging=false;dragLabel.Hide();
                            }
                        }
                        else if(ev is InputEventMouseMotion motion&&dragPlayer==index&&(motion.ButtonMask&MouseButtonMask.Left)!=0)
                        {
                            if(!groupDragging&&motion.GlobalPosition.DistanceTo(dragStart)>10)
                            {groupDragging=true;PrepareDrag(index);}
                            if(groupDragging){MoveDrag(motion.GlobalPosition);dragLabel.Show();}
                        }
                    };
                }
                if(members.Length>1)
                {
                    var split=new Button{Name="SplitGroup_"+root,Text="×",TooltipText="解除组合",CustomMinimumSize=new Vector2(26,26),SizeFlagsVertical=Control.SizeFlags.ShrinkBegin};
                    split.AddThemeColorOverride("font_color",new Color(1f,.3f,.3f));
                    split.AddThemeColorOverride("font_hover_color",new Color(1f,.55f,.55f));
                    split.AddThemeColorOverride("font_pressed_color",new Color(.85f,.15f,.15f));
                    split.AddThemeFontSizeOverride("font_size",20);row.AddChild(split);
                    split.Pressed+=()=>{design=LayoutDesign.Separate(design,root);Save();rebuildGroups();Refresh();};
                }
                // The brief scale-in provides a natural visual response to grouping.
                frame.PivotOffset=new Vector2(24,20);frame.Scale=Vector2.One*.96f;
                frame.TreeEntered+=()=>frame.CreateTween().TweenProperty(frame,"scale",Vector2.One,.16);
            }
            var plus=new Button{Name="LayoutAddPlayer",Text="+",TooltipText="增加牌区",CustomMinimumSize=new Vector2(44,44)};
            plus.AddThemeFontSizeOverride("font_size",25);groupBar.AddChild(plus);
            plus.Disabled=design.Zones.Length>=LayoutDesign.MaxZones;
            plus.Pressed+=()=>{design=LayoutDesign.Resize(design,design.Zones.Length+1);Save();rebuildGroups();Refresh();};
            var minus=new Button{Name="LayoutRemovePlayer",Text="−",TooltipText="移除最后一个牌区",CustomMinimumSize=new Vector2(44,44)};
            minus.AddThemeFontSizeOverride("font_size",25);groupBar.AddChild(minus);
            minus.Disabled=design.Zones.Length<=2;
            minus.Pressed+=()=>{design=LayoutDesign.Resize(design,design.Zones.Length-1);selected=Math.Min(selected,design.Zones.Length-1);Save();rebuildGroups();Refresh();};
        };
        for(int i=0;i<3;i++)
        {
            int index=i;var button=new Button{Text="从布局 "+(i+1)+" 开始"};sourceButtons.AddChild(button);
            button.Pressed+=()=>{source=index;SharedHandConfig.LayoutPresetTarget=index;SharedHandConfig.HandLayout=(SharedHandLayout)index;design=LayoutDesign.Editable(LayoutDesign.Preset(index));selected=0;Save();rebuildGroups();Refresh();};
        }
        var savePreset=new Button{Name="SaveLayoutPreset",Text="保存预设",TooltipText="把当前调整保存到上面选择的布局。布局 1、2、3 分别保存，重新启动后仍然保留。"};sourceButtons.AddChild(savePreset);
        savePreset.Pressed+=()=>{LayoutDesign.SavePreset(source,design);Save();Refresh();};
        var resetPresets=new Button{Name="ResetLayoutPresets",Text="恢复默认布局",TooltipText="清除布局 1、2、3 和自定义布局的调整，恢复初始摆放。其他模组配置不变。"};sourceButtons.AddChild(resetPresets);
        resetPresets.Pressed+=()=>{SharedHandConfig.SavedLayoutPresets="";design=LayoutDesign.Editable(LayoutDesign.Default(source));selected=0;Save();rebuildGroups();Refresh();};
        canvas.Draw+=()=>
        {
            var area=Area();float u=area.Size.X/1920f;
            canvas.DrawRect(new Rect2(Vector2.Zero,canvas.Size),new Color(.04f,.07f,.1f));
            if(background!=null)canvas.DrawTextureRect(background,area,false);
            for(int g=0;g<design.Zones.Length;g++)
            {
                if(LayoutDesign.Root(design,g)!=g)continue;
                var z=design.Zones[g];
                var center=area.Position+new Vector2(z.X*area.Size.X,z.Y*area.Size.Y);
                var previews=PreviewCards(design,g,SharedHandConfig.HandExpansion);
                foreach(var item in previews.OrderByDescending(p=>p.Slot.Layer))
                {
                    var slot=item.Slot;
                    var p=area.Position+slot.Center*u;
                    canvas.DrawSetTransform(p,Mathf.DegToRad(slot.Angle),Vector2.One);
                    var rect=new Rect2(new Vector2(-150,-211)*slot.Scale*u,new Vector2(300,422)*slot.Scale*u);
                    if(art[item.Owner%art.Length]!=null)canvas.DrawTextureRect(art[item.Owner%art.Length]!,rect,false);else canvas.DrawRect(rect,colors[item.Owner%colors.Length]);
                }
                canvas.DrawSetTransform(Vector2.Zero);
                var handle=new Rect2(center-new Vector2(28,13),new Vector2(56,26));
                canvas.DrawRect(handle,g==LayoutDesign.Root(design,selected)?Colors.White:colors[g%colors.Length]);
                canvas.DrawString(ThemeDB.FallbackFont,center+new Vector2(-14,5),"P"+(g+1),fontSize:15,modulate:Colors.Black);
            }
            // Repaint the native HUD strip over the preview cards, matching the
            // combat clipping option instead of covering it with the top fan.
            if(background!=null&&SharedHandConfig.TopHandOccluded)
                canvas.DrawTextureRectRegion(background,new Rect2(area.Position,new Vector2(area.Size.X,80*u)),
                    new Rect2(Vector2.Zero,new Vector2(background.GetWidth(),background.GetHeight()*80f/1080)));
        };
        canvas.GuiInput+=ev=>
        {
            var area=Area();
            if(ev is InputEventMouseButton {ButtonIndex:MouseButton.Left} mouse)
            {
                if(!mouse.Pressed){dragging=false;return;}
                var normalized=(mouse.Position-area.Position)/area.Size;
                var point=normalized*new Vector2(1920,1080);
                int root=LayoutDesign.Root(design,selected);
                if(!PreviewContains(design,root,SharedHandConfig.HandExpansion,point))return;
                delta=new Vector2(design.Zones[root].X,design.Zones[root].Y)-normalized;
                dragging=true;Refresh();canvas.AcceptEvent();
            }
            else if(ev is InputEventMouseMotion motion&&dragging)
            {
                if((motion.ButtonMask&MouseButtonMask.Left)==0){dragging=false;return;}
                var p=(motion.Position-area.Position)/area.Size+delta;
                Change(z=>z with{X=Math.Clamp(p.X,.03f,.97f),Y=Math.Clamp(p.Y,0f,1f),Mirror=p.Y<.5f});canvas.AcceptEvent();
            }
        };
        canvas.Resized+=()=>{canvas.CustomMinimumSize=new Vector2(640,canvas.Size.X*9/16);canvas.QueueRedraw();};
        Action reload=()=>{design=LayoutDesign.Custom;source=Math.Clamp(SharedHandConfig.LayoutPresetTarget,0,2);selected=0;rebuildGroups();Refresh();};
        bool wasEnabled=SharedHandConfig.HandExpansion;ulong flashStart=0;SceneTree? animationTree=null;
        void AnimateTitle()
        {
            if(!GodotObject.IsInstanceValid(expansionTitle)||!expansion.Visible)return;
            ulong now=Time.GetTicksMsec();expansionTitle.Position=new Vector2(1.2f*MathF.Sin(now*.008f),8);
            float t=flashStart==0?1:Math.Clamp((now-flashStart)/600f,0,1);
            float flash=MathF.Sin(Mathf.Pi*t);
            expansionTitle.AddThemeColorOverride("font_color",new Color(1,Mathf.Lerp(.76f,1,flash),Mathf.Lerp(.2f,1,flash)));
        }
        void CenterExpansionOption()
        {
            for(Node? n=panel.GetParent();n!=null;n=n.GetParent())
            {
                if(n is ScrollContainer scroll)
                {
                    float deltaY=expansion.GetGlobalRect().GetCenter().Y-scroll.GetGlobalRect().GetCenter().Y;
                    scroll.ScrollVertical=Math.Max(0,scroll.ScrollVertical+(int)deltaY);break;
                }
                if(n is NScrollableContainer native)
                {
                    var content=AccessTools.Field(typeof(NScrollableContainer),"_content").GetValue(native) as Control;
                    if(content?.GetParent() is not Control viewport)break;
                    float scale=Math.Max(.001f,native.GetGlobalTransformWithCanvas().Y.Length());
                    float delta=(expansion.GetGlobalRect().GetCenter().Y-viewport.GetGlobalRect().GetCenter().Y)/scale;
                    float padding=(float)AccessTools.Field(typeof(NScrollableContainer),"_paddingTop").GetValue(native)!;
                    AccessTools.Field(typeof(NScrollableContainer),"_targetDragPosY").SetValue(native,content.Position.Y-padding-delta);
                    break;
                }
            }
        }
        EventHandler changed=(_,_)=>
        {
            bool enabled=SharedHandConfig.HandExpansion;bool justEnabled=enabled&&!wasEnabled;wasEnabled=enabled;Refresh();
            if(!justEnabled||!panel.IsInsideTree())return;
            flashStart=Time.GetTicksMsec();
            var tree=panel.GetTree();int frames=0;Action? after=null;
            // The library settings screens perform deferred layout and focus
            // scrolling after the toggle. Recenter while those settle rather
            // than losing the jump to a later framework adjustment.
            after=()=>{frames++;if(!GodotObject.IsInstanceValid(panel)||!panel.IsInsideTree()){tree.ProcessFrame-=after;return;}
                if(frames>=3)CenterExpansionOption();if(frames>=18)tree.ProcessFrame-=after;};
            tree.ProcessFrame+=after;
        };
        SharedHandConfig.Instance.ConfigChanged+=changed;
        SharedHandConfig.Instance.OnConfigReloaded+=reload;
        panel.TreeEntered+=()=>{animationTree=panel.GetTree();animationTree.ProcessFrame+=AnimateTitle;};
        panel.TreeExiting+=()=>{if(animationTree!=null)animationTree.ProcessFrame-=AnimateTitle;animationTree=null;SharedHandConfig.Instance.OnConfigReloaded-=reload;SharedHandConfig.Instance.ConfigChanged-=changed;};
        rebuildGroups();Refresh();return panel;
    }
}


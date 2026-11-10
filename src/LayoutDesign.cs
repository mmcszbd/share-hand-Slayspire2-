using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Helpers;

namespace SharedHandPrototype;

public sealed record HandZone(float X=.5f,float Y=.9f,float Scale=.68f,float Spacing=80,string Shape="Fan",bool Mirror=false,bool AutoFitColumn=true,bool? Flip180=null,bool Expand=false,float ExpandDistance=145f);
public sealed record HandDesign(bool MergeBottom,bool MergeTop,HandZone[] Zones,int[]? Groups=null);
internal static class LayoutDesign
{
    internal const int MaxZones=12;
    internal static int Root(HandDesign d,int player)
    {
        if(player<0||player>=d.Zones.Length)return player;
        if(d.Groups is { } groups&&groups.Length==d.Zones.Length)
        {int root=groups[player];return root>=0&&root<groups.Length&&groups[root]==root?root:player;}
        return player==1&&d.MergeBottom?0:player==3&&d.MergeTop?2:player;
    }
    internal static int[] Roots(HandDesign d)=>Enumerable.Range(0,d.Zones.Length).Select(i=>Root(d,i)).ToArray();
    internal static int[] Members(HandDesign d,int player)
    {int root=Root(d,player);return Enumerable.Range(0,d.Zones.Length).Where(i=>Root(d,i)==root).ToArray();}
    internal static HandDesign Join(HandDesign d,int moving,int target)
    {
        if(moving<0||target<0||moving>=d.Zones.Length||target>=d.Zones.Length)return d;
        var groups=Roots(d);int from=groups[moving],to=groups[target];if(from==to)return d;
        int root=Math.Min(from,to);
        for(int i=0;i<groups.Length;i++)if(groups[i]==from||groups[i]==to)groups[i]=root;
        return d with{Groups=groups};
    }
    internal static HandDesign Separate(HandDesign d,int player)
    {
        if(player<0||player>=d.Zones.Length)return d;
        var groups=Roots(d);int root=groups[player];
        for(int i=0;i<groups.Length;i++)if(groups[i]==root)groups[i]=i;
        return d with{Groups=groups};
    }
    internal static HandDesign Resize(HandDesign d,int size)
    {
        size=Math.Clamp(size,2,MaxZones);var groups=Roots(d);
        var fallback=Default(2).Zones;
        var zones=Enumerable.Range(0,size).Select(i=>i<d.Zones.Length?d.Zones[i]:i<fallback.Length?fallback[i]:new HandZone(.5f,.5f,.56f,60,"Row")).ToArray();
        // Newly discovered extra players join the primary zone by default,
        // avoiding several unconfigured zones occupying the same center.
        // Explicitly saved extra-player group choices remain intact.
        var next=Enumerable.Range(0,size).Select(i=>i<groups.Length&&groups[i]<size?groups[i]:i>=4?0:i).ToArray();
        return d with{Zones=zones,Groups=next};
    }
    internal static HandDesign Default(int index)=>index switch
    {
        0=>new(true,true,new[]{new HandZone(.5f,1f),new HandZone(),new HandZone(.5f,80f/1080,Mirror:true),new HandZone()}),
        1=>new(false,false,new[]{new HandZone(.31f,.90f,.64f,50,"Row"),new HandZone(.675f,.90f,.64f,50,"Row"),new HandZone(.31f,.64f,.64f,50,"Row"),new HandZone(.675f,.64f,.64f,50,"Row")}),
        _=>new(true,false,new[]{new HandZone(.5f,1f,.68f,80),new HandZone(),new HandZone(.085f,.57f,.56f,39,"Column"),new HandZone(.915f,.57f,.56f,39,"Column")})
    };
    internal static HandDesign Preset(int index)
    {
        try
        {
            var presets=JsonSerializer.Deserialize<Dictionary<int,HandDesign>>(SharedHandConfig.SavedLayoutPresets);
            if(presets!=null&&presets.TryGetValue(index,out var d)&&Valid(d))return Normalize(d);
        }
        catch(JsonException) { }
        return Default(index);
    }
    private static bool Valid(HandDesign? d)=>d?.Zones?.Length is >=2 and <=MaxZones&&d.Zones.All(z=>z!=null&&float.IsFinite(z.X)&&float.IsFinite(z.Y)&&float.IsFinite(z.Scale)&&float.IsFinite(z.Spacing)&&float.IsFinite(z.ExpandDistance));
    private static HandDesign Normalize(HandDesign d)=>d with{Zones=d.Zones.Select(z=>z with{X=Math.Clamp(z.X,.03f,.97f),Y=Math.Clamp(z.Y,0f,1f),Scale=Math.Clamp(z.Scale,.3f,1.2f),Spacing=Math.Clamp(z.Spacing,24,180),ExpandDistance=Math.Clamp(z.ExpandDistance,40,320),Shape=z.Shape is "Row" or "Column"?z.Shape:"Fan"}).ToArray(),Groups=Roots(d)};
    internal static void SavePreset(int index,HandDesign design)
    {
        Dictionary<int,HandDesign>? presets=null;
        try{presets=JsonSerializer.Deserialize<Dictionary<int,HandDesign>>(SharedHandConfig.SavedLayoutPresets);}catch(JsonException) { }
        presets??=new();presets[index]=Normalize(design);
        SharedHandConfig.SavedLayoutPresets=JsonSerializer.Serialize(presets);
    }
    internal static HandDesign Custom
    {
        get
        {
            try
            {
                var d=JsonSerializer.Deserialize<HandDesign>(SharedHandConfig.CustomLayoutData);
                if(Valid(d))return Normalize(d!);
            }
            catch(JsonException) { }
            return Editable(Preset(2));
        }
        set=>SharedHandConfig.CustomLayoutData=JsonSerializer.Serialize(value);
    }
    internal static HandDesign Editable(HandDesign d)=>d with {Zones=d.Zones.Select((z,g)=>
    {
        if(z.Shape!="Column"||!z.AutoFitColumn)return z with{AutoFitColumn=false};
        var slots=Geometry(g,10,new Vector2(1920,1080),z);
        return z with{Scale=slots[0].Scale,Spacing=slots[1].Center.Y-slots[0].Center.Y,AutoFitColumn=false};
    }).ToArray()};
    internal static HandDesign Current=>SharedHandConfig.HandLayout==SharedHandLayout.Custom?Custom:Preset((int)SharedHandConfig.HandLayout);
    internal static HandDesign ForPlayers(HandDesign design,int count)
    {
        if(count>design.Zones.Length)design=Resize(design,count);
        // The untouched first preset uses two opposite fans in a two-player
        // party. Explicitly edited presets and custom groupings stay intact.
        if(count==2&&SharedHandConfig.HandLayout==SharedHandLayout.TopAndBottom&&design.Groups==null&&design.MergeBottom&&design.Zones.Length>=3)
        {
            var zones=(HandZone[])design.Zones.Clone();zones[1]=zones[2];
            var groups=Enumerable.Range(0,zones.Length).ToArray();if(design.MergeTop&&groups.Length>3)groups[3]=2;
            design=design with{Zones=zones,Groups=groups};
        }
        return design;
    }
    internal static Dictionary<CardModel,HandLayouts.Slot> Place(CardModel[][] hands,Vector2 size,HandDesign d)
    {
        if(hands.Length>d.Zones.Length)d=Resize(d,hands.Length);
        var result=new Dictionary<CardModel,HandLayouts.Slot>();
        float unit=Math.Min(size.X/1920f,size.Y/1080f);
        for(int g=0;g<hands.Length&&g<d.Zones.Length;g++)
        {
            if(Root(d,g)!=g)continue;
            var members=Enumerable.Range(0,hands.Length).Where(i=>i<d.Zones.Length&&Root(d,i)==g).ToArray();
            var cards=members.SelectMany(i=>hands[i]).ToArray();var zone=d.Zones[g];
            bool expand=SharedHandConfig.HandExpansion&&zone.Expand&&members.Length>1;
            if(!expand||!members.Skip(1).Any(i=>hands[i].Length>0))
            {
                var ordinary=Geometry(g,cards.Length,size,zone);
                for(int i=0;i<cards.Length;i++)result[cards[i]]=ordinary[i];
                continue;
            }
            var positions=Geometry(g,members.Max(i=>hands[i].Length),size,zone);
            for(int layer=0;layer<members.Length;layer++)
                for(int i=0;i<hands[members[layer]].Length;i++)
                    result[hands[members[layer]][i]]=positions[i] with{Layer=layer};
        }
        return result;
    }
    internal static List<HandLayouts.Slot> Geometry(int g,int count,Vector2 size,HandZone z)
    {
        var result=new List<HandLayouts.Slot>();
        float unit=Math.Min(size.X/1920f,size.Y/1080f);
        if (z.Shape == "Fan")
        {
            // Use the game's authored curve, including its irregular spacing.
            // Beyond ten cards interpolate that same curve instead of indexing
            // past the native table or inventing a different parabola.
            int n=Math.Min(count,10);
            for(int i=0;i<count;i++)
            {
                float index=count<=10?i:i*9f/(count-1);
                int lo=(int)Math.Floor(index),hi=Math.Min(n-1,lo+1);
                float fraction=index-lo;
                var offset=HandPosHelper.GetPosition(n,lo).Lerp(HandPosHelper.GetPosition(n,hi),fraction)*unit;
                offset.X*=z.Spacing/80f;
                if(z.Mirror)offset.Y=-offset.Y;
                float angle=Mathf.Lerp(HandPosHelper.GetAngle(n,lo),HandPosHelper.GetAngle(n,hi),fraction)*(z.Mirror?-1:1);
                if(z.Flip180??(z.Mirror&&SharedHandConfig.TopHandInverted))angle+=180;
                float fanScale=HandPosHelper.GetScale(n).X*(z.Scale/.68f)*unit;
                result.Add(new(new Vector2(z.X*size.X,z.Y*size.Y)+offset,fanScale,g,angle));
            }
            return result;
        }
        float scale=z.Scale*unit;
            float spacing=z.Spacing*unit;
            if(z.Shape=="Column")
            {
                // Full title strips remain exposed. Art may overlap. If a
                // column outgrows the screen, shrink both card and title pitch.
                spacing=Math.Max(spacing,66*scale);
                float extent=422*scale+Math.Max(0,count-1)*spacing;
                float factor=z.AutoFitColumn?Math.Min(1,size.Y*.38f/Math.Max(1,extent)):1;scale*=factor;spacing*=factor;
            }
            else spacing=Math.Min(spacing,(size.X*.64f-300*scale)/Math.Max(1,count-1));
            for(int i=0;i<count;i++)
            {
                float offset=i-(count-1)*.5f;
                float t=count<2?0:offset/((count-1)*.5f);
                float angle=(z.Shape=="Fan"?t*12*(z.Mirror?-1:1):0)+((z.Flip180??(z.Mirror&&SharedHandConfig.TopHandInverted))?180:0);
                var center=new Vector2(z.X*size.X,z.Y*size.Y);
                center+=z.Shape=="Column"?new Vector2(0,offset*spacing):new Vector2(offset*spacing,z.Shape=="Fan"?t*t*55*unit*(z.Mirror?-1:1):0);
                float radiusX=(150*Math.Abs(Mathf.Cos(Mathf.DegToRad(angle)))+211*Math.Abs(Mathf.Sin(Mathf.DegToRad(angle))))*scale;
                float radiusY=(211*Math.Abs(Mathf.Cos(Mathf.DegToRad(angle)))+150*Math.Abs(Mathf.Sin(Mathf.DegToRad(angle))))*scale;
                if(z.AutoFitColumn)center.X=Math.Clamp(center.X,radiusX+8*unit,size.X-radiusX-8*unit);
                // Bottom fans intentionally retain the native cropped lower edge.
                if(z.AutoFitColumn)center.Y=Math.Clamp(center.Y,82*unit+radiusY,size.Y+(z.Shape=="Fan"&&!z.Mirror?80*unit:-radiusY));
                result.Add(new(center,scale,g,angle));
            }

        return result;
    }

}


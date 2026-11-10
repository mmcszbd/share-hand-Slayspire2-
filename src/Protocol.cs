using System;
using System.Collections.Generic;
using System.Text.Json;

namespace SharedHandPrototype;

public enum PacketKind { Hello = 1, Session = 2, Play = 3, Result = 4 }

public sealed class Packet
{
    public int Version { get; set; } = 18;
    public PacketKind Kind { get; set; }
    public Guid Battle { get; set; }
    public ulong Sequence { get; set; }
    public uint Card { get; set; }
    public ulong Generation { get; set; }
    public ulong Holder { get; set; }
    public ulong Actor { get; set; }
    public uint? Target { get; set; }
    public ulong? Recipient { get; set; }
    public int OwnerTurn { get; set; }
    public int ActorTurn { get; set; }
    public bool Ready { get; set; }
    public bool Accepted { get; set; }
    public string Text { get; set; } = "";
}

public static class Protocol
{
    public static byte[] Encode(Packet packet) => JsonSerializer.SerializeToUtf8Bytes(packet);

    public static Packet Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0 || bytes.Length > 2048)
            throw new FormatException("Invalid packet size.");
        var packet = JsonSerializer.Deserialize<Packet>(bytes)
            ?? throw new FormatException("Missing packet.");
        if (packet.Version != 18 || !Enum.IsDefined(packet.Kind))
            throw new FormatException("Incompatible protocol.");
        return packet;
    }
}

// Consume a particular hand entry once, allowing the same physical card to be
// played again after it returns to hand. No game state is mutated by this gate.
public sealed class PlayGate
{
    private Guid _battle;
    private readonly Dictionary<ulong, ulong> _sequences = new();
    private readonly HashSet<(uint card, ulong generation)> _used = new();

    public void Reset(Guid battle)
    {
        _battle = battle;
        _sequences.Clear();
        _used.Clear();
    }

    public bool TryReserve(Guid battle, ulong sender, ulong sequence,
        uint card, ulong requestedGeneration, ulong currentGeneration, out string reason)
    {
        if (battle == Guid.Empty || battle != _battle)
            reason = "战斗已切换，请等待重新连接。";
        else if (sequence == 0 || (_sequences.TryGetValue(sender, out var last) && sequence <= last))
            reason = "重复或过期的出牌请求。";
        else if (requestedGeneration == 0 || requestedGeneration != currentGeneration)
            reason = "该牌已移动，手牌状态已过期。";
        else if (_used.Contains((card, currentGeneration)))
            reason = "这张手牌已被请求使用。";
        else
        {
            _sequences[sender] = sequence;
            _used.Add((card, currentGeneration));
            reason = "";
            return true;
        }
        return false;
    }

}








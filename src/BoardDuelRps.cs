using System;
using System.Collections.Generic;
using System.Linq;

namespace SharedHandPrototype;

// Values match the game's RelicPickingFightMove: rock, paper, scissors.
internal enum BoardDuelGesture : byte { Rock = 0, Paper = 1, Scissors = 2 }

internal sealed class BoardDuelRpsRound
{
    internal IReadOnlyList<ulong> Survivors { get; }
    internal IReadOnlyList<ulong> Eliminated { get; }
    internal ulong? Winner => Survivors.Count == 1 ? Survivors[0] : null;
    internal bool IsTie => Eliminated.Count == 0 && Survivors.Count > 1;

    internal BoardDuelRpsRound(ulong[] survivors, ulong[] eliminated)
    {
        Survivors = Array.AsReadOnly(survivors);
        Eliminated = Array.AsReadOnly(eliminated);
    }
}

internal static class BoardDuelRps
{
    // Called only after the host has collected every active player's gesture,
    // including a host-generated random choice for a timeout. Preserve input
    // order; networking and presentation must use the same authoritative data.
    internal static BoardDuelRpsRound Resolve(IReadOnlyList<ulong> participants,
        IReadOnlyDictionary<ulong, BoardDuelGesture> gestures)
    {
        if (participants == null) throw new ArgumentNullException(nameof(participants));
        if (gestures == null) throw new ArgumentNullException(nameof(gestures));
        if (participants.Count == 0) throw new ArgumentException("At least one participant is required.", nameof(participants));
        var ids = new HashSet<ulong>();
        var distinct = new HashSet<BoardDuelGesture>();
        foreach (var id in participants)
        {
            if (!ids.Add(id)) throw new ArgumentException("Duplicate participant.", nameof(participants));
            if (!gestures.TryGetValue(id, out var gesture) || (byte)gesture > 2)
                throw new ArgumentException("Each participant must have one valid gesture.", nameof(gestures));
            distinct.Add(gesture);
        }
        if (gestures.Count != ids.Count || gestures.Keys.Any(id => !ids.Contains(id)))
            throw new ArgumentException("Gestures must match the active participants exactly.", nameof(gestures));

        // Vanilla eliminates only when exactly two different gestures occur.
        // All equal, or all three present, means everyone retries.
        if (distinct.Count != 2)
            return new BoardDuelRpsRound(participants.ToArray(), Array.Empty<ulong>());
        var choices = distinct.Select(x => (int)x).ToArray();
        var losing = (BoardDuelGesture)(((choices[0] + 1) % 3 == choices[1]) ? choices[0] : choices[1]);
        return new BoardDuelRpsRound(participants.Where(id => gestures[id] != losing).ToArray(),
            participants.Where(id => gestures[id] == losing).ToArray());
    }
}

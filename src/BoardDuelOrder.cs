using System;
using System.Collections.Generic;
using System.Linq;

namespace SharedHandPrototype;

// Pure host-side turn ordering. The caller supplies an already synchronized
// initiative and advances time only through host-authoritative messages.
internal sealed class BoardDuelOrder
{
    private readonly List<ulong> _players;
    private readonly int _cardsPerVisit;
    private int _index;
    private int _played;
    internal ulong? Current => _players.Count == 0 ? null : _players[_index];
    internal int Played => _played;

    internal BoardDuelOrder(IEnumerable<ulong> initiative, int cardsPerVisit)
    {
        if (initiative == null) throw new ArgumentNullException(nameof(initiative));
        _players = initiative.ToList();
        if (_players.Any(id => id == 0) || _players.Distinct().Count() != _players.Count)
            throw new ArgumentException("Initiative must contain distinct player IDs.", nameof(initiative));
        _cardsPerVisit = Math.Clamp(cardsPerVisit, 1, 10);
    }

    internal bool CommitManualPlay(ulong actor)
    {
        if (Current != actor) return false;
        if (++_played >= _cardsPerVisit) Advance();
        return true;
    }

    internal bool Timeout(ulong expectedActor)
    {
        if (Current != expectedActor) return false;
        Advance();
        return true;
    }

    internal void Remove(ulong player)
    {
        int removed = _players.IndexOf(player);
        if (removed < 0) return;
        bool current = removed == _index;
        _players.RemoveAt(removed);
        if (_players.Count == 0) { _index = 0; _played = 0; return; }
        if (removed < _index) _index--;
        else if (_index >= _players.Count) _index = 0;
        if (current) _played = 0;
    }

    private void Advance()
    {
        _played = 0;
        if (_players.Count > 0) _index = (_index + 1) % _players.Count;
    }
}

using System.Collections.Generic;

namespace AnnW.LanMp.Protocol
{
    /// <summary>
    /// ADR-001 / ADR-004: MatchEnd is Host-authored multi-seat truth.
    /// Local UI victory is per seat / fraction — never Host EndGame(bool) for a Guest on the other team.
    /// A seat may be defeated (spectating) while its faction still wins at MatchEnd.
    /// </summary>
    public static class MatchEndRules
    {
        /// <summary>
        /// Set <see cref="SeatMatchResultDto.winner"/> from faction survival:
        /// any undefeated seat defines a winning fraction; all seats of that fraction win
        /// (including already-defeated spectating seats).
        /// </summary>
        public static int AssignFactionWinners(IList<SeatMatchResultDto> results)
        {
            var winnerFraction = -1;
            if (results == null || results.Count == 0)
                return winnerFraction;

            var winFracs = new HashSet<int>();
            for (var i = 0; i < results.Count; i++)
            {
                var r = results[i];
                if (r != null && !r.defeated)
                    winFracs.Add(r.fraction);
            }

            for (var i = 0; i < results.Count; i++)
            {
                var r = results[i];
                if (r == null)
                    continue;
                r.winner = winFracs.Contains(r.fraction);
                if (r.winner && winnerFraction < 0)
                    winnerFraction = r.fraction;
            }

            return winnerFraction;
        }

        public static SeatMatchResultDto FindLocalResult(
            MatchEndPayload end,
            int? localSeatIndex,
            string localPeerId)
        {
            if (end?.results == null)
                return null;

            if (localSeatIndex.HasValue)
            {
                for (var i = 0; i < end.results.Length; i++)
                {
                    var r = end.results[i];
                    if (r != null && r.playerIndex == localSeatIndex.Value)
                        return r;
                }
            }

            if (!string.IsNullOrEmpty(localPeerId))
            {
                for (var i = 0; i < end.results.Length; i++)
                {
                    var r = end.results[i];
                    if (r != null &&
                        !string.IsNullOrEmpty(r.ownerPeerId) &&
                        string.Equals(r.ownerPeerId, localPeerId, System.StringComparison.Ordinal))
                        return r;
                }
            }

            return null;
        }

        /// <param name="localSeatIndex">Draft / LocalHuman slot index.</param>
        /// <param name="localPeerId">This client's peer id.</param>
        /// <param name="localFraction">Local player's fraction if battle still loaded; else null.</param>
        /// <param name="allowHostVictoryFallback">
        /// True only for Host when results cannot be resolved — EndGame(bool) is Host-seat-centric.
        /// Guests must pass false.
        /// </param>
        public static bool ResolveLocalVictory(
            MatchEndPayload end,
            int? localSeatIndex,
            string localPeerId,
            int? localFraction,
            bool allowHostVictoryFallback)
        {
            if (end == null)
                return false;

            var row = FindLocalResult(end, localSeatIndex, localPeerId);
            if (row != null)
                return row.winner;

            if (end.results != null && end.results.Length > 0 && localFraction.HasValue)
            {
                // Same faction as any listed seat (allied LAN / spectate after wipe).
                for (var i = 0; i < end.results.Length; i++)
                {
                    var r = end.results[i];
                    if (r != null && r.fraction == localFraction.Value)
                        return r.winner;
                }
            }

            if (end.winnerFraction >= 0 && localFraction.HasValue)
                return localFraction.Value == end.winnerFraction;

            // Host-only last resort. Guest must never inherit Host EndGame(bool).
            if (allowHostVictoryFallback)
                return end.victory;

            return false;
        }
    }
}

using System.Collections.Generic;

namespace AnnW.LanMp.Protocol
{
    /// <summary>
    /// LAN skirmish end vs vanilla hotseat <c>SkirmishLogic.CheckSkirmishEndGame</c>.
    /// Vanilla ends the match when no <c>!is_ai</c> human remains — wrong for LAN
    /// (surrender → spectate while allied AI / remote humans still fight).
    /// LAN ends only when ≤1 living non-neutral faction remains among undefeated seats.
    /// </summary>
    public static class SkirmishEndRules
    {
        public readonly struct SeatAlive
        {
            public readonly bool Defeated;
            public readonly int Fraction;
            public readonly bool Neutral;

            public SeatAlive(bool defeated, int fraction, bool neutral)
            {
                Defeated = defeated;
                Fraction = fraction;
                Neutral = neutral;
            }
        }

        /// <summary>
        /// Returns true when the match should settle (Host may call EndGame / MatchEnd).
        /// </summary>
        public static bool ShouldSettleMatch(IList<SeatAlive> seats, out int livingFactionCount)
        {
            livingFactionCount = 0;
            if (seats == null || seats.Count == 0)
                return true;

            var fracs = new HashSet<int>();
            for (var i = 0; i < seats.Count; i++)
            {
                var s = seats[i];
                if (s.Defeated || s.Neutral)
                    continue;
                fracs.Add(s.Fraction);
            }

            livingFactionCount = fracs.Count;
            return livingFactionCount <= 1;
        }
    }
}

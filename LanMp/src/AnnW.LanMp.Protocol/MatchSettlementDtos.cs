namespace AnnW.LanMp.Protocol
{
    /// <summary>
    /// Host-authoritative battle settlement snapshot (ADR-001 / MatchEnd).
    /// Guest never accumulates <c>PlayerBattleStatics</c> / <c>turn_snaps</c> via Die/NextTurn —
    /// stamp from this attachment before vanilla <c>proc_EndGame</c>.
    /// </summary>
    public class MatchSettlementAttachment
    {
        public SeatSettlementDto[] seats;
    }

    public class SeatSettlementDto
    {
        public int playerIndex;
        public PlayerBattleStaticsDto statics;
        public TurnSnapDto[] turnSnaps;
    }

    /// <summary>Mirrors vanilla <c>PlayerBattleStatics</c> SaveOb fields (no owner ref).</summary>
    public class PlayerBattleStaticsDto
    {
        public int totalPrebuildAsset;
        public int totalLossAsset;
        public int totalMetalGet;
        public int totalPowerGet;
        public int totalResSpend;
        public int totalMetalFromMiner;
        public int totalMetalFromReclaim;
        public int totalMetalFromConvert;
        public int totalMetalLostFromCapacity;
        public int unitProduced;
        public int unitLost;
        public int unitKilled;
        public int bdCreated;
        public int bdLost;
        public int bdDestroyed;
    }

    /// <summary>Mirrors vanilla <c>TurnSnap</c> for curve + history replay minimap.</summary>
    public class TurnSnapDto
    {
        public int turn;
        public int metal;
        public int power;
        public int metalIncome;
        public int powerIncome;
        public int unitCount;
        public int totalAssetValue;
        public UnitTurnStateDto[] units;
    }

    public class UnitTurnStateDto
    {
        public int unitId;
        public int x;
        public int y;
    }

    public static class MatchSettlementCodec
    {
        public static string ToJson(MatchSettlementAttachment dto) =>
            JsonUtil.ToJson(dto ?? new MatchSettlementAttachment());

        public static MatchSettlementAttachment FromJson(string json)
        {
            if (string.IsNullOrEmpty(json))
                return null;
            return JsonUtil.FromJson<MatchSettlementAttachment>(json);
        }

        public static bool HasPayload(MatchSettlementAttachment dto)
        {
            if (dto?.seats == null || dto.seats.Length == 0)
                return false;
            foreach (var s in dto.seats)
            {
                if (s == null)
                    continue;
                if (s.statics != null)
                    return true;
                if (s.turnSnaps != null && s.turnSnaps.Length > 0)
                    return true;
            }
            return false;
        }
    }
}

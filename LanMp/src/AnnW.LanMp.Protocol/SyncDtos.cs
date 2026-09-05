namespace AnnW.LanMp.Protocol
{
    public class IntentDto
    {
        public string intentId;
        public string battleId;
        public int turn;
        public int playerIndex;
        public string kind;
        public int netUnitId;
        public int actionCate;
        public int targetX;
        public int targetY;
        public int fromX;
        public int fromY;
        public string extrasJson;
        /// <summary>False = vanilla null GameTileData (AutoSetPos for TRAIN/etc). Do NOT treat (0,0) as that.</summary>
        public bool hasTarget;
    }

    public class CommandDto
    {
        public string cmdId;
        public string sourceIntentId;
        public string battleId;
        public int turn;
        public int playerIndex;
        public string kind;
        public int netUnitId;
        public int actionCate;
        public int targetX;
        public int targetY;
        public int fromX;
        public int fromY;
        public string extrasJson;
        public string resultAttachmentJson;
        /// <summary>ADR-004 EndTurn board checkpoint (attachment-domain hash, not Save_General).</summary>
        public string stateHash;
        // CreateUnit / spawn sync
        public string templateId;
        public int createReason;
        public int ownerIndex;
        public bool building;
        public bool spawned;
        public float moveDuration;
        /// <summary>False = apply with null tile so AutoSetPos / effect-zone logic runs.</summary>
        public bool hasTarget;
        /// <summary>Host undo stack depth after this command (Guest UI; INV-T presentation).</summary>
        public int undoAvailable;
        // ADR-004 EndTurn cursor
        public int endedPlayerIndex;
        public int turnBefore;
        public int nextPlayerIndex;
        public int turnsAfter;
        public string endTurnReason;
    }

    public class IntentNackDto
    {
        public string intentId;
        public string code;
        public string message;
    }

    public class MatchAbortDto
    {
        public string reason;
        public string detail;
        public string battleId;
    }

    public class StateHashDto
    {
        public string battleId;
        public int turn;
        public int playerIndex;
        public string hash;
    }

    public class UnitSnapDto
    {
        public int unitId;
        public int ownerIndex;
        public int x;
        public int y;
        public float hpCur;
        public bool dead;
        public string templateId;
        public int createReason;
        public bool building;
        public int buildingProgress;
        public bool actioned;
        public bool moved;
        public int unitRank;
        /// <summary>Factory rally (train_pos). Only meaningful when wp_builder exists; -9999 = unset.</summary>
        public int trainPosX = -9999;
        public int trainPosY = -9999;
        public bool hasTrainPos;
    }

    public class PlayerSnapDto
    {
        public int index;
        public int metal;
        public int power;
        public bool defeated;
        public int storage;
        public int metalIncome;
        public int powerIncome;
    }

    /// <summary>Tile wreck metal puddle (Host Die → CreateWreck; Guest must not re-roll Random).</summary>
    public class WreckSnapDto
    {
        public int x;
        public int y;
        public int amount;
    }

    /// <summary>Optional Host→Guest UX hint stamped on Commands (not authoritative sim).</summary>
    public static class CommandPresentationHints
    {
        public const string UndoAvailableKey = "undoAvailable";
    }

    /// <summary>ADR-003: Host-authored authoritative deltas (no Guest re-roll).</summary>
    public class ResultAttachmentDto
    {
        public int turn;
        public int coIndex;
        public UnitSnapDto[] units;
        public PlayerSnapDto[] players;
        /// <summary>
        /// Authoritative non-zero wreck tiles. Null = legacy omit (do not touch Guest wrecks).
        /// Empty array = clear all wrecks on Guest.
        /// </summary>
        public WreckSnapDto[] wrecks;
    }

    public class SnapshotRequestDto
    {
        public string battleId;
        public int turn;
        public string reason;
    }

    public class StateSnapshotDto
    {
        public string battleId;
        public int turn;
        public int playerIndex;
        public string hashAfter;
        public ResultAttachmentDto attachment;
    }

    /// <summary>Pure helpers for attachment JSON (unit-testable).</summary>
    public static class ResultAttachmentCodec
    {
        public static string ToJson(ResultAttachmentDto dto) => JsonUtil.ToJson(dto ?? new ResultAttachmentDto());

        public static ResultAttachmentDto FromJson(string json)
        {
            if (string.IsNullOrEmpty(json))
                return null;
            return JsonUtil.FromJson<ResultAttachmentDto>(json);
        }

        public static UnitSnapDto FindUnit(ResultAttachmentDto dto, int unitId)
        {
            if (dto?.units == null)
                return null;
            foreach (var u in dto.units)
            {
                if (u != null && u.unitId == unitId)
                    return u;
            }
            return null;
        }

        public static bool HasPayload(ResultAttachmentDto dto)
        {
            if (dto == null)
                return false;
            if (dto.units != null && dto.units.Length > 0)
                return true;
            if (dto.players != null && dto.players.Length > 0)
                return true;
            // Explicit wrecks field (incl. empty = clear-all) is payload for ADR-003 wreck sync.
            return dto.wrecks != null;
        }
    }
}

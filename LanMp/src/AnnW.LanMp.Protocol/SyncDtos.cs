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

    /// <summary>Per-seat MatchEnd row (ADR-004 multi-seat; never Host-only victory bool).</summary>
    public class SeatMatchResultDto
    {
        public int playerIndex;
        public bool defeated;
        public bool winner;
        public int fraction;
        /// <summary>Lobby peer that owned this seat (Guest match when playerIndex drifts).</summary>
        public string ownerPeerId;
    }

    public class MatchEndPayload
    {
        /// <summary>Host-seat / EndGame(bool) legacy only — Guests must not treat as personal result.</summary>
        public bool victory;
        public bool victoryFlag;
        public string reason;
        public string battleId;
        public int winnerFraction = -1;
        public SeatMatchResultDto[] results;
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
        /// <summary>
        /// RankExp.exp toward next rank. -1 = legacy omit (do not overwrite Guest exp).
        /// 0 is a valid progress value.
        /// </summary>
        public float unitExp = -1f;
        /// <summary>RankExp.exp_req for current rank. -1 = legacy omit.</summary>
        public float unitExpReq = -1f;
        /// <summary>Remaining weapon reload turns (UnitData.cd). Shown on unit FUI txt_cd.</summary>
        public int cd;
        /// <summary>Reload indicator (UnitData.cding). End-turn list / FUI group_cd gate.</summary>
        public bool cding;
        /// <summary>Factory rally (train_pos). Only meaningful when wp_builder exists; -9999 = unset.</summary>
        public int trainPosX = -9999;
        public int trainPosY = -9999;
        public bool hasTrainPos;
        /// <summary>WP_Builder.bp_left. -1 = not a factory / legacy omit.</summary>
        public int factoryBpLeft = -1;
        /// <summary>
        /// WP_Shield.shd_percent (0=empty, &gt;0=active). -1 = no shield / legacy omit.
        /// Guest must rebuild FieldShield tiles via AddEffects when &gt; 0.
        /// </summary>
        public float shdPercent = -1f;

        /// <summary>
        /// Cargo / transport sync (ADR-003). Legacy omit: transporting=false, transporterUnitId=-1,
        /// cargoUnitIds=null, unloadBpLeft=-1 → Apply skips transport reconcile.
        /// </summary>
        public bool transporting;

        /// <summary>
        /// -1 = not cargo; &gt;=0 = UnitData.unit_id of transporter; -2 = player.teleport_logic
        /// (player identified by ownerIndex).
        /// </summary>
        public int transporterUnitId = -1;

        /// <summary>
        /// Cargo unit ids aboard this unit's wp_transport (non-teleporter). Null = legacy omit;
        /// empty = no cargo.
        /// </summary>
        public int[] cargoUnitIds;

        /// <summary>WP_Transport.unload_bp_left (remaining unload budget). -1 = omit.</summary>
        public int unloadBpLeft = -1;

        /// <summary>WP_Transport.unload_bp_max_base (unload capacity base). -1 = omit.</summary>
        public int unloadBpMaxBase = -1;

        /// <summary>TransportLogic.loaded_bp (current cargo weight). -1 = omit.</summary>
        public int transportLoadedBp = -1;

        /// <summary>TransportLogic.max_bp_base (load capacity base). -1 = omit.</summary>
        public int transportMaxBpBase = -1;
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
        /// <summary>Player.res_mul. 0 = legacy omit (do not overwrite on Apply).</summary>
        public float resMul;
        /// <summary>
        /// Player.teleport_logic cargo unit ids. Null = legacy omit; empty = no teleport cargo.
        /// </summary>
        public int[] teleportCargoUnitIds;
        /// <summary>Player.teleport_logic.loaded_bp. -1 = omit.</summary>
        public int teleportLoadedBp = -1;
        /// <summary>Player.teleport_logic.max_bp_base. -1 = omit.</summary>
        public int teleportMaxBpBase = -1;
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

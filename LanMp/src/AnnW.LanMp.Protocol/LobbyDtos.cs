using System;

namespace AnnW.LanMp.Protocol
{
    public enum LobbySeatState
    {
        Disabled = 0,
        Ai = 1,
        HumanStandby = 2,
        HumanSeated = 3
    }

    public enum LobbyPosMode
    {
        Fixed = 0,
        Random = 1
    }

    public enum LobbyRejectCode
    {
        Generic = 0,
        ProtocolMismatch = 1,
        BattleStarted = 2,
        RoomFull = 3,
        NoHumanSlot = 4,
        /// <summary>Legacy Phase A; Phase B uses NoHumanSlot / RoomFull.</summary>
        GuestSlotTaken = 5
    }

    public enum SeatEditNackCode
    {
        Generic = 0,
        NotAllowed = 1,
        ColorTaken = 2,
        PosTaken = 3,
        BadSeat = 4
    }

    /// <summary>One map seat mirrored for Guest UI (semantic of SGS_Player, not a clone).</summary>
    public class LobbySeatDto
    {
        public bool exist = true;
        /// <summary>Cast of <see cref="LobbySeatState"/>.</summary>
        public int state = (int)LobbySeatState.Ai;
        public string peerId = "";
        /// <summary>Cast of game PlayerControl (0 = Human). Standby/AI use AI_* ; Seated uses Human.</summary>
        public int controller;
        /// <summary>AI difficulty to restore when returning Seated → Standby.</summary>
        public int standbyController;
        public int team;
        public int color;
        /// <summary>Cast of <see cref="LobbyPosMode"/>.</summary>
        public int posMode;
        public int pos;
        public string coId = "";
        /// <summary>Human occupant display name; empty for AI / standby.</summary>
        public string occupantName = "";
        /// <summary>
        /// Economy multiplier stamped to SGS_Player.res_percent (Host-authoritative).
        /// Human: applied when &gt; 0. Custom AI: always. Preset AI: display + kept if Host switches to Custom.
        /// </summary>
        public float resPercent = 1f;
        /// <summary>
        /// Custom AI intelligence stamped to SGS_Player.ai_interlligence (Host-authoritative).
        /// Meaningful for Custom AI; preset AI seats store matching GetAIDiffIntelligence.
        /// </summary>
        public float aiIntelligence = 0.7f;
    }

    public class LobbyDraftDto
    {
        public string mapId = "";
        public string mapContentHash = "";
        public string mapDisplayName = "";
        public int fowType;
        public int winCondition;
        public int quickStart;
        public string hostPeerId = "";
        public string guestPeerId = "";
        public string hostDisplayName = "";
        public string guestDisplayName = "";
        /// <summary>Legacy helper; prefer seats[].peerId. Kept for older UI/logs.</summary>
        public int hostSlotIndex;
        public int guestSlotIndex = -1;
        public LobbySeatDto[] seats;
        public string rawNote = "";
    }

    public class ReadyPayload
    {
        public string peerId;
        public bool ready;
    }

    public class CanStartPayload
    {
        public bool canStart;
    }

    public class LobbyStartPayload
    {
        public string battleId;
        public int battleSeed;
        public LobbyDraftDto draft;
    }

    public class LobbyRejectPayload
    {
        public int code;
        public string message = "";
        public int maxHumans;
        public int onlineHumans;
        public int joinableSlots;
    }

    public class SeatEditRequest
    {
        public string requestId = "";
        public string peerId = "";
        public int seatIndex;
        public bool setState;
        public int state;
        public bool setController;
        public int controller;
        public bool setTeam;
        public int team;
        public bool setColor;
        public int color;
        public bool setPosMode;
        public int posMode;
        public bool setPos;
        public int pos;
        public bool setCoId;
        public string coId = "";
        public bool setResPercent;
        public float resPercent;
        public bool setAiIntelligence;
        public float aiIntelligence;
    }

    public class SeatEditNack
    {
        public string requestId = "";
        public int code;
        public string message = "";
    }

    public class HelloPayload
    {
        public string peerId;
        public int protocolVersion;
        public string displayName;
    }

    public class WelcomePayload
    {
        public string peerId;
        public int protocolVersion;
        public string displayName;
        public int assignedSeatIndex = -1;
    }
}

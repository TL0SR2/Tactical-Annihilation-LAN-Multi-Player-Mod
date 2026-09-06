using System;
using System.Collections.Generic;

namespace AnnW.LanMp.Protocol
{
    /// <summary>Pure seat rules for lobby (no network).</summary>
    public static class LobbySeatLogic
    {
        public const int ColorCount = 8;
        /// <summary>PlayerControl.AI_Normal — LAN draft/standby default (vanilla solo UI often starts at AI_Easy=2).</summary>
        public const int DefaultAiController = 3;

        public static LobbySeatState GetState(LobbySeatDto seat)
        {
            if (seat == null || !seat.exist)
                return LobbySeatState.Disabled;
            if (!Enum.IsDefined(typeof(LobbySeatState), seat.state))
                return LobbySeatState.Ai;
            return (LobbySeatState)seat.state;
        }

        public static LobbyPosMode GetPosMode(LobbySeatDto seat)
        {
            if (seat == null)
                return LobbyPosMode.Fixed;
            return seat.posMode == (int)LobbyPosMode.Random ? LobbyPosMode.Random : LobbyPosMode.Fixed;
        }

        public static int CountJoinable(LobbyDraftDto draft)
        {
            if (draft?.seats == null)
                return 0;
            var n = 0;
            foreach (var s in draft.seats)
            {
                if (GetState(s) == LobbySeatState.HumanStandby)
                    n++;
            }
            return n;
        }

        public static int CountSeatedHumans(LobbyDraftDto draft)
        {
            if (draft?.seats == null)
                return 0;
            var n = 0;
            foreach (var s in draft.seats)
            {
                if (GetState(s) == LobbySeatState.HumanSeated)
                    n++;
            }
            return n;
        }

        public static int FindSeatIndexByPeer(LobbyDraftDto draft, string peerId)
        {
            if (draft?.seats == null || string.IsNullOrEmpty(peerId))
                return -1;
            for (var i = 0; i < draft.seats.Length; i++)
            {
                var s = draft.seats[i];
                if (s != null && GetState(s) == LobbySeatState.HumanSeated && s.peerId == peerId)
                    return i;
            }
            return -1;
        }

        public static int FindFirstStandbyIndex(LobbyDraftDto draft)
        {
            if (draft?.seats == null)
                return -1;
            for (var i = 0; i < draft.seats.Length; i++)
            {
                if (GetState(draft.seats[i]) == LobbySeatState.HumanStandby)
                    return i;
            }
            return -1;
        }

        public static LobbySeatDto MakeAiSeat(int pos, int team, int color, string coId, int aiController)
        {
            var seat = new LobbySeatDto
            {
                exist = true,
                state = (int)LobbySeatState.Ai,
                peerId = "",
                controller = aiController,
                standbyController = aiController,
                team = team,
                color = color,
                posMode = (int)LobbyPosMode.Fixed,
                pos = pos,
                coId = coId ?? "",
                occupantName = ""
            };
            if (SkirmishSeatEconomy.IsPresetAiController(aiController))
                SkirmishSeatEconomy.ApplyPresetToSeat(seat, aiController);
            else
                SkirmishSeatEconomy.EnsureDefaults(seat);
            return seat;
        }

        public static LobbySeatDto MakeHostSeat(string peerId, string name, int pos, int team, int color, string coId)
        {
            return new LobbySeatDto
            {
                exist = true,
                state = (int)LobbySeatState.HumanSeated,
                peerId = peerId ?? "",
                controller = 0, // Human
                standbyController = DefaultAiController,
                team = team,
                color = color,
                posMode = (int)LobbyPosMode.Fixed,
                pos = pos,
                coId = coId ?? "",
                occupantName = name ?? "",
                resPercent = SkirmishSeatEconomy.DefaultResPercent,
                aiIntelligence = 0f
            };
        }

        /// <summary>Ai → HumanStandby: keep params, mark joinable, ensure AI controller.</summary>
        public static bool TryPromoteToStandby(LobbySeatDto seat, out string error)
        {
            error = null;
            if (seat == null || !seat.exist)
            {
                error = "bad seat";
                return false;
            }
            var st = GetState(seat);
            if (st == LobbySeatState.HumanSeated)
            {
                error = "occupied";
                return false;
            }
            if (st == LobbySeatState.Disabled)
            {
                error = "disabled";
                return false;
            }
            if (st == LobbySeatState.HumanStandby)
                return true;

            if (seat.standbyController <= 0)
                seat.standbyController = seat.controller > 0 ? seat.controller : DefaultAiController;
            seat.state = (int)LobbySeatState.HumanStandby;
            seat.peerId = "";
            seat.occupantName = "";
            seat.controller = seat.standbyController > 0 ? seat.standbyController : DefaultAiController;
            return true;
        }

        /// <summary>HumanStandby → Ai (no human).</summary>
        public static bool TryDemoteToAi(LobbySeatDto seat, out string error)
        {
            error = null;
            if (seat == null || !seat.exist)
            {
                error = "bad seat";
                return false;
            }
            if (GetState(seat) != LobbySeatState.HumanStandby)
            {
                error = "not standby";
                return false;
            }
            seat.state = (int)LobbySeatState.Ai;
            seat.peerId = "";
            seat.occupantName = "";
            if (seat.controller <= 0)
                seat.controller = seat.standbyController > 0 ? seat.standbyController : DefaultAiController;
            seat.standbyController = seat.controller;
            return true;
        }

        public static bool TrySeatHuman(LobbyDraftDto draft, string peerId, string displayName, out int seatIndex, out string error)
        {
            seatIndex = -1;
            error = null;
            if (draft?.seats == null || string.IsNullOrEmpty(peerId))
            {
                error = "bad args";
                return false;
            }
            if (FindSeatIndexByPeer(draft, peerId) >= 0)
            {
                seatIndex = FindSeatIndexByPeer(draft, peerId);
                return true;
            }
            seatIndex = FindFirstStandbyIndex(draft);
            if (seatIndex < 0)
            {
                error = "no standby";
                return false;
            }
            var seat = draft.seats[seatIndex];
            seat.standbyController = seat.controller > 0 ? seat.controller : DefaultAiController;
            seat.state = (int)LobbySeatState.HumanSeated;
            seat.peerId = peerId;
            seat.occupantName = displayName ?? "";
            seat.controller = 0; // Human
            RefreshLegacyGuestFields(draft);
            return true;
        }

        public static bool TryReleaseHuman(LobbyDraftDto draft, string peerId, out int seatIndex)
        {
            seatIndex = FindSeatIndexByPeer(draft, peerId);
            if (seatIndex < 0)
                return false;
            var seat = draft.seats[seatIndex];
            var ai = seat.standbyController > 0 ? seat.standbyController : DefaultAiController;
            seat.state = (int)LobbySeatState.HumanStandby;
            seat.peerId = "";
            seat.occupantName = "";
            seat.controller = ai;
            seat.standbyController = ai;
            RefreshLegacyGuestFields(draft);
            return true;
        }

        /// <summary>
        /// Legacy draft.guestPeerId / guestSlotIndex = first non-host HumanSeated (compat UI).
        /// </summary>
        public static void RefreshLegacyGuestFields(LobbyDraftDto draft)
        {
            if (draft?.seats == null)
            {
                if (draft != null)
                {
                    draft.guestPeerId = "";
                    draft.guestDisplayName = "";
                    draft.guestSlotIndex = -1;
                }
                return;
            }
            var host = draft.hostPeerId ?? "";
            for (var i = 0; i < draft.seats.Length; i++)
            {
                var s = draft.seats[i];
                if (s == null || GetState(s) != LobbySeatState.HumanSeated)
                    continue;
                if (string.IsNullOrEmpty(s.peerId) || s.peerId == host)
                    continue;
                draft.guestPeerId = s.peerId;
                draft.guestDisplayName = s.occupantName ?? "";
                draft.guestSlotIndex = i;
                return;
            }
            draft.guestPeerId = "";
            draft.guestDisplayName = "";
            draft.guestSlotIndex = -1;
        }

        public static bool IsColorTaken(LobbyDraftDto draft, int color, int exceptSeatIndex)
        {
            if (draft?.seats == null)
                return false;
            for (var i = 0; i < draft.seats.Length; i++)
            {
                if (i == exceptSeatIndex)
                    continue;
                var s = draft.seats[i];
                if (s == null || !s.exist)
                    continue;
                var st = GetState(s);
                if (st == LobbySeatState.Disabled)
                    continue;
                if (s.color == color)
                    return true;
            }
            return false;
        }

        public static bool IsPosTaken(LobbyDraftDto draft, int pos, int exceptSeatIndex)
        {
            if (draft?.seats == null)
                return false;
            for (var i = 0; i < draft.seats.Length; i++)
            {
                if (i == exceptSeatIndex)
                    continue;
                var s = draft.seats[i];
                if (s == null || !s.exist)
                    continue;
                var st = GetState(s);
                if (st == LobbySeatState.Disabled)
                    continue;
                if (GetPosMode(s) != LobbyPosMode.Fixed)
                    continue;
                if (s.pos == pos)
                    return true;
            }
            return false;
        }

        public static int NextFreeColor(LobbyDraftDto draft, int fromColor, int exceptSeatIndex)
        {
            var c = fromColor;
            for (var n = 0; n < ColorCount; n++)
            {
                c = (c + 1) % ColorCount;
                if (!IsColorTaken(draft, c, exceptSeatIndex))
                    return c;
            }
            return fromColor;
        }

        public static int NextFreePos(LobbyDraftDto draft, int fromPos, int exceptSeatIndex, int seatCount)
        {
            if (seatCount <= 0)
                return fromPos;
            var p = fromPos;
            for (var n = 0; n < seatCount; n++)
            {
                p = (p + 1) % seatCount;
                if (!IsPosTaken(draft, p, exceptSeatIndex))
                    return p;
            }
            return fromPos;
        }

        /// <summary>
        /// Host applies a seat edit. <paramref name="asHost"/> true = structural rights;
        /// otherwise only own HumanSeated preference fields.
        /// </summary>
        public static bool TryApplyEdit(
            LobbyDraftDto draft,
            SeatEditRequest req,
            bool asHost,
            string editorPeerId,
            out SeatEditNackCode nack,
            out string message)
        {
            nack = SeatEditNackCode.Generic;
            message = "";
            if (draft?.seats == null || req == null)
            {
                nack = SeatEditNackCode.BadSeat;
                message = "bad draft";
                return false;
            }
            if (req.seatIndex < 0 || req.seatIndex >= draft.seats.Length)
            {
                nack = SeatEditNackCode.BadSeat;
                message = "bad index";
                return false;
            }
            var seat = draft.seats[req.seatIndex];
            if (seat == null || !seat.exist)
            {
                nack = SeatEditNackCode.BadSeat;
                message = "missing";
                return false;
            }

            var st = GetState(seat);
            var ownsSeat = st == LobbySeatState.HumanSeated && seat.peerId == editorPeerId;

            if (req.setState)
            {
                if (!asHost)
                {
                    nack = SeatEditNackCode.NotAllowed;
                    message = "state host-only";
                    return false;
                }
                if (st == LobbySeatState.HumanSeated)
                {
                    nack = SeatEditNackCode.NotAllowed;
                    message = "cannot change occupied";
                    return false;
                }
                var target = (LobbySeatState)req.state;
                if (target == LobbySeatState.HumanStandby)
                {
                    if (!TryPromoteToStandby(seat, out var err))
                    {
                        nack = SeatEditNackCode.NotAllowed;
                        message = err;
                        return false;
                    }
                }
                else if (target == LobbySeatState.Ai)
                {
                    if (st == LobbySeatState.Ai)
                        return true;
                    if (!TryDemoteToAi(seat, out var err))
                    {
                        nack = SeatEditNackCode.NotAllowed;
                        message = err;
                        return false;
                    }
                }
                else
                {
                    nack = SeatEditNackCode.NotAllowed;
                    message = "unsupported state";
                    return false;
                }
                st = GetState(seat);
            }

            var hostMayTuneAi = asHost && (st == LobbySeatState.Ai || st == LobbySeatState.HumanStandby);
            var humanMayTune = ownsSeat;

            if (req.setController)
            {
                if (!hostMayTuneAi)
                {
                    nack = SeatEditNackCode.NotAllowed;
                    message = "controller";
                    return false;
                }
                if (req.controller <= 0)
                {
                    nack = SeatEditNackCode.NotAllowed;
                    message = "ai only";
                    return false;
                }
                if (req.controller > SkirmishSeatEconomy.ControllerCustom)
                {
                    nack = SeatEditNackCode.NotAllowed;
                    message = "bad controller";
                    return false;
                }
                seat.controller = req.controller;
                seat.standbyController = req.controller;
                if (SkirmishSeatEconomy.IsPresetAiController(req.controller))
                    SkirmishSeatEconomy.ApplyPresetToSeat(seat, req.controller);
                else if (SkirmishSeatEconomy.IsCustomController(req.controller))
                    SkirmishSeatEconomy.EnsureDefaults(seat);
            }

            // Economy + custom AI intel: Host-only for every seat (including seated humans).
            if (req.setResPercent)
            {
                if (!asHost)
                {
                    nack = SeatEditNackCode.NotAllowed;
                    message = "resPercent host-only";
                    return false;
                }
                seat.resPercent = req.resPercent > 0f
                    ? req.resPercent
                    : SkirmishSeatEconomy.DefaultResPercent;
                // Editing eco on a preset AI slot promotes to Custom so SetupForSkirmish applies SGS values.
                if (st == LobbySeatState.Ai || st == LobbySeatState.HumanStandby)
                {
                    if (SkirmishSeatEconomy.IsPresetAiController(seat.controller))
                    {
                        seat.controller = SkirmishSeatEconomy.ControllerCustom;
                        seat.standbyController = SkirmishSeatEconomy.ControllerCustom;
                        if (seat.aiIntelligence <= 0f)
                            seat.aiIntelligence = SkirmishSeatEconomy.DefaultAiIntelligence;
                    }
                }
            }

            if (req.setAiIntelligence)
            {
                if (!asHost)
                {
                    nack = SeatEditNackCode.NotAllowed;
                    message = "aiIntelligence host-only";
                    return false;
                }
                if (st != LobbySeatState.Ai && st != LobbySeatState.HumanStandby)
                {
                    nack = SeatEditNackCode.NotAllowed;
                    message = "aiIntelligence ai-only";
                    return false;
                }
                seat.aiIntelligence = req.aiIntelligence > 0f
                    ? req.aiIntelligence
                    : SkirmishSeatEconomy.DefaultAiIntelligence;
                if (SkirmishSeatEconomy.IsPresetAiController(seat.controller) ||
                    seat.controller <= 0)
                {
                    seat.controller = SkirmishSeatEconomy.ControllerCustom;
                    seat.standbyController = SkirmishSeatEconomy.ControllerCustom;
                }
            }

            if (req.setTeam)
            {
                if (!(hostMayTuneAi || humanMayTune))
                {
                    nack = SeatEditNackCode.NotAllowed;
                    message = "team";
                    return false;
                }
                seat.team = req.team;
            }

            if (req.setColor)
            {
                if (!(hostMayTuneAi || humanMayTune))
                {
                    nack = SeatEditNackCode.NotAllowed;
                    message = "color";
                    return false;
                }
                if (IsColorTaken(draft, req.color, req.seatIndex))
                {
                    nack = SeatEditNackCode.ColorTaken;
                    message = "color taken";
                    return false;
                }
                seat.color = req.color;
            }

            if (req.setPosMode)
            {
                if (!(hostMayTuneAi || humanMayTune))
                {
                    nack = SeatEditNackCode.NotAllowed;
                    message = "posMode";
                    return false;
                }
                seat.posMode = req.posMode;
            }

            if (req.setPos)
            {
                if (!(hostMayTuneAi || humanMayTune))
                {
                    nack = SeatEditNackCode.NotAllowed;
                    message = "pos";
                    return false;
                }
                if (GetPosMode(seat) == LobbyPosMode.Random && !req.setPosMode)
                {
                    // switching to fixed implied
                    seat.posMode = (int)LobbyPosMode.Fixed;
                }
                if (seat.posMode != (int)LobbyPosMode.Random && IsPosTaken(draft, req.pos, req.seatIndex))
                {
                    nack = SeatEditNackCode.PosTaken;
                    message = "pos taken";
                    return false;
                }
                seat.pos = req.pos;
            }

            if (req.setCoId)
            {
                if (!(hostMayTuneAi || humanMayTune))
                {
                    nack = SeatEditNackCode.NotAllowed;
                    message = "co";
                    return false;
                }
                seat.coId = req.coId ?? "";
            }

            return true;
        }

        /// <summary>Assign Random positions and empty CO ids using seed. Mutates draft in place.</summary>
        public static void BakeForStart(LobbyDraftDto draft, int battleSeed, IList<string> coPool)
        {
            if (draft?.seats == null)
                return;
            var rng = new Random(battleSeed);
            var seatCount = draft.seats.Length;
            var taken = new HashSet<int>();
            foreach (var s in draft.seats)
            {
                if (s == null || !s.exist || GetState(s) == LobbySeatState.Disabled)
                    continue;
                if (GetPosMode(s) == LobbyPosMode.Fixed)
                    taken.Add(s.pos);
            }

            var free = new List<int>();
            for (var p = 0; p < seatCount; p++)
            {
                if (!taken.Contains(p))
                    free.Add(p);
            }
            // Fisher-Yates
            for (var i = free.Count - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                var tmp = free[i];
                free[i] = free[j];
                free[j] = tmp;
            }

            var freeIdx = 0;
            foreach (var s in draft.seats)
            {
                if (s == null || !s.exist || GetState(s) == LobbySeatState.Disabled)
                    continue;
                if (GetPosMode(s) == LobbyPosMode.Random)
                {
                    if (freeIdx < free.Count)
                    {
                        s.pos = free[freeIdx++];
                        s.posMode = (int)LobbyPosMode.Fixed;
                    }
                }

                if (string.IsNullOrEmpty(s.coId) && coPool != null && coPool.Count > 0)
                    s.coId = coPool[rng.Next(coPool.Count)];
            }
        }

        public static string RejectMessage(LobbyRejectCode code)
        {
            switch (code)
            {
                case LobbyRejectCode.ProtocolMismatch:
                    return "协议版本不匹配";
                case LobbyRejectCode.BattleStarted:
                    return "对局已开始，无法加入";
                case LobbyRejectCode.RoomFull:
                    return "房间人类位已满";
                case LobbyRejectCode.NoHumanSlot:
                    return "房主尚未开放人类位（请等待房主将槽位切为「人类位·暂 AI」）";
                case LobbyRejectCode.GuestSlotTaken:
                    // Legacy Phase A code; Phase B paths use NoHumanSlot / RoomFull.
                    return "房间人类位已满";
                default:
                    return "无法加入房间";
            }
        }
    }
}

using System;
using System.Collections.Generic;
using ANNW;
using AnnW.LanMp.Protocol;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace AnnW.LanMp.Ui
{
    /// <summary>
    /// Seat table using vanilla-sized dropdown clones when available.
    /// Columns: 类型 | 指挥官 | 难度 | 经济 | AI智 | 队伍 | 颜色 | 位置
    /// Eco + custom AI intel are Host-authoritative (game skirmish update).
    /// </summary>
    internal static class LanRoomSeats
    {
        private const float ColKind = 1.35f;
        private const float ColCo = 1.25f;
        private const float ColDiff = 1.05f;
        private const float ColEco = 0.85f;
        private const float ColIntel = 0.85f;
        private const float ColTeam = 0.85f;
        private const float ColColor = 0.85f;
        private const float ColPos = 0.95f;
        private const int PosRandomId = -1;

        public static void Rebuild(
            RectTransform listRoot,
            LobbyDraftDto draft,
            string localPeerId,
            bool isHost,
            Func<string, bool> peerReady,
            Action onChanged)
        {
            LanDropMenu.CloseOpen();
            if (listRoot == null)
                return;
            foreach (Transform c in listRoot)
                UnityEngine.Object.Destroy(c.gameObject);

            if (draft?.seats == null)
                return;

            AnnwUiKit.EnsureSampled();
            LanSeatCell.SuppressCallbacks = true;
            try
            {
                BuildHeader(listRoot);
                var seatCount = draft.seats.Length;
                var rowH = AnnwUiKit.SeatRowHeight;

                for (var i = 0; i < seatCount; i++)
                {
                    var seat = draft.seats[i];
                    if (seat == null || !seat.exist)
                        continue;
                    var st = LobbySeatLogic.GetState(seat);
                    if (st == LobbySeatState.Disabled)
                        continue;

                    var idx = i;
                    var row = CreateRow(listRoot, "Seat" + i, rowH);
                    var owns = st == LobbySeatState.HumanSeated && seat.peerId == localPeerId;
                    var hostTunesAi = isHost && (st == LobbySeatState.Ai || st == LobbySeatState.HumanStandby);
                    var prefEditable = owns || hostTunesAi;

                    if (isHost && st != LobbySeatState.HumanSeated)
                    {
                        LanSeatCell.AddDropdown(row, "Kind", ColKind, new[]
                        {
                            new LanDropMenu.Option("AI", (int)LobbySeatState.Ai),
                            new LanDropMenu.Option("人类位·暂AI", (int)LobbySeatState.HumanStandby)
                        }, (int)st, id =>
                        {
                            LanMpPlugin.Instance?.Lobby.RequestSeatEdit(new SeatEditRequest
                            {
                                seatIndex = idx,
                                setState = true,
                                state = id
                            });
                            onChanged?.Invoke();
                        }, true);
                    }
                    else
                    {
                        var ready = st == LobbySeatState.HumanSeated
                                    && !string.IsNullOrEmpty(seat.peerId)
                                    && peerReady != null
                                    && peerReady(seat.peerId);
                        LanSeatCell.AddStatic(row, "Kind", ColKind, KindLabel(st, seat, ready));
                    }

                    LanSeatCell.AddCoButton(row, ColCo, CoLabel(seat), prefEditable, () =>
                    {
                        if (!prefEditable) return;
                        OpenCoSelect(chosen =>
                        {
                            LanMpPlugin.Instance?.Lobby.RequestSeatEdit(new SeatEditRequest
                            {
                                seatIndex = idx,
                                setCoId = true,
                                coId = chosen
                            });
                            onChanged?.Invoke();
                        });
                    });

                    if (st == LobbySeatState.Ai || st == LobbySeatState.HumanStandby)
                    {
                        var diffOpts = BuildDiffOptions();
                        var cur = seat.controller;
                        if (cur < (int)PlayerControl.AI_Beginner)
                            cur = (int)PlayerControl.AI_Normal;
                        LanSeatCell.AddDropdown(row, "Diff", ColDiff, diffOpts, cur, id =>
                        {
                            LanMpPlugin.Instance?.Lobby.RequestSeatEdit(new SeatEditRequest
                            {
                                seatIndex = idx,
                                setController = true,
                                controller = id
                            });
                            onChanged?.Invoke();
                        }, hostTunesAi);
                    }
                    else
                    {
                        LanSeatCell.AddStatic(row, "Diff", ColDiff, "—");
                    }

                    // Economy multiplier — Host only (all seats).
                    {
                        var ecoOpts = BuildResMulOptions();
                        var resTable = GameCompatProbe.ResMulOptionsLive();
                        var ecoId = GameCompatProbe.IndexOfNearest(
                            resTable,
                            seat.resPercent > 0f ? seat.resPercent : SkirmishSeatEconomy.DefaultResPercent);
                        LanSeatCell.AddDropdown(row, "Eco", ColEco, ecoOpts, ecoId, id =>
                        {
                            LanMpPlugin.Instance?.Lobby.RequestSeatEdit(new SeatEditRequest
                            {
                                seatIndex = idx,
                                setResPercent = true,
                                resPercent = GameCompatProbe.ValueAt(
                                    resTable, id, SkirmishSeatEconomy.DefaultResPercent)
                            });
                            onChanged?.Invoke();
                        }, isHost);
                    }

                    // Custom AI intelligence — Host only; AI / standby seats.
                    if (st == LobbySeatState.Ai || st == LobbySeatState.HumanStandby)
                    {
                        var intelOpts = BuildAiIntelOptions();
                        var intelTable = GameCompatProbe.AiIntelOptionsLive();
                        var intelVal = seat.aiIntelligence > 0f
                            ? seat.aiIntelligence
                            : SkirmishSeatEconomy.DefaultAiIntelligence;
                        var intelId = GameCompatProbe.IndexOfNearest(intelTable, intelVal);
                        LanSeatCell.AddDropdown(row, "Intel", ColIntel, intelOpts, intelId, id =>
                        {
                            LanMpPlugin.Instance?.Lobby.RequestSeatEdit(new SeatEditRequest
                            {
                                seatIndex = idx,
                                setAiIntelligence = true,
                                aiIntelligence = GameCompatProbe.ValueAt(
                                    intelTable, id, SkirmishSeatEconomy.DefaultAiIntelligence)
                            });
                            onChanged?.Invoke();
                        }, isHost);
                    }
                    else
                    {
                        LanSeatCell.AddStatic(row, "Intel", ColIntel, "—");
                    }

                    var teamOpts = new List<LanDropMenu.Option>();
                    // Fraction.TEAM1..TEAM8 (0..7); NEUTRAL/NONE are not skirmish seat teams.
                    for (var t = 0; t < 8; t++)
                        teamOpts.Add(new LanDropMenu.Option(TeamLabel(t), t));
                    LanSeatCell.AddDropdown(row, "Team", ColTeam, teamOpts, seat.team, id =>
                    {
                        LanMpPlugin.Instance?.Lobby.RequestSeatEdit(new SeatEditRequest
                        {
                            seatIndex = idx,
                            setTeam = true,
                            team = id
                        });
                        onChanged?.Invoke();
                    }, prefEditable);

                    var colorOpts = new List<LanDropMenu.Option>();
                    for (var c = 0; c < LobbySeatLogic.ColorCount; c++)
                    {
                        if (c != seat.color && LobbySeatLogic.IsColorTaken(draft, c, idx))
                            continue;
                        colorOpts.Add(new LanDropMenu.Option(ColorLabel(c), c));
                    }
                    if (colorOpts.Count == 0)
                        colorOpts.Add(new LanDropMenu.Option(ColorLabel(seat.color), seat.color));
                    LanSeatCell.AddDropdown(row, "Color", ColColor, colorOpts, seat.color, id =>
                    {
                        LanMpPlugin.Instance?.Lobby.RequestSeatEdit(new SeatEditRequest
                        {
                            seatIndex = idx,
                            setColor = true,
                            color = id
                        });
                        onChanged?.Invoke();
                    }, prefEditable);

                    var posOpts = BuildPosOptions(draft, idx, seatCount);
                    var posId = LobbySeatLogic.GetPosMode(seat) == LobbyPosMode.Random ? PosRandomId : seat.pos;
                    LanSeatCell.AddDropdown(row, "Pos", ColPos, posOpts, posId, id =>
                    {
                        if (id == PosRandomId)
                        {
                            LanMpPlugin.Instance?.Lobby.RequestSeatEdit(new SeatEditRequest
                            {
                                seatIndex = idx,
                                setPosMode = true,
                                posMode = (int)LobbyPosMode.Random
                            });
                        }
                        else
                        {
                            LanMpPlugin.Instance?.Lobby.RequestSeatEdit(new SeatEditRequest
                            {
                                seatIndex = idx,
                                setPosMode = true,
                                posMode = (int)LobbyPosMode.Fixed,
                                setPos = true,
                                pos = id
                            });
                        }
                        onChanged?.Invoke();
                    }, prefEditable);
                }
            }
            finally
            {
                LanSeatCell.SuppressCallbacks = false;
            }
        }

        private static List<LanDropMenu.Option> BuildPosOptions(LobbyDraftDto draft, int seatIndex, int seatCount)
        {
            var list = new List<LanDropMenu.Option> { new LanDropMenu.Option("随机", PosRandomId) };
            for (var p = 0; p < seatCount; p++)
            {
                if (LobbySeatLogic.IsPosTaken(draft, p, seatIndex))
                    continue;
                list.Add(new LanDropMenu.Option(PosLabel(p), p));
            }
            var seat = draft.seats[seatIndex];
            if (LobbySeatLogic.GetPosMode(seat) == LobbyPosMode.Fixed)
            {
                var found = false;
                foreach (var o in list)
                    if (o.Id == seat.pos) { found = true; break; }
                if (!found)
                    list.Add(new LanDropMenu.Option(PosLabel(seat.pos), seat.pos));
            }
            return list;
        }

        private static List<LanDropMenu.Option> BuildDiffOptions()
        {
            var list = new List<LanDropMenu.Option>();
            for (var c = (int)PlayerControl.AI_Beginner; c < (int)PlayerControl.num; c++)
                list.Add(new LanDropMenu.Option(ControlLabel(c), c));
            return list;
        }

        private static List<LanDropMenu.Option> BuildResMulOptions()
        {
            var list = new List<LanDropMenu.Option>();
            var opts = GameCompatProbe.ResMulOptionsLive();
            for (var i = 0; i < opts.Length; i++)
            {
                var v = opts[i];
                list.Add(new LanDropMenu.Option(FormatMul(v), i));
            }
            return list;
        }

        private static List<LanDropMenu.Option> BuildAiIntelOptions()
        {
            var list = new List<LanDropMenu.Option>();
            var opts = GameCompatProbe.AiIntelOptionsLive();
            for (var i = 0; i < opts.Length; i++)
            {
                var v = opts[i];
                list.Add(new LanDropMenu.Option(FormatMul(v), i));
            }
            return list;
        }

        private static string FormatMul(float v)
        {
            if (Math.Abs(v - 1f) < 0.001f)
                return "×1";
            if (Math.Abs(v * 10f - Math.Round(v * 10f)) < 0.001f)
                return "×" + v.ToString("0.#");
            return "×" + v.ToString("0.##");
        }

        private static void BuildHeader(RectTransform listRoot)
        {
            var row = CreateRow(listRoot, "Header", 28f);
            var img = row.GetComponent<Image>();
            if (img != null)
                img.color = new Color(0.12f, 0.07f, 0.03f, 0.9f);
            LanSeatCell.AddStatic(row, "H0", ColKind, "类型");
            LanSeatCell.AddStatic(row, "H1", ColCo, "指挥官");
            LanSeatCell.AddStatic(row, "H2", ColDiff, "难度");
            LanSeatCell.AddStatic(row, "H3", ColEco, "经济");
            LanSeatCell.AddStatic(row, "H4", ColIntel, "AI智");
            LanSeatCell.AddStatic(row, "H5", ColTeam, "队伍");
            LanSeatCell.AddStatic(row, "H6", ColColor, "颜色");
            LanSeatCell.AddStatic(row, "H7", ColPos, "位置");
        }

        private static RectTransform CreateRow(RectTransform parent, string name, float rowH)
        {
            var row = AnnwUiKit.CreateRect(name, parent);
            var le = row.gameObject.AddComponent<LayoutElement>();
            le.minHeight = rowH;
            le.preferredHeight = rowH;
            le.flexibleWidth = 1f;
            // No full-row chrome — only the cells themselves (no底槽).
            var h = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            h.spacing = 6f;
            h.padding = new RectOffset(0, 0, 0, 0);
            h.childAlignment = TextAnchor.MiddleCenter;
            h.childControlWidth = true;
            h.childControlHeight = true;
            h.childForceExpandWidth = true;
            h.childForceExpandHeight = true;
            return row;
        }

        /// <summary>
        /// Pick CO id without mutating Lobby.Draft (Guest must not write authority draft).
        /// </summary>
        private static void OpenCoSelect(Action<string> onPicked)
        {
            try
            {
                var floater = UI_Floater.self;
                if (floater == null || floater.co_select == null)
                {
                    onPicked?.Invoke(NextCoFallback(null));
                    return;
                }

                LanDropMenu.CloseOpen();
                LanDropMenu.BringFloaterPopupToFront(floater.co_select);
                floater.co_select.ShowForSkirmish(result =>
                {
                    var item = result.co_item;
                    string id;
                    if (item.sd_co == null)
                        id = item.is_random ? "" : "__none__";
                    else
                        id = item.sd_co.name;
                    onPicked?.Invoke(id);
                });
                LanDropMenu.BringFloaterPopupToFront(floater.co_select);
            }
            catch (Exception ex)
            {
                LanMpPlugin.Log?.LogWarning("[RoomUI] CO select failed: " + ex.Message);
                onPicked?.Invoke(NextCoFallback(null));
            }
        }

        private static string NextCoFallback(string current)
        {
            var names = ListCoNames();
            if (string.IsNullOrEmpty(current) || current == "__none__")
                return names.Count > 0 ? names[0] : "";
            var i = names.IndexOf(current);
            return (i < 0 || i >= names.Count - 1) ? "" : names[i + 1];
        }

        private static List<string> ListCoNames()
        {
            var list = new List<string>();
            try
            {
                foreach (var kv in SDBase<SD_ANNW_CO>.dic)
                    if (kv.Value != null)
                        list.Add(kv.Key);
            }
            catch { /* ignore */ }
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        private static string KindLabel(LobbySeatState st, LobbySeatDto seat, bool ready)
        {
            switch (st)
            {
                case LobbySeatState.Ai: return "AI";
                case LobbySeatState.HumanStandby: return "人类位·暂AI";
                case LobbySeatState.HumanSeated:
                {
                    var name = string.IsNullOrEmpty(seat?.occupantName) ? "玩家" : seat.occupantName;
                    return ready ? "人类 · " + name + " ✓" : "人类 · " + name;
                }
                default: return "关闭";
            }
        }

        private static string CoLabel(LobbySeatDto seat)
        {
            if (seat == null || seat.coId == "__none__")
                return LanGet("UI_NoCO", "无指挥官");
            if (string.IsNullOrEmpty(seat.coId))
                return LanGet("UI_RdCO", "随机指挥官");
            try
            {
                var n = typeof(LAN).GetMethod("GetCOName", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                    ?.Invoke(null, new object[] { seat.coId }) as string;
                return string.IsNullOrEmpty(n) ? seat.coId : n;
            }
            catch { return seat.coId; }
        }

        private static string ControlLabel(int controller)
        {
            try
            {
                // PlayerControl.Custom localizes under PlayerName.AI_Custom (game update).
                var name = controller == (int)PlayerControl.Custom
                    ? "AI_Custom"
                    : ((PlayerControl)controller).ToString();
                var t = LAN.Get("PlayerName", name);
                return string.IsNullOrEmpty(t) ? name : t;
            }
            catch { return controller.ToString(); }
        }

        private static string TeamLabel(int team)
        {
            try { return LAN.Get("UI_Team") + " " + (team + 1); }
            catch { return "队伍 " + (team + 1); }
        }

        private static string ColorLabel(int color)
        {
            try
            {
                var key = ((COColor)color).ToString();
                var t = LAN.Get("COLOR", key);
                return string.IsNullOrEmpty(t) ? key : t;
            }
            catch { return "色" + color; }
        }

        private static string PosLabel(int pos)
        {
            try { return LAN.Get("UI_SkirmishPos") + (pos + 1); }
            catch { return "位置" + (pos + 1); }
        }

        private static string LanGet(string key, string fallback)
        {
            try
            {
                var t = LAN.Get(key);
                return string.IsNullOrEmpty(t) ? fallback : t;
            }
            catch { return fallback; }
        }
    }
}

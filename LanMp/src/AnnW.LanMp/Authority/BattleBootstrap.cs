using System;
using System.Collections.Generic;
using System.IO;
using AnnW.LanMp.Protocol;
using BepInEx.Logging;
using UnityEngine;

namespace AnnW.LanMp.Authority
{
    /// <summary>Builds real <see cref="StartGameSetting"/> before LoadScene (M01/M03).</summary>
    public static class BattleBootstrap
    {
        public static bool TryApplyLobbyStart(LobbyStartPayload payload, NetSession net, ManualLogSource log)
        {
            if (payload == null || payload.draft == null)
            {
                log.LogError("[Bootstrap] LobbyStart payload/draft null");
                return false;
            }

            try
            {
                UnityEngine.Random.InitState(payload.battleSeed);
                var sgs = BuildStartGameSetting(payload.draft, log);
                if (sgs == null)
                    return false;

                SS_ANNW_Game.start_game_setting = sgs;
                log.LogInfo($"[Bootstrap] start_game_setting ready map={sgs.filename} players={sgs.players?.Count} seed={payload.battleSeed} battleId={payload.battleId}");
                if (sgs.players != null)
                {
                    for (var i = 0; i < sgs.players.Count; i++)
                    {
                        var p = sgs.players[i];
                        if (p == null || !p.exist)
                            continue;
                        log.LogInfo(
                            $"[Bootstrap] SGS[{i}] ctrl={p.controller} res_percent={p.res_percent:0.###} " +
                            $"ai_intel={p.ai_interlligence:0.###} role={net?.Role}");
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                log.LogError("[Bootstrap] TryApplyLobbyStart failed: " + ex);
                return false;
            }
        }

        public static StartGameSetting BuildStartGameSetting(LobbyDraftDto draft, ManualLogSource log)
        {
            var sgs = new StartGameSetting
            {
                game_type = GameType.SKIRMISH,
                is_new = true,
                fow_type = (FOW_Type)draft.fowType,
                win_condition = (SkirmishWinCondition)draft.winCondition,
                quick_start = (QuickStartSetting)draft.quickStart,
                players = new List<SGS_Player>()
            };

            DynOb ob = null;
            var mapKey = draft.mapId?.Trim() ?? "";
            if (string.IsNullOrEmpty(mapKey))
            {
                log.LogError("[Bootstrap] mapId empty");
                return null;
            }

            // 1) Resources TextAsset (e.g. Skirmish/MapName — game update dropped pack folder)
            var ta = Resources.Load<TextAsset>(mapKey);
            if (ta == null && !mapKey.StartsWith("Skirmish/", StringComparison.OrdinalIgnoreCase))
            {
                // Try resolve via SD_ANNW_SK_MAP name → Skirmish/{name}
                try
                {
                    var sd = SDBase<SD_ANNW_SK_MAP>.Get(mapKey);
                    if (sd != null)
                    {
                        // Game update: built-in path is Skirmish/{sd.name} (pack field removed).
                        var path = "Skirmish/" + sd.name;
                        ta = Resources.Load<TextAsset>(path);
                        if (ta != null)
                            mapKey = path;
                    }
                }
                catch (Exception ex)
                {
                    log.LogWarning("[Bootstrap] SD_ANNW_SK_MAP resolve failed: " + ex.Message);
                }
            }

            if (ta != null)
            {
                ob = Singleton<BattleAndMapFileSystem>.self.ReadFileWithMeta_Asset(ta.text);
                sgs.ob_file = ob;
                sgs.filename = ta.name;
                draft.mapId = mapKey;
                draft.mapContentHash = HashUtil.StableHash16(ta.text);
            }
            else if (File.Exists(mapKey))
            {
                ob = Singleton<BattleAndMapFileSystem>.self.ReadFileWithMeta_Local(mapKey);
                sgs.file_path = mapKey;
                sgs.filename = Path.GetFileNameWithoutExtension(mapKey);
                sgs.ob_file = ob;
                draft.mapContentHash = HashUtil.StableHash16(File.ReadAllText(mapKey));
            }
            else
            {
                log.LogError("[Bootstrap] Map not found as Resources or file: " + mapKey);
                log.LogError("[Bootstrap] Tip: use SD map name or Resources path like Skirmish/<name>");
                return null;
            }

            if (ob == null)
            {
                log.LogError("[Bootstrap] Map DynOb null");
                return null;
            }

            var preview = new AllPlayer();
            preview.LoadOb(ob.GetKey_Obj("commander"), preview: true);
            if (preview.players == null || preview.players.Count == 0)
            {
                log.LogError("[Bootstrap] Map has no players in commander block");
                return null;
            }

            for (var i = 0; i < preview.players.Count; i++)
            {
                var src = preview.players[i];
                var p = new SGS_Player
                {
                    exist = true,
                    pos_ind = i,
                    team = src.fraction,
                    color = src.co_color,
                    sd_co = src.co_data != null && src.co_data.sd_commander != null
                        ? src.co_data.sd_commander.name
                        : "",
                    ps_list = new List<SD_ANNW_PS>(),
                    skill = null
                };

                if (draft.seats != null && i < draft.seats.Length && draft.seats[i] != null)
                {
                    var seat = draft.seats[i];
                    var st = LobbySeatLogic.GetState(seat);
                    if (st == LobbySeatState.Disabled || !seat.exist)
                    {
                        p.exist = false;
                        sgs.players.Add(p);
                        continue;
                    }
                    p.exist = true;
                    p.team = (Fraction)seat.team;
                    p.color = (COColor)seat.color;
                    p.pos_ind = seat.pos;
                    if (!string.IsNullOrEmpty(seat.coId) && seat.coId != "__none__")
                        p.sd_co = seat.coId;
                    else
                        p.sd_co = "";
                    try
                    {
                        var human = st == LobbySeatState.HumanSeated;
                        var stamp = SkirmishSeatEconomy.ResolveForStart(seat, human);
                        p.controller = (PlayerControl)stamp.controller;
                        p.res_percent = stamp.resPercent;
                        p.ai_interlligence = stamp.aiIntelligence;
                        log?.LogInfo(
                            $"[Bootstrap] seat[{i}] st={st} ctrl={stamp.controller} " +
                            $"res={stamp.resPercent:0.###} intel={stamp.aiIntelligence:0.###} " +
                            $"(draft ctrl={seat.controller} res={seat.resPercent:0.###} intel={seat.aiIntelligence:0.###})");
                    }
                    catch (Exception ex)
                    {
                        log?.LogWarning("[Bootstrap] seat economy stamp failed: " + ex.Message);
                        p.controller = st == LobbySeatState.HumanSeated
                            ? PlayerControl.Human
                            : PlayerControl.AI_Normal;
                        p.res_percent = SkirmishSeatEconomy.DefaultResPercent;
                        p.ai_interlligence = st == LobbySeatState.HumanSeated
                            ? 0f
                            : SkirmishSeatEconomy.DefaultAiIntelligence;
                    }
                }
                else if (i == draft.hostSlotIndex || (draft.guestSlotIndex >= 0 && i == draft.guestSlotIndex))
                {
                    p.controller = PlayerControl.Human;
                    p.res_percent = SkirmishSeatEconomy.DefaultResPercent;
                    p.ai_interlligence = 0f;
                }
                else
                {
                    p.controller = PlayerControl.AI_Normal;
                    p.res_percent = SkirmishSeatEconomy.GetPresetResMul(3);
                    p.ai_interlligence = SkirmishSeatEconomy.GetPresetAiIntelligence(3);
                }

                sgs.players.Add(p);
            }

            // Ensure content hash for lobby readiness if still empty
            if (string.IsNullOrEmpty(draft.mapContentHash))
                draft.mapContentHash = "unknown";

            return sgs;
        }

        public static List<string> ListBuiltinSkirmishMapNames(ManualLogSource log, int max = 32)
        {
            var list = new List<string>();
            try
            {
                foreach (var kv in SDBase<SD_ANNW_SK_MAP>.dic)
                {
                    if (kv.Value == null || kv.Value.hide)
                        continue;
                    list.Add(kv.Key);
                    if (list.Count >= max)
                        break;
                }
            }
            catch (Exception ex)
            {
                log?.LogWarning("[Bootstrap] List maps failed: " + ex.Message);
            }
            return list;
        }
    }
}

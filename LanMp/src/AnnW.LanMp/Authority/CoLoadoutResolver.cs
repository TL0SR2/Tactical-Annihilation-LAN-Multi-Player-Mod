using System;
using System.Collections.Generic;
using AnnW.LanMp.Protocol;
using BepInEx.Logging;

namespace AnnW.LanMp.Authority
{
    /// <summary>
    /// Host-side table resolution for ADR-005 CO loadout (not GS_CO archive unlocks).
    /// Mirrors <c>CO_Data.SetAsDefaultSkillAndPS</c>: SD_ANNW_CO.skill + pss.
    /// </summary>
    public static class CoLoadoutResolver
    {
        public static void StampDraft(LobbyDraftDto draft, ManualLogSource log = null)
        {
            if (draft?.seats == null)
                return;
            foreach (var seat in draft.seats)
                StampSeat(seat, log);
        }

        public static void StampSeat(LobbySeatDto seat, ManualLogSource log = null)
        {
            if (seat == null || !seat.exist)
                return;
            if (LobbySeatLogic.GetState(seat) == LobbySeatState.Disabled)
            {
                CoLoadoutRules.Clear(seat);
                return;
            }

            if (string.IsNullOrEmpty(seat.coId) || seat.coId == "__none__")
            {
                CoLoadoutRules.Clear(seat);
                return;
            }

            if (!TryResolveDefaults(seat.coId, out var skillId, out var psIds, log))
            {
                CoLoadoutRules.Clear(seat);
                return;
            }

            CoLoadoutRules.Stamp(seat, skillId, psIds);
            log?.LogInfo(
                $"[CoLoadout] seat co={seat.coId} skill={skillId} ps={psIds?.Length ?? 0}");
        }

        /// <summary>Resolve default skill/PS ids from commander table (deterministic, no archive).</summary>
        public static bool TryResolveDefaults(
            string coId,
            out string skillId,
            out string[] psIds,
            ManualLogSource log = null)
        {
            skillId = "";
            psIds = new string[0];
            if (string.IsNullOrEmpty(coId) || coId == "__none__")
                return false;

            SD_ANNW_CO sd;
            try { sd = SDBase<SD_ANNW_CO>.Get(coId); }
            catch (Exception ex)
            {
                log?.LogWarning("[CoLoadout] Get CO failed " + coId + ": " + ex.Message);
                return false;
            }

            if (sd == null)
            {
                log?.LogWarning("[CoLoadout] unknown coId=" + coId);
                return false;
            }

            try
            {
                if (sd.skill != null && !string.IsNullOrEmpty(sd.skill.name))
                    skillId = sd.skill.name;
            }
            catch { /* ignore */ }

            var list = new List<string>();
            try
            {
                if (sd.pss != null)
                {
                    foreach (var ps in sd.pss)
                    {
                        if (ps != null && !string.IsNullOrEmpty(ps.name) && !list.Contains(ps.name))
                            list.Add(ps.name);
                    }
                }
            }
            catch { /* ignore */ }

            // Legacy table fields when pss empty.
            if (list.Count == 0)
            {
                try
                {
                    if (sd.ps1 != null && !string.IsNullOrEmpty(sd.ps1.name))
                        list.Add(sd.ps1.name);
                    if (sd.ps2 != null && !string.IsNullOrEmpty(sd.ps2.name) && !list.Contains(sd.ps2.name))
                        list.Add(sd.ps2.name);
                }
                catch { /* ignore */ }
            }

            psIds = list.ToArray();
            return true;
        }

        /// <summary>Fill SGS_Player.skill / ps_list from Host-stamped seat ids.</summary>
        public static void ApplyToSgsPlayer(SGS_Player p, LobbySeatDto seat, ManualLogSource log = null)
        {
            if (p == null)
                return;
            p.skill = null;
            p.ps_list = new List<SD_ANNW_PS>();

            var skillId = seat?.skillId;
            var psIds = seat?.psIds;

            // Safety: if draft omitted stamp but has coId, resolve table defaults (same on both peers).
            if ((string.IsNullOrEmpty(skillId) && (psIds == null || psIds.Length == 0)) &&
                !string.IsNullOrEmpty(p.sd_co) && p.sd_co != "__none__")
            {
                TryResolveDefaults(p.sd_co, out skillId, out psIds, log);
            }

            if (!string.IsNullOrEmpty(skillId))
            {
                try
                {
                    p.skill = SDBase<SD_ANNW_SKILL>.Get(skillId);
                    if (p.skill == null)
                        log?.LogWarning("[CoLoadout] skill missing id=" + skillId);
                }
                catch (Exception ex)
                {
                    log?.LogWarning("[CoLoadout] skill Get: " + ex.Message);
                }
            }

            if (psIds == null)
                return;
            foreach (var id in psIds)
            {
                if (string.IsNullOrEmpty(id))
                    continue;
                try
                {
                    var ps = SDBase<SD_ANNW_PS>.Get(id);
                    if (ps != null)
                        p.ps_list.Add(ps);
                    else
                        log?.LogWarning("[CoLoadout] ps missing id=" + id);
                }
                catch (Exception ex)
                {
                    log?.LogWarning("[CoLoadout] ps Get: " + ex.Message);
                }
            }
        }
    }
}

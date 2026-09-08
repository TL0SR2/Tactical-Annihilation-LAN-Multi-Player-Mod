using System;
using System.Collections.Generic;
using System.Reflection;
using AnnW.LanMp.Protocol;
using ANNW;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace AnnW.LanMp.Authority
{
    /// <summary>
    /// Host-side loadout resolution for ADR-005.
    /// Table defaults (SD_ANNW_CO.skill + pss) cover fixed COs; Zero / free-slot PS need
    /// Host profile <c>GS_CO.GetActual*</c> or unlocked-pool random — never Guest-local archive.
    /// </summary>
    public static class CoLoadoutResolver
    {
        // GS_CO.GetActual* are assembly-internal; reference DLL may not expose them.
        private static readonly MethodInfo GetActualSkillMi =
            AccessTools.Method(typeof(GS_CO), "GetActualSkill");
        private static readonly MethodInfo GetActualPsMi =
            AccessTools.Method(typeof(GS_CO), "GetActualPS");

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

            // Preserve CO-select UI / prior Host stamp (Zero + free-slot picks).
            if (CoLoadoutRules.HasAuthoredLoadout(seat))
                return;

            if (!TryResolveLoadout(seat.coId, out var skillId, out var psIds, log))
            {
                CoLoadoutRules.Clear(seat);
                return;
            }

            CoLoadoutRules.Stamp(seat, skillId, psIds);
            log?.LogInfo(
                $"[CoLoadout] seat co={seat.coId} skill={skillId} ps={psIds?.Length ?? 0}");
        }

        /// <summary>
        /// Resolve skill/PS ids for a CO: table → Host archive GetActual* → unlocked random.
        /// </summary>
        public static bool TryResolveLoadout(
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

            // 1) Table defaults (fixed commanders).
            try
            {
                if (sd.skill != null && !string.IsNullOrEmpty(sd.skill.name))
                    skillId = sd.skill.name;
            }
            catch { /* ignore */ }

            var list = new List<string>();
            AppendTablePassives(sd, list);

            // 2) Archive GetActual* — Zero custom_* and free-slot PS (level≥3 custom_ps[0]).
            TryMergeArchive(coId, sd, ref skillId, list, log);

            // 3) Still empty (fresh Zero / no archive): Host unlocked-pool random (skirmish equiv).
            if (sd.is_zero && string.IsNullOrEmpty(skillId))
            {
                var sk = PickRandomUnlockedSkill();
                if (sk != null && !string.IsNullOrEmpty(sk.name))
                    skillId = sk.name;
            }

            if (sd.is_zero && list.Count == 0)
            {
                var level = GetCoLevel(coId);
                var need = Mathf.Max(1, level);
                foreach (var ps in PickRandomDistinctPs(need, exclude: null))
                {
                    if (ps != null && !string.IsNullOrEmpty(ps.name) && !list.Contains(ps.name))
                        list.Add(ps.name);
                }
            }
            else if (!sd.is_zero && list.Count < 3 && GetCoLevel(coId) >= 3)
            {
                // Free slot (3rd PS) when archive level unlocked it but stamp still short.
                var free = PickRandomFreePs(sd);
                if (free != null && !string.IsNullOrEmpty(free.name) && !list.Contains(free.name))
                    list.Add(free.name);
            }

            psIds = list.ToArray();
            return true;
        }

        /// <summary>Legacy name — same as <see cref="TryResolveLoadout"/>.</summary>
        public static bool TryResolveDefaults(
            string coId,
            out string skillId,
            out string[] psIds,
            ManualLogSource log = null) =>
            TryResolveLoadout(coId, out skillId, out psIds, log);

        /// <summary>Fill SGS_Player.skill / ps_list from Host-stamped seat ids.</summary>
        public static void ApplyToSgsPlayer(SGS_Player p, LobbySeatDto seat, ManualLogSource log = null)
        {
            if (p == null)
                return;
            p.skill = null;
            p.ps_list = new List<SD_ANNW_PS>();

            var skillId = seat?.skillId;
            var psIds = seat?.psIds;

            if ((string.IsNullOrEmpty(skillId) && (psIds == null || psIds.Length == 0)) &&
                !string.IsNullOrEmpty(p.sd_co) && p.sd_co != "__none__")
            {
                TryResolveLoadout(p.sd_co, out skillId, out psIds, log);
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

        private static void AppendTablePassives(SD_ANNW_CO sd, List<string> list)
        {
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

            if (list.Count > 0)
                return;
            try
            {
                if (sd.ps1 != null && !string.IsNullOrEmpty(sd.ps1.name))
                    list.Add(sd.ps1.name);
                if (sd.ps2 != null && !string.IsNullOrEmpty(sd.ps2.name) && !list.Contains(sd.ps2.name))
                    list.Add(sd.ps2.name);
            }
            catch { /* ignore */ }
        }

        private static void TryMergeArchive(
            string coId,
            SD_ANNW_CO sd,
            ref string skillId,
            List<string> list,
            ManualLogSource log)
        {
            GS_CO gs;
            try
            {
                var profile = Singleton<GS_Overall>.self?.cur_profile;
                if (profile == null)
                    return;
                gs = profile.AcquireCO(coId);
            }
            catch (Exception ex)
            {
                log?.LogWarning("[CoLoadout] AcquireCO: " + ex.Message);
                return;
            }

            if (gs == null)
                return;

            try
            {
                if (string.IsNullOrEmpty(skillId))
                {
                    var sk = GetActualSkillMi?.Invoke(gs, null) as SD_ANNW_SKILL;
                    if (sk != null && !string.IsNullOrEmpty(sk.name))
                        skillId = sk.name;
                }
            }
            catch { /* ignore */ }

            try
            {
                var actual = GetActualPsMi?.Invoke(gs, null) as List<SD_ANNW_PS>;
                if (actual == null)
                    return;
                foreach (var ps in actual)
                {
                    if (ps != null && !string.IsNullOrEmpty(ps.name) && !list.Contains(ps.name))
                        list.Add(ps.name);
                }
            }
            catch { /* ignore */ }
        }

        private static int GetCoLevel(string coId)
        {
            try
            {
                var gs = Singleton<GS_Overall>.self?.cur_profile?.AcquireCO(coId);
                if (gs != null)
                    return Mathf.Max(1, gs.level);
            }
            catch { /* ignore */ }
            return 1;
        }

        private static SD_ANNW_SKILL PickRandomUnlockedSkill()
        {
            var pool = new List<SD_ANNW_SKILL>();
            try
            {
                var profile = Singleton<GS_Overall>.self?.cur_profile;
                if (profile == null || SDBase<SD_ANNW_SKILL>.dic == null)
                    return null;
                foreach (var sk in SDBase<SD_ANNW_SKILL>.dic.Values)
                {
                    if (sk == null || string.IsNullOrEmpty(sk.name))
                        continue;
                    var data = profile.AcquireSkill(sk.name);
                    if (data != null && data.unlocked)
                        pool.Add(sk);
                }
            }
            catch { return null; }

            if (pool.Count == 0)
                return null;
            return pool[UnityEngine.Random.Range(0, pool.Count)];
        }

        private static List<SD_ANNW_PS> PickRandomDistinctPs(int count, HashSet<string> exclude)
        {
            var result = new List<SD_ANNW_PS>();
            if (count <= 0)
                return result;
            var pool = new List<SD_ANNW_PS>();
            try
            {
                var profile = Singleton<GS_Overall>.self?.cur_profile;
                if (profile == null || SDBase<SD_ANNW_PS>.dic == null)
                    return result;
                foreach (var ps in SDBase<SD_ANNW_PS>.dic.Values)
                {
                    if (ps == null || string.IsNullOrEmpty(ps.name))
                        continue;
                    if (exclude != null && exclude.Contains(ps.name))
                        continue;
                    var data = profile.AcquirePS(ps.name);
                    if (data != null && data.unlocked)
                        pool.Add(ps);
                }
            }
            catch { return result; }

            while (result.Count < count && pool.Count > 0)
            {
                var i = UnityEngine.Random.Range(0, pool.Count);
                result.Add(pool[i]);
                pool.RemoveAt(i);
            }
            return result;
        }

        private static SD_ANNW_PS PickRandomFreePs(SD_ANNW_CO sd)
        {
            var exclude = new HashSet<string>();
            try
            {
                if (sd?.ps1 != null && !string.IsNullOrEmpty(sd.ps1.name))
                    exclude.Add(sd.ps1.name);
                if (sd?.ps2 != null && !string.IsNullOrEmpty(sd.ps2.name))
                    exclude.Add(sd.ps2.name);
            }
            catch { /* ignore */ }

            var picked = PickRandomDistinctPs(1, exclude);
            return picked.Count > 0 ? picked[0] : null;
        }
    }
}

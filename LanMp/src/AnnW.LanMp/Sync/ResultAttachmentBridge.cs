using System.Collections.Generic;
using AnnW.LanMp.Protocol;
using BepInEx.Logging;

namespace AnnW.LanMp.Sync
{
    /// <summary>Capture / apply ADR-003 result attachments against live GS_Battle.</summary>
    public static class ResultAttachmentBridge
    {
        public static ResultAttachmentDto CaptureBoard(ManualLogSource log = null)
        {
            var battle = GS_Battle.self;
            if (battle == null)
                return new ResultAttachmentDto
                {
                    units = new UnitSnapDto[0],
                    players = new PlayerSnapDto[0],
                    wrecks = new WreckSnapDto[0]
                };

            var units = new List<UnitSnapDto>();
            if (battle.all_unit?.units_alive != null)
            {
                foreach (var u in battle.all_unit.units_alive)
                {
                    if (u == null)
                        continue;
                    units.Add(CaptureUnit(u));
                }
            }

            var players = new List<PlayerSnapDto>();
            if (battle.all_player?.players != null)
            {
                foreach (var p in battle.all_player.players)
                {
                    if (p == null)
                        continue;
                    players.Add(new PlayerSnapDto
                    {
                        index = p.index,
                        metal = p.metal,
                        power = p.power,
                        defeated = p.defeated,
                        storage = p.storage,
                        metalIncome = p.metal_income,
                        powerIncome = p.power_income
                    });
                }
            }

            var wrecks = CaptureWrecks(battle);

            return new ResultAttachmentDto
            {
                turn = battle.turns,
                coIndex = battle.current_co_index,
                units = units.ToArray(),
                players = players.ToArray(),
                wrecks = wrecks
            };
        }

        /// <summary>All tiles with wreck_amount &gt; 0 (empty array when none — still authoritative).</summary>
        public static WreckSnapDto[] CaptureWrecks(GS_Battle battle = null)
        {
            battle = battle ?? GS_Battle.self;
            var list = new List<WreckSnapDto>();
            var tiles = battle?.terrain?.valid_tiles_list;
            if (tiles == null)
                return list.ToArray();

            foreach (var pos in tiles)
            {
                GameTileData tile = null;
                try { tile = GameTileData.Get(pos); }
                catch { /* ignore */ }
                if (tile == null || tile.wreck_amount <= 0)
                    continue;
                list.Add(new WreckSnapDto
                {
                    x = pos.x,
                    y = pos.y,
                    amount = tile.wreck_amount
                });
            }
            return list.ToArray();
        }

        public static ResultAttachmentDto CaptureFocused(UnitData primary, ManualLogSource log = null)
        {
            return CaptureBoard(log);
        }

        public static UnitSnapDto CaptureUnit(UnitData u)
        {
            string templateId = null;
            try { templateId = u.template?.sd_unit?.name; }
            catch { /* ignore */ }

            var rank = 0;
            try { rank = u.GetRankLogic().unit_rank; }
            catch { /* ignore */ }

            var snap = new UnitSnapDto
            {
                unitId = u.unit_id,
                ownerIndex = u.player != null ? u.player.index : -1,
                x = u.pos.x,
                y = u.pos.y,
                hpCur = u.hp_cur,
                dead = u.dead,
                templateId = templateId,
                createReason = (int)u.create_reason,
                building = u.building,
                buildingProgress = u.building_progress,
                actioned = u.actioned,
                moved = u.moved,
                unitRank = rank
            };
            try
            {
                if (u.wp_builder != null)
                {
                    snap.hasTrainPos = true;
                    snap.trainPosX = u.wp_builder.train_pos.x;
                    snap.trainPosY = u.wp_builder.train_pos.y;
                }
            }
            catch { /* ignore */ }
            return snap;
        }

        public static void Apply(ResultAttachmentDto dto, ManualLogSource log, bool snapPositions = true,
            bool applyPlayerResources = true, int? playerResourceSeatFilter = null)
        {
            if (!ResultAttachmentCodec.HasPayload(dto))
                return;

            var battle = GS_Battle.self;
            if (battle == null || GameAPI.self == null)
            {
                log?.LogWarning("[Attach] Apply skipped — no battle/API");
                return;
            }

            using (SyncContext.BeginRemoteApply())
            {
                if (applyPlayerResources && dto.players != null)
                {
                    foreach (var ps in dto.players)
                    {
                        if (ps == null)
                            continue;
                        if (playerResourceSeatFilter.HasValue && ps.index != playerResourceSeatFilter.Value)
                            continue;
                        var player = FindPlayer(ps.index);
                        if (player == null)
                            continue;
                        var oldMetal = player.metal;
                        var oldPower = player.power;
                        GameAPI.self.SetPlayerResource(player, ps.metal, ps.power);
                        player.defeated = ps.defeated;
                        if (ps.storage > 0)
                            player.storage = ps.storage;
                        if (ps.metalIncome != 0 || ps.powerIncome != 0 || ps.storage > 0)
                        {
                            player.metal_income = ps.metalIncome;
                            player.power_income = ps.powerIncome;
                        }
                        try
                        {
                            AnnW.LanMp.Presentation.RemoteTurnPresentation.NotifyResourceDelta(
                                player, oldMetal, ps.metal, oldPower, ps.power);
                            AnnW.LanMp.Presentation.RemoteTurnPresentation.RefreshEcoBar(player);
                        }
                        catch { /* ignore */ }
                    }
                }

                var hostIds = new HashSet<int>();
                if (dto.units != null)
                {
                    foreach (var us in dto.units)
                    {
                        if (us == null)
                            continue;
                        hostIds.Add(us.unitId);

                        var unit = FindUnit(us.unitId);
                        if (unit == null)
                        {
                            unit = TrySpawnMissing(us, log);
                            if (unit == null)
                            {
                                log?.LogWarning("[Attach] missing unit " + us.unitId + " tpl=" + us.templateId);
                                continue;
                            }
                        }

                        if (snapPositions && (unit.pos.x != us.x || unit.pos.y != us.y))
                            GameAPI.self.MoveUnitInstantly(unit, new Inctor2(us.x, us.y));

                        if (System.Math.Abs(unit.hp_cur - us.hpCur) > 0.01f)
                            unit.hp_cur = us.hpCur;

                        ApplyBuildingState(unit, us, log);

                        unit.actioned = us.actioned;
                        unit.moved = us.moved;

                        if (us.hasTrainPos)
                        {
                            try
                            {
                                if (unit.wp_builder != null)
                                    unit.wp_builder.train_pos = new Inctor2(us.trainPosX, us.trainPosY);
                            }
                            catch { /* ignore */ }
                        }

                        if (us.unitRank > 0)
                        {
                            try
                            {
                                if (unit.GetRankLogic().unit_rank != us.unitRank)
                                {
                                    unit.GetRankLogic().unit_rank = us.unitRank;
                                    unit.RefreshAfterLevelUp();
                                }
                            }
                            catch (System.Exception ex)
                            {
                                log?.LogWarning("[Attach] unitRank: " + ex.Message);
                            }
                        }

                        try { unit.Event_UpdatePos?.Invoke(); }
                        catch { /* ignore */ }

                        if (us.dead && !unit.dead)
                        {
                            try
                            {
                                // Presentation-only death cue before authoritative remove (Guest attach-only path).
                                GameAPI.self.PlayUnitDeathAnimation(
                                    unit.pos, 0f, unit, DieReason.COMBAT, null, null, null);
                            }
                            catch { /* ignore */ }
                            try { GameAPI.self.RemoveUnit(unit); }
                            catch (System.Exception ex) { log?.LogWarning("[Attach] RemoveUnit: " + ex.Message); }
                        }
                    }
                }

                // Drop Guest orphans created with divergent GenNewUnitID during local apply.
                if (hostIds.Count > 0 && battle.all_unit?.units_alive != null && battle.turns >= 1)
                {
                    var orphans = new List<UnitData>();
                    foreach (var u in battle.all_unit.units_alive)
                    {
                        if (u != null && !hostIds.Contains(u.unit_id))
                            orphans.Add(u);
                    }
                    foreach (var u in orphans)
                    {
                        try
                        {
                            log?.LogInfo("[Attach] Removing orphan unit " + u.unit_id);
                            GameAPI.self.RemoveUnit(u);
                        }
                        catch (System.Exception ex)
                        {
                            log?.LogWarning("[Attach] orphan remove: " + ex.Message);
                        }
                    }
                }

                if (dto.turn > 0)
                    battle.turns = dto.turn;
                if (dto.coIndex >= 0)
                    battle.current_co_index = dto.coIndex;

                // After unit remove: stamp Host wreck amounts (Guest RemoveUnit never Die→CreateWreck).
                ApplyWrecks(dto.wrecks, battle, log);
            }

            log?.LogInfo(
                $"[Attach] Applied units={dto.units?.Length ?? 0} players={dto.players?.Length ?? 0} wrecks={dto.wrecks?.Length.ToString() ?? "omit"} res={applyPlayerResources} seat={playerResourceSeatFilter?.ToString() ?? "*"}");
            RefreshUnactionedLists(log);
        }

        /// <summary>
        /// Authoritative wreck set. Null = legacy omit. Empty = clear all. Else set listed tiles and clear others.
        /// Writes wreck_amount directly — never CreateWreck (Host Random must not re-roll on Guest).
        /// </summary>
        public static void ApplyWrecks(WreckSnapDto[] wrecks, GS_Battle battle = null, ManualLogSource log = null)
        {
            if (wrecks == null)
                return;
            battle = battle ?? GS_Battle.self;
            if (battle?.terrain?.valid_tiles_list == null)
                return;

            var want = new Dictionary<long, int>();
            foreach (var w in wrecks)
            {
                if (w == null || w.amount <= 0)
                    continue;
                want[PackPos(w.x, w.y)] = w.amount;
            }

            var changed = 0;
            foreach (var pos in battle.terrain.valid_tiles_list)
            {
                GameTileData tile = null;
                try { tile = GameTileData.Get(pos); }
                catch { /* ignore */ }
                if (tile == null)
                    continue;

                var key = PackPos(pos.x, pos.y);
                var target = want.TryGetValue(key, out var amt) ? amt : 0;
                if (tile.wreck_amount == target)
                    continue;

                tile.wreck_amount = target;
                try { BattleEventBus.self.TriggerWreckChanged(tile); }
                catch { /* ignore */ }
                changed++;
            }

            if (changed > 0)
            {
                try { BattleEventBus.self.TriggerWreckAllChanged(); }
                catch { /* ignore */ }
                log?.LogInfo($"[Attach] Wrecks applied set={want.Count} changed={changed}");
            }
        }

        private static long PackPos(int x, int y) => ((long)x << 32) ^ (uint)y;

        /// <summary>
        /// Vanilla EndTurn popup reads Player.unactioned_units cache; setting actioned alone is not enough.
        /// </summary>
        public static void RefreshUnactionedLists(ManualLogSource log = null)
        {
            var battle = GS_Battle.self;
            if (battle?.all_player?.players == null)
                return;
            foreach (var p in battle.all_player.players)
            {
                if (p == null)
                    continue;
                try
                {
                    var mi = typeof(Player).GetMethod(
                        "UpdateUnactionedUnitCount",
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic);
                    mi?.Invoke(p, null);
                }
                catch (System.Exception ex)
                {
                    log?.LogWarning("[Attach] UpdateUnactionedUnitCount: " + ex.Message);
                }
            }
        }

        public static void ApplyBuildingState(UnitData unit, UnitSnapDto us, ManualLogSource log)
        {
            if (unit == null || us == null)
                return;
            try
            {
                unit.building_progress = us.buildingProgress;
                if (us.building && !unit.building)
                {
                    unit.unit_inactive.SetFlag(UNIT_INACTIVE.BUILDING, state: true);
                }
                else if (!us.building && unit.building)
                {
                    unit.unit_inactive.SetFlag(UNIT_INACTIVE.BUILDING, state: false);
                    try { unit.OnBuildingComplete(); }
                    catch (System.Exception ex)
                    {
                        log?.LogWarning("[Attach] OnBuildingComplete: " + ex.Message);
                    }
                }
            }
            catch (System.Exception ex)
            {
                log?.LogWarning("[Attach] building state: " + ex.Message);
            }
        }

        /// <summary>Spawn units present in attachment but missing locally (before ExecuteAction replay).</summary>
        public static void PreSpawnMissing(ResultAttachmentDto dto, ManualLogSource log)
        {
            if (dto?.units == null)
                return;
            foreach (var us in dto.units)
            {
                if (us == null || us.dead)
                    continue;
                if (FindUnit(us.unitId) != null)
                    continue;

                // Same tile + template already present under a divergent id → remap, do not stack.
                UnitTemplate tplProbe = null;
                try
                {
                    if (!string.IsNullOrEmpty(us.templateId))
                        tplProbe = UnitTemplate.Acquire(us.templateId);
                }
                catch { tplProbe = null; }
                var owner = FindPlayer(us.ownerIndex);
                var atPos = FindUnitAt(new Inctor2(us.x, us.y), tplProbe, owner);
                if (atPos != null)
                {
                    var oldId = atPos.unit_id;
                    SyncContext.ForceUnitId(atPos, us.unitId);
                    ApplyBuildingState(atPos, us, log);
                    log?.LogInfo($"[Attach] Remapped tile unit {oldId}→{us.unitId} at ({us.x},{us.y})");
                    continue;
                }

                TrySpawnMissing(us, log);
            }
        }

        public static UnitData TrySpawnMissing(UnitSnapDto us, ManualLogSource log)
        {
            if (us == null || string.IsNullOrEmpty(us.templateId) || GameAPI.self == null)
                return null;

            UnitTemplate tpl;
            try { tpl = UnitTemplate.Acquire(us.templateId); }
            catch { tpl = null; }
            if (tpl == null)
            {
                log?.LogWarning("[Attach] Acquire failed " + us.templateId);
                return null;
            }

            var player = FindPlayer(us.ownerIndex);
            if (player == null)
            {
                log?.LogWarning("[Attach] owner missing " + us.ownerIndex);
                return null;
            }

            var reason = (CREATE_REASON)us.createReason;
            SyncContext.AllowForcedCreate = true;
            SyncContext.ForcedUnitId = us.unitId;
            try
            {
                var unit = GameAPI.self.CreateUnit(
                    reason, tpl, new Inctor2(us.x, us.y), player,
                    us.building, spawned: false, trigger_ps: true);
                if (unit != null)
                {
                    SyncContext.ForceUnitId(unit, us.unitId);
                    ApplyBuildingState(unit, us, log);
                    if (System.Math.Abs(unit.hp_cur - us.hpCur) > 0.01f)
                        unit.hp_cur = us.hpCur;
                    try { unit.Event_UpdatePos?.Invoke(); } catch { /* ignore */ }
                }
                return unit;
            }
            catch (System.Exception ex)
            {
                log?.LogWarning("[Attach] spawn: " + ex.Message);
                return null;
            }
            finally
            {
                SyncContext.AllowForcedCreate = false;
                SyncContext.ForcedUnitId = null;
            }
        }

        public static UnitData FindUnit(int unitId)
        {
            var battle = GS_Battle.self;
            if (battle?.all_unit == null)
                return null;
            try
            {
                var byId = battle.all_unit.GetUnitByID(unitId);
                if (byId != null)
                    return byId;
            }
            catch { /* fall through */ }

            if (battle.all_unit.units_alive == null)
                return null;
            foreach (var u in battle.all_unit.units_alive)
            {
                if (u != null && u.unit_id == unitId)
                    return u;
            }
            return null;
        }

        public static UnitData FindUnitAt(Inctor2 pos, UnitTemplate template, Player player)
        {
            var battle = GS_Battle.self;
            if (battle?.all_unit?.units_alive == null)
                return null;
            string wantName = null;
            try { wantName = template?.sd_unit?.name; } catch { /* ignore */ }

            foreach (var u in battle.all_unit.units_alive)
            {
                if (u == null || u.dead)
                    continue;
                if (u.pos.x != pos.x || u.pos.y != pos.y)
                    continue;
                if (player != null && u.player != null && u.player.index != player.index)
                    continue;
                if (!string.IsNullOrEmpty(wantName))
                {
                    string have = null;
                    try { have = u.template?.sd_unit?.name; } catch { /* ignore */ }
                    if (have != wantName)
                        continue;
                }
                return u;
            }
            return null;
        }

        private static Player FindPlayer(int index)
        {
            var battle = GS_Battle.self;
            if (battle?.all_player?.players == null)
                return null;
            foreach (var p in battle.all_player.players)
            {
                if (p != null && p.index == index)
                    return p;
            }
            return null;
        }
    }
}

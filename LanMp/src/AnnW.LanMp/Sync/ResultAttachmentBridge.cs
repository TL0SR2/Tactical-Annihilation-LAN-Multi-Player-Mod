using System;
using System.Collections.Generic;
using System.Reflection;
using AnnW.LanMp.Protocol;
using BepInEx.Logging;
using HarmonyLib;

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
                    var ps = new PlayerSnapDto
                    {
                        index = p.index,
                        metal = p.metal,
                        power = p.power,
                        defeated = p.defeated,
                        storage = p.storage,
                        metalIncome = p.metal_income,
                        powerIncome = p.power_income,
                        resMul = p.res_mul
                    };
                    try
                    {
                        if (p.teleport_logic != null)
                        {
                            try { p.teleport_logic.UpdateAmount(); }
                            catch { /* ignore */ }
                            ps.teleportLoadedBp = p.teleport_logic.loaded_bp;
                            ps.teleportMaxBpBase = ReadMaxBpBase(p.teleport_logic);
                            if (p.teleport_logic.units != null)
                            {
                                var ids = new int[p.teleport_logic.units.Count];
                                for (var i = 0; i < ids.Length; i++)
                                    ids[i] = p.teleport_logic.units[i] != null
                                        ? p.teleport_logic.units[i].unit_id
                                        : -1;
                                ps.teleportCargoUnitIds = ids;
                            }
                            else
                                ps.teleportCargoUnitIds = new int[0];
                        }
                        else
                            ps.teleportCargoUnitIds = new int[0];
                    }
                    catch
                    {
                        ps.teleportCargoUnitIds = new int[0];
                    }
                    players.Add(ps);
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
            var unitExp = -1f;
            var unitExpReq = -1f;
            try
            {
                var rl = u.GetRankLogic();
                rank = rl.unit_rank;
                unitExp = rl.exp;
                unitExpReq = rl.exp_req;
            }
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
                unitRank = rank,
                unitExp = unitExp,
                unitExpReq = unitExpReq,
                cd = u.cd,
                cding = u.cding
            };
            try
            {
                if (u.wp_builder != null)
                {
                    snap.hasTrainPos = true;
                    snap.trainPosX = u.wp_builder.train_pos.x;
                    snap.trainPosY = u.wp_builder.train_pos.y;
                    snap.factoryBpLeft = u.wp_builder.bp_left;
                }
            }
            catch { /* ignore */ }
            try
            {
                if (u.wp_shield != null)
                    snap.shdPercent = u.wp_shield.shd_percent;
            }
            catch { /* ignore */ }

            try
            {
                snap.transporting = u.transporting;
                if (u.transporting && u.transporter != null)
                {
                    if (u.transporter.owner != null)
                        snap.transporterUnitId = u.transporter.owner.unit_id;
                    else
                        snap.transporterUnitId = -2; // player.teleport_logic
                }
                else
                    snap.transporterUnitId = -1;

                if (u.wp_transport != null)
                {
                    snap.unloadBpLeft = u.wp_transport.unload_bp_left;
                    snap.unloadBpMaxBase = ReadUnloadBpMaxBase(u.wp_transport);
                    var logic = u.wp_transport.tsp_logic;
                    if (logic != null)
                    {
                        try { logic.UpdateAmount(); }
                        catch { /* ignore */ }
                        snap.transportLoadedBp = logic.loaded_bp;
                        snap.transportMaxBpBase = ReadMaxBpBase(logic);
                    }
                    // Teleporters share player.teleport_logic — cargo ids on PlayerSnapDto.
                    if (!u.wp_transport.is_teleportor && logic?.units != null)
                    {
                        var cargo = logic.units;
                        var ids = new int[cargo.Count];
                        for (var i = 0; i < ids.Length; i++)
                            ids[i] = cargo[i] != null ? cargo[i].unit_id : -1;
                        snap.cargoUnitIds = ids;
                    }
                    else if (!u.wp_transport.is_teleportor)
                        snap.cargoUnitIds = new int[0];
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
                        // Host eco multiplier — Guest must not keep a wrong Setup res_mul
                        // (legacy 0 = omit).
                        if (ps.resMul > 0f && Math.Abs(player.res_mul - ps.resMul) > 0.001f)
                        {
                            log?.LogInfo(
                                $"[Attach] player[{ps.index}] res_mul {player.res_mul:0.###} → {ps.resMul:0.###}");
                            player.res_mul = ps.resMul;
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

                        if (snapPositions && !us.transporting &&
                            (unit.pos.x != us.x || unit.pos.y != us.y))
                            GameAPI.self.MoveUnitInstantly(unit, new Inctor2(us.x, us.y));

                        if (System.Math.Abs(unit.hp_cur - us.hpCur) > 0.01f)
                            unit.hp_cur = us.hpCur;

                        ApplyBuildingState(unit, us, log);

                        unit.actioned = us.actioned;
                        unit.moved = us.moved;
                        // Host OnUnitActionEnd / StartTurn own cd; Guest attach-only never runs those.
                        var cdDirty = unit.cd != us.cd || unit.cding != us.cding;
                        unit.cd = us.cd;
                        unit.cding = us.cding;
                        if (cdDirty)
                            TryReDrawUnit(unit);

                        if (us.hasTrainPos)
                        {
                            try
                            {
                                if (unit.wp_builder != null)
                                    unit.wp_builder.train_pos = new Inctor2(us.trainPosX, us.trainPosY);
                            }
                            catch { /* ignore */ }
                        }

                        if (us.factoryBpLeft >= 0)
                        {
                            try
                            {
                                if (unit.wp_builder != null)
                                    unit.wp_builder.bp_left = us.factoryBpLeft;
                            }
                            catch { /* ignore */ }
                        }

                        ApplyShieldState(unit, us, log);
                        ApplyRankExpState(unit, us, log);

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

                // Transport / teleporter cargo must be applied after units exist (ADR-003).
                ApplyTransportState(dto, log);
            }

            log?.LogInfo(
                $"[Attach] Applied units={dto.units?.Length ?? 0} players={dto.players?.Length ?? 0} wrecks={dto.wrecks?.Length.ToString() ?? "omit"} res={applyPlayerResources} seat={playerResourceSeatFilter?.ToString() ?? "*"}");
            RefreshUnactionedLists(log);
        }

        private static readonly MethodInfo UnloadUnitMi = AccessTools.Method(
            typeof(TransportLogic), "UnloadUnit",
            new[] { typeof(UnitData), typeof(Inctor2), typeof(bool), typeof(bool) });

        private static readonly FieldInfo MaxBpBaseFi =
            AccessTools.Field(typeof(TransportLogic), "max_bp_base");
        private static readonly FieldInfo UnloadBpMaxBaseFi =
            AccessTools.Field(typeof(WP_Transport), "unload_bp_max_base");

        private static int ReadMaxBpBase(TransportLogic logic)
        {
            if (logic == null || MaxBpBaseFi == null)
                return -1;
            try { return (int)MaxBpBaseFi.GetValue(logic); }
            catch { return -1; }
        }

        private static void WriteMaxBpBase(TransportLogic logic, int value)
        {
            if (logic == null || MaxBpBaseFi == null || value < 0)
                return;
            try { MaxBpBaseFi.SetValue(logic, value); }
            catch { /* ignore */ }
        }

        private static int ReadUnloadBpMaxBase(WP_Transport wp)
        {
            if (wp == null || UnloadBpMaxBaseFi == null)
                return -1;
            try { return (int)UnloadBpMaxBaseFi.GetValue(wp); }
            catch { return -1; }
        }

        private static void WriteUnloadBpMaxBase(WP_Transport wp, int value)
        {
            if (wp == null || UnloadBpMaxBaseFi == null || value < 0)
                return;
            try { UnloadBpMaxBaseFi.SetValue(wp, value); }
            catch { /* ignore */ }
        }

        /// <summary>
        /// Stamp load/unload budgets after cargo list reconcile (UI percents + CanTransport).
        /// </summary>
        private static void ApplyTransportCapacity(WP_Transport wp, UnitSnapDto us, ManualLogSource log)
        {
            if (wp == null || us == null)
                return;

            if (us.unloadBpMaxBase >= 0)
                WriteUnloadBpMaxBase(wp, us.unloadBpMaxBase);
            if (us.unloadBpLeft >= 0)
                wp.unload_bp_left = us.unloadBpLeft;

            var logic = wp.tsp_logic;
            if (logic == null)
                return;

            if (us.transportMaxBpBase >= 0)
                WriteMaxBpBase(logic, us.transportMaxBpBase);

            try { logic.UpdateAmount(); }
            catch { /* ignore */ }

            // Host loaded_bp is authoritative if GetBC() drifted on Guest.
            if (us.transportLoadedBp >= 0 && logic.loaded_bp != us.transportLoadedBp)
            {
                logic.loaded_bp = us.transportLoadedBp;
                log?.LogInfo(
                    $"[Attach] transport loaded_bp stamped unit={us.unitId} → {us.transportLoadedBp}");
            }
        }

        private static void ApplyTeleportCapacity(TransportLogic logic, PlayerSnapDto ps, ManualLogSource log)
        {
            if (logic == null || ps == null)
                return;
            if (ps.teleportMaxBpBase >= 0)
                WriteMaxBpBase(logic, ps.teleportMaxBpBase);
            try { logic.UpdateAmount(); }
            catch { /* ignore */ }
            if (ps.teleportLoadedBp >= 0 && logic.loaded_bp != ps.teleportLoadedBp)
            {
                logic.loaded_bp = ps.teleportLoadedBp;
                log?.LogInfo(
                    $"[Attach] teleport loaded_bp stamped player={ps.index} → {ps.teleportLoadedBp}");
            }
        }

        private static bool AttachmentHasTransportPayload(ResultAttachmentDto dto)
        {
            if (dto?.units != null)
            {
                foreach (var u in dto.units)
                {
                    if (u == null)
                        continue;
                    if (u.transporting || u.transporterUnitId != -1 ||
                        u.cargoUnitIds != null || u.unloadBpLeft >= 0 ||
                        u.unloadBpMaxBase >= 0 || u.transportLoadedBp >= 0 ||
                        u.transportMaxBpBase >= 0)
                        return true;
                }
            }
            if (dto?.players != null)
            {
                foreach (var p in dto.players)
                {
                    if (p?.teleportCargoUnitIds != null ||
                        (p != null && (p.teleportLoadedBp >= 0 || p.teleportMaxBpBase >= 0)))
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Reconcile cargo lists + transporting flags from Host attachment.
        /// Guest MoveUnitInstantly / attach-only must not leave cargo registered on the map.
        /// </summary>
        public static void ApplyTransportState(ResultAttachmentDto dto, ManualLogSource log = null)
        {
            if (!AttachmentHasTransportPayload(dto))
                return;

            var battle = GS_Battle.self;
            if (battle == null)
                return;

            var desired = new Dictionary<int, TransportLink>();
            if (dto.units != null)
            {
                foreach (var us in dto.units)
                {
                    if (us == null)
                        continue;
                    if (us.transporting || us.transporterUnitId != -1)
                    {
                        desired[us.unitId] = new TransportLink
                        {
                            TransporterUnitId = us.transporterUnitId,
                            PlayerIndex = us.ownerIndex,
                            PosX = us.x,
                            PosY = us.y,
                            Actioned = us.actioned,
                            Moved = us.moved
                        };
                    }
                    if (us.cargoUnitIds == null)
                        continue;
                    foreach (var cid in us.cargoUnitIds)
                    {
                        if (cid < 0 || desired.ContainsKey(cid))
                            continue;
                        desired[cid] = new TransportLink
                        {
                            TransporterUnitId = us.unitId,
                            PlayerIndex = us.ownerIndex,
                            PosX = us.x,
                            PosY = us.y,
                            Actioned = true,
                            Moved = true
                        };
                    }
                }
            }

            if (dto.players != null)
            {
                foreach (var ps in dto.players)
                {
                    if (ps?.teleportCargoUnitIds == null)
                        continue;
                    foreach (var cid in ps.teleportCargoUnitIds)
                    {
                        if (cid < 0)
                            continue;
                        var cargoSnap = ResultAttachmentCodec.FindUnit(dto, cid);
                        desired[cid] = new TransportLink
                        {
                            TransporterUnitId = -2,
                            PlayerIndex = ps.index,
                            PosX = cargoSnap != null ? cargoSnap.x : 0,
                            PosY = cargoSnap != null ? cargoSnap.y : 0,
                            Actioned = cargoSnap == null || cargoSnap.actioned,
                            Moved = cargoSnap == null || cargoSnap.moved
                        };
                    }
                }
            }

            var alive = battle.all_unit?.units_alive;
            if (alive != null)
            {
                foreach (var u in new List<UnitData>(alive))
                {
                    if (u == null || !u.transporting || u.transporter == null)
                        continue;
                    var want = desired.TryGetValue(u.unit_id, out var link);
                    var curId = u.transporter.owner != null ? u.transporter.owner.unit_id : -2;
                    if (want && curId == link.TransporterUnitId)
                    {
                        if (link.TransporterUnitId != -2)
                            continue;
                        var ownerPlayer = u.player != null ? u.player.index : -1;
                        if (ownerPlayer == link.PlayerIndex)
                            continue;
                    }

                    var pos = want ? new Inctor2(link.PosX, link.PosY) : u.pos;
                    TryUnloadCargo(u, pos, log);
                }
            }

            foreach (var kv in desired)
            {
                var cargo = FindUnit(kv.Key);
                if (cargo == null)
                    continue;
                var link = kv.Value;
                var logic = ResolveTransportLogic(link);
                if (logic == null)
                {
                    log?.LogWarning("[Attach] transport logic missing for cargo " + kv.Key +
                                    " tsp=" + link.TransporterUnitId);
                    continue;
                }

                if (cargo.transporting && ReferenceEquals(cargo.transporter, logic))
                {
                    cargo.actioned = link.Actioned;
                    cargo.moved = link.Moved;
                    cargo.pos = new Inctor2(link.PosX, link.PosY);
                    continue;
                }

                if (cargo.transporting && cargo.transporter != null &&
                    !ReferenceEquals(cargo.transporter, logic))
                    TryUnloadCargo(cargo, new Inctor2(link.PosX, link.PosY), log);

                try { cargo.UnRegPos(null); }
                catch { /* ignore */ }

                try
                {
                    if (!logic.LoadUnit(cargo, enforce_capacity: false))
                    {
                        log?.LogWarning("[Attach] LoadUnit refused cargo=" + cargo.unit_id);
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    log?.LogWarning("[Attach] LoadUnit: " + ex.Message);
                    continue;
                }

                cargo.pos = new Inctor2(link.PosX, link.PosY);
                cargo.actioned = link.Actioned;
                cargo.moved = link.Moved;
                try { cargo.Event_UpdatePos?.Invoke(); }
                catch { /* ignore */ }
                try
                {
                    AccessTools.Method(typeof(UnitData), "ReDraw", new[] { typeof(bool) })
                        ?.Invoke(cargo, new object[] { false });
                }
                catch { /* ignore */ }
            }

            if (dto.units != null)
            {
                foreach (var us in dto.units)
                {
                    if (us == null)
                        continue;
                    var carrier = FindUnit(us.unitId);
                    if (carrier?.wp_transport == null)
                        continue;
                    if (us.cargoUnitIds != null && !carrier.wp_transport.is_teleportor &&
                        carrier.wp_transport.tsp_logic != null)
                        EnsureCargoList(carrier.wp_transport.tsp_logic, us.cargoUnitIds, us, log);
                    ApplyTransportCapacity(carrier.wp_transport, us, log);
                    try
                    {
                        AccessTools.Method(typeof(WP_Transport), "UpdateActionedState")
                            ?.Invoke(carrier.wp_transport, null);
                    }
                    catch { /* ignore */ }
                }
            }

            if (dto.players != null)
            {
                foreach (var ps in dto.players)
                {
                    if (ps == null)
                        continue;
                    var player = FindPlayer(ps.index);
                    if (player?.teleport_logic == null)
                        continue;
                    if (ps.teleportCargoUnitIds != null)
                        EnsureCargoList(player.teleport_logic, ps.teleportCargoUnitIds, null, log);
                    ApplyTeleportCapacity(player.teleport_logic, ps, log);
                    try { player.UpdateAllTeleporters(); }
                    catch { /* ignore */ }
                }
            }

            try { BattleEventBus.self.TriggerFOWChanged(); }
            catch { /* ignore */ }
            log?.LogInfo("[Attach] Transport state applied cargoLinks=" + desired.Count);
        }

        private struct TransportLink
        {
            public int TransporterUnitId;
            public int PlayerIndex;
            public int PosX;
            public int PosY;
            public bool Actioned;
            public bool Moved;
        }

        private static TransportLogic ResolveTransportLogic(TransportLink link)
        {
            if (link.TransporterUnitId == -2)
            {
                var player = FindPlayer(link.PlayerIndex);
                return player?.teleport_logic;
            }
            if (link.TransporterUnitId < 0)
                return null;
            var carrier = FindUnit(link.TransporterUnitId);
            return carrier?.wp_transport?.tsp_logic;
        }

        private static void TryUnloadCargo(UnitData cargo, Inctor2 pos, ManualLogSource log)
        {
            if (cargo?.transporter == null)
                return;
            try
            {
                if (UnloadUnitMi != null)
                {
                    UnloadUnitMi.Invoke(cargo.transporter,
                        new object[] { cargo, pos, true, false });
                }
                else
                {
                    cargo.transporter.units.Remove(cargo);
                    cargo.transporter.UpdateAmount();
                    cargo.transporting = false;
                    cargo.transporter = null;
                    if (GameAPI.self != null)
                        GameAPI.self.MoveUnitInstantly(cargo, pos);
                }
            }
            catch (Exception ex)
            {
                log?.LogWarning("[Attach] UnloadUnit: " + ex.Message);
            }
        }

        private static void EnsureCargoList(
            TransportLogic logic,
            int[] wantIds,
            UnitSnapDto carrierSnap,
            ManualLogSource log)
        {
            if (logic?.units == null || wantIds == null)
                return;

            var want = new HashSet<int>();
            foreach (var id in wantIds)
            {
                if (id >= 0)
                    want.Add(id);
            }

            foreach (var u in new List<UnitData>(logic.units))
            {
                if (u == null || want.Contains(u.unit_id))
                    continue;
                var pos = carrierSnap != null
                    ? new Inctor2(carrierSnap.x, carrierSnap.y)
                    : u.pos;
                TryUnloadCargo(u, pos, log);
            }
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

        /// <summary>
        /// Sync RankExp: unit_rank + exp progress + exp_req (Guest previously only got rank).
        /// unitExp/unitExpReq &lt; 0 = legacy omit.
        /// </summary>
        private static void ApplyRankExpState(UnitData unit, UnitSnapDto us, ManualLogSource log)
        {
            if (unit == null || us == null)
                return;

            var hasExp = us.unitExp >= 0f || us.unitExpReq >= 0f;
            // Legacy: only stamped unitRank when &gt; 0 and never sent exp.
            if (!hasExp && us.unitRank <= 0)
                return;

            try
            {
                var rl = unit.GetRankLogic();
                if (rl == null)
                    return;

                var rankDirty = rl.unit_rank != us.unitRank;
                var prevExp = rl.exp;
                var prevReq = rl.exp_req;

                rl.unit_rank = us.unitRank;
                if (us.unitExpReq >= 0f)
                    rl.exp_req = us.unitExpReq;
                else
                    rl.exp_req = rl.GetExpReq();

                if (us.unitExp >= 0f)
                    rl.exp = us.unitExp;

                var expDirty = Math.Abs(prevExp - rl.exp) > 0.001f ||
                               Math.Abs(prevReq - rl.exp_req) > 0.001f;

                if (rankDirty)
                {
                    unit.RefreshAfterLevelUp();
                    log?.LogInfo(
                        $"[Attach] unit={unit.unit_id} rank→{us.unitRank} exp={rl.exp:0.#}/{rl.exp_req:0.#}");
                }
                else if (expDirty)
                {
                    TryReDrawUnit(unit);
                }
            }
            catch (Exception ex)
            {
                log?.LogWarning("[Attach] rank/exp unit=" + unit.unit_id + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Sync WP_Shield.open state: shd_percent + FieldShield tile zone (GameTileData.AddShield).
        /// Without this, Guest sees no bubble and thinks ATTACK damage vanished into a Host shield.
        /// </summary>
        private static void ApplyShieldState(UnitData unit, UnitSnapDto us, ManualLogSource log)
        {
            if (unit == null || us == null || us.shdPercent < 0f)
                return;
            WP_Shield shd = null;
            try { shd = unit.wp_shield; }
            catch { return; }
            if (shd == null)
                return;

            var prev = shd.shd_percent;
            var next = us.shdPercent;
            var active = next > 0.001f;
            var wasActive = prev > 0.001f;
            var pctDirty = Math.Abs(prev - next) > 0.0001f;

            try
            {
                shd.shd_percent = next;
                if (!active)
                {
                    // Empty() is internal — ClearCastedEffects + event matches Break/Empty visuals.
                    shd.ClearCastedEffects();
                    try { shd.Event_ShieldChanged?.Invoke(); }
                    catch { /* ignore */ }
                    if (wasActive || pctDirty)
                    {
                        log?.LogInfo(
                            $"[Attach] unit={unit.unit_id} shield OFF (was {prev:0.###})");
                        TryReDrawUnit(unit);
                    }
                    return;
                }

                // Active: rebuild circle FieldShields so Guest FOW/UI match Host absorb zone.
                shd.AddEffects();
                try { shd.Event_ShieldChanged?.Invoke(); }
                catch { /* ignore */ }
                if (pctDirty || !wasActive)
                {
                    log?.LogInfo(
                        $"[Attach] unit={unit.unit_id} shield {prev:0.###} → {next:0.###} range={shd.shd_range}");
                    TryReDrawUnit(unit);
                }
            }
            catch (Exception ex)
            {
                log?.LogWarning("[Attach] shield state unit=" + unit.unit_id + ": " + ex.Message);
            }
        }

        /// <summary>Refresh FUI (txt_cd / group_cd) after attach stamps UnitData.cd.</summary>
        private static void TryReDrawUnit(UnitData unit)
        {
            if (unit == null)
                return;
            try
            {
                typeof(UnitData).GetMethod(
                    "ReDraw",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.Invoke(unit, new object[] { false });
            }
            catch
            {
                /* ignore */
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
                    ApplyShieldState(unit, us, log);
                    ApplyRankExpState(unit, us, log);
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

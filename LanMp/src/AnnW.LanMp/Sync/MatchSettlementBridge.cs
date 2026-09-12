using System;
using System.Collections.Generic;
using AnnW.LanMp.Protocol;
using BepInEx.Logging;

namespace AnnW.LanMp.Sync
{
    /// <summary>
    /// MatchEnd settlement chokepoint: Host captures <c>PlayerBattleStatics</c> + <c>turn_snaps</c>;
    /// both peers stamp before vanilla <c>proc_EndGame</c> (ADR-001 truth for Guest UX).
    /// </summary>
    internal static class MatchSettlementBridge
    {
        public static MatchSettlementAttachment Capture(ManualLogSource log = null)
        {
            var battle = GS_Battle.self;
            if (battle?.all_player?.players == null)
                return new MatchSettlementAttachment { seats = new SeatSettlementDto[0] };

            var seats = new List<SeatSettlementDto>();
            foreach (var p in battle.all_player.players)
            {
                if (p == null || p.fraction == Fraction.NEUTRAL)
                    continue;
                try
                {
                    seats.Add(new SeatSettlementDto
                    {
                        playerIndex = p.index,
                        statics = CaptureStatics(p.statics),
                        turnSnaps = CaptureTurnSnaps(p.turn_snaps)
                    });
                }
                catch (Exception ex)
                {
                    log?.LogWarning("[MatchSettle] capture seat=" + p.index + ": " + ex.Message);
                }
            }

            return new MatchSettlementAttachment { seats = seats.ToArray() };
        }

        public static void Apply(MatchSettlementAttachment att, ManualLogSource log = null)
        {
            if (!MatchSettlementCodec.HasPayload(att))
                return;
            var battle = GS_Battle.self;
            if (battle?.all_player?.players == null)
                return;

            var applied = 0;
            foreach (var seat in att.seats)
            {
                if (seat == null)
                    continue;
                var player = FindPlayer(battle, seat.playerIndex);
                if (player == null)
                {
                    log?.LogWarning("[MatchSettle] missing player index=" + seat.playerIndex);
                    continue;
                }
                try
                {
                    if (seat.statics != null)
                        ApplyStatics(player, seat.statics);
                    if (seat.turnSnaps != null)
                        ApplyTurnSnaps(player, seat.turnSnaps);
                    applied++;
                }
                catch (Exception ex)
                {
                    log?.LogWarning("[MatchSettle] apply seat=" + seat.playerIndex + ": " + ex.Message);
                }
            }

            log?.LogInfo("[MatchSettle] stamped seats=" + applied + "/" + att.seats.Length);
        }

        public static void ApplyFromMatchEnd(MatchEndPayload end, ManualLogSource log = null)
        {
            if (end == null || string.IsNullOrEmpty(end.settlementJson))
                return;
            try
            {
                Apply(MatchSettlementCodec.FromJson(end.settlementJson), log);
            }
            catch (Exception ex)
            {
                log?.LogWarning("[MatchSettle] settlementJson: " + ex.Message);
            }
        }

        private static Player FindPlayer(GS_Battle battle, int index)
        {
            foreach (var p in battle.all_player.players)
            {
                if (p != null && p.index == index)
                    return p;
            }
            if (index >= 0 && index < battle.all_player.players.Count)
                return battle.all_player.players[index];
            return null;
        }

        private static PlayerBattleStaticsDto CaptureStatics(PlayerBattleStatics s)
        {
            if (s == null)
                return null;
            return new PlayerBattleStaticsDto
            {
                totalPrebuildAsset = s.total_prebuild_asset,
                totalLossAsset = s.total_loss_asset,
                totalMetalGet = s.total_metal_get,
                totalPowerGet = s.total_power_get,
                totalResSpend = s.total_res_spend,
                totalMetalFromMiner = s.total_metal_from_miner,
                totalMetalFromReclaim = s.total_metal_from_reclaim,
                totalMetalFromConvert = s.total_metal_from_convert,
                totalMetalLostFromCapacity = s.total_metal_lost_from_capacity,
                unitProduced = s.unit_produced,
                unitLost = s.unit_lost,
                unitKilled = s.unit_killed,
                bdCreated = s.bd_created,
                bdLost = s.bd_lost,
                bdDestroyed = s.bd_destroyed
            };
        }

        private static void ApplyStatics(Player player, PlayerBattleStaticsDto dto)
        {
            if (player.statics == null)
                player.statics = new PlayerBattleStatics { owner = player };
            var s = player.statics;
            s.owner = player;
            s.total_prebuild_asset = dto.totalPrebuildAsset;
            s.total_loss_asset = dto.totalLossAsset;
            s.total_metal_get = dto.totalMetalGet;
            s.total_power_get = dto.totalPowerGet;
            s.total_res_spend = dto.totalResSpend;
            s.total_metal_from_miner = dto.totalMetalFromMiner;
            s.total_metal_from_reclaim = dto.totalMetalFromReclaim;
            s.total_metal_from_convert = dto.totalMetalFromConvert;
            s.total_metal_lost_from_capacity = dto.totalMetalLostFromCapacity;
            s.unit_produced = dto.unitProduced;
            s.unit_lost = dto.unitLost;
            s.unit_killed = dto.unitKilled;
            s.bd_created = dto.bdCreated;
            s.bd_lost = dto.bdLost;
            s.bd_destroyed = dto.bdDestroyed;
        }

        private static TurnSnapDto[] CaptureTurnSnaps(List<TurnSnap> snaps)
        {
            if (snaps == null || snaps.Count == 0)
                return new TurnSnapDto[0];
            var list = new TurnSnapDto[snaps.Count];
            for (var i = 0; i < snaps.Count; i++)
            {
                var snap = snaps[i];
                if (snap == null)
                {
                    list[i] = new TurnSnapDto { units = new UnitTurnStateDto[0] };
                    continue;
                }
                list[i] = new TurnSnapDto
                {
                    turn = snap.turn,
                    metal = snap.metal,
                    power = snap.power,
                    metalIncome = snap.metal_income,
                    powerIncome = snap.power_income,
                    unitCount = snap.unit_count,
                    totalAssetValue = snap.total_asset_value,
                    units = CaptureUnits(snap.units)
                };
            }
            return list;
        }

        private static UnitTurnStateDto[] CaptureUnits(List<UnitTurnState> units)
        {
            if (units == null || units.Count == 0)
                return new UnitTurnStateDto[0];
            var arr = new UnitTurnStateDto[units.Count];
            for (var i = 0; i < units.Count; i++)
            {
                var u = units[i];
                if (u == null)
                {
                    arr[i] = new UnitTurnStateDto { unitId = -1 };
                    continue;
                }
                arr[i] = new UnitTurnStateDto
                {
                    unitId = u.unit_id,
                    x = u.pos.x,
                    y = u.pos.y
                };
            }
            return arr;
        }

        private static void ApplyTurnSnaps(Player player, TurnSnapDto[] dtos)
        {
            if (player.turn_snaps == null)
                player.turn_snaps = new List<TurnSnap>();
            else
                player.turn_snaps.Clear();

            foreach (var dto in dtos)
            {
                if (dto == null)
                    continue;
                var snap = new TurnSnap
                {
                    turn = dto.turn,
                    metal = dto.metal,
                    power = dto.power,
                    metal_income = dto.metalIncome,
                    power_income = dto.powerIncome,
                    unit_count = dto.unitCount,
                    total_asset_value = dto.totalAssetValue,
                    units = new List<UnitTurnState>()
                };
                if (dto.units != null)
                {
                    foreach (var u in dto.units)
                    {
                        if (u == null)
                            continue;
                        snap.units.Add(new UnitTurnState
                        {
                            unit_id = u.unitId,
                            pos = new Inctor2(u.x, u.y)
                        });
                    }
                }
                player.turn_snaps.Add(snap);
            }
        }
    }
}

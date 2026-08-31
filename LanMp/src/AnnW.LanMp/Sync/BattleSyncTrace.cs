using System;
using System.Globalization;
using System.IO;
using System.Text;
using AnnW.LanMp.Protocol;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace AnnW.LanMp.Sync
{
    /// <summary>
    /// Dev-time structured battle sync trace (NDJSON). One file per peer role so Host/Guest
    /// views of the same fight can be diffed without relying on vague verbal reports.
    /// </summary>
    public static class BattleSyncTrace
    {
        private static readonly object Gate = new object();
        private static StreamWriter _writer;
        private static string _path;
        private static string _role = "?";
        private static string _battleId = "";
        private static bool _enabled;
        private static ConfigEntry<bool> _cfg;

        public static string CurrentPath => _path;

        public static void BindConfig(ConfigEntry<bool> enabled)
        {
            _cfg = enabled;
            _enabled = enabled == null || enabled.Value;
        }

        public static void SetRole(PeerRole role, string battleId)
        {
            if (!_enabled)
                return;
            var r = role == PeerRole.Host ? "Host" : (role == PeerRole.Guest ? "Guest" : "None");
            var bid = battleId ?? "";
            if (r == _role && bid == _battleId && _writer != null)
                return;
            lock (Gate)
            {
                WriteTraceClose("role-change");
                FlushAndDispose();
            }
            _role = r;
            _battleId = bid;
            if (role == PeerRole.None)
                return;
            OpenFile();
        }

        public static void Close(string reason = null)
        {
            lock (Gate)
            {
                WriteTraceClose(reason);
                FlushAndDispose();
            }
        }

        /// <summary>Battle finished — flush footer and release writer; never deletes the NDJSON file.</summary>
        public static void EndBattleSession(string reason)
        {
            if (!_enabled)
                return;
            Ev("BattleSessionEnd", detail: reason ?? "ended");
            Close(reason ?? "ended");
        }

        /// <summary>Optional startup wipe (Debug.ClearSyncTraceOnStartup). Manual: LanMp/tools/Clear-LanMpLogs.ps1</summary>
        public static void ClearLogDirectory()
        {
            try
            {
                var dir = Path.Combine(Paths.GameRootPath, "LanMp", "logs");
                if (!Directory.Exists(dir))
                    return;
                var n = 0;
                foreach (var f in Directory.GetFiles(dir, "sync-trace-*.ndjson"))
                {
                    File.Delete(f);
                    n++;
                }
                LanMpPlugin.Log?.LogInfo("[SyncTrace] cleared " + n + " file(s) under " + dir);
            }
            catch (Exception ex)
            {
                LanMpPlugin.Log?.LogWarning("[SyncTrace] clear: " + ex.Message);
            }
        }

        private static void WriteTraceClose(string reason)
        {
            if (_writer == null)
                return;
            try
            {
                _writer.WriteLine(
                    "{\"ev\":\"TraceClose\",\"role\":\"" + Esc(_role) + "\",\"battleId\":\"" + Esc(_battleId) +
                    "\",\"reason\":\"" + Esc(reason ?? "") + "\",\"path\":\"" + Esc(_path ?? "") + "\"}");
                _writer.Flush();
            }
            catch { /* ignore */ }
        }

        private static void FlushAndDispose()
        {
            try { _writer?.Flush(); } catch { /* ignore */ }
            try { _writer?.Dispose(); } catch { /* ignore */ }
            _writer = null;
            _path = null;
        }

        public static void Ev(
            string ev,
            string kind = null,
            string cmdId = null,
            string intentId = null,
            int? turn = null,
            int? curPlayer = null,
            int? endedPlayer = null,
            int? nextPlayer = null,
            int? turnsAfter = null,
            int? unitId = null,
            int? cate = null,
            string target = null,
            bool? hasTarget = null,
            bool? attach = null,
            bool? localControl = null,
            string detail = null)
        {
            if (!_enabled)
                return;
            try
            {
                EnsureOpen();
                var battle = GS_Battle.self;
                var sb = new StringBuilder(256);
                sb.Append('{');
                Append(sb, "t", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), true);
                Append(sb, "ms", Environment.TickCount);
                Append(sb, "role", _role);
                Append(sb, "battleId", string.IsNullOrEmpty(_battleId)
                    ? (LanMpPlugin.Instance?.Lobby?.BattleId ?? "")
                    : _battleId);
                Append(sb, "ev", ev);
                if (!string.IsNullOrEmpty(kind)) Append(sb, "kind", kind);
                if (!string.IsNullOrEmpty(cmdId)) Append(sb, "cmdId", cmdId);
                if (!string.IsNullOrEmpty(intentId)) Append(sb, "intentId", intentId);
                var t = turn ?? (battle != null ? battle.turns : (int?)null);
                var cp = curPlayer ?? (battle?.cur_player != null ? battle.cur_player.index : (int?)null);
                if (t.HasValue) Append(sb, "turn", t.Value);
                if (cp.HasValue) Append(sb, "curPlayer", cp.Value);
                if (endedPlayer.HasValue) Append(sb, "endedPlayer", endedPlayer.Value);
                if (nextPlayer.HasValue) Append(sb, "nextPlayer", nextPlayer.Value);
                if (turnsAfter.HasValue) Append(sb, "turnsAfter", turnsAfter.Value);
                if (unitId.HasValue) Append(sb, "unitId", unitId.Value);
                if (cate.HasValue) Append(sb, "cate", cate.Value);
                if (!string.IsNullOrEmpty(target)) Append(sb, "target", target);
                if (hasTarget.HasValue) Append(sb, "hasTarget", hasTarget.Value);
                if (attach.HasValue) Append(sb, "attach", attach.Value);
                if (localControl.HasValue) Append(sb, "localControl", localControl.Value);
                if (battle != null)
                    Append(sb, "coIndex", battle.current_co_index);
                if (!string.IsNullOrEmpty(detail)) Append(sb, "detail", detail);
                sb.Append('}');
                lock (Gate)
                {
                    if (_writer == null)
                        return;
                    _writer.WriteLine(sb.ToString());
                    _writer.Flush();
                }
            }
            catch (Exception ex)
            {
                LanMpPlugin.Log?.LogWarning("[SyncTrace] write: " + ex.Message);
            }
        }

        public static void EvIntent(string ev, IntentDto intent, string detail = null)
        {
            if (intent == null)
            {
                Ev(ev, detail: detail);
                return;
            }
            Ev(ev,
                kind: intent.kind,
                intentId: intent.intentId,
                turn: intent.turn,
                curPlayer: intent.playerIndex,
                unitId: intent.netUnitId >= 0 ? intent.netUnitId : (int?)null,
                cate: intent.kind == "DoAction" ? intent.actionCate : (int?)null,
                target: intent.hasTarget ? intent.targetX + "," + intent.targetY : null,
                hasTarget: intent.hasTarget,
                detail: detail);
        }

        public static void EvCommand(string ev, CommandDto cmd, string detail = null)
        {
            if (cmd == null)
            {
                Ev(ev, detail: detail);
                return;
            }
            Ev(ev,
                kind: cmd.kind,
                cmdId: cmd.cmdId,
                intentId: cmd.sourceIntentId,
                turn: cmd.kind == "EndTurn" ? cmd.turnBefore : cmd.turn,
                curPlayer: cmd.playerIndex,
                endedPlayer: cmd.kind == "EndTurn" ? cmd.endedPlayerIndex : (int?)null,
                nextPlayer: cmd.kind == "EndTurn" ? cmd.nextPlayerIndex : (int?)null,
                turnsAfter: cmd.kind == "EndTurn" ? cmd.turnsAfter : (int?)null,
                unitId: cmd.netUnitId >= 0 ? cmd.netUnitId : (int?)null,
                cate: cmd.kind == "DoAction" ? cmd.actionCate : (int?)null,
                target: cmd.hasTarget ? cmd.targetX + "," + cmd.targetY : null,
                hasTarget: cmd.kind == "DoAction" || cmd.kind == "UnitMoved" ? cmd.hasTarget : (bool?)null,
                attach: !string.IsNullOrEmpty(cmd.resultAttachmentJson),
                detail: detail ?? cmd.endTurnReason);
        }

        private static void EnsureOpen()
        {
            if (_writer != null)
                return;
            if (_role == "?" || _role == "None")
                SetRole(LanMpPlugin.Instance?.Net?.Role ?? PeerRole.None,
                    LanMpPlugin.Instance?.Lobby?.BattleId);
            if (_writer == null && _role != "None" && _role != "?")
                OpenFile();
        }

        private static void OpenFile()
        {
            lock (Gate)
            {
                try
                {
                    var dir = Path.Combine(Paths.GameRootPath, "LanMp", "logs");
                    Directory.CreateDirectory(dir);
                    var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                    var bid = string.IsNullOrEmpty(_battleId) ? "nobattle" : Sanitize(_battleId);
                    _path = Path.Combine(dir, "sync-trace-" + _role + "-" + bid + "-" + stamp + ".ndjson");
                    _writer = new StreamWriter(new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8)
                    {
                        AutoFlush = true
                    };
                    LanMpPlugin.Log?.LogInfo("[SyncTrace] writing " + _path);
                    // header line
                    _writer.WriteLine(
                        "{\"ev\":\"TraceOpen\",\"role\":\"" + Esc(_role) + "\",\"battleId\":\"" + Esc(_battleId) +
                        "\",\"plugin\":\"" + Esc(LanMpPlugin.PluginVersion) + "\",\"path\":\"" + Esc(_path) + "\"}");
                }
                catch (Exception ex)
                {
                    LanMpPlugin.Log?.LogWarning("[SyncTrace] open: " + ex.Message);
                    _writer = null;
                    _path = null;
                }
            }
        }

        private static string Sanitize(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                s = s.Replace(c, '_');
            if (s.Length > 32)
                s = s.Substring(0, 32);
            return s;
        }

        private static void Append(StringBuilder sb, string key, string value, bool first = false)
        {
            if (!first) sb.Append(',');
            sb.Append('"').Append(key).Append("\":\"").Append(Esc(value)).Append('"');
        }

        private static void Append(StringBuilder sb, string key, int value)
        {
            sb.Append(',');
            sb.Append('"').Append(key).Append("\":").Append(value.ToString(CultureInfo.InvariantCulture));
        }

        private static void Append(StringBuilder sb, string key, bool value)
        {
            sb.Append(',');
            sb.Append('"').Append(key).Append("\":").Append(value ? "true" : "false");
        }

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", "");
        }
    }
}

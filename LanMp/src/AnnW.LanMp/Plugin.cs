using System;
using AnnW.LanMp.Authority;
using AnnW.LanMp.Checksum;
using AnnW.LanMp.Core;
using AnnW.LanMp.Protocol;
using AnnW.LanMp.Sync;
using AnnW.LanMp.Ui;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using System.IO;
using System.Security.Cryptography;

namespace AnnW.LanMp
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class LanMpPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "annw.lanmp";
        public const string PluginName = "AnnW LAN Multiplayer";
        public const string PluginVersion = "0.16.12";

        internal static LanMpPlugin Instance { get; private set; }
        internal static ManualLogSource Log { get; private set; }

        internal ConfigEntry<bool> Enabled;
        internal ConfigEntry<int> HostPort;
        internal ConfigEntry<string> JoinAddress;
        internal ConfigEntry<string> DisplayName;
        internal ConfigEntry<bool> ForceNoSteam;
        /// <summary>Debug only. Default false — never draw injector IMGUI.</summary>
        internal ConfigEntry<bool> EnableDebugImgui;
        internal ConfigEntry<bool> AutoOpenLobbyOnSkirmish;
        internal ConfigEntry<bool> StrictStateHash;
        internal ConfigEntry<bool> AttachResultsOnCommands;
        internal ConfigEntry<bool> RepairOnMismatch;
        internal ConfigEntry<bool> InterceptSkirmishStart;
        internal ConfigEntry<bool> EnableSyncTrace;
        internal ConfigEntry<bool> ClearSyncTraceOnStartup;

        // Kept so old cfg keys don't break; ignored for drawing.
        internal ConfigEntry<bool> ShowOverlay;
        internal ConfigEntry<bool> ShowHudBanner;

        internal ModuleHost Modules { get; private set; }
        internal NetSession Net { get; private set; }
        internal LobbySession Lobby { get; private set; }
        internal AuthorityService Authority { get; private set; }
        internal TurnAuthority TurnAuth { get; private set; }
        internal CommandSyncService Sync { get; private set; }
        internal StateChecksumService Checksum { get; private set; }

        private Harmony _harmony;
        private string _lastScene;
        private ILanLogger _lanLog;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            _lanLog = new BepInExLanLogger(Logger);

            Enabled = Config.Bind("General", "Enabled", true, "Enable LAN multiplayer plugin.");
            // Auto-on when running from *.Guest sandbox copy (same-PC dual instance).
            var defaultNoSteam = Application.dataPath != null
                && Application.dataPath.IndexOf(".Guest", System.StringComparison.OrdinalIgnoreCase) >= 0;
            ForceNoSteam = Config.Bind("General", "ForceNoSteam", defaultNoSteam,
                "Skip Steam at PreMenu (needed for 2nd AnnW.exe on one PC). Guest sandbox defaults true.");
            HostPort = Config.Bind("Network", "HostPort", 24555, "TCP port when hosting.");
            JoinAddress = Config.Bind("Network", "JoinAddress", "127.0.0.1:24555", "Host address for guest join.");
            DisplayName = Config.Bind("Network", "DisplayName", "", "Player display name shown in LAN room (empty = auto).");
            ShowOverlay = Config.Bind("UI", "ShowOverlay", false, "DEPRECATED — ignored. Use EnableDebugImgui.");
            ShowHudBanner = Config.Bind("UI", "ShowHudBanner", false, "DEPRECATED — ignored. Injector HUD disabled.");
            EnableDebugImgui = Config.Bind("UI", "EnableDebugImgui", false, "If true, draw legacy IMGUI debug panels. Keep false for normal play.");
            AutoOpenLobbyOnSkirmish = Config.Bind("UI", "AutoOpenLobbyOnSkirmish", false, "Deprecated.");
            StrictStateHash = Config.Bind("Sync", "StrictStateHash", true,
                "Pause only on EndTurn WIRE integrity failure (stamp≠attachment). ApplyDrift vs recapture never pauses.");
            AttachResultsOnCommands = Config.Bind("Sync", "AttachResultsOnCommands", true, "Host attaches ResultAttachment to DoAction/UnitMoved.");
            RepairOnMismatch = Config.Bind("Sync", "RepairOnMismatch", true, "Guest requests StateSnapshot on hash mismatch.");
            InterceptSkirmishStart = Config.Bind("UI", "InterceptSkirmishStart", false,
                "DEPRECATED. Vanilla skirmish is always single-player; LAN uses dedicated room UI.");
            InterceptSkirmishStart.Value = false;
            EnableSyncTrace = Config.Bind("Debug", "EnableSyncTrace", true,
                "Write Host/Guest NDJSON battle sync traces under <game>/LanMp/logs for Compare-SyncTrace.ps1.");
            ClearSyncTraceOnStartup = Config.Bind("Debug", "ClearSyncTraceOnStartup", false,
                "Delete prior sync-trace-*.ndjson on game launch. Default false — logs persist for debugging; use LanMp/tools/Clear-LanMpLogs.ps1 to wipe manually.");
            BattleSyncTrace.BindConfig(EnableSyncTrace);
            if (ClearSyncTraceOnStartup.Value)
                BattleSyncTrace.ClearLogDirectory();

            // Force-off legacy injector UI regardless of old cfg values.
            ShowOverlay.Value = false;
            ShowHudBanner.Value = false;
            EnableDebugImgui.Value = false;
            LanLobbyPanel.Visible = false;

            LogGameAssemblyHash();

            if (!Enabled.Value)
            {
                Log.LogInfo("LanMp disabled by config.");
                return;
            }

            Net = new NetSession(_lanLog);
            Lobby = new LobbySession(Net, _lanLog);
            Authority = new AuthorityService(Lobby, Net, Log);
            Lobby.IsBattleStartedGate = () => Lobby.StartAuthorized || Authority.InLanBattle;
            Lobby.CoPoolProvider = ListCoPool;
            TurnAuth = new TurnAuthority(Net, Authority, Log);
            Sync = new CommandSyncService(Net, Authority, AttachResultsOnCommands, Log);
            Sync.TurnAuth = TurnAuth;
            Checksum = new StateChecksumService(Net, Authority, StrictStateHash, RepairOnMismatch, Log);

            Modules = new ModuleHost(Log);
            Modules.Register(new NetModule(Net));
            Modules.Register(new LobbyModule(Lobby));
            Modules.Register(Authority);
            Modules.Register(TurnAuth);
            Modules.Register(Sync);
            Modules.Register(Checksum);
            Modules.StartAll();

            Net.OnLobbyRejected += p =>
            {
                var msg = p == null
                    ? "无法加入房间"
                    : (string.IsNullOrEmpty(p.message)
                        ? LobbySeatLogic.RejectMessage((LobbyRejectCode)p.code)
                        : p.message);
                UiFeedback.Push(msg);
                LanMpPlugin.Log?.LogWarning("[Net] LobbyReject: " + msg);
            };

            Sync.OnIntentNack += n =>
            {
                if (n == null) return;
                if (string.IsNullOrEmpty(n.message))
                    return;
                if (n.code == "already-moved" || n.code == "already-actioned")
                    return;
                UiFeedback.Push(n.message);
            };

            // Guest opens room on Welcome via OnConnected; hooks must exist before first join.
            LanRoomPanel.EnsureNetHooks();

            _harmony = new Harmony(PluginGuid);
            try
            {
                // Patch per-type so one bad Prefix signature cannot abort the whole assembly
                // (that previously wiped MainMenu LAN injection).
                var ok = 0;
                var fail = 0;
                foreach (var type in typeof(LanMpPlugin).Assembly.GetTypes())
                {
                    if (type.GetCustomAttributes(typeof(HarmonyPatch), inherit: true).Length == 0)
                        continue;
                    try
                    {
                        _harmony.CreateClassProcessor(type).Patch();
                        ok++;
                    }
                    catch (Exception ex)
                    {
                        fail++;
                        Log.LogError("[Harmony] Failed " + type.FullName + ": " + ex.Message);
                    }
                }

                Patches.DualInstanceSteamBypass.ApplyManualPatches(_harmony);
                var patched = 0;
                foreach (var m in _harmony.GetPatchedMethods())
                {
                    if (m.DeclaringType != null &&
                        (m.DeclaringType.Name.Contains("PreMenu") || m.DeclaringType.Name.Contains("SteamInterface")))
                    {
                        patched++;
                        Log.LogInfo("[Dual] Patched " + m.DeclaringType.Name + "." + m.Name);
                    }
                }
                Log.LogInfo("Harmony patch sweep done ok=" + ok + " fail=" + fail +
                            " ForceNoSteam=" + ForceNoSteam.Value +
                            " dualPatches=" + patched +
                            " dataPath=" + Application.dataPath);
            }
            catch (Exception ex)
            {
                Log.LogError("Harmony patch sweep failed: " + ex);
            }

            SceneManager.sceneLoaded += OnSceneLoaded;
            try { LanLocalization.EnsureRegistered(); } catch (Exception ex) { Log.LogWarning("[UI] LAN register early: " + ex.Message); }
            Log.LogInfo($"{PluginName} {PluginVersion} loaded. Injector IMGUI disabled.");
        }

        private void Update()
        {
            if (!Enabled.Value)
                return;

            if (Input.GetKeyDown(KeyCode.F8))
            {
                // Do not open LAN UI during solo skirmish / campaign battles.
                var sceneName = SceneManager.GetActiveScene().name ?? "";
                var inBattleScene = sceneName.IndexOf("Battle", StringComparison.OrdinalIgnoreCase) >= 0;
                var lanActive = (Authority != null && Authority.InLanBattle) ||
                                (Lobby != null && Lobby.StartAuthorized) ||
                                (Net != null && Net.Role != PeerRole.None);
                if (inBattleScene && !lanActive)
                {
                    // ignore F8 in single-player / campaign
                }
                else if (LanRoomPanel.IsOpen)
                {
                    // Stay in room; use 离开房间. F8 opens connect only when idle.
                }
                else if (LanLobbyNativePanel.IsOpen)
                    LanLobbyNativePanel.Close();
                else
                    LanLobbyNativePanel.Open();
            }

            var scene = SceneManager.GetActiveScene().name;
            if (scene != _lastScene)
            {
                _lastScene = scene;
                Log.LogInfo($"[Probe] Active scene -> {scene}");
                Modules.OnSceneChanged(scene);
            }

            Net?.Pump();
            Modules.Tick(Time.unscaledDeltaTime);
            LanLobbyNativePanel.Tick();
            LanRoomPanel.Tick();
        }

        private void OnGUI()
        {
            // Injector IMGUI thoroughly disabled. Lobby is native game popup only.
            if (!Enabled.Value || EnableDebugImgui == null || !EnableDebugImgui.Value)
                return;
            LanHud.Draw(this);
            if (ShowOverlay.Value)
                LanOverlay.Draw(this);
            if (LanLobbyPanel.Visible)
                LanLobbyPanel.Draw(this);
        }

        private void OnDestroy()
        {
            try
            {
                NotifyPeerBeforeShutdown("plugin-stop");
            }
            catch { /* shutting down */ }

            SceneManager.sceneLoaded -= OnSceneLoaded;
            Modules?.StopAll();
            BattleSyncTrace.Close("plugin-stop");
            _harmony?.UnpatchSelf();
            if (ReferenceEquals(Instance, this))
                Instance = null;
        }

        private void OnApplicationQuit()
        {
            try
            {
                NotifyPeerBeforeShutdown("app-quit");
            }
            catch { /* quitting */ }
        }

        private void NotifyPeerBeforeShutdown(string reason)
        {
            if (Authority == null || Net == null)
                return;
            if (!Authority.InLanBattle && !Net.IsConnected)
                return;

            if (Authority.InLanBattle)
            {
                var host = Net.Role == PeerRole.Host;
                Authority.AbortMatch(
                    host ? "host-left" : "guest-left",
                    reason,
                    broadcast: host,
                    loadMenu: false);
            }

            if (Net.IsConnected || Net.Role != PeerRole.None)
            {
                try { Net.Disconnect(reason); }
                catch { /* ignore */ }
            }
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            Log.LogInfo($"[Probe] sceneLoaded: {scene.name} mode={mode}");
        }

        private static System.Collections.Generic.IList<string> ListCoPool()
        {
            var list = new System.Collections.Generic.List<string>();
            try
            {
                foreach (var kv in SDBase<SD_ANNW_CO>.dic)
                {
                    if (kv.Value == null)
                        continue;
                    list.Add(kv.Key);
                }
            }
            catch
            {
                // Game types unavailable in some contexts
            }
            return list;
        }

        private void LogGameAssemblyHash()
        {
            try
            {
                var path = Path.Combine(Paths.GameRootPath, "AnnW_Data", "Managed", "Assembly-CSharp.dll");
                if (!File.Exists(path))
                {
                    Log.LogWarning("Assembly-CSharp.dll not found for hash.");
                    return;
                }

                using (var fs = File.OpenRead(path))
                using (var sha = SHA256.Create())
                {
                    var hash = sha.ComputeHash(fs);
                    var hex = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                    Log.LogInfo($"Assembly-CSharp SHA256={hex}");
                }
            }
            catch (Exception ex)
            {
                Log.LogWarning("Failed hashing Assembly-CSharp: " + ex.Message);
            }
        }
    }
}

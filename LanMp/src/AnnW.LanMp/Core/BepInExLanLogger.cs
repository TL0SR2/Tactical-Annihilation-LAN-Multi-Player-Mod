using AnnW.LanMp.Protocol;
using BepInEx.Logging;

namespace AnnW.LanMp.Core
{
    public sealed class BepInExLanLogger : ILanLogger
    {
        private readonly ManualLogSource _log;
        public BepInExLanLogger(ManualLogSource log) => _log = log;
        public void Info(string message) => _log.LogInfo(message);
        public void Warn(string message) => _log.LogWarning(message);
        public void Error(string message) => _log.LogError(message);
    }

    /// <summary>Adapts Protocol NetSession to plugin module lifecycle.</summary>
    public sealed class NetModule : ILanMpModule
    {
        public string Name => "M02-Net";
        public NetSession Session { get; }
        public NetModule(NetSession session) => Session = session;
        public void Start() { }
        public void Stop() => Session.Disconnect("plugin-stop");
        public void Tick(float dt) => Session.Tick(dt);
        public void OnSceneChanged(string sceneName) { }
    }

    public sealed class LobbyModule : ILanMpModule
    {
        public string Name => "M01-Lobby";
        public LobbySession Session { get; }
        public LobbyModule(LobbySession session) => Session = session;
        public void Start() => Session.Start();
        public void Stop() { }
        public void Tick(float dt) { }
        public void OnSceneChanged(string sceneName) { }
    }
}

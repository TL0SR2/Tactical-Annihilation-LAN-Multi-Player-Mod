using BepInEx.Logging;

namespace AnnW.LanMp.Core
{
    public interface ILanMpModule
    {
        string Name { get; }
        void Start();
        void Stop();
        void Tick(float dt);
        void OnSceneChanged(string sceneName);
    }

    public sealed class ModuleHost
    {
        private readonly ManualLogSource _log;
        private readonly System.Collections.Generic.List<ILanMpModule> _modules =
            new System.Collections.Generic.List<ILanMpModule>();

        public ModuleHost(ManualLogSource log) => _log = log;

        public void Register(ILanMpModule module)
        {
            _modules.Add(module);
            _log.LogInfo($"[Host] Registered module {module.Name}");
        }

        public void StartAll()
        {
            foreach (var m in _modules)
            {
                m.Start();
                _log.LogInfo($"[Host] Started {m.Name}");
            }
        }

        public void StopAll()
        {
            for (var i = _modules.Count - 1; i >= 0; i--)
            {
                try { _modules[i].Stop(); }
                catch (System.Exception ex) { _log.LogError($"Stop {_modules[i].Name}: {ex}"); }
            }
        }

        public void Tick(float dt)
        {
            foreach (var m in _modules)
                m.Tick(dt);
        }

        public void OnSceneChanged(string sceneName)
        {
            foreach (var m in _modules)
                m.OnSceneChanged(sceneName);
        }
    }
}

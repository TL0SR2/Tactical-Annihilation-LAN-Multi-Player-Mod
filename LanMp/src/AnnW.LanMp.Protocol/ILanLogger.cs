namespace AnnW.LanMp.Protocol
{
    public interface ILanLogger
    {
        void Info(string message);
        void Warn(string message);
        void Error(string message);
    }

    public sealed class NullLanLogger : ILanLogger
    {
        public static readonly NullLanLogger Instance = new NullLanLogger();
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { }
    }

    public sealed class CollectingLanLogger : ILanLogger
    {
        public readonly System.Collections.Generic.List<string> Lines =
            new System.Collections.Generic.List<string>();

        public void Info(string message) => Lines.Add("I:" + message);
        public void Warn(string message) => Lines.Add("W:" + message);
        public void Error(string message) => Lines.Add("E:" + message);
    }
}

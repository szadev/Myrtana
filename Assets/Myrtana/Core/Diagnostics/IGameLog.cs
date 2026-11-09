namespace Myrtana.Core
{
    /// <summary>Diagnostics port (Unity console, G3Cli stdout, a list in tests).</summary>
    public interface IGameLog
    {
        void Info(string message);
        void Warning(string message);
    }

    /// <summary>Discards everything.</summary>
    public sealed class NullGameLog : IGameLog
    {
        public static readonly NullGameLog Instance = new NullGameLog();
        public void Info(string message) { }
        public void Warning(string message) { }
    }
}

using System;
using System.Collections.Generic;

namespace Myrtana.Core
{
    /// <summary>Forwards each distinct message once, so per-frame checks ("not supported yet") do not flood the log.</summary>
    public sealed class OnceGameLog : IGameLog
    {
        readonly IGameLog _inner;
        readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);

        public OnceGameLog(IGameLog inner) => _inner = inner;

        public void Info(string message)
        {
            if (_seen.Add(message)) _inner.Info(message);
        }

        public void Warning(string message)
        {
            if (_seen.Add(message)) _inner.Warning(message);
        }
    }
}

using Myrtana.Core;
using UnityEngine;

namespace Myrtana.Unity
{
    /// <summary>Game log → Unity console.</summary>
    public sealed class UnityGameLog : IGameLog
    {
        public void Info(string message) => Debug.Log("[Myrtana] " + message);
        public void Warning(string message) => Debug.LogWarning("[Myrtana] " + message);
    }
}

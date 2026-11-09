using Myrtana.Core;

namespace Myrtana.Rules
{
    /// <summary>
    /// An enclave gave up and flees (G3 ChangeEnclaveStatus → Flee, which means liberation;
    /// PSQuestManager::OnEnclaveStateChanged counts FreeEnclave quests).
    /// </summary>
    public readonly struct EnclaveLiberated : IGameEvent
    {
        public EnclaveLiberated(string enclave) => Enclave = enclave;

        public string Enclave { get; }
    }
}

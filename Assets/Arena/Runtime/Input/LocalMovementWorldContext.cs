#nullable enable
using UnityEngine.SceneManagement;

namespace Arena.Input
{
    /// <summary>
    /// Local-player view of authoritative world membership.
    /// This controls which movement environment the client may use for prediction.
    /// </summary>
    public sealed class LocalMovementWorldContext
    {
        private string _worldKind = "OPEN";
        private ulong? _instanceId;
        private ulong? _arenaSeed;
        private bool _hasWorld;

        public string WorldKind => _worldKind;
        public ulong? InstanceId => _instanceId;
        public ulong? ArenaSeed => _arenaSeed;
        public bool HasWorld => _hasWorld;
        public bool IsOpenWorld => _worldKind == "OPEN";
        public bool IsInstance => _worldKind == "INSTANCE";

        public void SetWorld(string worldKind, ulong? instanceId, ulong? arenaSeed = null)
        {
            _worldKind = string.IsNullOrWhiteSpace(worldKind) ? "OPEN" : worldKind.ToUpperInvariant();
            _instanceId = instanceId;
            _arenaSeed = instanceId.HasValue ? arenaSeed : null;
            _hasWorld = true;
        }

        public void SetArenaSeedForInstance(ulong instanceId, ulong arenaSeed)
        {
            if (_instanceId == instanceId)
                _arenaSeed = arenaSeed;
        }

        public void Clear()
        {
            _worldKind = "OPEN";
            _instanceId = null;
            _arenaSeed = null;
            _hasWorld = false;
        }

        public bool TryGetPredictionEnvironment(out IMovementEnvironment? environment)
        {
            if (SceneManager.GetActiveScene().name == "TrainingGround")
            {
                environment = TrainingGroundMovementEnvironment.Shared;
                return true;
            }

            if (IsOpenWorld || !_hasWorld)
            {
                environment = OpenWorldMovementEnvironment.SharedForScene(SceneManager.GetActiveScene().name);
                return true;
            }

            if (IsInstance && _arenaSeed.HasValue)
            {
                environment = ArenaMovementEnvironment.Shared(_arenaSeed.Value);
                return true;
            }

            environment = null;
            return false;
        }
    }
}

#if UNITY_EDITOR
#nullable enable
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Arena.Editor
{
    /// <summary>
    /// Netcode audit R5: player builds must never ship stale copies of the
    /// shared movement/collision JSON, so the bundled Resources copies are
    /// re-synced from the server sources before every build. The runtime
    /// ContractVersionGuard then verifies the same files against the
    /// module's content stamps on connect.
    /// </summary>
    internal sealed class SharedMovementDataBuildSync : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            Debug.Log("[SharedMovementDataBuildSync] Syncing shared movement data before build.");
            GameplayCollisionExporter.SyncSharedMovementData();
        }
    }
}
#endif

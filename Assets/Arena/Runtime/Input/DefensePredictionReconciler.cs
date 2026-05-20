#nullable enable

using UnityEngine;
using Arena.Entity;
using Arena.Network;

namespace Arena.Input
{
    /// <summary>
    /// Keeps locally predicted defense presentation aligned with authoritative state.
    /// Defense input itself is dispatched through action-bar fixed actions.
    /// </summary>
    public sealed class DefensePredictionReconciler : MonoBehaviour
    {
        private void Update()
        {
            var entity = EntityRegistry.Instance?.LocalPlayerEntity;
            if (entity == null || !entity.IsAlive)
                return;

            var conn = NetworkManager.Instance?.Conn;
            if (conn == null)
                return;

            LocalDefensePrediction.Reconcile(conn, entity, System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
    }
}

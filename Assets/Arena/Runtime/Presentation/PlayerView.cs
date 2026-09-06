#nullable enable
using UnityEngine;
using Arena.Simulation;

namespace Arena.Presentation
{
    /// <summary>
    /// Reads render state from ClientSimulationState each frame and
    /// applies it to the GameObject's transform.
    ///
    /// For remote players only. The local player's transform is driven by
    /// LocalMovementPredictionDriver.
    ///
    /// INVARIANT: This class has zero knowledge of SpacetimeDB, reducers, or
    ///            table rows. It only reads from ClientSimulationState.
    /// </summary>
    public class PlayerView : MonoBehaviour
    {
        private const float RootMarkerRadius = 0.08f;
        private static readonly Color LocalPredictedColor = Color.cyan;
        private static readonly Color LocalAuthoritativeColor = Color.yellow;
        private static readonly Color RemoteObserverColor = new(1f, 0.2f, 1f);
        private static readonly Color RemotePlayerRenderedColor = new(0.2f, 0.9f, 1f);
        private static readonly Color AuthoritativeServerColor = new(1f, 0.3f, 0.3f);
        private static readonly Color HitCapsuleColor = new(1f, 0.55f, 0.1f);
        private ClientSimulationState _simState = null!;
        private bool _isLocalPlayer;

        public void Initialize(ClientSimulationState simState, bool isLocalPlayer = false)
        {
            _simState = simState;
            _isLocalPlayer = isLocalPlayer;
        }

        private void Update()
        {
            // Local pose is driven by LocalMovementPredictionDriver; this view presents remotes.
            if (_isLocalPlayer) return;

            if (_simState == null || !_simState.HasState) return;

            _simState.Tick(Time.deltaTime);

            transform.position = _simState.GetRenderPosition();

            float yawDeg = _simState.GetRenderYawDegrees();
            transform.rotation = Quaternion.Euler(0f, yawDeg, 0f);
        }

        private void OnDrawGizmos()
        {
            if (!Application.isPlaying || _simState == null || !_simState.HasState)
                return;

            DrawRootMarker(transform.position, _isLocalPlayer ? LocalPredictedColor : RemotePlayerRenderedColor);

            Vector3 serverPos = _simState.GetServerPosition();
            DrawRootMarker(serverPos, _isLocalPlayer ? LocalAuthoritativeColor : AuthoritativeServerColor);
            DrawHitCapsule(serverPos, _simState.HitRadius, _simState.HitHeight, HitCapsuleColor);

            float delta = Vector3.Distance(transform.position, serverPos);
            if (delta > 0.01f)
            {
                Gizmos.color = Color.white;
                Gizmos.DrawLine(transform.position, serverPos);
            }

            if (!_isLocalPlayer ||
                !_simState.TryGetRemoteObserverSample(Time.realtimeSinceStartup, out ClientSimulationState.RemoteObserverSample observerSample))
            {
                return;
            }

            Vector3 observerPos = observerSample.Position;
            DrawRootMarker(observerPos, RemoteObserverColor);
            DrawHitCapsule(observerPos, _simState.HitRadius, _simState.HitHeight, RemoteObserverColor);

            float localToObserverDelta = Vector3.Distance(transform.position, observerPos);
            if (localToObserverDelta > 0.01f)
            {
                Gizmos.color = RemoteObserverColor;
                Gizmos.DrawLine(transform.position, observerPos);
            }

            float observerToServerDelta = Vector3.Distance(observerPos, serverPos);
            if (observerToServerDelta > 0.01f)
            {
                Gizmos.color = Color.gray;
                Gizmos.DrawLine(observerPos, serverPos);
            }
        }

        private static void DrawRootMarker(Vector3 position, Color color)
        {
            Gizmos.color = color;
            Gizmos.DrawSphere(position, RootMarkerRadius);
            Gizmos.DrawLine(position + Vector3.left * 0.18f, position + Vector3.right * 0.18f);
            Gizmos.DrawLine(position + Vector3.forward * 0.18f, position + Vector3.back * 0.18f);
        }

        private static void DrawHitCapsule(Vector3 rootPosition, float hitRadius, float hitHeight, Color color)
        {
            float radius = Mathf.Max(hitRadius, 0.1f);
            float height = Mathf.Max(hitHeight, radius * 2.0f);
            Vector3 bottom = rootPosition;
            Vector3 top = rootPosition + Vector3.up * height;

            Gizmos.color = color;
            Gizmos.DrawWireSphere(bottom, radius);
            Gizmos.DrawWireSphere(top, radius);
            Gizmos.DrawLine(bottom + Vector3.forward * radius, top + Vector3.forward * radius);
            Gizmos.DrawLine(bottom - Vector3.forward * radius, top - Vector3.forward * radius);
            Gizmos.DrawLine(bottom + Vector3.right * radius, top + Vector3.right * radius);
            Gizmos.DrawLine(bottom - Vector3.right * radius, top - Vector3.right * radius);
        }
    }
}

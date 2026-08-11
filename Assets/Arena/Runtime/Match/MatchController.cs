#nullable enable
using UnityEngine;
using UnityEngine.SceneManagement;
using Arena.Entity;
using Arena.Network;
using Arena.Presentation;
using Arena.UI;
using SpacetimeDB;

namespace Arena.Match
{
    /// <summary>
    /// Drives match-phase transitions on the client.
    ///
    /// Exposes CanCast — the single gate checked by SpellInputHandler before
    /// sending any cast request. False when the match is not in progress or
    /// when the local player has been eliminated.
    ///
    /// Notifies MatchOverlay whenever phase changes.
    ///
    /// INVARIANT: No network writes. Read-only from MatchStateCache.
    /// </summary>
    public class MatchController : MonoBehaviour
    {
        public static MatchController? Instance { get; private set; }

        public bool CanCast
        {
            get
            {
                var cache = MatchStateCache.Instance;
                // Outside of an arena match (training, practice, no instance) — always allow.
                if (!cache.IsArenaMode) return true;
                // Inside an arena match — require in-progress and not eliminated.
                return cache.IsInProgress &&
                       !(EntityRegistry.Instance?.LocalPlayerEntity?.IsEliminated ?? false);
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (Instance != null) return;
            if (!ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
                return;

            var go = new GameObject("MatchController");
            DontDestroyOnLoad(go);
            go.AddComponent<MatchController>();
            go.AddComponent<TrainingGroundSceneController>();
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        private MatchPhase _lastPhase = MatchPhase.Waiting;
        private Identity? _lastWinnerId;
        private byte? _lastWinnerTeamId;
        private bool _lastIsTeamMatch;
        private bool _lastIsArenaMode;
        private ulong? _lastLocalInstanceId;
        private ulong? _trackedInstanceId;
        private bool _sawMultipleParticipants;
        private bool _spectatorCameraRetargeted;

        private void Update()
        {
            if (!ArenaRuntimeSceneGate.ShouldRunArenaRuntimeInActiveScene())
                return;

            var cache = MatchStateCache.Instance;

            UpdateClientFallbackState(cache);
            MaybeRetargetSpectatorCamera(cache);

            if (cache.Phase == _lastPhase &&
                cache.WinnerId == _lastWinnerId &&
                cache.WinnerTeamId == _lastWinnerTeamId &&
                cache.IsTeamMatch == _lastIsTeamMatch &&
                cache.IsArenaMode == _lastIsArenaMode &&
                cache.LocalInstanceId == _lastLocalInstanceId)
            {
                return;
            }

            _lastPhase = cache.Phase;
            _lastWinnerId = cache.WinnerId;
            _lastWinnerTeamId = cache.WinnerTeamId;
            _lastIsTeamMatch = cache.IsTeamMatch;
            _lastIsArenaMode = cache.IsArenaMode;
            _lastLocalInstanceId = cache.LocalInstanceId;
            MatchOverlay.Instance?.Refresh(cache);

            // Reset spectator retarget when phase resets to Waiting (new match).
            if (cache.Phase == MatchPhase.Waiting)
                _spectatorCameraRetargeted = false;
        }

        private void MaybeRetargetSpectatorCamera(MatchStateCache cache)
        {
            if (_spectatorCameraRetargeted) return;
            if (!cache.IsArenaMode) return;

            var localEntity = EntityRegistry.Instance?.LocalPlayerEntity;
            if (localEntity == null || !localEntity.IsEliminated) return;

            // Find a surviving player to spectate.
            PlayerEntity? survivor = null;
            if (EntityRegistry.Instance != null)
            {
                foreach (var entity in EntityRegistry.Instance.AllPlayers)
                {
                    if (entity.Identity == localEntity.Identity) continue;
                    if (!entity.IsEliminated && entity.IsAlive)
                    {
                        survivor = entity;
                        break;
                    }
                }
            }

            if (survivor == null) return;

            _spectatorCameraRetargeted = true;
            LocalPlayerCamera.SetTarget(survivor.GameObject.transform);

            var orbit = localEntity.GameObject.GetComponent<CameraOrbitController>();
            if (orbit != null)
            {
                var survivorRoot = FindChildRecursive(survivor.GameObject.transform, "PlayerCameraRoot");
                orbit.SetTarget(survivorRoot != null ? survivorRoot : survivor.GameObject.transform);
            }
        }

        private static Transform? FindChildRecursive(Transform root, string childName)
        {
            if (root.name == childName) return root;
            foreach (Transform child in root)
            {
                var found = FindChildRecursive(child, childName);
                if (found != null) return found;
            }
            return null;
        }

        private void UpdateClientFallbackState(MatchStateCache cache)
        {
            if (_trackedInstanceId != cache.LocalInstanceId)
            {
                _trackedInstanceId = cache.LocalInstanceId;
                _sawMultipleParticipants = false;
            }

            if (!cache.IsArenaMode || !cache.LocalInstanceId.HasValue)
            {
                cache.ClearClientFallbackEnded();
                return;
            }

            int totalVisible = 0;
            int aliveVisible = 0;
            Identity? lastAliveIdentity = null;

            if (EntityRegistry.Instance != null)
            {
                foreach (var entity in EntityRegistry.Instance.AllPlayers)
                {
                    totalVisible++;
                    if (!entity.IsAlive || entity.IsEliminated)
                        continue;

                    aliveVisible++;
                    lastAliveIdentity = entity.Identity;
                }
            }

            if (totalVisible >= 2)
                _sawMultipleParticipants = true;

            if (_sawMultipleParticipants && totalVisible >= 2 && aliveVisible <= 1)
            {
                cache.SetClientFallbackEnded(aliveVisible == 1 ? lastAliveIdentity : null);
            }
            else
            {
                cache.ClearClientFallbackEnded();
            }
        }
    }

    public sealed class TrainingGroundSceneController : MonoBehaviour
    {
        private const string SceneName = "TrainingGround";
        private static readonly float[] LaneX = { 42f, 50f, 58f, 66f };
        private static readonly float[] LaneZ = { -12f, -4f, 4f, 12f };

        private bool _joinRequested;
        private GameObject? _laneMarkersRoot;

        private void Update()
        {
            bool inTrainingGround = SceneManager.GetActiveScene().name == SceneName;
            if (!inTrainingGround)
            {
                _joinRequested = false;
                DestroyLaneMarkers();
                return;
            }

            EnsureLaneMarkers();

            if (NetworkManager.Instance?.IsConnected != true || NetworkManager.Instance.Conn == null)
                return;

            if (MatchStateCache.Instance.IsPracticeMode)
            {
                _joinRequested = false;
                return;
            }

            if (_joinRequested)
                return;

            _joinRequested = true;
            NetworkManager.Instance.Conn.Reducers.JoinTrainingInstance();
        }

        private void EnsureLaneMarkers()
        {
            if (_laneMarkersRoot != null)
                return;

            _laneMarkersRoot = new GameObject("TrainingGroundMeleeLaneMarkers");
            DontDestroyOnLoad(_laneMarkersRoot);

            for (int column = 0; column < LaneX.Length; column++)
            {
                for (int row = 0; row < LaneZ.Length; row++)
                {
                    int strikeIndex = column * LaneZ.Length + row + 1;
                    CreatePad(
                        strikeIndex,
                        new Vector3(LaneX[column] - 3f, 0.05f, LaneZ[row]),
                        $"Strike {strikeIndex}");
                }
            }
        }

        private void CreatePad(int strikeIndex, Vector3 position, string label)
        {
            if (_laneMarkersRoot == null)
                return;

            var pad = GameObject.CreatePrimitive(PrimitiveType.Cube);
            pad.name = $"TrainingPad_{strikeIndex}";
            pad.transform.SetParent(_laneMarkersRoot.transform, false);
            pad.transform.position = position;
            pad.transform.localScale = new Vector3(2.5f, 0.1f, 2.5f);
            var renderer = pad.GetComponent<Renderer>();
            if (renderer != null)
                renderer.material.color = new Color(0.85f, 0.68f, 0.22f, 1f);
            var collider = pad.GetComponent<Collider>();
            if (collider != null)
                collider.enabled = false;

            var labelGo = new GameObject($"TrainingPadLabel_{strikeIndex}");
            labelGo.transform.SetParent(_laneMarkersRoot.transform, false);
            labelGo.transform.position = position + new Vector3(0f, 1.2f, 0f);
            var text = labelGo.AddComponent<TextMesh>();
            text.text = label;
            text.characterSize = 0.18f;
            text.fontSize = 48;
            text.anchor = TextAnchor.MiddleCenter;
            text.alignment = TextAlignment.Center;
            text.color = Color.white;
        }

        private void DestroyLaneMarkers()
        {
            if (_laneMarkersRoot == null)
                return;

            Destroy(_laneMarkersRoot);
            _laneMarkersRoot = null;
        }
    }
}

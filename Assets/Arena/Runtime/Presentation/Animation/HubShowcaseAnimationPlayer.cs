#nullable enable

using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Arena.Presentation
{
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class HubShowcaseAnimationPlayer : MonoBehaviour
    {
        [SerializeField] private Animator? _animator;
        [SerializeField] private AnimationClip? _clip;
        [SerializeField] private float _speed = 1f;
        [SerializeField] private bool _playInEditMode = true;

        private PlayableGraph _graph;
        private AnimationClipPlayable _clipPlayable;
        private double _time;

#if UNITY_EDITOR
        private double _lastEditorTime;
#endif

        public void Configure(Animator animator, AnimationClip clip, float speed = 1f, float startTime = 0f)
        {
            _animator = animator;
            _clip = clip;
            _speed = Mathf.Max(0f, speed);
            _time = Mathf.Repeat(startTime, Mathf.Max(clip.length, 0.001f));
            RebuildGraph();
        }

        private void OnEnable()
        {
            ResolveAnimator();
            RebuildGraph();
#if UNITY_EDITOR
            _lastEditorTime = EditorApplication.timeSinceStartup;
            EditorApplication.update += EditorTick;
#endif
        }

        private void OnDisable()
        {
#if UNITY_EDITOR
            EditorApplication.update -= EditorTick;
#endif
            DestroyGraph();
        }

        private void OnDestroy()
        {
            DestroyGraph();
        }

        private void Update()
        {
            if (Application.isPlaying)
                Tick(Time.deltaTime);
        }

#if UNITY_EDITOR
        private void EditorTick()
        {
            if (Application.isPlaying || !_playInEditMode)
                return;

            double now = EditorApplication.timeSinceStartup;
            float deltaTime = Mathf.Clamp((float)(now - _lastEditorTime), 0f, 0.1f);
            _lastEditorTime = now;

            Tick(deltaTime);
            EditorApplication.QueuePlayerLoopUpdate();
        }
#endif

        private void Tick(float deltaTime)
        {
            if (_clip == null || _animator == null || !_graph.IsValid())
                return;

            float length = Mathf.Max(_clip.length, 0.001f);
            _time = (_time + deltaTime * _speed) % length;
            _clipPlayable.SetTime(_time);
            _graph.Evaluate(0f);
        }

        private void ResolveAnimator()
        {
            if (_animator == null)
                _animator = GetComponentInChildren<Animator>(true);
        }

        private void RebuildGraph()
        {
            DestroyGraph();
            ResolveAnimator();
            if (_animator == null || _clip == null)
                return;

            // The showcase avatar reuses the gameplay prefab, which ships with the full
            // Arena_Character controller. If we leave the controller bound, its state
            // machine keeps re-posing the rig every frame and fights this graph —
            // the avatar snaps to the controller's default state (T-pose) between our
            // evaluations. Clearing the controller leaves this graph as the sole driver.
            _animator.runtimeAnimatorController = null;
            _animator.applyRootMotion = false;
            _animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            _animator.enabled = true;

            _graph = PlayableGraph.Create($"{nameof(HubShowcaseAnimationPlayer)}_{name}");
            _graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);

            _clipPlayable = AnimationClipPlayable.Create(_graph, _clip);
            _clipPlayable.SetApplyFootIK(false);
            _clipPlayable.SetTime(_time);
            _clipPlayable.SetSpeed(0d);

            AnimationPlayableOutput output = AnimationPlayableOutput.Create(_graph, "Hub Showcase Animation", _animator);
            output.SetSourcePlayable(_clipPlayable);
            _graph.Play();
            _graph.Evaluate(0f);
        }

        private void DestroyGraph()
        {
            if (_graph.IsValid())
                _graph.Destroy();
        }
    }
}

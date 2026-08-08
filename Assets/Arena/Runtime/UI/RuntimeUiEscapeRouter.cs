#nullable enable

using System.Collections.Generic;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Arena.UI
{
    public interface IEscapeCloseable
    {
        int EscapeClosePriority { get; }
        bool IsEscapeCloseable { get; }
        bool TryCloseForEscape();
    }

    public static class RuntimeUiEscapeRouter
    {
        private static readonly List<IEscapeCloseable> Closeables = new();
        private static int s_escapeConsumedFrame = -1;

        public static bool EscapeConsumedThisFrame => s_escapeConsumedFrame == Time.frameCount;

        public static void ConsumeEscapeThisFrame()
        {
            s_escapeConsumedFrame = Time.frameCount;
        }

        public static void Register(IEscapeCloseable closeable)
        {
            if (closeable == null || Closeables.Contains(closeable))
                return;

            Closeables.Add(closeable);
        }

        public static void Unregister(IEscapeCloseable closeable)
        {
            Closeables.Remove(closeable);
        }

        public static bool TryCloseTopmost()
        {
            if (EscapeConsumedThisFrame)
                return true;

            int bestIndex = -1;
            int bestPriority = int.MinValue;
            for (int i = Closeables.Count - 1; i >= 0; i--)
            {
                IEscapeCloseable closeable = Closeables[i];
                if (IsDestroyed(closeable))
                {
                    Closeables.RemoveAt(i);
                    continue;
                }

                if (!closeable.IsEscapeCloseable)
                    continue;

                if (closeable.EscapeClosePriority > bestPriority)
                {
                    bestPriority = closeable.EscapeClosePriority;
                    bestIndex = i;
                }
            }

            if (bestIndex < 0)
                return false;

            bool closed = Closeables[bestIndex].TryCloseForEscape();
            if (closed)
                ConsumeEscapeThisFrame();
            return closed;
        }

        private static bool IsDestroyed(IEscapeCloseable closeable)
            => closeable is Object unityObject && unityObject == null;
    }

    /// <summary>
    /// Pumps Escape into the shared closeable stack even in non-gameplay scenes
    /// such as the Hub, where the combat input handlers are not present.
    /// Gameplay-specific Escape fallbacks still run when no overlay closes.
    /// </summary>
    [DefaultExecutionOrder(-950)]
    internal sealed class RuntimeUiEscapeInputDriver : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (FindAnyObjectByType<RuntimeUiEscapeInputDriver>() != null)
                return;

            GameObject host = new(nameof(RuntimeUiEscapeInputDriver));
            DontDestroyOnLoad(host);
            host.AddComponent<RuntimeUiEscapeInputDriver>();
        }

        private void Update()
        {
            if (RuntimeUiEscapeRouter.EscapeConsumedThisFrame || !WasEscapePressedThisFrame())
                return;

            RuntimeUiEscapeRouter.TryCloseTopmost();
        }

        private static bool WasEscapePressedThisFrame()
        {
#if ENABLE_INPUT_SYSTEM
            return Keyboard.current?.escapeKey.wasPressedThisFrame == true;
#else
            return UnityEngine.Input.GetKeyDown(KeyCode.Escape);
#endif
        }
    }

    public static class RuntimeUiLayer
    {
        private const int BaseInterfaceSortingOrder = 40;
        private const int MaxInterfaceSortingOrder = 30000;
        private static int s_nextInterfaceSortingOrder = BaseInterfaceSortingOrder;

        public static void BringToFront(Canvas? canvas)
        {
            if (canvas == null)
                return;

            canvas.sortingOrder = NextSortingOrder();
        }

        /// <summary>
        /// Allocates the next interface sorting order. UI Toolkit panels share
        /// the same allocation pool so uGUI canvases and UITK documents keep a
        /// consistent front-most ordering.
        /// </summary>
        public static int NextSortingOrder()
        {
            if (s_nextInterfaceSortingOrder >= MaxInterfaceSortingOrder)
                s_nextInterfaceSortingOrder = BaseInterfaceSortingOrder;

            return ++s_nextInterfaceSortingOrder;
        }
    }
}

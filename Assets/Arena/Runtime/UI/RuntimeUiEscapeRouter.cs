#nullable enable

using System.Collections.Generic;
using UnityEngine;

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
}

#nullable enable

using UnityEngine;

namespace Arena.UI
{
    internal static class ActionBarLayout
    {
        public const int Rows = 3;
        public const int Columns = 9;
        public const int CellCount = Rows * Columns;
        public const float SlotSize = 68f;
        public const float Gap = 4f;
        public const string SlotPrefabResourcePath = "UI/ActionBar/ActionBarSlot";

        public static float Pitch => SlotSize + Gap;

        public static Vector2 SlotVector => new(SlotSize, SlotSize);

        public static Vector2 GapVector => new(Gap, Gap);

        public static Vector2 GridSize => new(
            Columns * SlotSize + (Columns - 1) * Gap,
            Rows * SlotSize + (Rows - 1) * Gap);

        public static Vector2 CellPosition(int row, int col)
        {
            return new Vector2(col * Pitch, (Rows - 1 - row) * Pitch);
        }

        public static Vector2 CenteredOffset(Vector2 containerSize)
        {
            Vector2 gridSize = GridSize;
            return new Vector2(
                (containerSize.x - gridSize.x) * 0.5f,
                (containerSize.y - gridSize.y) * 0.5f);
        }
    }
}

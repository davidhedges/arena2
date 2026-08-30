#nullable enable

using UnityEngine;

namespace Arena.UI
{
    internal static class ActionBarLayout
    {
        public const int RowsPerBar = 2;
        public const int BarCount = 2;
        public const int Rows = RowsPerBar * BarCount;
        public const int VisibleActionRows = Rows;
        public const int DisplayRows = Rows;
        public const int FirstActionDisplayRow = 0;
        public const int Columns = 9;
        public const int CellCount = Rows * Columns;
        public const float SlotSize = 68f;
        public const float Gap = 4f;
        public const float IconInset = 6f;
        public const string SlotPrefabResourcePath = "UI/ActionBar/ActionBarSlot";

        public static float Pitch => SlotSize + Gap;

        public static Vector2 SlotVector => new(SlotSize, SlotSize);

        public static Vector2 GapVector => new(Gap, Gap);

        public static Vector2 GridSize => new(
            Columns * SlotSize + (Columns - 1) * Gap,
            DisplayRows * SlotSize + (DisplayRows - 1) * Gap);

        public static Vector2 CellPosition(int row, int col)
        {
            return DisplayCellPosition(row, col);
        }

        public static Vector2 ActionCellPosition(int actionRow, int col)
        {
            return DisplayCellPosition(FirstActionDisplayRow + actionRow, col);
        }

        public static Vector2 DisplayCellPosition(int displayRow, int col)
        {
            return new Vector2(col * Pitch, (DisplayRows - 1 - displayRow) * Pitch);
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

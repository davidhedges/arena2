#nullable enable

namespace Arena.UI
{
    public enum HubViewScreen
    {
        Play = 0,
    }

    public static class HubViewState
    {
        public static HubViewScreen Current { get; private set; } = HubViewScreen.Play;

        public static void Show(HubViewScreen screen)
        {
            Current = screen;
        }
    }
}

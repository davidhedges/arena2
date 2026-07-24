#nullable enable

namespace Arena.Presentation.Dice
{
    /// <summary>
    /// Network-neutral input to the visual presenter. It contains an already
    /// resolved result; it has no RNG, transport, reward, or gameplay types.
    /// </summary>
    public readonly struct ResolvedDiceRoll
    {
        public string RequestId { get; }
        public string DieId { get; }
        public int Value { get; }

        public ResolvedDiceRoll(string requestId, string dieId, int value)
        {
            RequestId = requestId ?? string.Empty;
            DieId = dieId ?? string.Empty;
            Value = value;
        }
    }
}

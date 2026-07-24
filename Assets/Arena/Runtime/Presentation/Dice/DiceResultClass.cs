#nullable enable
namespace Arena.Presentation.Dice
{
    public enum DiceResultClass
    {
        Ordinary,
        Positive,
        Negative
    }

    public static class DiceResultClassifier
    {
        public static DiceResultClass Classify(DiceDefinition definition, int result)
        {
            if (definition.DieId == "d20" && result == 1)
                return DiceResultClass.Negative;
            if (result == definition.Sides)
                return DiceResultClass.Positive;
            return DiceResultClass.Ordinary;
        }
    }
}

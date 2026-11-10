using PawnMind.Core;

namespace PawnMind.Tests
{
    internal static class LabelValidatorTests
    {
        [Test]
        public static void Label_Accepts_Editable_Condition()
        {
            Assert.Equal(LabelCheck.Ok, LabelValidator.Validate(true, true, true, true), "valid");
        }

        [Test]
        public static void Label_Rejects_Not_Editable()
        {
            Assert.Equal(LabelCheck.NotEditable, LabelValidator.Validate(true, false, true, true), "not editable");
        }

        [Test]
        public static void Label_Rejects_Missing_Condition()
        {
            Assert.Equal(LabelCheck.ConditionNotFound, LabelValidator.Validate(true, true, true, false), "missing condition");
        }
    }
}

namespace PawnMind.Core
{
    public enum LabelCheck
    {
        Ok,
        ItemNotFound,
        NotEditable,
        CategoryNotFound,
        ConditionNotFound
    }

    public static class LabelValidator
    {
        public static LabelCheck Validate(bool itemExists, bool editable, bool categoryExists, bool conditionExists)
        {
            if (!itemExists) return LabelCheck.ItemNotFound;
            if (!editable) return LabelCheck.NotEditable;
            if (!categoryExists) return LabelCheck.CategoryNotFound;
            if (!conditionExists) return LabelCheck.ConditionNotFound;
            return LabelCheck.Ok;
        }
    }
}

namespace PawnMind.Core
{
    public static class MarkingPenMode
    {
        public const int Eraser = 5;

        public static bool IsValid(int mode)
        {
            return mode >= 0 && mode <= Eraser;
        }

        public static string ColorId(int mode)
        {
            switch (mode)
            {
                case 0: return "red";
                case 1: return "purple";
                case 2: return "green";
                case 3: return "blue";
                case 4: return "orange";
                case 5: return "eraser";
                default: return "unknown";
            }
        }
    }
}

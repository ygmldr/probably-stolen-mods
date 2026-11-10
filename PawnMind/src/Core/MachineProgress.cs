namespace PawnMind.Core
{
    public static class MachineProgress
    {
        public static int Percent(int current, int target)
        {
            if (target <= 0) return 0;
            int value = current * 100 / target;
            if (value < 0) return 0;
            if (value > 100) return 100;
            return value;
        }
    }
}

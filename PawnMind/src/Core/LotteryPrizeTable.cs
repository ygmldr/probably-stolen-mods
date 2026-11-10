namespace PawnMind.Core
{
    public static class LotteryPrizeTable
    {
        public static int ForMatches(int matches)
        {
            switch (matches)
            {
                case 6: return 1000000;
                case 5: return 100000;
                case 4: return 2500;
                case 3: return 300;
                case 2: return 35;
                default: return 0;
            }
        }
    }
}

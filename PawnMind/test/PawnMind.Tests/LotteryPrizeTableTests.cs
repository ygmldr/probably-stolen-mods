using PawnMind.Core;

namespace PawnMind.Tests
{
    internal static class LotteryPrizeTableTests
    {
        [Test]
        public static void Prize_Table_Matches_Game()
        {
            Assert.Equal(0, LotteryPrizeTable.ForMatches(0), "none");
            Assert.Equal(35, LotteryPrizeTable.ForMatches(2), "2");
            Assert.Equal(300, LotteryPrizeTable.ForMatches(3), "3");
            Assert.Equal(2500, LotteryPrizeTable.ForMatches(4), "4");
            Assert.Equal(100000, LotteryPrizeTable.ForMatches(5), "5");
            Assert.Equal(1000000, LotteryPrizeTable.ForMatches(6), "6");
        }
    }
}

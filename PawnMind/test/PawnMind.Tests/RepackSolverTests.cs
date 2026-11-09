using System.Collections.Generic;
using PawnMind.Core;

namespace PawnMind.Tests
{
    internal static class RepackSolverTests
    {
        private static RepackItem Item(int id, int w, int h)
        {
            var cells = new int[w * h];
            for (int i = 0; i < cells.Length; i++) cells[i] = 1;
            return new RepackItem { Id = id, Width = w, Height = h, Cells = cells };
        }

        [Test]
        public static void Solver_Packs_All_Items_When_Space_Exists()
        {
            var items = new List<RepackItem> { Item(1, 2, 2), Item(2, 1, 2), Item(3, 1, 2) };
            RepackResult result = RepackSolver.Solve(4, 4, new bool[16], items);
            Assert.True(result.Success, "solution found");
            Assert.Equal(3, result.Placements.Count, "all items placed");
        }

        [Test]
        public static void Solver_Fails_When_Overfull()
        {
            var items = new List<RepackItem>
            {
                Item(1, 1, 1), Item(2, 1, 1), Item(3, 1, 1), Item(4, 1, 1), Item(5, 1, 1)
            };
            RepackResult result = RepackSolver.Solve(2, 2, new bool[4], items);
            Assert.False(result.Success, "no solution");
        }

        [Test]
        public static void Solver_Respects_Blocked_Cells()
        {
            var blocked = new bool[4];
            blocked[0] = true;
            RepackResult result = RepackSolver.Solve(2, 2, blocked, new List<RepackItem> { Item(1, 2, 2) });
            Assert.False(result.Success, "blocked cell prevents placement");
        }

        [Test]
        public static void Solver_Rotates_To_Fit()
        {
            // 3x2 grid: 3x1 horizontal plus 1x3 vertical (needs rotation to fit).
            var items = new List<RepackItem> { Item(1, 3, 1), Item(2, 1, 3) };
            RepackResult result = RepackSolver.Solve(3, 2, new bool[6], items);
            Assert.True(result.Success, "rotation allows fit");
        }
    }
}

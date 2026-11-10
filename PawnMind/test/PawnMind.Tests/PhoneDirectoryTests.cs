using System.Collections.Generic;
using PawnMind.Core;

namespace PawnMind.Tests
{
    internal static class PhoneDirectoryTests
    {
        private static List<PhoneContact> Contacts()
        {
            return new List<PhoneContact>
            {
                new PhoneContact { Number = 8376, Name = "Wild", Shown = true },
                new PhoneContact { Number = 8815, Name = "Red Imp", Shown = false }
            };
        }

        [Test]
        public static void Visible_Hides_Unshown_By_Default()
        {
            List<PhoneContact> result = PhoneDirectory.Visible(Contacts(), false);
            Assert.Equal(1, result.Count, "one shown");
            Assert.Equal(8376L, result[0].Number, "wild");
        }

        [Test]
        public static void Visible_Includes_All_When_Requested()
        {
            List<PhoneContact> result = PhoneDirectory.Visible(Contacts(), true);
            Assert.Equal(2, result.Count, "all contacts");
        }
    }
}

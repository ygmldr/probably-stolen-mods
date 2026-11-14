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

        [Test]
        public static void Hidden_Contacts_Need_The_Debug_Profile()
        {
            bool includeHidden;
            string error;

            Assert.True(PhoneDirectory.TryResolveIncludeHidden(false, false, false, out includeHidden, out error),
                "a visible-only request is always allowed");
            Assert.False(includeHidden, "visible-only hides unshown contacts");
            Assert.Null(error, "no error for the default request");

            Assert.True(PhoneDirectory.TryResolveIncludeHidden(false, true, true, out includeHidden, out error),
                "visible-only stays allowed in debug/recording");
            Assert.False(includeHidden, "visible-only never expands");

            Assert.False(PhoneDirectory.TryResolveIncludeHidden(true, false, false, out includeHidden, out error),
                "production must refuse hidden contacts");
            Assert.Equal(PhoneDirectory.HiddenContactsForbidden, error, "stable production error code");
            Assert.False(includeHidden, "refused request expands nothing");

            Assert.False(PhoneDirectory.TryResolveIncludeHidden(true, true, true, out includeHidden, out error),
                "recording must refuse hidden contacts even in debug");
            Assert.Equal(PhoneDirectory.HiddenContactsForbidden, error, "stable recording error code");
            Assert.False(includeHidden, "recording expands nothing");

            Assert.True(PhoneDirectory.TryResolveIncludeHidden(true, true, false, out includeHidden, out error),
                "the explicit debug profile may expand hidden contacts");
            Assert.True(includeHidden, "debug expands");
            Assert.Null(error, "no error in debug");
        }
    }
}

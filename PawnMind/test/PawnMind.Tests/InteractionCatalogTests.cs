using System.Collections.Generic;
using PawnMind.Core;

namespace PawnMind.Tests
{
    internal static class InteractionCatalogTests
    {
        [Test]
        public static void Catalog_Reports_All_Available()
        {
            List<ItemInteraction> actions = InteractionCatalog.Available(new InteractionFlags
            {
                Owned = true, CanActivate = true, CanToggle = true, CanTarget = true
            });
            Assert.Equal(3, actions.Count, "three actions");
            Assert.Equal(ItemInteraction.Activate, actions[0], "activate first");
            Assert.Equal(ItemInteraction.Toggle, actions[1], "toggle second");
            Assert.Equal(ItemInteraction.Target, actions[2], "target third");
        }

        [Test]
        public static void Catalog_Empty_When_Not_Owned()
        {
            List<ItemInteraction> actions = InteractionCatalog.Available(new InteractionFlags
            {
                Owned = false, CanActivate = true, CanToggle = true, CanTarget = true
            });
            Assert.Equal(0, actions.Count, "not owned");
        }

        [Test]
        public static void Catalog_Empty_When_Nothing_Available()
        {
            Assert.Equal(0, InteractionCatalog.Available(new InteractionFlags { Owned = true }).Count, "none");
        }
    }
}

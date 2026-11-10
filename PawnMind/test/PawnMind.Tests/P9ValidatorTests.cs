using PawnMind.Core;

namespace PawnMind.Tests
{
    internal static class P9ValidatorTests
    {
        [Test]
        public static void Request_Requires_Fillable()
        {
            Assert.Equal(RequestCheck.Ok, RequestValidator.Validate(true, true), "ok");
            Assert.Equal(RequestCheck.NotFound, RequestValidator.Validate(false, true), "missing");
            Assert.Equal(RequestCheck.NotFillable, RequestValidator.Validate(true, false), "not fillable");
        }

        [Test]
        public static void Barter_Requires_Acceptable_Offer()
        {
            Assert.Equal(BarterCheck.Ok, BarterValidator.Validate(true, true), "ok");
            Assert.Equal(BarterCheck.NoOffer, BarterValidator.Validate(false, true), "no offer");
            Assert.Equal(BarterCheck.NotAcceptable, BarterValidator.Validate(true, false), "not acceptable");
        }
    }
}

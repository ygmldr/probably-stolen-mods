namespace PawnMind.Core
{
    public enum RequestCheck
    {
        Ok,
        NotFound,
        NotFillable
    }

    public static class RequestValidator
    {
        public static RequestCheck Validate(bool exists, bool fillable)
        {
            if (!exists) return RequestCheck.NotFound;
            if (!fillable) return RequestCheck.NotFillable;
            return RequestCheck.Ok;
        }
    }

    public enum BarterCheck
    {
        Ok,
        NoOffer,
        NotAcceptable
    }

    public static class BarterValidator
    {
        public static BarterCheck Validate(bool hasOffer, bool acceptable)
        {
            if (!hasOffer) return BarterCheck.NoOffer;
            if (!acceptable) return BarterCheck.NotAcceptable;
            return BarterCheck.Ok;
        }
    }
}

namespace PawnMind.Core
{
    public enum ReportCheck
    {
        Ok,
        NoClient,
        Unavailable,
        AlreadyReported
    }

    public static class ReportRules
    {
        public static ReportCheck Validate(bool hasClient, bool canReport, bool reported)
        {
            if (!hasClient) return ReportCheck.NoClient;
            if (reported) return ReportCheck.AlreadyReported;
            if (!canReport) return ReportCheck.Unavailable;
            return ReportCheck.Ok;
        }
    }
}

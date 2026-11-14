namespace PawnMind.Bridge
{
    /// <summary>
    /// One completed action, reported to the evidence hook. Every write action is
    /// reported (success and failure); read actions are reported only when their
    /// error is a hard failure.
    /// </summary>
    public sealed class ActionOutcome
    {
        public string Op { get; private set; }
        public bool Ok { get; private set; }
        public bool Write { get; private set; }
        public string Error { get; private set; }
        public long Revision { get; private set; }

        public ActionOutcome(string op, bool ok, bool write, string error, long revision)
        {
            Op = op;
            Ok = ok;
            Write = write;
            Error = error;
            Revision = revision;
        }
    }
}

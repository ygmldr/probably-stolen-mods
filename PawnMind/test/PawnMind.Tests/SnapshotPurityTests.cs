using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using PawnMind.Bridge;
using PawnMind.Core;

namespace PawnMind.Tests
{
    /// <summary>
    /// B module: the state snapshot is a read path. It must never call a
    /// Refresh()-backed native getter (which can reset the bargain state), and it
    /// must not emit the four can_* flags or can_report that used to be produced
    /// by such getters.
    ///
    /// Evidence level: L0-static (source drift) + L1-fake (pure projection). The
    /// Unity-side Refresh() counter is a separate instrumented build and remains
    /// PP-READ-01-real / NOT_RUN.
    /// </summary>
    internal static class SnapshotPurityTests
    {
        private const string StateSnapshotPath = "src/Bridge/StateSnapshot.cs";
        private const string GameStatePath = "src/Game/GameState.cs";

        // ------------------------------------------------------------------ util

        private static string RepoRoot()
        {
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
            {
                if (Directory.Exists(Path.Combine(dir, "src", "Bridge")))
                    return dir;
                dir = Path.GetDirectoryName(dir);
            }

            throw new InvalidOperationException("repo root not found from " + AppContext.BaseDirectory);
        }

        private static string ReadSource(string relative)
        {
            string path = Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) throw new InvalidOperationException("missing source: " + path);
            return File.ReadAllText(path);
        }

        /// <summary>Body of one method, matched by its signature prefix.</summary>
        private static string MethodBody(string source, string signaturePrefix)
        {
            int start = source.IndexOf(signaturePrefix, StringComparison.Ordinal);
            if (start < 0) throw new InvalidOperationException("method not found: " + signaturePrefix);

            int bodyStart = source.IndexOf('{', start);
            if (bodyStart < 0) throw new InvalidOperationException("no body: " + signaturePrefix);

            int depth = 0;
            for (int i = bodyStart; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}')
                {
                    depth--;
                    if (depth == 0) return source.Substring(bodyStart, i - bodyStart + 1);
                }
            }

            throw new InvalidOperationException("unbalanced body: " + signaturePrefix);
        }

        private static List<string> Distinct(string source, string pattern, int group = 1)
        {
            var found = new List<string>();
            MatchCollection matches = Regex.Matches(source, pattern);
            for (int i = 0; i < matches.Count; i++)
            {
                string value = matches[i].Groups[group].Value;
                if (!found.Contains(value)) found.Add(value);
            }

            return found;
        }

        /// <summary>Keys read via ["key"] (state/description/summary/row access).</summary>
        private static List<string> BracketKeys(string source)
        {
            return Distinct(source, "\\[\"([A-Za-z_][A-Za-z0-9_]*)\"\\]");
        }

        /// <summary>Keys actually emitted by assignments like state["key"] = ...</summary>
        private static List<string> EmittedKeys(string source, string variable)
        {
            return Distinct(source,
                "\\b" + Regex.Escape(variable) + "\\[\"([A-Za-z_][A-Za-z0-9_]*)\"\\]\\s*=");
        }

        // -------------------------------------------------------- source drift

        [Test]
        public static void PP_READ_03_Forbidden_Calls_Absent_From_Snapshot_Build()
        {
            // CanReport() / CanOffer* / Refresh() / RNG.Roll are the known
            // side-effect entry points; none of them may appear on the read path.
            string[] paths = { StateSnapshotPath, GameStatePath };
            for (int i = 0; i < paths.Length; i++)
            {
                List<string> hits = SnapshotFieldMap.ForbiddenTokenHits(ReadSource(paths[i]));
                Assert.Equal(0, hits.Count,
                    paths[i] + " still references impure calls: " + string.Join(",", hits.ToArray()));
            }
        }

        [Test]
        public static void PP_READ_03_Every_GameState_Call_Is_Registered()
        {
            string source = ReadSource(StateSnapshotPath);
            List<string> calls = Distinct(source, "GameState\\.([A-Za-z_][A-Za-z0-9_]*)\\b");

            var unregistered = new List<string>();
            var impure = new List<string>();
            for (int i = 0; i < calls.Count; i++)
            {
                SnapshotSource registered = SnapshotFieldMap.FindGameCall(calls[i]);
                if (registered == null) unregistered.Add(calls[i]);
                else if (registered.Purity == SnapshotPurity.Impure) impure.Add(calls[i]);
            }

            Assert.Equal(0, unregistered.Count,
                "unregistered GameState calls: " + string.Join(",", unregistered.ToArray()));
            Assert.Equal(0, impure.Count,
                "impure GameState calls: " + string.Join(",", impure.ToArray()));
            Assert.True(calls.Count >= 20, "scanner should see the real call set, saw " + calls.Count);
        }

        [Test]
        public static void PP_READ_03_Emitted_State_Keys_Are_Registered()
        {
            string source = ReadSource(StateSnapshotPath);
            List<string> keys = EmittedKeys(source, "state");

            var unregistered = new List<string>();
            for (int i = 0; i < keys.Count; i++)
            {
                if (SnapshotFieldMap.Find(SnapshotFieldMap.SectionState, keys[i]) == null)
                    unregistered.Add(keys[i]);
            }

            Assert.Equal(0, unregistered.Count,
                "unregistered state keys: " + string.Join(",", unregistered.ToArray()));
            Assert.True(keys.Contains("in_game") && keys.Contains("legal_actions"),
                "scanner sees the real top-level keys");
        }

        [Test]
        public static void PP_READ_03_Client_Section_Is_Registered_And_Pure()
        {
            string body = MethodBody(ReadSource(StateSnapshotPath),
                "private static Dictionary<string, object> DescribeClient(");
            List<string> keys = EmittedKeys(body, "description");

            var unregistered = new List<string>();
            var notPure = new List<string>();
            for (int i = 0; i < keys.Count; i++)
            {
                SnapshotSource row = SnapshotFieldMap.Find(SnapshotFieldMap.SectionClient, keys[i]);
                if (row == null) unregistered.Add(keys[i]);
                else if (row.Purity != SnapshotPurity.Pure) notPure.Add(keys[i]);
            }

            Assert.Equal(0, unregistered.Count,
                "unregistered client keys: " + string.Join(",", unregistered.ToArray()));
            Assert.Equal(0, notPure.Count,
                "client keys not proven pure: " + string.Join(",", notPure.ToArray()));
            Assert.False(keys.Contains("can_report"), "can_report must not be emitted");
        }

        [Test]
        public static void PP_READ_03_Client_Adapter_Only_Reads_Registered_Members()
        {
            string source = ReadSource(StateSnapshotPath);
            List<string> reads = Distinct(source, "_client\\.([A-Za-z_][A-Za-z0-9_]*)\\b");

            var allowed = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < SnapshotFieldMap.ClientKeys.Length; i++)
            {
                string origin = SnapshotFieldMap.ClientKeys[i].Origin; // "StoreClient.<member>"
                int dot = origin.LastIndexOf('.');
                if (dot >= 0 && dot + 1 < origin.Length) allowed.Add(origin.Substring(dot + 1));
            }

            var unexpected = new List<string>();
            for (int i = 0; i < reads.Count; i++)
            {
                if (!allowed.Contains(reads[i])) unexpected.Add(reads[i]);
            }

            Assert.Equal(0, unexpected.Count,
                "client adapter reads unregistered members: " + string.Join(",", unexpected.ToArray()));
        }

        [Test]
        public static void PP_READ_03_RunSummary_And_LegalAction_Keys_Are_Registered()
        {
            string source = ReadSource(StateSnapshotPath);

            CheckSection(source, "private static Dictionary<string, object> BuildRunSummary(",
                SnapshotFieldMap.SectionRunSummary, "run_summary");
            CheckSection(source, "private static List<Dictionary<string, object>> DescribeLegalActions(",
                SnapshotFieldMap.SectionLegalAction, "legal_action");
        }

        [Test]
        public static void PP_READ_03_Item_Keys_Are_Registered()
        {
            string source = ReadSource(StateSnapshotPath);
            string items = MethodBody(source, "private static List<Dictionary<string, object>> DescribeItems(");
            string item = MethodBody(source, "private static Dictionary<string, object> DescribeItem(");

            var keys = new List<string>();
            keys.AddRange(EmittedKeys(items, "row"));
            keys.AddRange(EmittedKeys(item, "description"));

            var unregistered = new List<string>();
            for (int i = 0; i < keys.Count; i++)
            {
                if (SnapshotFieldMap.Find(SnapshotFieldMap.SectionItem, keys[i]) == null)
                    unregistered.Add(keys[i]);
            }

            Assert.Equal(0, unregistered.Count,
                "unregistered item keys: " + string.Join(",", unregistered.ToArray()));
        }

        [Test]
        public static void PP_READ_03_Service_Calls_Are_Registered()
        {
            string source = ReadSource(StateSnapshotPath);
            string pattern = "\\b(ReportService|CalendarService|TutorialAdapter|FirstWeekService|"
                + "FirstWeekJson|PromptJson|PromptPolicy|PromptAdapter|LegalActionBuilder|Revision)"
                + "\\.([A-Za-z_][A-Za-z0-9_]*)";
            MatchCollection matches = Regex.Matches(source, pattern);

            var unregistered = new List<string>();
            for (int i = 0; i < matches.Count; i++)
            {
                string call = matches[i].Groups[1].Value + "." + matches[i].Groups[2].Value;
                if (SnapshotFieldMap.FindServiceCall(call) == null && !unregistered.Contains(call))
                    unregistered.Add(call);
            }

            Assert.Equal(0, unregistered.Count,
                "unregistered service calls: " + string.Join(",", unregistered.ToArray()));
        }

        [Test]
        public static void PP_READ_03_Snapshot_Emits_No_Impure_Key()
        {
            string source = ReadSource(StateSnapshotPath);
            List<string> keys = BracketKeys(source);

            // Refresh-backed keys are the historic defect: can_report was emitted
            // by StoreClient.CanReport() -> BargainUIManager.Refresh().
            List<string> violations = SnapshotRules.Violations(keys);
            Assert.Equal(0, violations.Count,
                "impure keys emitted by the snapshot: " + string.Join(",", violations.ToArray()));
        }

        [Test]
        public static void PP_READ_04_Snapshot_Reader_Aliases_Match_The_Policy_Catalog()
        {
            var expected = new List<string>(SnapshotFieldMap.SnapshotReaderAliases);
            expected.Sort(StringComparer.Ordinal);

            var actual = new List<string>(PolicyCatalog.SnapshotReaders);
            actual.Sort(StringComparer.Ordinal);

            Assert.Equal(string.Join(",", expected.ToArray()), string.Join(",", actual.ToArray()),
                "the six snapshot aliases drifted from PolicyCatalog");
            Assert.True(PolicyCatalog.IsSnapshotReader("get_state"), "get_state is a snapshot reader");
            Assert.False(PolicyCatalog.IsSnapshotReader("get_operation"),
                "get_operation does not build the state snapshot");
        }

        private static void CheckSection(string source, string signature, string section, string label)
        {
            string body = MethodBody(source, signature);
            string variable = section == SnapshotFieldMap.SectionRunSummary ? "summary" : "row";
            List<string> keys = EmittedKeys(body, variable);

            var unregistered = new List<string>();
            for (int i = 0; i < keys.Count; i++)
            {
                if (SnapshotFieldMap.Find(section, keys[i]) == null) unregistered.Add(keys[i]);
            }

            Assert.Equal(0, unregistered.Count,
                "unregistered " + label + " keys: " + string.Join(",", unregistered.ToArray()));
        }

        // ------------------------------------------------- fake: pure projection

        private sealed class FakeClientSnapshot : IClientSnapshotSource
        {
            public int ImpureProbes;
            public string DisplayName { get { return "Fake Client"; } }
            public string Intent { get { return "BUY"; } }
            public bool UsesBudget { get { return true; } }
            public int Impatience { get { return 3; } }
            public bool IsWanted { get { return false; } }
            public int ReportReward { get { return 0; } }
            public bool Reported { get { return false; } }
            public string WantedPosterId { get { return string.Empty; } }
            public bool BargainAvailable { get { return true; } }
            public string Faction { get { return "NONE"; } }
            public int Budget { get { return 500; } }

            /// <summary>
            /// Stands in for StoreClient.CanReport(): the old snapshot field called
            /// this on every build and it reached BargainUIManager.Refresh().
            /// </summary>
            public bool ProbeCanReport()
            {
                ImpureProbes++;
                return true;
            }
        }

        [Test]
        public static void PP_READ_01_Fake_Ten_Client_Projections_Never_Probe_Impure()
        {
            var fake = new FakeClientSnapshot();
            for (int i = 0; i < 10; i++)
            {
                Dictionary<string, object> row = ClientSnapshotProjection.Describe(fake);
                Assert.NotNull(row, "projection row");
                Assert.Equal(11, row.Count, "only the registered pure fields are emitted");
            }

            Assert.Equal(0, fake.ImpureProbes, "a snapshot build must not probe CanReport/Refresh");
        }

        [Test]
        public static void PP_READ_01_Fake_Projection_Omits_Can_Report_And_Reports_Pure_Fields()
        {
            Dictionary<string, object> row = ClientSnapshotProjection.Describe(new FakeClientSnapshot());
            Assert.False(row.ContainsKey("can_report"), "can_report is gone");
            Assert.True(row.ContainsKey("reported"), "reported is the pure replacement");
            Assert.True(row.ContainsKey("is_wanted"), "is_wanted stays");
            Assert.Equal("Fake Client", row["name"], "name");
            Assert.Equal(500, row["budget"], "budget");
        }

        [Test]
        public static void PP_READ_07_Fake_Client_Switch_Does_Not_Reset_Bargain_State()
        {
            // Switching clients is exactly when Refresh() resets the offer flags.
            // A pure read may observe the change but must never touch the impure
            // getter, so the fake probe counter has to stay at 0.
            var first = new FakeClientSnapshot();
            var second = new FakeClientSnapshot();

            for (int i = 0; i < 10; i++)
            {
                ClientSnapshotProjection.Describe(i % 2 == 0 ? first : second);
            }

            Assert.Equal(0, first.ImpureProbes, "first client was never probed impurely");
            Assert.Equal(0, second.ImpureProbes, "second client was never probed impurely");
        }

        // ------------------------------------------------- fake: stale revision

        [Test]
        public static void PP_READ_02_Stale_Rev_Does_Not_Dispatch_Or_Mutate()
        {
            int dispatchCalls = 0;
            int worldMutations = 0;
            int snapshotBuilds = 0;

            var router = new ActionRouter(
                new ActionGate(),
                delegate { return 5L; },
                delegate(string op, JObject request)
                {
                    dispatchCalls++;
                    worldMutations++;
                    return ActionResult.Success(op);
                },
                new ActionHistory(),
                delegate { snapshotBuilds++; return 5L; },
                null,
                delegate
                {
                    return GameVersionGate.Evaluate(
                        new GameIdentity("VERSION 1-Rev1", "11111111-2222-3333-4444-555555555555"),
                        "VERSION 1-Rev1",
                        "11111111-2222-3333-4444-555555555555");
                });

            string response = router.Handle("{\"op\":\"open_shop\",\"action_id\":\"read02\",\"rev\":4}");
            Assert.Contains(response, "stale_rev", "stale revision is refused");
            Assert.Equal(0, dispatchCalls, "the refused action was not dispatched");
            Assert.Equal(0, worldMutations, "the refused action did not touch the world");
            Assert.Equal(1, snapshotBuilds, "one read to compare revisions; the build itself is pure");
        }
    }
}

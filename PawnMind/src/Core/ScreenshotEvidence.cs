using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PawnMind.Core
{
    /// <summary>Current on-disk footprint of the screenshot folder.</summary>
    public sealed class ScreenshotUsage
    {
        public int Count { get; private set; }
        public long Bytes { get; private set; }

        public ScreenshotUsage(int count, long bytes)
        {
            Count = Math.Max(0, count);
            Bytes = Math.Max(0L, bytes);
        }
    }

    /// <summary>One queued or completed evidence capture.</summary>
    public sealed class ScreenshotEntry
    {
        public string Path { get; internal set; }
        public string Op { get; internal set; }
        public string Outcome { get; internal set; }
        public long Rev { get; internal set; }
        public int Day { get; internal set; }
        public long Cash { get; internal set; }
        public string Timestamp { get; internal set; }

        /// <summary>Auto captures use the op; manual captures pass the reason as the op.</summary>
        public string Reason
        {
            get { return Op; }
        }

        /// <summary>Stable error code, or null for a successful action.</summary>
        public string Error
        {
            get
            {
                return string.Equals(Outcome, ScreenshotLedger.SuccessOutcome, StringComparison.Ordinal)
                    ? null
                    : Outcome;
            }
        }

        public bool Captured { get; internal set; }
        public string CaptureError { get; internal set; }
    }

    public sealed class ScreenshotRequest
    {
        public bool Accepted { get; private set; }
        public string Error { get; private set; }
        public ScreenshotEntry Entry { get; private set; }

        internal ScreenshotRequest(bool accepted, string error, ScreenshotEntry entry)
        {
            Accepted = accepted;
            Error = error;
            Entry = entry;
        }

        internal static ScreenshotRequest Accept(ScreenshotEntry entry)
        {
            return new ScreenshotRequest(true, null, entry);
        }

        internal static ScreenshotRequest Reject(string error)
        {
            return new ScreenshotRequest(false, error, null);
        }
    }

    public sealed class ScreenshotStatus
    {
        public int TotalCount { get; private set; }
        public long TotalBytes { get; private set; }
        public long BudgetBytes { get; private set; }
        public int Dropped { get; private set; }
        public bool BudgetExhausted { get; private set; }

        public ScreenshotStatus(int totalCount, long totalBytes, long budgetBytes,
            int dropped, bool budgetExhausted)
        {
            TotalCount = Math.Max(0, totalCount);
            TotalBytes = Math.Max(0L, totalBytes);
            BudgetBytes = budgetBytes;
            Dropped = Math.Max(0, dropped);
            BudgetExhausted = budgetExhausted;
        }
    }

    /// <summary>
    /// Pure evidence-capture policy: safe JPEG file names, queue backpressure,
    /// and the total-size soft budget. No Unity or game types live here.
    /// </summary>
    public sealed class ScreenshotLedger
    {
        public const long DefaultBudgetBytes = 10L * 1024L * 1024L * 1024L;
        public const int UsageRefreshEvery = 50;
        public const int DefaultQueueCapacity = 8;
        public const int DefaultRecentLimit = 20;
        public const int MaxRecentLimit = 100;
        public const int MaxLabelLength = 40;
        public const string SuccessOutcome = "ok";

        private const int RecentCapacity = 200;

        private readonly object _sync = new object();
        private readonly Func<ScreenshotUsage> _usageProvider;
        private readonly Func<DateTime> _utcNow;
        private readonly long _budgetBytes;
        private readonly int _queueCapacity;
        private readonly Queue<ScreenshotEntry> _queue = new Queue<ScreenshotEntry>();
        private readonly List<ScreenshotEntry> _recent = new List<ScreenshotEntry>();

        private DateTime _lastUtc = DateTime.MinValue;
        private int _totalCount;
        private long _totalBytes;
        private int _dropped;
        private int _capturesSinceRefresh;
        private bool _budgetExhausted;
        private bool _usageLoaded;
        private string _lastPath;
        private string _lastError;

        public ScreenshotLedger(
            Func<ScreenshotUsage> usageProvider = null,
            Func<DateTime> utcNow = null,
            long budgetBytes = DefaultBudgetBytes,
            int queueCapacity = DefaultQueueCapacity)
        {
            _usageProvider = usageProvider;
            _utcNow = utcNow ?? delegate { return DateTime.UtcNow; };
            _budgetBytes = budgetBytes > 0 ? budgetBytes : DefaultBudgetBytes;
            _queueCapacity = queueCapacity > 0 ? queueCapacity : DefaultQueueCapacity;
        }

        public long BudgetBytes
        {
            get { return _budgetBytes; }
        }

        public int QueuedCount
        {
            get
            {
                lock (_sync) return _queue.Count;
            }
        }

        public string LastPath
        {
            get
            {
                lock (_sync) return _lastPath;
            }
        }

        public string LastError
        {
            get
            {
                lock (_sync) return _lastError;
            }
        }

        public ScreenshotStatus Status
        {
            get
            {
                lock (_sync)
                {
                    return new ScreenshotStatus(
                        _totalCount,
                        _totalBytes,
                        _budgetBytes,
                        _dropped,
                        _budgetExhausted);
                }
            }
        }

        /// <summary>
        /// Queue one capture. The caller must not block the main thread: the JPEG
        /// is encoded from the queue by the game-side coroutine.
        /// </summary>
        public ScreenshotRequest Request(string op, string outcome, long rev, int day, long cash)
        {
            lock (_sync)
            {
                EnsureUsageLoaded();

                if (_budgetExhausted || _totalBytes >= _budgetBytes)
                {
                    _budgetExhausted = true;
                    return ScreenshotRequest.Reject("screenshot_budget_exhausted");
                }

                if (_queue.Count >= _queueCapacity)
                {
                    _dropped++;
                    return ScreenshotRequest.Reject("screenshot_queue_full");
                }

                string safeOp = NormalizeLabel(op);
                string safeOutcome = NormalizeLabel(outcome);
                if (safeOp.Length == 0) safeOp = "unknown";
                if (safeOutcome.Length == 0) safeOutcome = "unknown";

                DateTime utc = NextUtc();
                string stamp = utc.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture);
                string timestamp = utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

                long safeRev = Math.Max(0L, rev);
                int safeDay = Math.Max(0, day);

                var entry = new ScreenshotEntry
                {
                    Path = "screenshots/" + stamp
                        + "_r" + safeRev.ToString(CultureInfo.InvariantCulture)
                        + "_d" + safeDay.ToString(CultureInfo.InvariantCulture)
                        + "_" + safeOp
                        + "_" + safeOutcome
                        + ".jpg",
                    Op = safeOp,
                    Outcome = safeOutcome,
                    Rev = safeRev,
                    Day = safeDay,
                    Cash = cash,
                    Timestamp = timestamp
                };

                _queue.Enqueue(entry);
                _recent.Add(entry);
                while (_recent.Count > RecentCapacity) _recent.RemoveAt(0);

                return ScreenshotRequest.Accept(entry);
            }
        }

        public bool TryDequeue(out ScreenshotEntry entry)
        {
            lock (_sync)
            {
                if (_queue.Count == 0)
                {
                    entry = null;
                    return false;
                }

                entry = _queue.Dequeue();
                return true;
            }
        }

        public void MarkCaptured(ScreenshotEntry entry, long bytes)
        {
            if (entry == null) return;

            lock (_sync)
            {
                entry.Captured = true;
                entry.CaptureError = null;
                _lastPath = entry.Path;
                _lastError = null;
                _totalBytes += Math.Max(0L, bytes);
                _totalCount++;
                _capturesSinceRefresh++;

                if (_totalBytes >= _budgetBytes) _budgetExhausted = true;
                if (_capturesSinceRefresh >= UsageRefreshEvery) RefreshUsage();
            }
        }

        public void MarkFailed(ScreenshotEntry entry, string error)
        {
            if (entry == null) return;

            lock (_sync)
            {
                entry.Captured = false;
                entry.CaptureError = string.IsNullOrEmpty(error)
                    ? "screenshot_failed"
                    : error;
                _lastError = entry.CaptureError;
            }
        }

        public IReadOnlyList<ScreenshotEntry> Recent(int limit)
        {
            int count = NormalizeLimit(limit);
            var result = new List<ScreenshotEntry>();

            lock (_sync)
            {
                for (int i = _recent.Count - 1; i >= 0 && result.Count < count; i--)
                    result.Add(_recent[i]);
            }

            return result;
        }

        public static int NormalizeLimit(int limit)
        {
            if (limit <= 0) return DefaultRecentLimit;
            return limit > MaxRecentLimit ? MaxRecentLimit : limit;
        }

        /// <summary>Lowercase [a-z0-9_] with runs collapsed; never returns null.</summary>
        public static string NormalizeLabel(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            var builder = new StringBuilder(value.Length);
            bool previousUnderscore = false;

            for (int i = 0; i < value.Length && builder.Length < MaxLabelLength; i++)
            {
                char c = char.ToLowerInvariant(value[i]);
                bool keep = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
                if (keep)
                {
                    builder.Append(c);
                    previousUnderscore = false;
                }
                else if (!previousUnderscore)
                {
                    builder.Append('_');
                    previousUnderscore = true;
                }
            }

            while (builder.Length > 0 && builder[0] == '_') builder.Remove(0, 1);
            while (builder.Length > 0 && builder[builder.Length - 1] == '_')
                builder.Length--;

            return builder.ToString();
        }

        private void EnsureUsageLoaded()
        {
            if (_usageLoaded) return;
            RefreshUsage();
        }

        private void RefreshUsage()
        {
            ScreenshotUsage usage = null;
            if (_usageProvider != null)
            {
                try
                {
                    usage = _usageProvider();
                }
                catch (Exception)
                {
                    usage = null;
                }
            }

            if (usage != null)
            {
                _totalCount = usage.Count;
                _totalBytes = usage.Bytes;
            }

            _usageLoaded = true;
            _capturesSinceRefresh = 0;
        }

        private DateTime NextUtc()
        {
            DateTime now;
            try
            {
                now = _utcNow();
            }
            catch (Exception)
            {
                now = DateTime.UtcNow;
            }

            if (now.Kind != DateTimeKind.Utc) now = now.ToUniversalTime();
            if (now <= _lastUtc) now = _lastUtc.AddMilliseconds(1);
            _lastUtc = now;
            return now;
        }
    }
}

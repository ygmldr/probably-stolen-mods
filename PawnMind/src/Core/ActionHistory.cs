using System;
using System.Collections.Generic;
using System.Text;

namespace PawnMind.Core
{
    public sealed class ActionHistory
    {
        private sealed class Entry
        {
            public string Op;
            public string ArgsJson;
            public bool Ok;
            public string Error;
            public long Rev;
        }

        private readonly Queue<Entry> _entries = new Queue<Entry>();
        private readonly int _capacity;

        public ActionHistory(int capacity = 50)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException("capacity");
            _capacity = capacity;
        }

        public int Count
        {
            get { return _entries.Count; }
        }

        public void Add(string op, string argsJson, bool ok, string error, long rev)
        {
            _entries.Enqueue(new Entry
            {
                Op = op ?? string.Empty,
                ArgsJson = string.IsNullOrEmpty(argsJson) ? "{}" : argsJson,
                Ok = ok,
                Error = error,
                Rev = rev
            });

            while (_entries.Count > _capacity) _entries.Dequeue();
        }

        public string ToJson(int limit)
        {
            if (limit <= 0) limit = _capacity;

            int skip = Math.Max(0, _entries.Count - Math.Min(limit, _capacity));
            var builder = new StringBuilder();
            builder.Append('[');

            int index = 0;
            foreach (Entry entry in _entries)
            {
                if (index++ < skip) continue;

                if (builder.Length > 1) builder.Append(',');
                builder.Append("{\"op\":\"").Append(Escape(entry.Op)).Append('"');
                builder.Append(",\"args\":").Append(entry.ArgsJson);
                builder.Append(",\"ok\":").Append(entry.Ok ? "true" : "false");
                // An ok row may still carry a delivery-axis error (the business
                // action succeeded but the response was lost), so the error field
                // is independent of the ok flag.
                if (!string.IsNullOrEmpty(entry.Error))
                {
                    builder.Append(",\"error\":\"").Append(Escape(entry.Error)).Append('"');
                }

                builder.Append(",\"rev\":").Append(entry.Rev).Append('}');
            }

            builder.Append(']');
            return builder.ToString();
        }

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}

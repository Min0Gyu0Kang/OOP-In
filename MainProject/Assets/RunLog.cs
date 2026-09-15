using System;
using System.Collections.Generic;

namespace OOPIn
{
    public struct RunLogEntry
    {
        /// <summary>1-based editor line, or 0 when unknown.</summary>
        public int line;
        public bool ok;
        public string text;
        /// <summary>A Python exception (as opposed to a rejected Bridge command).</summary>
        public bool pythonError;
    }

    /// <summary>Results of the current code run, in source order.</summary>
    public static class RunLog
    {
        private static readonly List<RunLogEntry> entries = new List<RunLogEntry>();

        public static event Action<RunLogEntry> Logged;
        public static event Action Cleared;

        public static IReadOnlyList<RunLogEntry> Entries { get { return entries; } }

        public static void Add(RunLogEntry entry)
        {
            entries.Add(entry);
            if (Logged != null) Logged(entry);
        }

        public static void Clear()
        {
            entries.Clear();
            if (Cleared != null) Cleared();
        }
    }
}

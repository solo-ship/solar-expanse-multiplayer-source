using System;
using System.Collections.Generic;
using System.Threading;

namespace SolarExpanseMultiplayer
{
    // string -> int id, assigned once from 0 up, never reused. MAIN THREAD ONLY: the writer thread never reads it (names
    // reach it through DriftWriter's name ring). The strings are the game's own existing strings: nothing is built here.
    public sealed class IdTable
    {
        readonly Dictionary<string, int> map;
        string[] names;
        int count, pendingFrom;
        bool guard; int owner;

        public IdTable(int capacity)
        {
            if (capacity < 16) capacity = 16;
            map = new Dictionary<string, int>(capacity, StringComparer.Ordinal);
            names = new string[capacity];
        }

        public int Count { get { return count; } }

        // The first id not yet handed to the writer. MAIN THREAD only.
        public int PendingFrom { get { return pendingFrom; } }

        // [add] Tests turn this on: from then on any call from another thread than the one that set it throws.
        public bool ThreadGuard
        {
            get { return guard; }
            set { guard = value; owner = Thread.CurrentThread.ManagedThreadId; }
        }

        // A known name: a dictionary lookup, no allocation. A new name: one entry (allowed by the allocation budget).
        // null is no name: it gets -1 and isNew is false.
        public int IdOf(string existing, out bool isNew)
        {
            Check();
            isNew = false;
            if (existing == null) return -1;
            int id;
            if (map.TryGetValue(existing, out id)) return id;
            id = count;
            if (id == names.Length) Array.Resize(ref names, names.Length * 2);
            names[id] = existing;
            map.Add(existing, id);
            count = id + 1;
            isNew = true;
            return id;
        }

        // null for an unknown id.
        public string NameOf(int id)
        {
            Check();
            return id >= 0 && id < count ? names[id] : null;
        }

        // Called only by DriftWriter.SubmitNames, on the main thread, after it copied the ids PendingFrom..upTo-1.
        public void MarkWritten(int upTo)
        {
            Check();
            if (upTo < pendingFrom || upTo > count) throw new ArgumentOutOfRangeException("upTo", "must be from PendingFrom to Count");
            pendingFrom = upTo;
        }

        void Check()
        {
            if (guard && Thread.CurrentThread.ManagedThreadId != owner) throw new InvalidOperationException("IdTable is main-thread only");
        }
    }
}

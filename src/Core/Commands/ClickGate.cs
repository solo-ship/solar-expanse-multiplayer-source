using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // What a window capture needs from the session: the guard questions, the one way a request leaves (the host's own
    // click or a client's), the pending value of a set action that is still on its way, and the line a player reads.
    // The game glue builds one over its CommandSession (host) or CommandClientGlue (client); the tests build theirs.
    public interface ICaptureSeam
    {
        bool Active { get; }
        bool Applying { get; }
        void Click(ActionRequest a);
        bool TryPendingValue(string key, out int value);
        // A line for the player, shown as the status line does.
        void Note(string text);
        // The lists a window has edited and the host has not answered yet (one per seam, so a session's own).
        CaptureMemory Memory { get; }
    }

    // What the session gives the captures: its seam, or null before a session has one. CommandSession implements it.
    public interface ICaptureOwner { ICaptureSeam Capture { get; } }

    // The set actions that carry a whole list (the research queue, the target lists) cannot use the budget's pending
    // value, which is a number: the capture remembers the list it last sent for a key, and uses it as the base of the
    // next edit for as long as the budget still holds that key as pending. A list the host has answered is read from
    // the game again.
    public sealed class CaptureMemory
    {
        const int MaxKeys = 64;
        readonly Dictionary<string, string[]> ids = new Dictionary<string, string[]>();
        readonly Dictionary<string, TargetRow[]> targets = new Dictionary<string, TargetRow[]>();
        readonly Dictionary<string, int> reported = new Dictionary<string, int>();

        // F10: the row a click was on, until the plan it starts reaches PlanFlyCode.
        public readonly SupplyMemory Supply = new SupplyMemory();

        // The tripwire's lines per name are capped, so a path nobody listed cannot fill the log.
        public const int MaxTripwireLines = 20;

        // True while the tripwire may still write a line for this name; counts the line it is asked for.
        public bool MayReport(string name)
        {
            int n;
            reported.TryGetValue(name ?? "", out n);
            if (n >= MaxTripwireLines) return false;
            reported[name ?? ""] = n + 1;
            return true;
        }

        public string[] BaseIds(ICaptureSeam seam, string key, string[] local)
        {
            string[] last;
            int ignored;
            if (key != null && ids.TryGetValue(key, out last) && seam.TryPendingValue(key, out ignored))
                return (string[])last.Clone();
            return local;
        }

        public TargetRow[] BaseTargets(ICaptureSeam seam, string key, TargetRow[] local)
        {
            TargetRow[] last;
            int ignored;
            if (key != null && targets.TryGetValue(key, out last) && seam.TryPendingValue(key, out ignored))
                return CopyRows(last);
            return local;
        }

        // A topic a click started or queued whose entry has not come back: the next remove or move builds on it.
        public const long AddWindowMs = 3000;
        readonly List<string> addIds = new List<string>();
        readonly List<bool> addFront = new List<bool>();
        readonly List<long> addAt = new List<long>();

        public void NoteQueueAdd(string id, bool front, long nowMs)
        {
            if (id == null) return;
            addIds.Add(id);
            addFront.Add(front);
            addAt.Add(nowMs);
            while (addIds.Count > 32) { addIds.RemoveAt(0); addFront.RemoveAt(0); addAt.RemoveAt(0); }
        }

        // The queue with the topics added a moment ago in it (a started topic first, a queued one last).
        public string[] WithAdds(string[] queue, long nowMs)
        {
            var list = new List<string>(queue);
            for (int i = 0; i < addIds.Count; i++)
            {
                if (nowMs - addAt[i] > AddWindowMs) continue;
                list.Remove(addIds[i]);
                if (addFront[i]) list.Insert(0, addIds[i]); else list.Add(addIds[i]);
            }
            return list.ToArray();
        }

        public void SentIds(string key, string[] list)
        {
            if (key == null) return;
            if (ids.Count >= MaxKeys && !ids.ContainsKey(key)) ids.Clear();
            ids[key] = (string[])list.Clone();
        }

        public void SentTargets(string key, TargetRow[] list)
        {
            if (key == null) return;
            if (targets.Count >= MaxKeys && !targets.ContainsKey(key)) targets.Clear();
            targets[key] = CopyRows(list);
        }

        public void Clear()
        {
            ids.Clear();
            targets.Clear();
            reported.Clear();
            Supply.Clear();
            addIds.Clear();
            addFront.Clear();
            addAt.Clear();
        }

        static TargetRow[] CopyRows(TargetRow[] rows)
        {
            var copy = new TargetRow[rows.Length];
            for (int i = 0; i < rows.Length; i++)
                copy[i] = new TargetRow { Body = rows[i].Body, Ratio = rows[i].Ratio, Count = rows[i].Count };
            return copy;
        }
    }

    // The last step of every capture: the request leaves, or the player is told why it does not. Nothing here knows the
    // game, so the refusals are tests (a capture on another agency's object sends nothing).
    public static class ClickGate
    {
        // `owner` is the wire id of the agency that owns what the click is on (null when the click names no owned
        // object, such as a research topic); `local` the wire id of the agency this game plays. True = sent.
        public static bool Send(ICaptureSeam seam, ActionRequest a, string owner, string local)
        {
            if (seam == null) throw new ArgumentNullException("seam");
            if (a == null) return Refuse(seam, RefuseCodes.CannotSend, "no request");
            if (owner != null && owner != local) return Refuse(seam, RefuseCodes.NotYourAgency, a, "owned by " + owner);
            a.Agency = local;
            string why = a.Validate();
            if (why != null) return Refuse(seam, RefuseCodes.CannotSend, a, why);
            seam.Click(a);
            return true;
        }

        // Several requests of one click (a drop split by MaxMoveNewIds): all or none leave.
        public static bool SendAll(ICaptureSeam seam, IList<ActionRequest> list, string owner, string local)
        {
            if (list == null || list.Count == 0) return Refuse(seam, RefuseCodes.CannotSend, "no request");
            if (owner != null && owner != local)
                return Refuse(seam, RefuseCodes.NotYourAgency, list[0], "owned by " + owner);
            foreach (ActionRequest a in list)
            {
                if (a == null) return Refuse(seam, RefuseCodes.CannotSend, "an empty request in a split click");
                a.Agency = local;
                string why = a.Validate();
                if (why != null) return Refuse(seam, RefuseCodes.CannotSend, a, why);
            }
            foreach (ActionRequest a in list) seam.Click(a);
            return true;
        }

        // `why`: what the log line names (a Validate reason, or where a capture gave up), never shown to the player.
        public static bool Refuse(ICaptureSeam seam, int code, string why = null)
        {
            return Refuse(seam, code, null, why);
        }

        // Every refusal the player is shown also leaves a line, so a pop-up in a test game can be traced to its rule.
        public static bool Refuse(ICaptureSeam seam, int code, ActionRequest a, string why)
        {
            Log.Info(RefusalLine(code, a, why));
            Tell(seam, CommandText.Refused(code));
            return false;
        }

        // "command: refused OfferTake: CannotSend (Offer.StartTicks is not a date)"
        public static string RefusalLine(int code, ActionRequest a, string why)
        {
            string kind = a != null && ActionKind.IsKnown(a.Kind) ? ActionKind.Name(a.Kind) + ": " : "";
            return "command: refused " + kind + RefuseCodes.Name(code)
                + (why != null ? " (" + TextSanitiser.OneLine(why) + ")" : "");
        }

        // A greyed button was used: the note and a log line naming the path, and nothing else happens.
        public static void Greyed(ICaptureSeam seam, string path = null)
        {
            Log.Info("command: greyed " + TextSanitiser.OneLine(path ?? "(no path)"));
            Tell(seam, CommandText.NotYetInMultiplayer());
        }

        // The click did nothing: a seam that can show it in the game's dialog does, any other notes it.
        static void Tell(ICaptureSeam seam, string text)
        {
            var sink = seam as IRefusalSink;
            if (sink != null) sink.Refused(text);
            else seam.Note(text);
        }

        // A set action's number: the value still on its way when the player clicks again, else the window's.
        public static int PendingOr(ICaptureSeam seam, ActionRequest draft, int fromWindow)
        {
            int v;
            string key = ActionShapes.Key(draft);
            return key != null && seam.TryPendingValue(key, out v) ? v : fromWindow;
        }

        // Whether a set action of this key is still on its way.
        public static bool IsPending(ICaptureSeam seam, ActionRequest draft)
        {
            int v;
            string key = ActionShapes.Key(draft);
            return key != null && seam.TryPendingValue(key, out v);
        }
    }
}

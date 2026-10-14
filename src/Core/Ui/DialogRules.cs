using System;
using System.Collections.Generic;
using System.Text;

namespace SolarExpanseMultiplayer
{
    // The mod's session dialogs (DD14-DD16, DD18, DD21; Task 21). The early session cannot reach some of them; each is
    // still built, behind its capability, so turning the capability on later changes no screen.
    public enum DialogKind
    {
        None = 0, Mismatch, Refused, AgencyRefused, SessionOver, Dropped, Reloading, CarryOn, ConfirmLeave, ConfirmEnd,
        Progress
    }

    // What a dialog button does.
    public enum DialogAction { None = 0, Close, CopyReport, MpSaves, Leave, EndSession, CarryOn }

    // One line of a dialog's body: a UiText key and its arguments. Peer: an argument came from another player, so the
    // line goes on screen only through PeerText (every line does, in fact; this says the line must not be trusted).
    public sealed class DialogLine
    {
        public string Key; public object[] Args = new object[0]; public bool Small, Peer, Gap, Warning;

        public static DialogLine Of(string key, params object[] args)
        {
            return new DialogLine { Key = key, Args = args ?? new object[0] };
        }

        public string Text() { return UiText.Get(Key, Args); }
    }

    // Everything a dialog shows and does, as table keys; the screen only lays it out. Id names the cause, so the same
    // cause shows once (DialogTracker); a different cause is a new dialog.
    public sealed class DialogSpec
    {
        public DialogKind Kind;
        public string Id;
        public string TitleKey; public object[] TitleArgs = new object[0]; public bool TitlePeer;
        public List<DialogLine> Lines = new List<DialogLine>();
        public List<string> Report;              // the host's report lines, cleaned (peer text); null when none
        public string OkKey; public DialogAction OkAction = DialogAction.Close;
        public string OtherKey; public DialogAction OtherAction = DialogAction.None;   // null key: no second button
        public int Player = -1;                  // CarryOn: the dropped player
        public bool Overlay;                     // closes by itself once its cause is gone (a drop, a reload)
        public bool MenuOnly;                    // a session's end lands on the main menu (DD16): it waits for it
        public long AskedMs = -1;                // a dialog a click asked for: when (DialogFlow drops it when stale)

        // Carry on, leave and end are asked for by a click; every other dialog the view calls for.
        public bool Asked
        {
            get
            {
                return Kind == DialogKind.CarryOn || Kind == DialogKind.ConfirmLeave ||
                       Kind == DialogKind.ConfirmEnd;
            }
        }

        public string Title() { return UiText.Get(TitleKey, TitleArgs); }

        // What the screen shows, in one string: an open dialog whose signature changes is refreshed in place.
        public string Signature()
        {
            var sb = new StringBuilder();
            sb.Append(Kind).Append('|').Append(Id).Append('|').Append(Title());
            foreach (DialogLine l in Lines) sb.Append('|').Append(l.Gap ? "" : l.Text());
            if (Report != null) foreach (string r in Report) sb.Append('|').Append(r);
            sb.Append('|').Append(OkKey).Append('|').Append(OtherKey);
            return sb.ToString();
        }

        // What "Copy the list" puts on the clipboard: the lead line, then every report line (the report holds no
        // secret, DD14). Null for a dialog with nothing to copy.
        public string CopyText()
        {
            if (Report == null || Report.Count == 0) return null;
            var sb = new StringBuilder();
            if (Lines.Count > 0) sb.Append(Lines[0].Text()).Append('\n');
            foreach (string r in Report) sb.Append(r).Append('\n');
            return sb.ToString().TrimEnd('\n');
        }
    }

    // Which dialog the session calls for, from SessionView and the capabilities alone.
    public static class DialogRules
    {
        const string RefusedPrefix = "join.refused.";
        const string EndedPrefix = "join.ended.";

        // The refusal codes whose report lists what differs (a content or files mismatch), with "Copy the list".
        static readonly int[] MismatchCodes =
        {
            JoinCodes.PluginsDiffer, JoinCodes.ContentDiffers, JoinCodes.ManagedDiffers, JoinCodes.TooMuchDetail,
            JoinCodes.RefUnresolved, JoinCodes.PostLoadContent,
        };

        // A claim the host refused while the join goes on: the picker opens again (DD26).
        static readonly int[] AgencyCodes =
            { JoinCodes.AgencyTaken, JoinCodes.AgencyUnknown, JoinCodes.AgencyNotPlayable };

        // The code of a "join.refused.<n>" key; -1 for any other key.
        public static int RefusalCode(string key)
        {
            if (key == null || !key.StartsWith(RefusedPrefix, StringComparison.Ordinal)) return -1;
            int n;
            return int.TryParse(key.Substring(RefusedPrefix.Length), out n) && n >= 0 ? n : -1;
        }

        // The dialog the view calls for now, or null: a session that ended (a refusal, or the session over), a claim
        // refused, a start's or an automatic rejoin's progress (JoinProgress), the player's own world reloading, its
        // link to the host dropped. The first that holds wins.
        public static DialogSpec FromView(SessionView v, Capability caps)
        {
            if (v == null) return null;
            if (!string.IsNullOrEmpty(v.EndedWhy)) return Ended(v, caps);
            string status = v.Status != null ? v.Status.Key : null;
            if (Array.IndexOf(AgencyCodes, RefusalCode(status)) >= 0) return AgencyRefused(status);
            if (v.Progress != null && v.Progress.Cause != ProgressCause.None)
                return JoinProgress.Dialog(v.Progress, v.IsHost, caps);
            if (status == StatusLine.OutOfStep) return Reloading(v);
            if (status == StatusLine.Dropped || status == StatusLine.Rejoining) return Dropped(caps);
            return null;
        }

        static DialogSpec Ended(SessionView v, Capability caps)
        {
            string key = v.EndedWhy;
            if (key.StartsWith(EndedPrefix, StringComparison.Ordinal)) return SessionOver(key, caps);
            int code = RefusalCode(key);
            var d = new DialogSpec { Id = "ended " + key, OkKey = "dialog.ok", MenuOnly = true };
            string host = TextSanitiser.Clean(v.EndedHost, PlayerNames.MaxChars, "");
            string[] title = TitleKeys(code);
            if (host.Length > 0 && title[0] != null)
            {
                d.TitleKey = title[0];
                d.TitleArgs = new object[] { host };
                d.TitlePeer = true;
            }
            else d.TitleKey = title[1];
            DialogLine body = DialogLine.Of(UiText.Has(key) ? key : "join.status.failed");
            d.Lines.Add(body);
            // a refusal with no report of its own carries the code's sentence as its one line: said once, not twice
            d.Report = Report(v.EndedReport, body.Text());
            bool mismatch = Array.IndexOf(MismatchCodes, code) >= 0;
            d.Kind = mismatch ? DialogKind.Mismatch : DialogKind.Refused;
            if (mismatch && d.Report != null)
            {
                d.Lines.Add(new DialogLine { Key = "dialog.refused.inLog", Small = true });
                d.OtherKey = "dialog.refused.copy";
                d.OtherAction = DialogAction.CopyReport;
            }
            return d;
        }

        // The title keys {with the host's name, without} that fit why the join ended: a player who left or turned the
        // join down did not "fail to join", and one the host removed or banned says so. Any other refusal is the
        // plain "Couldn't join".
        public static string[] TitleKeys(int code)
        {
            switch (code)
            {
                case JoinCodes.Left: return new[] { "dialog.title.left", "dialog.title.leftNoHost" };
                case JoinCodes.Declined:
                case JoinCodes.Aborted: return new[] { null, "dialog.title.cancelled" };
                case JoinCodes.KickedFromLobby: return new[] { "dialog.title.removed", "dialog.title.removedNoHost" };
                case JoinCodes.Banned: return new[] { "dialog.title.banned", "dialog.title.bannedNoHost" };
                default: return new[] { "dialog.refused.title", "dialog.refused.titleNoHost" };
            }
        }

        // Back at the menu after the session ended (DD16). With saved copies (full track), where the copy is, and a
        // way to the Multiplayer saves list.
        static DialogSpec SessionOver(string key, Capability caps)
        {
            var d = new DialogSpec
            {
                Kind = DialogKind.SessionOver, Id = "ended " + key, TitleKey = "dialog.ended.title",
                OkKey = "dialog.ok",
                MenuOnly = true,
            };
            d.Lines.Add(DialogLine.Of(UiText.Has(key) ? key : "join.ended.hostLeft"));
            if (CapabilityRules.Has(caps, Capability.SaveCopies))
                d.Lines.Add(new DialogLine { Key = "dialog.ended.copy", Small = true });
            if (CapabilityRules.Has(caps, Capability.MpSaves))
            {
                d.OtherKey = "dialog.ended.mpSaves";
                d.OtherAction = DialogAction.MpSaves;
            }
            return d;
        }

        // The refused claim's dialog leads back to the agency picker once it closes (the join's own picker, with its
        // earlier answers kept), while the join's claims and deadline last.
        public static bool PickerAfter(DialogSpec d) { return d != null && d.Kind == DialogKind.AgencyRefused; }

        static DialogSpec AgencyRefused(string key)
        {
            var d = new DialogSpec
            {
                Kind = DialogKind.AgencyRefused, Id = "claim " + key, TitleKey = "dialog.agency.title",
                OkKey = "dialog.agency.back",
            };
            d.Lines.Add(DialogLine.Of(key));
            return d;
        }

        // The player's own world is reloaded from the host's (board 16): the download's progress from its own row.
        static DialogSpec Reloading(SessionView v)
        {
            var d = new DialogSpec
            {
                Kind = DialogKind.Reloading, Id = "reload", TitleKey = "dialog.reload.title", Overlay = true,
            };
            d.Lines.Add(DialogLine.Of(ReloadReason(v.Status)));
            PlayerRow me = Me(v);
            int pct = me != null ? me.JoinPct : -1;
            if (pct >= 0 && pct < 100)
                d.Lines.Add(me.JoinSecsLeft >= 0 ? DialogLine.Of("dialog.reload.progress", Pct(pct), me.JoinSecsLeft)
                                                 : DialogLine.Of("dialog.reload.progressNoTime", Pct(pct)));
            else d.Lines.Add(DialogLine.Of(pct >= 100 ? "dialog.reload.loading" : "dialog.reload.waiting"));
            d.Lines.Add(new DialogLine { Gap = true });
            d.Lines.Add(new DialogLine { Key = "dialog.reload.keep", Small = true });
            return d;
        }

        // The link to the host dropped (DD15): the game waits while it reconnects; Leave session needs the goodbye
        // that holds no drop pause (LeaveNoPause).
        static DialogSpec Dropped(Capability caps)
        {
            var d = new DialogSpec
            {
                Kind = DialogKind.Dropped, Id = "dropped", TitleKey = "dialog.dropped.title", Overlay = true,
            };
            d.Lines.Add(new DialogLine { Key = StatusLine.Dropped, Warning = true });
            d.Lines.Add(new DialogLine { Key = "dialog.dropped.body", Small = true });
            if (CapabilityRules.Has(caps, Capability.LeaveNoPause))
            {
                d.OkKey = "dialog.leave.button";
                d.OkAction = DialogAction.Leave;
            }
            return d;
        }

        // The host's "Carry on without <name>" (DD15): it lifts a dropped player's drop pause, so it shows only while
        // the clock holds one for that player (a drop that paused nobody, or one carried on already, has none).
        public static DialogSpec CarryOn(SessionView v, Capability caps, int player)
        {
            if (v == null || !v.IsHost || !CapabilityRules.Has(caps, Capability.CarryOn) || !HoldsDropPause(v, player))
                return null;
            PlayerRow r = v.Row(player);
            string name = Name(r != null ? r.Name : null, player);
            var d = new DialogSpec
            {
                Kind = DialogKind.CarryOn, Id = "carry on " + player, TitleKey = "dialog.carryOn.title",
                TitleArgs = new object[] { name }, TitlePeer = true, Player = player,
                OkKey = "dialog.carryOn.yes", OkAction = DialogAction.CarryOn,
                OtherKey = "dialog.carryOn.no", OtherAction = DialogAction.Close,
            };
            d.Lines.Add(new DialogLine { Key = "dialog.carryOn.body", Args = new object[] { name }, Peer = true });
            return d;
        }

        // The clock holds a drop pause for this player.
        public static bool HoldsDropPause(SessionView v, int player)
        {
            if (v == null || player < 0) return false;
            if (v.Pauses != null)
                foreach (PauseLine p in v.Pauses)
                    if (p != null && p.Player == player && p.Why == PauseWhy.Dropped) return true;
            return false;
        }

        // A client's "Leave session" (DD18, DD21): the goodbye that holds no drop pause. Null when it may not show.
        public static DialogSpec ConfirmLeave(SessionView v, Capability caps)
        {
            if (v == null || v.IsHost || !InSession(v) || !CapabilityRules.Has(caps, Capability.LeaveNoPause))
                return null;
            var d = new DialogSpec
            {
                Kind = DialogKind.ConfirmLeave, Id = "leave", TitleKey = "dialog.leave.title",
                OkKey = "dialog.leave.button", OkAction = DialogAction.Leave,
                OtherKey = "dialog.leave.stay", OtherAction = DialogAction.Close,
            };
            d.Lines.Add(DialogLine.Of("dialog.leave.body"));
            return d;
        }

        // The host's "End session" (DD16, DD21). With saved copies it names who gets one. Null when it may not show.
        public static DialogSpec ConfirmEnd(SessionView v, Capability caps)
        {
            if (v == null || !v.IsHost || !InSession(v) || !CapabilityRules.Has(caps, Capability.EndSession))
                return null;
            var d = new DialogSpec
            {
                Kind = DialogKind.ConfirmEnd, Id = "end", TitleKey = "dialog.end.title",
                OkKey = "dialog.end.yes", OkAction = DialogAction.EndSession,
                OtherKey = "dialog.end.no", OtherAction = DialogAction.Close,
            };
            var others = new List<string>();
            foreach (PlayerRow r in v.Players) if (r != null && !r.Me) others.Add(Name(r.Name, r.Id));
            if (CapabilityRules.Has(caps, Capability.SaveCopies) && others.Count > 0)
                d.Lines.Add(new DialogLine
                {
                    Key = "dialog.end.bodyCopy", Args = new object[] { NameList(others) }, Peer = true,
                });
            else d.Lines.Add(DialogLine.Of("dialog.end.body"));
            if (CapabilityRules.Has(caps, Capability.MpSaves | Capability.HostMpSave))
                d.Lines.Add(new DialogLine { Key = "dialog.end.later", Small = true });
            return d;
        }

        static bool InSession(SessionView v)
        {
            return v.Phase != SessionPhase.Idle && v.Phase != SessionPhase.Ending;
        }

        // "Sam", "Sam and Jo", "Sam, Jo and Kim": names already cleaned.
        public static string NameList(IList<string> names)
        {
            if (names == null || names.Count == 0) return "";
            string s = names[names.Count - 1];
            if (names.Count == 1) return s;
            s = UiText.Get("dialog.names.two", names[names.Count - 2], s);
            for (int i = names.Count - 3; i >= 0; i--) s = UiText.Get("dialog.names.more", names[i], s);
            return s;
        }

        // A player's name, cleaned; the view's unknown name ("Player N") when it cleans to nothing.
        public static string Name(string raw, int id)
        {
            string n = TextSanitiser.Clean(raw, PlayerNames.MaxChars, "");
            return n.Length > 0 ? n : PlayerRow.Unknown(id).Name;
        }

        // The report as rows: each line cleaned, empty ones left out, every one kept (the frame's own scroll view
        // scrolls them; the host's report is already bounded by its message's budget). Null when nothing is left.
        public static List<string> Report(IList<string> lines) { return Report(lines, null); }

        // The same, without any line that only repeats the dialog's own body sentence.
        public static List<string> Report(IList<string> lines, string body)
        {
            if (lines == null) return null;
            string said = body != null ? TextSanitiser.Clean(body, JoinLimits.MaxReportLineChars, "") : null;
            var rows = new List<string>();
            foreach (string raw in lines)
            {
                string l = TextSanitiser.Clean(raw, JoinLimits.MaxReportLineChars, "");
                if (l.Length > 0 && l != said) rows.Add(l);
            }
            return rows.Count > 0 ? rows : null;
        }

        static PlayerRow Me(SessionView v)
        {
            foreach (PlayerRow r in v.Players) if (r != null && r.Me) return r;
            return null;
        }

        // Why the world is reloaded: the full rejoin's own reason (its RejoinCodes code rides on the status line), or
        // the general words when none came.
        static string ReloadReason(StatusLine status)
        {
            object[] args = status != null ? status.Args : null;
            string key = args != null && args.Length > 0 && args[0] is int
                ? "join.rejoin.reload." + (int)args[0] : null;
            return key != null && UiText.Has(key) ? key : "dialog.reload.body";
        }

        static int Pct(int p) { return p < 0 ? 0 : p > 100 ? 100 : p; }
    }

    // One state dialog at a time, shown once per cause: the view's cause is compared each tick with the last one.
    // A new cause opens its dialog (and a dialog of the old cause that is still open closes); the same cause shown
    // again is refreshed when its words changed, and never reopened once the player closed it; an overlay closes by
    // itself when its cause is gone.
    public sealed class DialogTracker
    {
        public enum Step { None, Open, Refresh, Close }

        string causeId;        // the cause seen last (shown, or closed by the player), null when none
        string signature;      // what the open dialog shows
        bool open;
        bool overlay;

        public bool IsOpen { get { return open; } }

        public Step Next(DialogSpec want)
        {
            if (want == null)
            {
                causeId = null;
                if (open && overlay) { open = false; signature = null; return Step.Close; }
                return Step.None;
            }
            if (want.Id != causeId)
            {
                causeId = want.Id;
                open = true;
                overlay = want.Overlay;
                signature = want.Signature();
                return Step.Open;
            }
            if (!open) return Step.None;
            string now = want.Signature();
            if (now == signature) return Step.None;
            signature = now;
            return Step.Refresh;
        }

        // The player closed it (OK, Esc, a button): it stays closed while the same cause holds.
        public void Closed() { open = false; signature = null; }

        // It went without the player closing it (its scene unloaded): while its cause holds it opens again.
        public void Lost()
        {
            if (!open) return;
            open = false;
            signature = null;
            causeId = null;
        }
    }

    // Dialogs waiting for the game's own Load/Save or Settings window to close (both close on Esc whatever is over
    // them, so a mod dialog never goes over either), and a session's end waiting for the main menu. A newer dialog of
    // the same Id replaces the waiting one; an overlay's cause can end while it waits (Drop).
    public sealed class DialogQueue
    {
        readonly List<DialogSpec> waiting = new List<DialogSpec>();

        public int Count { get { return waiting.Count; } }

        public void Add(DialogSpec d, bool first = false)
        {
            if (d == null) return;
            Drop(d.Id);
            if (first) waiting.Insert(0, d); else waiting.Add(d);
        }

        public void Drop(string id) { waiting.RemoveAll(w => w.Id == id); }

        public void DropKind(DialogKind kind) { waiting.RemoveAll(w => w.Kind == kind); }

        public bool Has(DialogKind kind) { return waiting.Exists(w => w.Kind == kind); }

        // Every waiting dialog a click asked for (a new scene: its context is gone).
        public void DropAsked() { waiting.RemoveAll(w => w.Asked); }

        // The oldest dialog that may show now (no game window open; a menu-only one only at the menu), else null.
        public DialogSpec Take(bool gameWindowOpen, bool inGame)
        {
            if (gameWindowOpen) return null;
            for (int i = 0; i < waiting.Count; i++)
            {
                DialogSpec d = waiting[i];
                if (d.MenuOnly && inGame) continue;
                waiting.RemoveAt(i);
                return d;
            }
            return null;
        }

        public void Clear() { waiting.Clear(); }
    }

    // The table key for how a session ended for good, from JoinerFlow's BackCode, BackInGame and BackByHost: the
    // key of the very words its status line gave (JoinText), for SessionView.EndedWhy and the screens' dialog.
    public static class SessionEnds
    {
        public static string Key(int code, bool inGame, bool goodbye)
        {
            if (inGame && goodbye)
            {
                if (code == 0 || code == JoinCodes.HostLeft) return "join.ended.hostLeft";
                return code == JoinCodes.AgencyTaken ? "join.ended.agencyTaken" : "join.ended.kicked";
            }
            if (inGame)
                switch (code)
                {
                    case JoinCodes.Reconnects: return "join.ended.reconnects";
                    case JoinCodes.RejoinRefused: return "join.ended.rejoinRefused";
                    case JoinCodes.Protocol: return "join.ended.protocol";
                }
            string key = "join.refused." + code;
            return UiText.Has(key) ? key : "join.status.failed";
        }
    }
}

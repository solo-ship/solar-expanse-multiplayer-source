using System;
using System.Globalization;

namespace SolarExpanseMultiplayer
{
    // Every player-facing line of M4's join, in one place (Design decision 15): nothing else in the join builds UI
    // text. Plain sentences, no jargon, no rich text; what happened, then what to do when there is something to do.
    // Australian/British spelling. Text that came from the other side (names, ids, versions) goes through
    // TextSanitiser before it is put into a line, and the report assembled from these lines goes through TextBudget
    // (I-13), so no line can push a message past its cap. The M4 UX-copy pass is recorded in the task report.
    // Since M8 (Design decision 12) the words live in UiStrings, by key, and every method here is a lookup through
    // UiText: the methods, their arguments and their results are as they were, and the wording did not change.
    public static class JoinText
    {
        // A game build this mod version was not verified on (KnownBuilds): no hosting and no joining.
        public static string UnsupportedGame { get { return UiText.Get("join.unsupportedGame"); } }

        static string Unknown() { return UiText.Get("join.unknown"); }

        // ---- version refusals (the host's Reject, once the credential has passed) ----

        // The plain-words line for a version refusal, or null when this is not one (or the host sent no versions).
        // Reject.VersionSentence delegates here.
        public static string VersionSentence(int rejectCode, string hostGameVersion, string hostModVersion,
            string hostGameBuild, string yourGameVersion, string yourModVersion, string yourGameBuild)
        {
            string game = string.IsNullOrEmpty(yourGameVersion) ? Unknown() : yourGameVersion;
            string mod = string.IsNullOrEmpty(yourModVersion) ? Unknown() : yourModVersion;
            bool modRefusal = rejectCode == RejectCode.Protocol || rejectCode == RejectCode.ModVersion;
            if (modRefusal && !string.IsNullOrEmpty(hostModVersion))
                return ModVersionLine(hostModVersion, mod);
            if (rejectCode == RejectCode.GameBuild && !string.IsNullOrEmpty(hostGameVersion))
                return hostGameVersion != game ? GameVersionLine(hostGameVersion, game)
                    : GameBuildLine(hostGameVersion, hostGameBuild, yourGameBuild);
            return null;
        }

        public static string GameVersionLine(string host, string yours)
        {
            return UiText.Get("join.version.game", host, yours);
        }

        // The same version on both sides, so the builds are what differ: both are named, like the version line (a
        // player comparing notes with the host needs both). The builds may come from the other side: cleaned here.
        public static string GameBuildLine(string version, string hostBuild, string yourBuild)
        {
            string host = TextSanitiser.Clean(hostBuild, 40, Unknown());
            string yours = TextSanitiser.Clean(yourBuild, 40, Unknown());
            return UiText.Get("join.version.build", version, host, yours);
        }

        // The host said goodbye after this joiner had loaded: the joiner goes back to the main menu, where this is
        // shown, and nothing reconnects. What it says follows the goodbye's code: the host closed the session (or
        // gave no code), took this player's agency for itself, or removed the player for any other reason.
        public static string HostLeftInGame()
        {
            return UiText.Get("join.ended.hostLeft");
        }

        public static string AgencyTakenInGame()
        {
            return UiText.Get("join.ended.agencyTaken");
        }

        public static string KickedInGame()
        {
            return UiText.Get("join.ended.kicked");
        }

        // The words for a goodbye in game, by its code (Bye.Code): 0 is a host that gave none.
        public static string GoodbyeInGame(int code)
        {
            if (code == 0 || code == JoinCodes.HostLeft) return HostLeftInGame();
            return code == JoinCodes.AgencyTaken ? AgencyTakenInGame() : KickedInGame();
        }

        // The other endings of a game in progress, shown at the menu it goes back to: the reconnects ran out, the host
        // refused the reconnect, or it broke the protocol. Any other code has no in-game ending and reads as a join's.
        public static string EndedInGame(int code)
        {
            switch (code)
            {
                case JoinCodes.Reconnects: return UiText.Get("join.ended.reconnects");
                case JoinCodes.RejoinRefused: return UiText.Get("join.ended.rejoinRefused");
                case JoinCodes.Protocol: return UiText.Get("join.ended.protocol");
                default: return Refused(code);
            }
        }

        // The host's goodbye words when it closes a player's link (SessionHost.Kick); its code picks the joiner's
        // words.
        public static string KickReason(int code) { return UiText.Get("join.link.kicked", code); }

        // The host's goodbye words when a join ended (not a kick): the join's own code.
        public static string JoinEndReason(int code) { return UiText.Get("join.link.joinEnded", code); }

        // Why the client is not connected, for its status line. The host closes the link of a joiner whose join has
        // ended, with that join's code in its goodbye: after the joiner's own refusal (reported to the host) or the
        // host's refusal, that close is not a kick and must not read as one. Only a goodbye with another code (the
        // host's own decision) keeps the link's words ("left: kicked by the host (N)"); so does a link that just ended.
        // endCode / endedHere: JoinerFlow.EndCode and EndedHere.
        public static string LinkEnded(string reason, bool bye, int byeCode, int endCode, bool endedHere)
        {
            if (bye && endCode > 0 && byeCode == endCode)
                return endedHere ? StoppedHere(endCode) : RefusedByHost(endCode);
            return reason;
        }

        public static string StoppedHere(int code) { return UiText.Get("join.link.stoppedHere", code); }

        public static string RefusedByHost(int code) { return UiText.Get("join.link.refusedByHost", code); }

        // Under the first lines of a refusal's report on screen: the rest are only in the log.
        public static string MoreInLog(int more) { return UiText.Get("join.moreInLog", more); }

        public static string ModVersionLine(string host, string yours)
        {
            return UiText.Get("join.version.mod", host, yours);
        }

        // ---- the content report (the lines the host sends in JoinRefused) ----

        public static string ManagedLine() { return UiText.Get("join.report.managed"); }

        // nothing else to say, yet the two sides do not match (a broken summary)
        public static string OverallLine() { return UiText.Get("join.report.overall"); }

        static string Of(string version)
        {
            return string.IsNullOrEmpty(version) ? "" : UiText.Get("join.report.versionPart", version);
        }

        // " (text)" when there is text, else nothing
        static string Bracketed(string text)
        {
            return string.IsNullOrEmpty(text) ? "" : UiText.Get("join.report.bracketed", text);
        }

        public static string OnlyHostLine(string label, string name, string version)
        {
            return UiText.Get("join.report.onlyHost", label, name, Of(version));
        }

        public static string OnlyJoinerLine(string label, string name, string version)
        {
            return UiText.Get("join.report.onlyJoiner", label, name, Of(version));
        }

        public static string VersionsLine(string label, string name, string hostVersion, string yourVersion)
        {
            return UiText.Get("join.report.versions", label, name, hostVersion, yourVersion);
        }

        public static string SameVersionOtherFileLine(string label, string name, string version)
        {
            return UiText.Get("join.report.sameVersion", label, name, Bracketed(version));
        }

        public static string MoreDifferencesLine(string label, int more)
        {
            return UiText.Get("join.report.moreDifferences", more, label.ToLowerInvariant());
        }

        public static string CategoryDiffersLine(string category, int hostCount, int yourCount)
        {
            return UiText.Get("join.report.categoryDiffers", category, hostCount, yourCount);
        }

        public static string MissingLine(string category, int count, bool atLeast, string ids)
        {
            return UiText.Get(atLeast ? "join.report.missingAtLeast" : "join.report.missing",
                category, count, Bracketed(ids));
        }

        public static string ExtraLine(string category, int count, bool atLeast, string ids)
        {
            return UiText.Get(atLeast ? "join.report.extraAtLeast" : "join.report.extra",
                category, count, Bracketed(ids));
        }

        public static string DifferLine(string category, int count, bool atLeast, string ids)
        {
            return UiText.Get(atLeast ? "join.report.differAtLeast" : "join.report.differ",
                category, count, Bracketed(ids));
        }

        // ---- why a join was refused or failed (JoinCodes): shown to the joiner; the number goes to the log ----

        // The line for a code (keys join.refused.<code>), or the bare "Joining failed (code N)." for a code with none.
        public static string Refused(int code)
        {
            string key = "join.refused." + code.ToString(CultureInfo.InvariantCulture);
            return UiText.Has(key) ? UiText.Get(key) : UiText.Get("join.refused.unknown", code);
        }

        // ---- the host's notices ("<name> is joining") ----

        public static string Notice(int kind, string name, string command)
        {
            string n = TextSanitiser.Clean(name, 24, UiText.Get("name.fallback"));
            if (kind != NoticeKind.HostConsole) return Notice(kind, name);
            return UiText.Get("join.notice.console", n, TextSanitiser.Clean(command, SolarExpanseMultiplayer.Notice.MaxTextChars, ""));
        }

        public static string Notice(int kind, string name)
        {
            string n = TextSanitiser.Clean(name, 24, UiText.Get("name.fallback"));
            switch (kind)
            {
                case NoticeKind.Joining: return UiText.Get("join.notice.joining", n);
                case NoticeKind.Joined: return UiText.Get("join.notice.joined", n);
                case NoticeKind.CouldNotJoin: return UiText.Get("join.notice.couldNotJoin", n);
                case NoticeKind.Waiting: return UiText.Get("join.notice.waiting", n);
                case NoticeKind.Rejoined: return UiText.Get("join.notice.rejoined", n);
                case NoticeKind.Left: return UiText.Get("join.notice.left", n);
                case NoticeKind.CopySaved: return UiText.Get("join.notice.copySaved", n);
                default: return n;
            }
        }

        // ---- the joiner's status line ----

        public static string Status(int stage)
        {
            switch (stage)
            {
                case JoinStageKind.Checking: return UiText.Get("join.status.checking");
                case JoinStageKind.Loading: return UiText.Get("join.status.loading");
                case JoinStageKind.Loaded: return UiText.Get("join.status.loaded");
                default: return UiText.Get("join.status.failed");
            }
        }

        public static string Downloading(int got, int total) { return UiText.Get("join.downloading", got, total); }

        public static string Waiting(int ahead)
        {
            return UiText.Get(ahead == 1 ? "join.waiting.one" : "join.waiting.many", ahead);
        }

        // Shown for five seconds before the load (Design decision 14).
        public static string Summary(string hostName, long dateTicks, string agency)
        {
            string host = TextSanitiser.Clean(hostName, 64, "");
            string who = TextSanitiser.Clean(agency, 64, "");
            string date = dateTicks >= 1 && dateTicks <= DateTime.MaxValue.Ticks
                ? new DateTime(dateTicks).ToString("d MMM yyyy", CultureInfo.InvariantCulture)
                : UiText.Get("join.summary.unknownDate");
            return UiText.Get("join.summary", host.Length == 0 ? UiText.Get("join.summary.host") : host, date,
                who.Length == 0 ? UiText.Get("join.summary.unknownAgency") : who);
        }

        // ---- rejoining ----

        public static string RejoinQuick() { return UiText.Get("join.rejoin.quick"); }

        // A rejoin that claimed another agency than the one its seat keeps for the session.
        public static string HoldsAgency(string agency)
        {
            string a = TextSanitiser.Clean(agency, 64, UiText.Get("join.rejoin.yourAgency"));
            return UiText.Get("join.rejoin.holds", a);
        }

        // A rejoin that claimed another agency is given the seat it keeps: shown while it claims that seat.
        public static string TakingKeptSeat(string agency)
        {
            string a = TextSanitiser.Clean(agency, 64, UiText.Get("join.rejoin.yourAgency"));
            return UiText.Get("join.rejoin.keptSeat", a);
        }

        // Why a rejoin is a full reload: the first failing condition of Design decision 16 (keys
        // join.rejoin.reload.<code>; any other code gets "default").
        public static string RejoinReload(int code)
        {
            string key = "join.rejoin.reload." + code.ToString(CultureInfo.InvariantCulture);
            return UiText.Get(UiText.Has(key) ? key : "join.rejoin.reload.default");
        }
    }
}

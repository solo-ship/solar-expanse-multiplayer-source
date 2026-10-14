using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SolarExpanseMultiplayer
{
    // The host's join driver, part 2: the content check, the claim and the seat (see JoinService.cs).
    public sealed partial class JoinService
    {
        void OnSummary(Joiner j, ContentSummary s, long now)
        {
            if (j.Phase != Phase.Summary) { Violation(j, "a content summary out of turn"); return; }
            ClearDrop(j);   // the summary path: whatever drop record there was is spent (a restarted game, or after a full answer)
            j.MayOffer = false;
            ContentSummary own = OwnSummary();
            ContentDiff d = null;
            try { if (own != null) d = ContentCompare.Compare(own, s); }
            catch (Exception e) { Log.Warn("join: comparing player " + j.Id + "'s content failed: " + e.GetType().Name); }
            if (d == null) { Leave(j, JoinCodes.HostSnapshotFailed, "the host has no content summary to compare with", false, true, null, true); return; }
            j.Own = own; j.Summary = s; j.Diff = d;
            if (d.Same)
            {
                Log.Info("join: player " + j.Id + " content same" + (d.LoaderOnly ? " (the loaders differ, which is allowed)" : ""));
                ContentSame(j, now);
                return;
            }
            Log.Info("join: player " + j.Id + " content differs: " + Groups(d));
            if (d.Categories.Count == 0) { RefuseContent(j); return; }
            int n = d.Categories.Count;
            j.Requested = d.Categories.ToArray();
            j.Parts = new List<ContentDetail>[n];
            j.LastSeen = new bool[n];
            for (int i = 0; i < n; i++) j.Parts[i] = new List<ContentDetail>();
            j.Phase = Phase.Detail; j.PhaseMs = now;
            if (!link.Send(j.Id, new ContentRequest { Categories = j.Requested })) Log.Warn("join: player " + j.Id + ": the detail request was not sent");
        }

        // The host's summary as the joiner's would be built (ContentSummaries.Build): its game version clipped to the 32-character cap
        // first, so the sanitiser sees the same text on both sides. A copy: the world's own is not changed.
        ContentSummary OwnSummary()
        {
            ContentSummary w;
            try { w = world.OwnSummary; } catch (Exception) { w = null; }
            if (w == null) return null;
            return new ContentSummary
            {
                GameBuild = w.GameBuild, GameVersion = ModInfo.ClipGameVersion(w.GameVersion), ModVersion = w.ModVersion, Loader = w.Loader,
                ManagedHash = w.ManagedHash, ManagedCount = w.ManagedCount, Plugins = w.Plugins, Assemblies = w.Assemblies,
                Categories = w.Categories, Overall = w.Overall,
            };
        }

        static string Groups(ContentDiff d)
        {
            var parts = new List<string>();
            if (d.Build) parts.Add("build");
            if (d.GameVersion) parts.Add("game version");
            if (d.Mod) parts.Add("mod version");
            if (d.Managed) parts.Add("game files");
            if (d.Plugins != null && d.Plugins.Count > 0) parts.Add("plugins " + d.Plugins.Count);
            if (d.Assemblies != null && d.Assemblies.Count > 0) parts.Add("assemblies " + d.Assemblies.Count);
            if (d.Categories != null) foreach (int c in d.Categories) parts.Add(ContentCategory.Name(c));
            if (d.Overall) parts.Add("overall hash");
            return string.Join(", ", parts.ToArray());
        }

        // The refusal code: the first kind of difference, in the order the report lists them.
        internal static int CodeOf(ContentDiff d)
        {
            if (d.Build || d.GameVersion) return JoinCodes.BuildDiffers;
            if (d.Mod) return JoinCodes.ModDiffers;
            if (d.Managed) return JoinCodes.ManagedDiffers;
            if ((d.Plugins != null && d.Plugins.Count > 0) || (d.Assemblies != null && d.Assemblies.Count > 0)) return JoinCodes.PluginsDiffer;
            return JoinCodes.ContentDiffers;
        }

        void OnDetail(Joiner j, ContentDetail d)
        {
            if (j.Phase != Phase.Detail) { Violation(j, "a content detail out of turn"); return; }
            int k = Array.IndexOf(j.Requested, d.Category);
            if (k < 0 || j.LastSeen[k] || d.Part != j.Parts[k].Count) { Violation(j, "a content detail that was not asked for, or out of order"); return; }
            // measured as the joiner's ContentReport.Details trims (one method on both sides), so the joiner's own trimming always
            // passes and anything more is TooMuchDetail (I-5: what the joiner may send stays under the host's byte burst)
            j.DetailBytes += ContentReport.DetailBytes(d);
            if (j.DetailBytes > JoinLimits.MaxDetailBytesTotal)
            {
                Leave(j, JoinCodes.TooMuchDetail, "more than " + JoinLimits.MaxDetailBytesTotal + " bytes of content detail", false, true, null, true);
                return;
            }
            j.Parts[k].Add(d);
            if (d.Last) j.LastSeen[k] = true;
            for (int i = 0; i < j.LastSeen.Length; i++) if (!j.LastSeen[i]) return;
            RefuseContent(j);
        }

        static int CountOf(CategoryHash[] list, int category)
        {
            if (list != null) foreach (CategoryHash c in list) if (c != null && c.Category == category) return c.Count;
            return 0;
        }

        // The report, from both sides' items of every category that was asked for, then the refusal and the close.
        void RefuseContent(Joiner j)
        {
            string[] lines = null;
            try
            {
                var hostItems = new Dictionary<int, ItemList>();
                var joinerItems = new Dictionary<int, ItemList>();
                if (j.Requested != null)
                    for (int k = 0; k < j.Requested.Length; k++)
                    {
                        int c = j.Requested[k];
                        ItemList mine = world.OwnItems(c);
                        if (mine != null) hostItems[c] = mine;
                        joinerItems[c] = ContentReport.ItemsFrom(j.Parts[k], CountOf(j.Summary.Categories, c));
                    }
                lines = ContentReport.Lines(j.Diff, j.Own, j.Summary, hostItems, joinerItems);
            }
            catch (Exception e) { Log.Warn("join: player " + j.Id + ": the content report failed (" + e.GetType().Name + "); the code alone is sent"); }
            Leave(j, CodeOf(j.Diff), "its content differs from the host's", false, true, lines, true);
        }

        // Every joiner claims straight after its summary, on every path, a full rejoin and a retry included (a standing rule),
        // so every joiner is answered on its claim and the host never sends a ClaimResult unprompted.
        // A claim that came before the content was judged is held and answered here; otherwise the claim deadline runs from here.
        void ContentSame(Joiner j, long now)
        {
            string held = j.HeldClaim;
            j.HeldClaim = null;
            if (Kept(j))
            {
                // a seat the ledger keeps for the session (Design decision 8): only once the ledger is known to be sound
                bool sound = RefreshPlayable();
                if (players[j.Id] != j) return;   // it held the host's own agency and was removed
                if (!sound) { Leave(j, JoinCodes.HostSnapshotFailed, "the host's agency is held by another player", false, true, null, true); return; }
            }
            j.Phase = Phase.Claim; j.PhaseMs = now;   // the 30 s claim deadline runs from here; a held claim meets it at once
            if (held != null) Answer(j, held, now);
        }

        // The player has loaded in this session and the ledger keeps its seat.
        bool Kept(Joiner j) { return everLoaded[j.Id] && ledger.AgencyOf(j.Id) != null; }

        void Answer(Joiner j, string agency, long now)
        {
            if (Kept(j)) AnswerKept(j, agency, now);
            else Judge(j, agency, now);
        }

        // A claim from a player whose seat the ledger keeps: Ok when it names that agency; otherwise refused, naming the agency it
        // holds, and the join stays open for a claim of that seat (a player who closed the game may start it again with
        // another -semp-agency). The seat is the player id's, which only its rejoin key (and on Steam its verified account)
        // gives back, so naming it tells the joiner nothing it does not hold. The claims a link may make still cap it, and
        // the claim deadline ends a joiner that never claims its seat (no failed join is counted: it never reached a batch).
        void AnswerKept(Joiner j, string agency, long now)
        {
            string kept = ledger.AgencyOf(j.Id);
            if (agency == kept) { Grant(j, kept, now); return; }
            j.Refusal = JoinCodes.AgencyTaken;
            link.Send(j.Id, new ClaimResult { Ok = false, Code = JoinCodes.AgencyTaken, Agency = kept });
            Log.Info("join: player " + j.Id + " claimed " + agency + " but holds " + kept + " in this session");
            if (j.Claims >= JoinLimits.MaxClaims)
                Leave(j, JoinCodes.AgencyTaken, "it claimed " + agency + " but holds " + kept + " in this session", false,
                      true, new[] { JoinText.HoldsAgency(kept), JoinText.Refused(JoinCodes.AgencyTaken) }, true);
        }

        // The joiner claims straight after its summary, since it is never told "content same" (the orchestrator's ruling from Task
        // 9b): a claim that arrives before or during the content check is held, and judged once the content is the same. If the
        // content differs the held claim is simply dropped with the refusal; an early claim is never a violation and never counts as
        // a failed join. Every claim, held or not, counts towards MaxClaims.
        void OnClaim(Joiner j, AgencyClaim c, long now)
        {
            if (j.Phase == Phase.Summary || j.Phase == Phase.Detail)
            {
                if (++j.Claims > JoinLimits.MaxClaims) { Leave(j, JoinCodes.TooManyClaims, "too many claims before the content was judged", false, true, null, true); return; }
                j.HeldClaim = c.Agency;
                return;
            }
            if (j.Phase == Phase.Queued)
            {
                // every claim counts towards MaxClaims; claiming again the agency it was just given is a protocol breach (it has
                // its answer), so a joiner cannot make the host answer for ever
                j.Claims++;
                if (c.Agency == ledger.AgencyOf(j.Id)) { Violation(j, "a second claim for the agency it was given"); return; }
                if (Kept(j)) { AnswerKept(j, c.Agency, now); return; }
                // seated by its own claim: the same agency is answered Ok again, another one is refused (the ledger's own rule)
                int held = ledger.TryClaim(j.Id, c.Agency);
                link.Send(j.Id, held == 0 ? new ClaimResult { Ok = true, Agency = c.Agency } : new ClaimResult { Ok = false, Code = held });
                if (held != 0 && j.Claims >= JoinLimits.MaxClaims) Leave(j, JoinCodes.TooManyClaims, JoinLimits.MaxClaims + " claims refused", false, true, null, true);
                return;
            }
            if (j.Phase != Phase.Claim) { Violation(j, "a claim out of turn"); return; }
            j.Claims++;
            Answer(j, c.Agency, now);
        }

        void Judge(Joiner j, string agency, long now)
        {
            // the host's list first: a ledger that would seat one agency twice is refused, as the host's fault
            int code = RefreshPlayable() ? ledger.TryClaim(j.Id, agency) : JoinCodes.HostSnapshotFailed;
            if (code == 0) { Grant(j, agency, now); return; }
            j.Refusal = code;
            link.Send(j.Id, new ClaimResult { Ok = false, Code = code });
            Log.Info("join: player " + j.Id + " claim refused (" + code + ")");
            if (j.Claims >= JoinLimits.MaxClaims) Leave(j, JoinCodes.TooManyClaims, JoinLimits.MaxClaims + " claims refused", false, true, null, true);
        }

        // The joiner never picked an agency the ledger would seat. After a refusal the player did not stall, it was
        // refused, so the host ends the join with that refusal's code, not with "took too long".
        void ClaimTimedOut(Joiner j)
        {
            int code = j.Refusal != 0 ? j.Refusal : JoinCodes.ClaimDeadline;
            string kept = Kept(j) ? ledger.AgencyOf(j.Id) : null;
            string[] lines = kept != null && code == JoinCodes.AgencyTaken
                ? new[] { JoinText.HoldsAgency(kept), JoinText.Refused(JoinCodes.AgencyTaken) } : null;
            Leave(j, code, j.Refusal != 0 ? "no agency it could hold was claimed" : "no agency claimed in time",
                  false, true, lines, true);
        }

        void Grant(Joiner j, string agency, long now)
        {
            if (!link.Send(j.Id, new ClaimResult { Ok = true, Agency = agency })) Log.Warn("join: player " + j.Id + ": the claim result was not sent");
            Log.Info("join: player " + j.Id + " claimed " + agency);
            batcher.Queue(j.Id, now);
            j.Phase = Phase.Queued; j.PhaseMs = now;
            if (prep != Prep.Idle) link.Send(j.Id, new JoinWait { Ahead = Ahead() });
        }

        // The joiners still loading in the running batch, and those queued before this one.
        int Ahead()
        {
            int n = batcher.Waiting - 1;
            foreach (int id in members) { Joiner m = players[id]; if (m != null && m.Phase == Phase.Batch) n++; }
            return n < 0 ? 0 : n > HostGate.MaxPlayerIds ? HostGate.MaxPlayerIds : n;
        }

        // The ledger's view of who may be claimed, from the host's game now. SetPlayable refuses (and changes nothing) when a joiner
        // holds the agency the host now plays, after a host load or a change of player: an agency is never seated twice. That joiner
        // must go (AgencyTaken). A seat that never loaded is then free and the ledger sound again; a loaded one stays (the ledger has
        // no Forget until M8), so the ledger stays refused and every claim and batch after it is the host's fault (HostSnapshotFailed).
        bool RefreshPlayable()
        {
            IList<string> list; string host;
            try { list = world.PlayableAgencies(); host = world.HostAgency; }
            catch (Exception e) { Log.Warn("join: the host's agencies could not be read: " + e.GetType().Name); return false; }
            if (ledger.SetPlayable(list, host)) return true;
            for (int id = 1; id < players.Length; id++)
            {
                if (ledger.AgencyOf(id) != host) continue;
                Log.Warn("join: player " + id + " holds " + TextSanitiser.OneLine(host) + ", the agency the host plays now; it is removed");
                Joiner j = players[id];
                if (j != null) Leave(j, JoinCodes.AgencyTaken, "it holds the host's own agency", false, true, null, j.Phase != Phase.Dropped);
            }
            return ledger.SetPlayable(list, host);
        }
    }
}

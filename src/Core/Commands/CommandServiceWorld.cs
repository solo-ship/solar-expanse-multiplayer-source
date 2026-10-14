using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // The host driver's shared-world part (M7 Task 6; plan DD-W1, W6, W7, W9, W10, W14): the origins of an entry, a
    // rival's seam record, the held completions and activations raised at the day event, the world print at the close,
    // the kept group prints and the heals asked of them. The resume is in CommandServiceResume.cs.
    public sealed partial class CommandService
    {
        // null = M6's behaviour exactly: the close carries a blank world print and nothing here runs
        public IWorldWorld World { get; set; }
        // optional: the notes' due test, the agencies' names, the fingerprint of a resume
        public IWorldHostSide WorldSide { get; set; }

        // ---- origins (DD-W1) ----

        // The host's own entry with an origin (Host or Ai). Raised inside an apply or a record it waits for it.
        public void HostDecided(ActionRequest a, int origin)
        {
            if (a == null) return;
            if (origin != EntryOrigin.Host && origin != EntryOrigin.Ai)
            {
                Fault("a host-decided " + ActionKind.Name(a.Kind) + " with origin " + origin);
                return;
            }
            if (applying) { deferred.Add(new Deferred { A = a, Origin = origin }); return; }
            ApplyEntry(0, a, true, origin);
        }

        // A rival's entry (a kind AiAllowed names): never for a human agency, which would be a rival's AI changing a
        // player's world, so the stream starts again.
        public void AiDecided(ActionRequest a)
        {
            if (a == null) return;
            if (!AiAllowed(a)) return;
            HostDecided(a, EntryOrigin.Ai);
        }

        bool AiAllowed(ActionRequest a)
        {
            if (!ActionKind.AiAllowed(a.Kind))
            {
                Fault("an AI entry of kind " + ActionKind.Name(a.Kind));
                return false;
            }
            if (a.Agency == null || humans.Contains(a.Agency))
            {
                Fault("an AI " + ActionKind.Name(a.Kind) + " for the human agency " + a.Agency);
                return false;
            }
            return true;
        }

        // A player's UI-only objective (88) is followed at once by the 92 that completes it for every game: it has no
        // world cause, so the day event would be too late (a paused session has none).
        void AfterPublish(ActionRequest a, bool hostDecided)
        {
            if (hostDecided || a.Kind != ActionKind.ObjectiveUi || a.Contract == null) return;
            deferred.Add(new Deferred
            {
                Origin = EntryOrigin.Host,
                A = new ActionRequest { Kind = ActionKind.ObjectiveDone, Agency = a.Agency,
                    Contract = new ContractRef { Contract = a.Contract.Contract, Objective = a.Contract.Objective } }
            });
        }

        // ---- a rival's seam record (DD-W6) ----

        sealed class AiRecord
        {
            public string Agency;
            public int Depth;
            public bool Opened;      // this record owns the BeginRecord scope
            public int DeferredAt;   // the deferred list's length when it opened: its entry goes in at that place
            public List<AiStep> Steps;
            public List<MarketOp> Ops;
            public List<long> NewIds;
            public CounterRow[] Counters;
        }

        readonly AiRecord aiRecord = new AiRecord();
        bool aiOpen;

        // A seam method (one task call) starts: world changes made now are the rival's. A second open inside it is the
        // same record. The record owns M5's scope (BeginRecord) so an entry raised inside it waits behind the rival's.
        public void AiRecordOpen(string agency)
        {
            if (aiOpen) { aiRecord.Depth++; return; }
            aiOpen = true;
            aiRecord.Agency = agency;
            aiRecord.Depth = 1;
            aiRecord.DeferredAt = deferred.Count;
            aiRecord.Opened = BeginRecord();
        }

        public bool AiRecordOpened { get { return aiOpen; } }

        public void AiRecordStep(AiStep s)
        {
            if (!aiOpen || s == null) return;
            if (aiRecord.Steps == null) aiRecord.Steps = new List<AiStep>();
            aiRecord.Steps.Add(s);
        }

        public void AiRecordOp(MarketOp o)
        {
            if (!aiOpen || o == null) return;
            if (aiRecord.Ops == null) aiRecord.Ops = new List<MarketOp>();
            aiRecord.Ops.Add(o);
        }

        public void AiRecordNewId(long id)
        {
            if (!aiOpen) return;
            if (aiRecord.NewIds == null) aiRecord.NewIds = new List<long>();
            aiRecord.NewIds.Add(id);
        }

        // A rival's observatory order (kind 40) the game already made inside the open record: its own entry after the
        // record's 94, with the numbers it was given, so the host's game does not order it a second time. The numbers
        // are the dice the order drew and the observatory's state after: a client's apply replays and checks both,
        // so an order without them could only put every client out of step.
        public void AiOrderRecorded(ActionRequest a, ApplyNumbers numbers)
        {
            if (!aiOpen || a == null || !AiAllowed(a)) return;
            if (numbers == null || numbers.After == null)
            {
                Fault("ai: " + a.Agency + "'s " + ActionKind.Name(a.Kind) + " came without its dice and state");
                return;
            }
            deferred.Add(new Deferred { A = a, Origin = EntryOrigin.Ai, Recorded = numbers });
        }

        // The counters the call moved (ProductionCountId, NameCounter, OfferCount), read by the seam at open and close.
        public void AiRecordCounters(CounterRow[] rows)
        {
            if (aiOpen) aiRecord.Counters = rows;
        }

        // The seam method ended (in a finally): the record's 94 if it has steps or ops, then the scope ends and what
        // was raised inside it drains behind it. Raises nothing and allocates nothing for a call that changed nothing.
        public void AiRecordClose()
        {
            if (!aiOpen) return;
            if (--aiRecord.Depth > 0) return;
            aiOpen = false;
            bool opened = aiRecord.Opened;
            try
            {
                if (HasContent(aiRecord)) RaiseRecord(aiRecord);
            }
            finally
            {
                aiRecord.Steps = null; aiRecord.Ops = null; aiRecord.NewIds = null; aiRecord.Counters = null;
                EndRecord(opened);
            }
        }

        static bool HasContent(AiRecord r)
        {
            return (r.Steps != null && r.Steps.Count > 0) || (r.Ops != null && r.Ops.Count > 0);
        }

        void RaiseRecord(AiRecord r)
        {
            var a = new ActionRequest { Kind = ActionKind.AiAction, Agency = r.Agency };
            if (!AiAllowed(a)) return;
            var n = new WorldNumbers
            {
                Steps = r.Steps != null && r.Steps.Count > 0 ? r.Steps.ToArray() : null,
                Ops = r.Ops != null && r.Ops.Count > 0 ? r.Ops.ToArray() : null,
                NewIds = r.NewIds != null && r.NewIds.Count > 0 ? r.NewIds.ToArray() : null,
                Counters = r.Counters
            };
            // the rival's entry goes ahead of what was raised inside its call (a craft its build finished)
            int at = Math.Min(r.DeferredAt, deferred.Count);
            deferred.Insert(at, new Deferred { A = a, Origin = EntryOrigin.Ai, Recorded = n });
        }

        // The market kinds' entries carry the ops the host made, so the room asked for before the apply is the widest
        // such entry: one op of the kind's shape in the widest form (the buy-what-is-missing kinds, up to their cap).
        static void WidenForWorld(ActionResult worst, ActionRequest a)
        {
            switch (a.Kind)
            {
                case ActionKind.OfferCreate: worst.Ops = new[] { WorstOp(MarketOps.Add, a.Agency) }; break;
                case ActionKind.OfferCancel: worst.Ops = new[] { WorstOp(MarketOps.Cancel, a.Agency) }; break;
                case ActionKind.OfferEdit:
                    worst.Ops = new[] { WorstOp(MarketOps.Cancel, a.Agency), WorstOp(MarketOps.Add, a.Agency) }; break;
                case ActionKind.OfferTake: worst.Ops = new[] { WorstOp(MarketOps.Fill, a.Agency) }; break;
                case ActionKind.MarketInstant: worst.Ops = new[] { WorstOp(MarketOps.Instant, a.Agency) }; break;
                case ActionKind.BuyAndBuild: case ActionKind.CraftBuyAndBuild:
                    worst.Ops = new MarketOp[ActionLimits.MaxBuyFills];
                    for (int i = 0; i < worst.Ops.Length; i++) worst.Ops[i] = WorstOp(MarketOps.Fill, a.Agency);
                    break;
            }
        }

        static MarketOp WorstOp(int op, string agency)
        {
            var key = new OfferKey { Id = int.MaxValue, Agency = agency, Resource = CounterRows.WideId, Buy = true,
                Where = ActionLimits.MaxBodyId, StartTicks = JoinCheck.MaxTicks };
            switch (op)
            {
                case MarketOps.Add:
                    return new MarketOp { Op = op, Offer = key, Count = ActionLimits.MaxOfferCount,
                        Price = 999999999999999 };
                case MarketOps.Cancel:
                    return new MarketOp { Op = op, Offer = key, Count = ActionLimits.MaxOfferCount,
                        Price = 999999999999999, Value = 1 };
                case MarketOps.Fill:
                    return new MarketOp { Op = op, Offer = key, Taker = agency, Count = 999999999999999.5,
                        Price = 999999999999999.5 };
                default:   // Instant
                    return new MarketOp { Op = op, Taker = agency, Resource = CounterRows.WideId,
                        Body = ActionLimits.MaxBodyId, Count = 999999999999999.5, Price = 999999999999999.5 };
            }
        }

        // ---- held completions and activations (DD-W7) ----

        readonly List<WorldNote> notes = new List<WorldNote>();
        readonly HashSet<string> noteKeys = new HashSet<string>(StringComparer.Ordinal);

        public int NotesPending { get { return notes.Count; } }

        // A completion every game held: raised at the day event, in first-noted order, once however often it is noted.
        public void NoteCompletion(string agency, ContractRef objective)
        {
            if (agency == null || objective == null || objective.Contract == null) return;
            if (!noteKeys.Add("o|" + agency + "|" + objective.Contract + "|" + objective.Objective)) return;
            notes.Add(new WorldNote { Agency = agency, Contract = objective.Contract, Objective = objective.Objective });
            Log.Info("contract: noted " + agency + " " + objective.Contract + " " + objective.Objective);
        }

        public void NoteActivation(string agency, string contract)
        {
            if (agency == null || contract == null) return;
            if (!noteKeys.Add("a|" + agency + "|" + contract)) return;
            notes.Add(new WorldNote { Activation = true, Agency = agency, Contract = contract, Objective = -1 });
            Log.Info("contract: activation noted " + agency + " " + contract);
        }

        // The noted list as 92s and 95s, in the order noted, each stamped by the moment it is raised. A note a chain
        // has made moot meanwhile (the objective is complete, the contract no longer Available) is dropped. What the
        // raised entries note in turn waits for the next round.
        int RaiseNotes()
        {
            if (notes.Count == 0) return 0;
            WorldNote[] take = notes.ToArray();
            notes.Clear();
            int raised = 0, startEpoch = epoch;
            foreach (WorldNote n in take)
            {
                if (epoch != startEpoch) return raised;   // a fault started a new stream: its notes went with it
                bool due = true;
                IWorldHostSide side = WorldSide;
                try { due = side == null || side.Due(n); }
                catch (Exception e) { Log.Warn("contract: the due test threw: " + e.GetType().Name); }
                noteKeys.Remove(n.Activation ? "a|" + n.Agency + "|" + n.Contract
                    : "o|" + n.Agency + "|" + n.Contract + "|" + n.Objective);
                if (!due) continue;
                HostDecided(new ActionRequest
                {
                    Kind = n.Activation ? ActionKind.ContractActivated : ActionKind.ObjectiveDone, Agency = n.Agency,
                    Contract = new ContractRef { Contract = n.Contract, Objective = n.Objective }
                });
                raised++;
            }
            return raised;
        }

        // The host's SaveToFile prefix: every note raised and applied now, round after round until none is left (an
        // applied entry can note more). Past MaxFlushRounds the world is not settling: a host fault.
        public void FlushNotes()
        {
            int rounds = 0, startEpoch = epoch;
            while (notes.Count > 0)
            {
                if (rounds++ >= ActionLimits.MaxFlushRounds)
                {
                    Log.Error("contract: save flush did not settle");
                    Fault("the save flush did not settle in " + ActionLimits.MaxFlushRounds + " rounds");
                    return;
                }
                Log.Info("contract: save flush " + notes.Count + " notes");
                RaiseNotes();
                if (epoch != startEpoch) return;
            }
        }

        // ---- the log lines the tools read ----

        int growthPart;

        void LogWorldEntry(ActionResult r)
        {
            string line = WorldLog.EntryLine(r, ref growthPart);
            if (line != null) Log.Info(line);
        }

        // ---- the world print and the kept group prints (DD-W9, DD-W10) ----

        sealed class KeptWorld { public GroupPrint[][] ByArea; public int Bytes; }

        readonly Dictionary<long, KeptWorld> keptWorld = new Dictionary<long, KeptWorld>();
        readonly List<long> keptWorldDays = new List<long>();
        long keptWorldBytes;
        long lastYearEventDay, lastHealDay;
        readonly HealPlan healRules = new HealPlan(ActionLimits.HealRepeatDays, ActionLimits.ValueHealsPerAgency);

        // The close's world print: one read at the day-event point. A host that cannot read the world throws, which
        // DayClosing turns into a new stream; with no world side attached the print is the blank one.
        DayClosed WithWorld(DayClosed d, long day)
        {
            IWorldWorld w = World;
            if (w == null) return DayClosed.WithBlankWorld(d);
            DaySnapshot snap = w.ReadWorld();
            AreaPrint[] areas = WorldPrint.Areas(snap);
            string whole = WorldPrint.Day(snap);
            if (areas == null || whole == null) throw new InvalidOperationException("the world could not be read");
            if (w.YearEventDue(world.NextDayTicks)) lastYearEventDay = day;
            bool valueDay = WorldTolerance.IsValueDay(day, lastYearEventDay, lastHealDay);
            d.World = areas;
            d.WorldDay = whole;
            if (valueDay) d.Values = NamedValues(WorldPrint.Values(snap));
            KeepGroups(w, day, valueDay);
            Log.Info("world: day " + day + " print " + whole);
            return d;
        }

        // The block sums with the agencies' names in place of the snapshot's agency positions.
        AreaValue[] NamedValues(AreaValue[] values)
        {
            if (values == null) return null;
            string[] names = WorldSide != null ? WorldSide.AgencyNames() : null;
            int n = Math.Min(values.Length, ActionLimits.MaxAreaValues);
            if (values.Length > n)
                Log.Warn("world: " + (values.Length - n) + " block sums over the cap of " + n + " are not sent");
            if (names == null) throw new InvalidOperationException("no agency names for the block positions");
            var cut = new AreaValue[n];
            Array.Copy(values, cut, n);
            AreaValue[] named = WorldPrint.Named(cut, names);   // the client names its own sums the same way
            if (named.Length != n) throw new InvalidOperationException("no agency name for a block position");
            return named;
        }

        bool AnyClient()
        {
            foreach (Player p in players) if (p.Id > 0) return true;
            return false;
        }

        // Every area's groups, kept for the last KeptPrintDays so a client's difference can be found by group. Only
        // while a client has joined: nobody else could ask. A failed group read costs this day's groups, not the stream.
        void KeepGroups(IWorldWorld w, long day, bool valueDay)
        {
            if (!AnyClient()) return;
            try
            {
                string[] names = WorldSide != null ? WorldSide.AgencyNames() : null;
                var kept = new KeptWorld { ByArea = new GroupPrint[DriftAreas.All.Length][] };
                for (int area = 0; area < kept.ByArea.Length; area++)
                {
                    var groups = new List<GroupPrint>();
                    if (!DriftAreas.PerAgency(area))
                        groups.AddRange(WorldPrint.Groups(w.ReadItems(area, null), area, valueDay));
                    else if (names != null)
                        foreach (string agency in names)
                            groups.AddRange(WorldPrint.Groups(w.ReadItems(area, agency), area, valueDay));
                    kept.ByArea[area] = groups.ToArray();
                    kept.Bytes += groups.Count * (valueDay ? 160 : 64);
                }
                Keep(day, kept);
            }
            catch (Exception e)
            {
                Log.Warn("world: day " + day + " group prints not kept: " + e.GetType().Name + " " + e.Message);
            }
        }

        void Keep(long day, KeptWorld kept)
        {
            if (keptWorld.ContainsKey(day)) keptWorldBytes -= keptWorld[day].Bytes;
            else keptWorldDays.Add(day);
            keptWorld[day] = kept;
            keptWorldBytes += kept.Bytes;
            while (keptWorldDays.Count > 1 && (day - keptWorldDays[0] >= ActionLimits.KeptPrintDays
                || keptWorldBytes > ActionLimits.MaxKeptPrintBytes))
            {
                keptWorldBytes -= keptWorld[keptWorldDays[0]].Bytes;
                keptWorld.Remove(keptWorldDays[0]);
                keptWorldDays.RemoveAt(0);
            }
        }

        // ---- heals (DD-W10) ----

        sealed class WorldPlayer
        {
            public readonly Queue<HealRequest> Heals = new Queue<HealRequest>();
            public readonly HashSet<string> HealKeys = new HashSet<string>(StringComparer.Ordinal);
            public readonly HashSet<string> PrintsAsked = new HashSet<string>(StringComparer.Ordinal);
            public ResumeState Resume;
        }

        readonly Dictionary<int, WorldPlayer> worldPlayers = new Dictionary<int, WorldPlayer>();

        WorldPlayer WorldOf(int id)
        {
            WorldPlayer w;
            if (!worldPlayers.TryGetValue(id, out w)) worldPlayers[id] = w = new WorldPlayer();
            return w;
        }

        void Answer(Player p, HealRequest r, int code)
        {
            SendMinor(p, new HealData { Code = code, Day = r.Day, Area = r.Area, Agency = r.Agency, Group = r.Group,
                Pieces = 1 });
        }

        // A client's difference in one area. No group named: the area's group prints for that day, from the kept ones,
        // now. A group named: read at the next close. Always answered, however it ends; bounded by structure (one
        // outstanding heal per group, one print ask per area and day, only restorable areas, only kept days).
        public void HealWanted(int player, HealRequest r)
        {
            if (r == null || r.Validate() != null) return;
            Player p = PlayerOf(player);
            WorldPlayer w = WorldOf(player);
            KeptWorld kept;
            if (!keptWorld.TryGetValue(r.Day, out kept)) { Answer(p, r, HealCodes.NoPrint); return; }
            if (r.Group == null) { SendGroupPrints(p, w, r, kept); return; }
            if (!healRules.Restorable(r.Area)) { Answer(p, r, HealCodes.NotRestorable); return; }
            if (!HasGroup(kept.ByArea[r.Area], r.Agency, r.Group)) { Answer(p, r, HealCodes.NoPrint); return; }
            if (!w.HealKeys.Add(r.Area + "|" + r.Agency + "|" + r.Group)) { Answer(p, r, HealCodes.TooMany); return; }
            w.Heals.Enqueue(r);
            Log.Info("heal: player " + player + " asked " + DriftAreas.All[r.Area] + " " + (r.Agency ?? "-") + " "
                + r.Group + " day " + r.Day);
        }

        static bool HasGroup(GroupPrint[] groups, string agency, string group)
        {
            foreach (GroupPrint g in groups)
                if (string.Equals(g.Group, group, StringComparison.Ordinal)
                    && string.Equals(g.Agency, agency, StringComparison.Ordinal)) return true;
            return false;
        }

        void SendGroupPrints(Player p, WorldPlayer w, HealRequest r, KeptWorld kept)
        {
            if (!w.PrintsAsked.Add(r.Area + "|" + r.Day)) { Answer(p, r, HealCodes.TooMany); return; }
            GroupPrint[] all = kept.ByArea[r.Area];
            int per = ActionLimits.MaxGroupsPerPiece;
            int pieces = Math.Max(1, (all.Length + per - 1) / per);
            if (all.Length > ActionLimits.MaxGroupPrints || pieces > ActionLimits.MaxHealPieces)
            {
                Log.Warn("heal: " + DriftAreas.All[r.Area] + " day " + r.Day + " has " + all.Length
                    + " group prints, over " + ActionLimits.MaxGroupPrints);
                Answer(p, r, HealCodes.TooBig);
                return;
            }
            var items = new List<Item>();
            for (int k = 0; k < pieces; k++)
            {
                var groups = new GroupPrint[Math.Min(per, all.Length - k * per)];
                Array.Copy(all, k * per, groups, 0, groups.Length);
                var d = new HealData { Code = HealCodes.Ok, Day = r.Day, Area = r.Area, Agency = null, Group = null,
                    Piece = k, Pieces = pieces, Groups = groups };
                string why;
                Item it = Encode(d, out why);
                if (it == null)
                {
                    Log.Warn("heal: group prints not sendable: " + why);
                    Answer(p, r, HealCodes.TooBig);
                    return;
                }
                items.Add(it);
            }
            if (p.Objects.Count + items.Count > MaxObjectQueue) { Answer(p, r, HealCodes.TooMany); return; }
            foreach (Item it in items) p.Objects.Enqueue(it);
        }

        // After DayClosed(day) went out: up to MaxHealsPerClose queued group heals per client are read now and sent as
        // pieces stamped with the close's day and last seq, which is the moment the client restores them.
        void SendQueuedHeals(long day)
        {
            if (World == null) return;
            foreach (KeyValuePair<int, WorldPlayer> kv in worldPlayers)
            {
                WorldPlayer w = kv.Value;
                Player p;
                if (w.Heals.Count == 0 || !byId.TryGetValue(kv.Key, out p)) continue;
                for (int n = 0; n < ActionLimits.MaxHealsPerClose && w.Heals.Count > 0; n++)
                {
                    HealRequest r = w.Heals.Dequeue();
                    w.HealKeys.Remove(r.Area + "|" + r.Agency + "|" + r.Group);
                    SendHeal(p, r, day);
                }
            }
        }

        void SendHeal(Player p, HealRequest r, long day)
        {
            HealData[] pieces;
            try { pieces = World.ReadGroup(r.Area, r.Agency, r.Group, ActionLimits.MaxHealRows); }
            catch (Exception e)
            {
                Log.Warn("heal: reading " + r.Group + " threw: " + e.GetType().Name);
                Answer(p, r, HealCodes.NotRestorable);
                return;
            }
            if (pieces == null || pieces.Length == 0) { Answer(p, r, HealCodes.NotRestorable); return; }
            var items = new List<Item>();
            int rows = 0;
            foreach (HealData d in pieces)
            {
                if (d == null) { Answer(p, r, HealCodes.NotRestorable); return; }
                if (d.Code != HealCodes.Ok) { Answer(p, r, d.Code); return; }
                d.Day = day; d.AfterSeq = seq;
                rows += d.Rows == null ? 0 : d.Rows.Length;
                string why;
                Item it = Encode(d, out why);
                if (it == null)
                {
                    Log.Warn("heal: " + r.Group + " not sendable: " + why);
                    Answer(p, r, HealCodes.TooBig);
                    return;
                }
                items.Add(it);
            }
            if (p.Objects.Count + items.Count > MaxObjectQueue) { Answer(p, r, HealCodes.TooMany); return; }
            foreach (Item it in items) p.Objects.Enqueue(it);
            lastHealDay = day;
            Log.Info("heal: sent " + DriftAreas.All[r.Area] + " " + (r.Agency ?? "-") + " " + r.Group + " day " + day
                + " rows " + rows + " pieces " + items.Count);
        }

        // ---- a new stream ----

        void ResetWorldState()
        {
            notes.Clear();
            noteKeys.Clear();
            keptWorld.Clear();
            keptWorldDays.Clear();
            keptWorldBytes = 0;
            lastYearEventDay = 0;
            lastHealDay = 0;
            growthPart = 0;
            replay.Reset();
            foreach (WorldPlayer w in worldPlayers.Values)
            {
                w.Heals.Clear(); w.HealKeys.Clear(); w.PrintsAsked.Clear();
                if (w.Resume != null) EndResume(w.Resume, false, "a new stream", false);
            }
            aiOpen = false;
            aiRecord.Steps = null; aiRecord.Ops = null; aiRecord.NewIds = null; aiRecord.Counters = null;
        }

        // A player's link ended or was replaced: its heals and print asks go with it; a resume it was in fails.
        void WorldForget(int player)
        {
            WorldPlayer w;
            if (!worldPlayers.TryGetValue(player, out w)) return;
            w.Heals.Clear(); w.HealKeys.Clear(); w.PrintsAsked.Clear();
            if (w.Resume != null) EndResume(w.Resume, false, "its link ended", false);
        }

        void WorldUpdate(long now)
        {
            if (resumes.Count == 0) return;
            for (int i = resumes.Count - 1; i >= 0; i--)
            {
                if (i >= resumes.Count) continue;
                ResumeState s = resumes[i];
                if (now - s.StartMs > ActionLimits.ResumeDeadlineMs) { EndResume(s, false, "deadline", true); continue; }
                FeedResume(s);
                TryCheck(s);
            }
        }
    }
}

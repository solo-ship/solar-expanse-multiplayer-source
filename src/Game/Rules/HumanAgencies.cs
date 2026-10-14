using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Company = Game.Company;
using GameManager = Manager.GameManager;

namespace SolarExpanseMultiplayer
{
    // What the host raises into the command stream from inside a rule (CommandService.HostDecided; Task 10a gives the
    // host's implementation). A client has none: it only holds and applies.
    public interface IHostDecisions
    {
        // The day event found a human agency's money gone (DD9): the entry stops the agency on every game.
        void AgencyBankrupt(string agency);

        // A build finished and took craft ids outside any applied entry (DD11), or a facility finished (R19-1).
        void CraftFinished(CraftTaken taken);

        // An entry the host's own game decided and the rules saw happen (DD14, DD15): an observatory's step, find,
        // system or re-plan (kinds 54-57), a spawn with no removal (58), an impact's removal (59). The request names
        // it; the numbers carry the dice, the state after and the spawns it made, which every client replays.
        void Decided(ActionRequest request, ApplyNumbers numbers);

        // The host's own game did something the clients cannot be told (an observatory that cannot be named, a
        // record already open): the entry cannot be refused, so the stream resets (StreamCodes.HostFault) and every
        // client rejoins fully, rather than carrying on with a world that has parted from the host's.
        void HostFault(string why);

        // M7's world entries (the maker, the growth, the AI record, the held contracts, the end-game ship) are the
        // other host seam, IWorldDecisions (src/Game/World/WorldRuleState.cs), which the same CommandSession is.
    }

    // One build completion as the host saw it: which item finished and how far each counter moved. Item is the
    // finished item's content id (a spacecraft or launch vehicle type, the facility that made the ship, or a finished
    // facility's descriptor); ItemId is its item id in the agency's own counter. ProductionBefore and After are the
    // agency's own item counter (a facility that stacks takes a new id); Draws are the dice a probe observatory made
    // while the craft was built.
    public sealed class CraftTaken
    {
        public string Agency, Item;
        public int Body;
        public bool Outer;     // not inside another craft-taking Build (internal to the rule)
        public bool Facility;  // a finished facility, not a craft
        public bool Recorded;  // this Build opened the dice record (internal to the rule)
        public long ItemId, SpacecraftBefore, SpacecraftAfter, LaunchVehicleBefore, LaunchVehicleAfter;
        public long ProductionBefore, ProductionAfter;
        public ObsDraw[] Draws;
    }

    // The session's human agencies as the rule patches see them (docs/superpowers/plans/2026-10-12-m5-commands.md,
    // "Game seams"). Every rule patch first asks Active, so with no session the game runs exactly as it was; Session
    // starts it (the HumanSet is the session's, kept across loads) and stops it with the session. The one object
    // holding the state is the Root below; nothing else in the rules is static state.
    public static class HumanAgencies
    {
        sealed class Root
        {
            public HumanSet Humans;
            public IHostDecisions Host;
            public Func<bool> Applying;
            public Func<int> Kind;
            public Func<ActionRequest> Entry;
            public Action Changed;
            public int CraftBuildDepth;
            public bool RefreshSkipped;
            public RuleState State = new RuleState();
        }

        static Root root;

        // A session is running with the rules on (never with -semp-no-human-rules, never outside a session).
        public static bool Active { get { return root != null; } }

        // The host's raise seam; null on a client and with no session.
        public static IHostDecisions Host { get { Root r = root; return r != null ? r.Host : null; } }

        // A command entry is being applied on this game (the rules leave an applied entry's own changes alone).
        public static bool Applying
        {
            get { Root r = root; return r != null && r.Applying != null && r.Applying(); }
        }

        // The kind of the entry being applied on this game (ActionKind), -1 when none or before the glue set it. M7's
        // holds read it, never the bare Applying flag.
        public static int ApplyingKind
        {
            get { Root r = root; return r != null && r.Kind != null ? r.Kind() : -1; }
        }

        // The entry being applied (null when none): the holds of M7 name the contract or objective it carries.
        public static ActionRequest ApplyingEntry
        {
            get { Root r = root; return r != null && r.Entry != null ? r.Entry() : null; }
        }

        public static void SetKindProbe(Func<int> kind, Func<ActionRequest> entry)
        {
            Root r = root;
            if (r == null) return;
            r.Kind = kind;
            r.Entry = entry;
        }

        // The set holds wire ids (the game's id in lower case, as the stream, the header and the seats name an
        // agency), so the game's Company.ID is matched in that form.
        public static bool IsHuman(Company company)
        {
            Root r = root;
            return r != null && company != null && r.Humans.Contains(AgencyWire.From(company.ID));
        }

        // The observatory, asteroid and flight rules' state (see RuleState); null with no session.
        public static RuleState State { get { Root r = root; return r != null ? r.State : null; } }

        // The dice and the spawns, as CommandSession wraps them around an apply or a host-decided creation.
        public static ObservatoryDrawState Dice { get { Root r = root; return r != null ? r.State.Dice : null; } }

        public static AsteroidSpawnState Spawns { get { Root r = root; return r != null ? r.State.Spawns : null; } }

        // A load: no record, replay, note or pending system survives it (the session's other state does).
        public static void ResetForLoad()
        {
            Root r = root;
            if (r == null) return;
            r.State.Reset();
            r.CraftBuildDepth = 0;   // a Build that a load cut short must not leave every later one "inner"
        }

        // A craft-taking Build() is running (a Build can start another through AddFacility): only the outermost one
        // raises its entry, whose counters span the inner ones, so a client's Release finds its item first.
        public static int EnterCraftBuild()
        {
            Root r = root;
            return r == null ? 0 : ++r.CraftBuildDepth;
        }

        public static void LeaveCraftBuild()
        {
            Root r = root;
            if (r != null && r.CraftBuildDepth > 0) r.CraftBuildDepth--;
        }

        // ProductionItem.UpdateBuilding is running on the host: a facility whose Build() it reaches finished by the
        // progress update, which is what the clients hold and the host announces (a facility built any other way is
        // inside an applied entry or a load).
        public static void EnterBuildUpdate()
        {
            Root r = root;
            if (r != null) r.State.BuildUpdateDepth++;
        }

        public static void LeaveBuildUpdate()
        {
            Root r = root;
            if (r != null && r.State.BuildUpdateDepth > 0) r.State.BuildUpdateDepth--;
        }

        public static bool InBuildUpdate { get { Root r = root; return r != null && r.State.BuildUpdateDepth > 0; } }

        // The queue payment skipped refreshing the local window (it was another agency's); the section-opening call
        // that follows the refresh is skipped with it. TakeRefreshSkipped reads and clears it.
        public static void NoteRefreshSkipped()
        {
            Root r = root;
            if (r != null) r.RefreshSkipped = true;
        }

        public static bool TakeRefreshSkipped()
        {
            Root r = root;
            if (r == null || !r.RefreshSkipped) return false;
            r.RefreshSkipped = false;
            return true;
        }

        public static void Start(HumanSet humans, IHostDecisions host, Func<bool> applying)
        {
            if (humans == null) throw new ArgumentNullException("humans");
            Stop();
            var r = new Root { Humans = humans, Host = host, Applying = applying };
            r.Changed = () => Log.Info("rules: human agencies " + string.Join(",", humans.All()));
            humans.Changed += r.Changed;
            root = r;
            r.Changed();
        }

        // A client's rule could not decide what the host's game decides: this game has left the stream, so it takes
        // the full rejoin. A no-op on the host and outside a session.
        public static void ClientOutOfStep(string why)
        {
            CommandSession c = Session.Commands;
            CommandClientGlue g = c != null ? c.ClientGlue : null;
            if (g != null) g.Client.OutOfStepNow(why);
        }

        public static void Stop()
        {
            Root r = root;
            root = null;
            if (r != null) r.Humans.Changed -= r.Changed;
        }

        // ---- called from the changed game code (Il.Replace puts a call to these where the game asks "is this the
        // local player?") ----

        // S1 and S3: the game's "company != the local player" test. With no session it is exactly that test; in a
        // session a human agency passes it too.
        public static bool SkipsAgency(UnityEngine.Object company, UnityEngine.Object player)
        {
            if (!Active) return company != player;
            return HumanRules.SkipsAgency(true, company == player, IsHuman(company as Company));
        }

        // S4: whose research slot the daily research cost reads. The game: the local player's.
        public static Company ResearchOwner(GameManager manager, Company company)
        {
            if (!Active) return manager.Player;
            return HumanRules.OwnBonus(true) ? company : manager.Player;
        }
    }

    // Rewriting a game method's IL: replace calls and say so loudly when the game is not what the rule expects. A
    // transpiler that finds another count than it was written for throws, which fails its hook, which stops the session
    // (I8), so a game update cannot leave a rule half applied.
    static class Il
    {
        static readonly Type GameManagerType = typeof(GameManager);

        public static bool Calls(CodeInstruction i, Type declaring, string name, int parameters)
        {
            if (i.opcode != OpCodes.Call && i.opcode != OpCodes.Callvirt) return false;
            var m = i.operand as MethodBase;
            return m != null && m.DeclaringType == declaring && m.Name == name
                && (parameters < 0 || m.GetParameters().Length == parameters);
        }

        public static bool IsGetPlayer(CodeInstruction i) { return Calls(i, GameManagerType, "get_Player", 0); }

        // Every instruction `when` accepts (given the whole list and its index) becomes the instructions `with` makes.
        // Labels and exception blocks that pointed at it move to the first new one. `expected` is how many the game's
        // method has today.
        public static List<CodeInstruction> Replace(IEnumerable<CodeInstruction> code, string where, int expected,
            Func<List<CodeInstruction>, int, bool> when, Func<CodeInstruction[]> with)
        {
            var list = new List<CodeInstruction>(code);
            var result = new List<CodeInstruction>(list.Count + expected * 2);
            int found = 0;
            for (int i = 0; i < list.Count; i++)
            {
                if (!when(list, i)) { result.Add(list[i]); continue; }
                CodeInstruction[] repl = with();
                repl[0].labels.AddRange(list[i].labels);
                repl[0].blocks.AddRange(list[i].blocks);
                result.AddRange(repl);
                found++;
            }
            if (found != expected)
                throw new InvalidOperationException(where + ": found " + found
                    + " place(s) to change, the rule expects "
                    + expected + " (the game's code is not what it was written for)");
            return result;
        }

        // The game's "x != GameManager.Player" test (UnityEngine.Object's operator) becomes HumanAgencies.SkipsAgency.
        public static List<CodeInstruction> SkipTestToHumans(IEnumerable<CodeInstruction> code, string where)
        {
            MethodInfo to = AccessTools.Method(typeof(HumanAgencies), "SkipsAgency");
            return Replace(code, where, 1,
                (c, i) => i > 0 && IsGetPlayer(c[i - 1]) && Calls(c[i], typeof(UnityEngine.Object), "op_Inequality", 2),
                () => new[] { new CodeInstruction(OpCodes.Call, to) });
        }

        // The game's "GameManager.Player" (a call with the manager on the stack) becomes `to(manager, <argument>)`
        // where the argument is the instruction `load` pushes: this, or the method's own company parameter.
        public static List<CodeInstruction> PlayerToOwner(IEnumerable<CodeInstruction> code, string where, int expected,
            Func<List<CodeInstruction>, int, bool> when, OpCode load, MethodInfo to)
        {
            return Replace(code, where, expected, (c, i) => IsGetPlayer(c[i]) && when(c, i),
                () => new[] { new CodeInstruction(load), new CodeInstruction(OpCodes.Call, to) });
        }
    }
}

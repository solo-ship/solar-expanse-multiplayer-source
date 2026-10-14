using System;
using System.Collections.Generic;
using ObjectInfo = Game.Info.ObjectInfo;
using ObservatoryFacility = Game.ObjectInfoDataScripts.ObservatoryFacility;
using RowExploredResourcesData = Game.UI.Windows.Elements.ObjectInfoElements.RowExploredResourcesData;

namespace SolarExpanseMultiplayer
{
    // An observatory re-plan the host held back to run on its own record, right after what caused it (DD14, R8-1):
    // its target was destroyed (Destroyed), or its target's last deposit was explored while another observatory's
    // step was running (Row). The host runs it and raises it as an ObservatoryReplan.
    public sealed class ReplanNote
    {
        public ObservatoryFacility Observatory;
        public ObjectInfo Destroyed;
        public RowExploredResourcesData Row;
        // the body the entry's ToBody names: the destroyed object, or the examined body for a listener
        public int ToBody;
    }

    // A new star system being made or applied: the host records what it generated, a client is given what to make.
    public sealed class PendingSystem
    {
        public int Index;            // the systemsToDiscover index
        public int Seed;             // 0..MaxSeed
        public string Name;          // the generated system's name; null for a system that already exists
        public int ObjectId = -1;    // the world object's id: the host's after the call, a client's to give
        public bool Generated;       // this call generated the system (the host)
        public bool Given;           // the host's numbers were handed in (a client applying)
        public string Problem;       // what the client's apply found different from the entry
    }

    // Everything the observatory, asteroid and flight rules remember between one game call and the next, in one
    // object the HumanAgencies root owns (so "no loose static state" holds). A load or a new session starts it again.
    public sealed class RuleState
    {
        // the dice and spawns: the host records into them, a client replays from them (CommandSession reaches them
        // through HumanAgencies)
        public ObservatoryDrawState Dice;
        public AsteroidSpawnState Spawns;
        // the host's generators for a new system's seed and for a spawned asteroid's values
        public ModRandom SystemRng, SpawnRng;
        public readonly List<ReplanNote> Notes = new List<ReplanNote>();
        // which observatories have a listener re-plan noted (once each, and only a real one)
        public readonly ListenerGate<ObservatoryFacility> Listeners = new ListenerGate<ObservatoryFacility>();
        // the game time each held build was first held, for the length of the hold when it is released
        public readonly Dictionary<string, long> HeldSince = new Dictionary<string, long>();
        // the observatory whose re-plan handler is allowed to run now (the host's own run of a noted one, or a client
        // applying an ObservatoryReplan); null = none, every handler is held
        public ObservatoryFacility Replanning;
        // the observatory MyUpdate is stepping, so a survey crossing inside it can be named
        public ObservatoryFacility Stepping;
        public PendingSystem Pending;
        // a client applying an impact's ObjectRemoved: the mission whose landing handler may run (-1 = none)
        public long ImpactMission = -1;
        // FS2: the leftover fuel a saved flight was read with, kept across the game's scheduling call
        public double SavedFuel;
        public bool FuelArmed;
        // how many UpdateBuilding calls are running on the host (a facility finished inside one is an entry)
        public int BuildUpdateDepth;
        // where each fresh build unit's progress starts counting (BuildStarts)
        public readonly BuildStarts Starts = new BuildStarts();
        // M6: the flight rules' and flight sources' state (src/Game/Flights/FlightState.cs)
        public readonly FlightState Flights = new FlightState();
        // the host logged once that an agency had more ship-name counters than a message carries (kept over loads)
        public bool NameKeysDropLogged;
        // M7's world rules (src/Game/World): the maker, growth, AI and contract records
        public readonly WorldRuleState World = new WorldRuleState();

        public RuleState()
        {
            long seed = DateTime.UtcNow.Ticks ^ ((long)Environment.TickCount << 17);
            Dice = new ObservatoryDrawState(new ModRandom(seed));
            Spawns = new AsteroidSpawnState();
            SystemRng = new ModRandom(seed * 31 + 7);
            SpawnRng = new ModRandom(seed * 17 + 3);
        }

        // A load: nothing open, nothing remembered. The generators carry on.
        public void Reset()
        {
            Dice.Reset();
            Spawns.Reset();
            Notes.Clear();
            Listeners.Clear();
            HeldSince.Clear();
            Replanning = null;
            Stepping = null;
            Pending = null;
            ImpactMission = -1;
            FuelArmed = false;
            BuildUpdateDepth = 0;
            Starts.Reset();
            World.Reset();
            Flights.Reset();
        }
    }
}

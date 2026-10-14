using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // The joiner's habitability prewarm skip for one snapshot load. At every load the game steps each body's
    // habitability model 16 times and moves its deposit rows between gas, liquid and solid (ObjectInfo.AfterLoadState).
    // That model has memory the save keeps (the saturation pressure moves at most 10% a step) and the load loop starts
    // its temperature from a different point than the monthly step, so a joiner that ran it would hold a world that
    // differs from the host's for good, while the snapshot already holds the host's exact values. The game glue opens
    // this gate around the joiner's ExtractAllFromSaveData only; the Harmony prefixes skip a step while it is open.
    // Main thread only (the game runs the model there).
    public sealed class PrewarmSkip
    {
        readonly Dictionary<int, int> stepsOf = new Dictionary<int, int>();      // model steps per body
        readonly Dictionary<int, int> depositsOf = new Dictionary<int, int>();   // deposit steps per body
        readonly HashSet<int> bodies = new HashSet<int>();

        public bool Active { get; private set; }
        public int Steps { get; private set; }      // HabitabilityParametersNew.Update calls skipped
        public int Deposits { get; private set; }   // UpdateDepositStates calls skipped
        public int Bodies { get { return bodies.Count; } }

        // Right before the extract; the counts are this load's own.
        public void Open()
        {
            stepsOf.Clear();
            depositsOf.Clear();
            bodies.Clear();
            Steps = 0;
            Deposits = 0;
            Active = true;
        }

        // True = skip this model step (the gate is open); body: any id that tells the bodies apart.
        public bool Step(int body)
        {
            if (!Active) return false;
            Steps++;
            bodies.Add(body);
            int n;
            stepsOf.TryGetValue(body, out n);
            stepsOf[body] = n + 1;
            return true;
        }

        // True = skip this deposit step.
        public bool Deposit(int body)
        {
            if (!Active) return false;
            Deposits++;
            bodies.Add(body);
            int n;
            depositsOf.TryGetValue(body, out n);
            depositsOf[body] = n + 1;
            return true;
        }

        // Shuts the gate. The log line for the load that opened it, or null when it was not open (a second close).
        public string Close()
        {
            if (!Active) return null;
            Active = false;
            if (Steps == 0 && Deposits == 0)
                return "join: habitability prewarm skip was armed but skipped nothing (a game change?)";
            return "join: habitability prewarm skipped for " + Bodies + " bodies (" + Steps + " model steps, " +
                   Deposits + " deposit steps)";
        }

        // After the extract: null when the skip saw the warm-up it was made for, else why not. rounds: the game's
        // warm-up rounds per body (preWarmInitCalculationsPerObject; below 1 when it could not be read, which is
        // refused too: the mod cannot tell what the game did). The rule is per body, so a save with fewer bodies
        // passes: every body whose model was stepped must have had at least `rounds` model steps and as many deposit
        // steps. Fewer means the warm-up moved (a game update) and the joiner would drift from the host for good, so
        // the load is refused.
        public string Verdict(int rounds)
        {
            if (rounds < 1) return "the game's habitability warm-up count could not be read";
            if (stepsOf.Count == 0) return "no habitability warm-up step came during the load";
            foreach (KeyValuePair<int, int> b in stepsOf)
            {
                int deposits;
                depositsOf.TryGetValue(b.Key, out deposits);
                if (b.Value < rounds)
                    return "a body's habitability model was stepped " + b.Value + " times during the load, " +
                           rounds + " expected";
                if (deposits < rounds)
                    return "a body's deposits were stepped " + deposits + " times during the load, " + rounds +
                           " expected";
            }
            return null;
        }
    }
}

using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // One of a site's module stacks, as a flight's cargo row sees it.
    public struct ModuleStack
    {
        public bool Same;        // the row's module
        public long Quantity;    // finished units
        public bool Building;    // still under construction (nothing finished yet) and in the site's production
    }

    // The stack a module cargo row is taken from. The saved record does not keep it, so every game picks the same
    // way. The game's planner offers a module still under construction as cargo (Start.GetAvailableModulesForCargo
    // lists a stack with no finished unit; the plan only refuses one whose construction was cancelled) and its
    // CreateFly takes it then, so such a stack is a source too, after every finished one.
    public static class ModuleSource
    {
        // The index of the stack the next row comes out of, or -1; taken[i] counts the rows stack i already gives.
        public static int Pick(IList<ModuleStack> stacks, long[] taken)
        {
            for (int i = 0; i < stacks.Count; i++)
                if (stacks[i].Same && stacks[i].Quantity - taken[i] > 0) return i;
            for (int i = 0; i < stacks.Count; i++)
                if (stacks[i].Same && stacks[i].Quantity == 0 && stacks[i].Building && taken[i] == 0) return i;
            return -1;
        }

        // Pick, and count the row against the stack it came out of.
        public static int Take(IList<ModuleStack> stacks, long[] taken)
        {
            int i = Pick(stacks, taken);
            if (i >= 0) taken[i]++;
            return i;
        }
    }
}

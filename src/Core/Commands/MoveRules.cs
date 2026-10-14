using System;

namespace SolarExpanseMultiplayer
{
    // One body as B11's drag rules see it (ObjectInfo.OnDragAndDrop, D 124316-124339).
    public sealed class DragBody
    {
        public int Id;
        public int Parent;           // the parent body's id; 0 when it has none
        public bool Orbit;           // EObjectTypes.Orbit
        public bool PlanetOrMoon;    // EObjectTypes.Planet or Moons
        public bool Linked;          // asteroidFromPush or interstellarConstructionFacility
    }

    // How a drop moves things between two bodies, rewritten from ObjectInfo.OnDragAndDrop so it can be judged for the
    // acting agency (the handler reads the local player throughout).
    public static class DragPair
    {
        public const int None = 0;
        public const int Up = 1;        // onto a child of the source (a body to its orbit): needs a lift facility
        public const int Down = 2;      // onto the source's parent (an orbit to its body)
        public const int Sibling = 3;   // between two non-planet, non-moon bodies with one parent
    }

    // B11's decisions apart from the game types. The game side (BuildingChecks) gives the acting agency's facts.
    public static class MoveRules
    {
        // The handler's three flags; the last that holds wins, as its stock cap does (D 124351-124362).
        public static int Pair(DragBody from, DragBody to)
        {
            if (from == null || to == null || from.Id == to.Id) return DragPair.None;
            bool either = from.Orbit || to.Orbit || from.Linked || to.Linked;
            bool up = to.Parent == from.Id && from.Id != 0 && either;
            bool down = from.Parent == to.Id && to.Id != 0 && either;
            if (from.Orbit && to.Orbit) { up = false; down = false; }
            bool sibling = to.Parent != 0 && from.Parent != 0 && to.Parent == from.Parent && !to.PlanetOrMoon
                && !from.PlanetOrMoon;
            if (sibling) return DragPair.Sibling;
            if (down) return DragPair.Down;
            if (up) return DragPair.Up;
            return DragPair.None;
        }

        // The handler's fixed cap for a drop that needs no lift.
        public const double FreeCap = 100000.0;

        // Stock: the input field is clamped to [min(1, max), max(1, max)] with max = min(stock, cap), and the handler
        // takes what is there when the removal falls short. The amount applied comes back in `amount`.
        public static int StockAmount(double requested, double stock, double cap, out double amount)
        {
            amount = 0;
            if (double.IsNaN(requested) || double.IsNaN(stock) || double.IsNaN(cap)) return RefuseCodes.BadTarget;
            double max = Math.Min(stock, cap);
            if (requested < Math.Min(1.0, max) || requested > Math.Max(1.0, max)) return RefuseCodes.TooManyUnits;
            double take = Math.Min(requested, stock);
            if (!(take > 0)) return RefuseCodes.CannotAfford;
            amount = take;
            return 0;
        }

        // Whether a module drop takes the count popup's path (more than one unit free) or moves one. The host decides
        // it, because the free count subtracts the units the local mission planner has picked as cargo (UI state that
        // is the host's own), and the request carries it (Value) to every game.
        public static bool PopupPath(long available) { return available > 1; }

        // Modules: the units free to move are quantity - minimum - units promised to planned cargo
        // (GetAvailableCountOffSpaceModule). With more than one free the field runs 1..free; with one the drop moves
        // exactly one. None free is refused: the window would move one anyway, but that one is promised to a flight.
        // A type that never stacks makes one new id per unit, so a drop is held to MaxMoveNewIds of them.
        // blockStacking: the module's type or the one it installs as never stacks.
        public static int ModuleCount(int requested, long available, long quantity, bool blockStacking, out int count)
        {
            count = 0;
            if (quantity < 1) return RefuseCodes.BadTarget;
            if (available < 1) return RefuseCodes.InUse;
            long max = available > 1 ? available : 1;
            if (requested < 1 || requested > max) return RefuseCodes.TooManyUnits;
            long n = Math.Min(requested, quantity);
            if (blockStacking && n > ActionLimits.MaxMoveNewIds) return RefuseCodes.TooManyUnits;
            count = (int)n;
            return 0;
        }
    }
}

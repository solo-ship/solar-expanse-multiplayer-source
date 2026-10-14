namespace SolarExpanseMultiplayer
{
    // Who may press the host's buttons of the HUD and the PLAYERS screen, asked twice: when the screen decides whether
    // to show the button (PlayersModel, HudModel) and again when it is pressed, on the session's view and capabilities
    // of that moment, since a view can change between the two (the player left, the session handed over, a
    // capability went off).
    public static class ActionRules
    {
        // Remove a player: the host, Kick on, a player who is there and is not the host itself.
        public static bool MayKick(SessionView v, Capability caps, int player)
        {
            if (v == null || !v.IsHost || !CapabilityRules.Has(caps, Capability.Kick)) return false;
            PlayerRow r = v.Row(player);
            return r != null && !r.Me;
        }

        // Ban a player: as Remove, with Ban on too.
        public static bool MayBan(SessionView v, Capability caps, int player)
        {
            return MayKick(v, caps, player) && CapabilityRules.Has(caps, Capability.Ban);
        }

        // Carry on without a player: the host, CarryOn on, a player who is there, not the host, and dropped (its row
        // says so, or its drop pause is still on).
        public static bool MayCarryOn(SessionView v, Capability caps, int player)
        {
            if (v == null || !v.IsHost || !CapabilityRules.Has(caps, Capability.CarryOn)) return false;
            PlayerRow r = v.Row(player);
            if (r == null || r.Me) return false;
            if (r.State == PlayerRow.Dropped) return true;
            foreach (PauseLine p in v.Pauses)
                if (p != null && p.Player == player && p.Why == PauseWhy.Dropped) return true;
            return false;
        }

        // End the session: the host, EndSession on.
        public static bool MayEndSession(SessionView v, Capability caps)
        {
            return v != null && v.IsHost && CapabilityRules.Has(caps, Capability.EndSession);
        }
    }
}

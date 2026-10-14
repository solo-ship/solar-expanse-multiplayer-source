namespace SolarExpanseMultiplayer
{
    // The requests the flight window captures send for the small clicks (plan DD-F1: 61, 63, 64, 66, 67), built from
    // plain values so the shapes are tests. The captures read the game's windows and call these; ClickGate sets the
    // agency and runs the request's own Validate.
    public static class FlightClickRequests
    {
        // FlightCancel's Value: the abort popup's branch (D 140002-140075).
        public const int AbortShip = 1, AbortLaunchVehicle = 2, AbortConstruct = 3, AbortCyclical = 4, AbortShipInfo = 5;

        // firstShip: the mission's first ship, or -1 for a facility craft or a construct.
        public static ActionRequest Cancel(long mission, int firstShip, int branch)
        {
            return new ActionRequest { Kind = ActionKind.FlightCancel, ItemId = mission, Ship = firstShip,
                Value = branch };
        }

        public static ActionRequest Pause(int ship, bool pause)
        {
            return new ActionRequest { Kind = ActionKind.CyclicalPause, Ship = ship, Value = pause ? 1 : 0 };
        }

        public static ActionRequest Delete(int ship)
        {
            return new ActionRequest { Kind = ActionKind.CyclicalDelete, Ship = ship };
        }

        public static ActionRequest ScrapShip(int body, int ship)
        {
            return new ActionRequest { Kind = ActionKind.CraftScrap, Body = body, Ship = ship };
        }

        public static ActionRequest ScrapRocket(int body, long rocketId)
        {
            return new ActionRequest { Kind = ActionKind.CraftScrap, Body = body, ItemId = rocketId };
        }

        // The preset's name: the planner's mission name cleaned, and vanilla's default ("Preset N", D 149373) whenever
        // that leaves nothing, so an honest save is never refused (DD-F11). `held` is how many presets the agency has.
        public static string PresetName(string missionName, int held)
        {
            string name = missionName == null || missionName == "empty" ? "" : MissionNames.Clean(missionName);
            return name.Length > 0 ? name : "Preset " + (held + 1);
        }
    }
}

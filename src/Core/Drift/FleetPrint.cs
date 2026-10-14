namespace SolarExpanseMultiplayer
{
    // One ship as the state reader finds it. Phase is Spacecraft.EPhase as a number. LandsOn: the body vanilla's
    // landing step would put it on (the reader works it out); CarrierPhase: the phase of the ship carrying it, -1 when
    // none; GoneOnLanding: its landing step removes it; TrajEnds: its trajectory is destroyed at the end of the frame
    // (the landing frame completed its mission). Ticks are long.MinValue for no date.
    public struct ShipRow
    {
        public string Agency, Type, Name;
        public long Id, Phase, OnObject, LandsOn, CarrierPhase, TrajStart, TrajEnd;
        public long TrajStartDate, TrajEndDate, LaunchTicks, LandingTicks, CargoCount, Crew;
        public double CargoMass;
        public bool GoneOnLanding, TrajEnds;
    }

    // The fleet's ship rows. Vanilla moves a ship from Fly to Landing on the first frame past its arrival and from
    // Landing to None, on its destination and with its riders, one frame later. A step cut at the day point can stop
    // one game on that frame while the other has passed it, so a ship in Landing (and a rider whose carrier is) prints
    // as it will be a frame later, without the trajectory its landing frame destroys. Landing times are dates, outside
    // the discrete check.
    public static class FleetPrint
    {
        public const long None = 0, Fly = 3, Landing = 4;

        public static bool Settling(long phase, long carrierPhase)
        {
            return phase == Landing || carrierPhase == Landing;
        }

        public static void Write(IItemSink s, ref ShipRow r)
        {
            bool settling = Settling(r.Phase, r.CarrierPhase);
            if (settling && r.Phase == Landing && r.GoneOnLanding) return;
            s.BeginItem(); s.Key(r.Agency); s.Key(r.Id);
            s.Val(DriftFields.Type, r.Type); s.Val(DriftFields.Name, r.Name);
            s.Val(DriftFields.Phase, r.Phase == Landing ? None : r.Phase);
            s.Val(DriftFields.OnObject, settling ? r.LandsOn : r.OnObject);
            bool trajGone = settling && r.TrajEnds;
            s.Val(DriftFields.TrajStart, trajGone ? -1L : r.TrajStart);
            s.Val(DriftFields.TrajEnd, trajGone ? -1L : r.TrajEnd);
            s.Date(DriftFields.TrajStartDate, trajGone ? Quantise.NoDate : r.TrajStartDate);
            s.Date(DriftFields.TrajEndDate, trajGone ? Quantise.NoDate : r.TrajEndDate);
            s.Date(DriftFields.LaunchTime, r.LaunchTicks); s.Date(DriftFields.LandingTime, r.LandingTicks);
            s.Val(DriftFields.CargoCount, r.CargoCount); s.Val(DriftFields.CargoMass, r.CargoMass);
            s.Val(DriftFields.Crew, r.Crew);
            s.EndItem();
        }
    }
}

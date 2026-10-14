namespace SolarExpanseMultiplayer
{
    // One production-list item as the state reader finds it. IsFacility / InFacilityList: whether it is a facility,
    // and whether that facility is in its body's facility list.
    public struct ProductionRow
    {
        public long Body, Id, CountToBuild, CountBuild;
        public string Type;
        public double Progress;
        public bool StartBuild, IsFacility, InFacilityList;
    }

    // The production list's "pi" rows. A joiner's world is the host's save, and a load rebuilds the production list
    // from the facility list (and the ship hulls), while a facility that stacked onto one already there leaves the
    // facility list but not the production list. So the print holds only the items a save keeps.
    public static class ProductionPrint
    {
        public static bool SaveKeeps(bool isFacility, bool inFacilityList) { return !isFacility || inFacilityList; }

        public static void Write(IItemSink s, ref ProductionRow r)
        {
            if (!SaveKeeps(r.IsFacility, r.InFacilityList)) return;
            s.BeginItem(); s.Key(r.Body); s.Key("pi"); s.Key(r.Id);
            s.Val(DriftFields.Type, r.Type); s.Val(DriftFields.BuildProgress, r.Progress);
            s.Val(DriftFields.CountToBuild, r.CountToBuild); s.Val(DriftFields.CountBuild, r.CountBuild);
            s.Val(DriftFields.StartBuild, r.StartBuild);
            s.EndItem();
        }
    }
}

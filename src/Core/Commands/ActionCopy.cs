namespace SolarExpanseMultiplayer
{
    // A request copied field by field, lists included: the drivers keep the request they are given, so a caller that
    // sends the same script line twice hands each send its own object.
    public static class ActionCopy
    {
        public static ActionRequest Of(ActionRequest a)
        {
            if (a == null) return null;
            var c = new ActionRequest
            {
                Req = a.Req, Kind = a.Kind, Agency = a.Agency, Body = a.Body, ToBody = a.ToBody, Item = a.Item,
                Count = a.Count, ItemId = a.ItemId, Position = a.Position, ToPosition = a.ToPosition, Value = a.Value,
                Amount = a.Amount, Group = a.Group, Ship = a.Ship,
                Ids = a.Ids == null ? null : (string[])a.Ids.Clone(),
                Mission = a.Mission == null ? null : a.Mission.Copy(),
                Route = a.Route == null ? null : a.Route.Copy(),
                Preset = a.Preset == null ? null : a.Preset.Copy(),
                Market = MarketOp.CopyList(a.Market), Growth = GrowthRow.CopyList(a.Growth),
                Contract = a.Contract == null ? null : a.Contract.Copy()
            };
            if (a.Targets != null)
            {
                c.Targets = new TargetRow[a.Targets.Length];
                for (int i = 0; i < a.Targets.Length; i++)
                    if (a.Targets[i] != null)
                        c.Targets[i] = new TargetRow { Body = a.Targets[i].Body, Ratio = a.Targets[i].Ratio,
                            Count = a.Targets[i].Count };
            }
            return c;
        }
    }
}

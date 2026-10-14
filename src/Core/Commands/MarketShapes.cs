namespace SolarExpanseMultiplayer
{
    // The shapes of the five market request kinds (80-84, DD-W4): which ops each carries and whose agency they name.
    // The op fields themselves are MarketOp.Validate(request: true)'s. A host-made entry's Ops are held to the same
    // sequences by ActionResult.Validate.
    public static class MarketShapes
    {
        public const int SourceMarketWindow = 0, SourceMakeOfferWindow = 1;   // MarketInstant's ItemId (DD-W4)

        public static string Check(ActionRequest a)
        {
            MarketOp[] m = a.Market;
            string name = ActionKind.Name(a.Kind);
            switch (a.Kind)
            {
                case ActionKind.OfferCreate:
                    if (m.Length != 1 || m[0].Op != MarketOps.Add) return name + " carries one Add";
                    return m[0].Offer.Agency != a.Agency ? name + " makes an offer for the acting agency" : null;
                case ActionKind.OfferCancel:
                    return m.Length != 1 || m[0].Op != MarketOps.Cancel ? name + " carries one Cancel" : null;
                case ActionKind.OfferEdit:
                    if (m.Length != 2 || m[0].Op != MarketOps.Cancel || m[1].Op != MarketOps.Add)
                        return name + " carries a Cancel and an Add";
                    // vanilla's edit window locks neither the resource nor the side: only the agency stays
                    return m[0].Offer.Agency != a.Agency || m[1].Offer.Agency != a.Agency
                        ? name + " edits an offer of the acting agency into one of the same agency" : null;
                case ActionKind.OfferTake:
                    if (m.Length != 1 || m[0].Op != MarketOps.Fill) return name + " carries one Fill";
                    return m[0].Taker != a.Agency ? name + " takes for the acting agency" : null;
                default:   // MarketInstant
                    if (a.ItemId != SourceMarketWindow && a.ItemId != SourceMakeOfferWindow) return name + " names a source (0 or 1)";
                    if (a.Value != 0 && a.Value != 1) return "Value is not 0 or 1";
                    if (a.Amount <= 0) return name + " needs an Amount above 0";
                    if (m == null) return a.ItemId == SourceMakeOfferWindow ? name + " from the offer window carries its limit price" : null;
                    return m.Length != 1 || m[0].Op != MarketOps.Instant ? name + " carries one Instant" : null;
            }
        }
    }
}

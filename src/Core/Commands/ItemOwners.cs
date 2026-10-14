using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // Who owns an item a request names by its id. Item ids are each agency's own counter (productionCountID), so two
    // agencies at one body often hold the same id: the acting agency's own item is the one it means. Only when it holds
    // no such item is the owner another agency, which AgencyAccess then refuses.
    public static class ItemOwners
    {
        // holders: the wire ids of the agencies whose data at the body hold an item with that id, in the body's order.
        public static string Of(string acting, IList<string> holders)
        {
            if (holders == null || holders.Count == 0) return null;
            foreach (string h in holders) if (h != null && h == acting) return acting;
            return holders[0];
        }
    }
}

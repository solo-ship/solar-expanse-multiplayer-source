using Company = Game.Company;
using EObjectTypes = Data.EObjectTypes;
using Facility = Game.ObjectInfoDataScripts.Facility;
using ObjectInfo = Game.Info.ObjectInfo;
using ObjectInfoData = Game.ObjectInfoDataScripts.ObjectInfoData;

namespace SolarExpanseMultiplayer
{
    // The host's checks of kind 85, the nuke (M7 DD-W8): the "destroy asteroid" button's conditions for the ACTING
    // agency (a NuclearBomb facility of its own with that id on an asteroid; vanilla asks for no build progress,
    // quantity or switch), and the session's one added rule, no flight due before the next day (NukeRules, over M6's
    // cancel set). Main thread.
    static class WorldNuke
    {
        // 0, or the refusal.
        public static int Check(ActionRequest a, Company actor)
        {
            ObjectInfo o = GameAgencies.Body(a.Body);
            bool gone = o == null || o.IsInGameDestroy;
            bool asteroid = !gone && o.objectTypes == EObjectTypes.Asteroid;
            bool bomb = asteroid && Bomb(o, actor, a.Item, a.ItemId) != null;
            bool due = bomb && NukeRules.FlightsDue(FlightCancels.Rows(), o.id, Now(),
                Session.Commands.World.NextDayTicks);
            return NukeRules.Decide(gone, asteroid, bomb, due);
        }

        // The agency's NuclearBomb with this descriptor and item id on the body, or null.
        public static Facility Bomb(ObjectInfo o, Company actor, string descriptor, long itemId)
        {
            ObjectInfoData site = GameAgencies.Site(o, actor);
            if (site == null || site.ListFacility == null) return null;
            foreach (Facility f in site.ListFacility)
                if (f is NuclearBomb && f.ID == itemId && f.facilityDescriptor != null
                    && f.facilityDescriptor.ID == descriptor) return f;
            return null;
        }

        public static long Now() { return Manager.TimeController.Instance.CurrentTime.Ticks; }
    }
}

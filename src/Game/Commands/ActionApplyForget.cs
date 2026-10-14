using System;
using System.Collections.Generic;
using Company = Game.Company;
using Facility = Game.ObjectInfoDataScripts.Facility;
using ObjectInfo = Game.Info.ObjectInfo;
using ObjectInfoData = Game.ObjectInfoDataScripts.ObjectInfoData;
using UIManager = Game.UI.UIManager;

namespace SolarExpanseMultiplayer
{
    // ForgetObject (DD15): the trash popup's own world calls (D 163699-163716) on every game, under the spawn record
    // (the host: the replacement asteroids the removal makes become the entry's spawns) or its replay (a client: the
    // game makes exactly the host's). Only the spawn record is open around VirtualDestroy: a removal entry carries
    // spawns and no dice. The observatory re-plans the removal holds become the host's own entries right after it.
    public static partial class ActionApply
    {
        static ApplyOutcome ApplyForget(ActionRequest a, ApplyNumbers given)
        {
            bool host = given == null;
            ObjectInfo o = GameAgencies.Body(a.Body);
            if (host)
            {
                int code = ForgetCommand.Decide(a.Agency, ForgetWorld.Facts(o, a.Agency));
                if (code != 0) return Refused(code);
            }
            else if (o == null || o.IsInGameDestroy) return OutOfStep(a, "the object is not here to forget");
            AsteroidSpawnState spawns = HumanAgencies.Spawns;
            if (spawns == null)
            {
                if (host) return Refused(RefuseCodes.NotAllowedHere);
                return OutOfStep(a, "no asteroid rules are running");
            }
            List<PrintEntry> touched = MakerTouched(a.Agency, o);
            bool opened = host ? spawns.BeginRecord() : spawns.BeginReplay(given.Spawns);
            if (!opened)
            {
                if (host) throw new InvalidOperationException("an asteroid spawn record is open already");
                return OutOfStep(a, "an asteroid spawn replay is open already");
            }
            AsteroidSpawn[] made = null;
            string replay = null;
            try { Forget(o); }
            finally
            {
                if (host) made = spawns.EndRecord();
                else replay = spawns.EndReplay();
            }
            CloseWindowsOn(o);
            if (!host) return replay != null ? OutOfStep(a, replay) : Done(a, null, touched);
            // raised inside this apply, the re-plans go out right after this entry, in the game's order, each run now
            // under its own dice record (the host's own observatories never see a destroyed target)
            ObservatoryRule.RaiseReplans(ObservatoryRule.TakeNotes());
            return Done(a, new ApplyNumbers { Spawns = made }, touched);
        }

        // A station site's stock goes to its builder's data on the parent's low orbit first, as the popup does.
        static void Forget(ObjectInfo o)
        {
            if (o.SpaceStationConstructionFacility)
            {
                Company builder = ForgetWorld.Builder(o);
                ObjectInfoData from = builder != null ? ForgetWorld.StockFrom(o, builder) : null;
                ObjectInfoData to = builder != null ? ForgetWorld.StockTo(o, builder) : null;
                if (from == null || to == null)
                    throw new InvalidOperationException("the station site's stock has nowhere to go");
                from.TransferResourcesTo(to);
            }
            o.VirtualDestroy(null);
        }

        // A construction site's removal scraps the facility that made it (VirtualDestroy, D 124783-124790).
        static List<PrintEntry> MakerTouched(string agency, ObjectInfo o)
        {
            var list = new List<PrintEntry>();
            if (!ForgetWorld.IsConstructionSite(o)) return list;
            Facility maker = o.CreateByFacility;
            ObjectInfoData d = maker != null ? maker.ObjectInfoData : null;
            if (d == null || d.ObjectInfo == null) return list;
            list.Add(Print(ObjectKind.SiteFacilities, agency, d.ObjectInfo.id));
            list.Add(Print(ObjectKind.SiteBuilds, agency, d.ObjectInfo.id));
            return list;
        }

        // The windows that showed the object close, as the popup closes them on the game that clicked; here on every
        // game, since a forgotten object is gone everywhere. A window that fails to close must not undo the removal.
        static void CloseWindowsOn(ObjectInfo o)
        {
            try
            {
                UIManager ui = SerializedMonoBehaviourSingleton<UIManager>.Instance;
                if (ui == null) return;
                var main = ui.GetWindow<Game.UI.Windows.Windows.ObjectInfoWindow>();
                if (main != null && main.Open && main.ObjectInfoCurrent == o) main.HideImmediately();
                var second = ui.GetSecondWindow<Game.UI.Windows.Windows.ObjectInfoWindow>();
                if (second != null && second.Open && second.ObjectInfoCurrent == o) second.HideImmediately();
            }
            catch (Exception e) { Log.Warn("command: a window on the forgotten object did not close: "
                + e.GetType().Name); }
        }
    }
}

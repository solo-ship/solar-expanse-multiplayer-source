using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Company = Game.Company;
using EnergyTransferWindow = Game.UI.Windows.Windows.EnergyTransferWindow;
using Facility = Game.ObjectInfoDataScripts.Facility;
using IEnergyTransfer = Game.ObjectInfoDataScripts.CustomFacilitiesAndModules.IEnergyTransferFacilityOrModule;
using MirrorFacility = Game.ObjectInfoDataScripts.CustomFacilitiesAndModules.SpaceMirrorOrShadeFacility;
using MirrorTargetingWindow = Game.UI.Windows.Windows.MirrorTargetingWindow;
using ObjectInfo = Game.Info.ObjectInfo;
using TransferTargetInfo =
    Game.ObjectInfoDataScripts.CustomFacilitiesAndModules.EnergyTransferFacility.TransferTargetInfo;

namespace SolarExpanseMultiplayer
{
    // B9's two list windows: every edit becomes the facility's WHOLE list after the edit (a set action, so only the
    // latest of a burst reaches the host), worked out on a copy (TargetEdits) from the list the player last sent while
    // the host has not answered it, else from the facility's own. The window's rows do not change until the host's
    // entry arrives and redraws them. Energy rows carry the game's fraction, a row with no target yet is Body 0.
    public static partial class CaptureBuildings
    {
        static IEnumerable<MethodBase> ListTargets()
        {
            yield return HookTargets.Method(typeof(EnergyTransferWindow), "OnAddRowButtonClick");
            yield return HookTargets.Method(typeof(EnergyTransferWindow), "OnTargetSelected");
            yield return HookTargets.Method(typeof(EnergyTransferWindow), "RatioInputFieldEndEdit");
            yield return HookTargets.Method(typeof(EnergyTransferWindow), "OnRowTrashClick");
            yield return HookTargets.Method(typeof(MirrorTargetingWindow), "OnAddRowButtonClick");
            yield return HookTargets.Method(typeof(MirrorTargetingWindow), "OnTargetSelected");
            yield return HookTargets.Method(typeof(MirrorTargetingWindow), "MirrorsInputFieldEndEdit");
            yield return HookTargets.Method(typeof(MirrorTargetingWindow), "OnRowTrashClick");
            yield return HookTargets.Method(typeof(MirrorTargetingWindow), "OnCloseButtonClick");
        }

        // What an edit works on: the facility, its request (no list yet), the list as the game holds it and the list
        // to edit (the pending one when there is one).
        sealed class Edit
        {
            public Facility Facility;
            public ActionRequest Draft;
            public TargetRow[] Local, Base;
            public int Row;          // the edited row in the facility's own list, -1 for none
        }

        static Edit Begin(ICaptureSeam seam, int kind, Facility f, TargetRow[] local, int row)
        {
            if (f == null || f.facilityDescriptor == null || f.ObjectInfoData == null
                || f.ObjectInfoData.ObjectInfo == null) return null;
            var e = new Edit { Facility = f, Local = local, Row = row };
            e.Draft = new ActionRequest { Kind = kind, Agency = WindowCapture.Local(),
                Body = f.ObjectInfoData.ObjectInfo.id, Item = f.facilityDescriptor.ID, ItemId = f.ID };
            e.Base = seam.Memory.BaseTargets(seam, ActionShapes.Key(e.Draft), local);
            // the row clicked, named by what it is, in the list the edit is made on (the window may not have
            // redrawn after an earlier edit)
            if (row >= 0) e.Row = TargetEdits.Locate(e.Base, local, row);
            return e;
        }

        static bool Finish(ICaptureSeam seam, Edit e, TargetRow[] edited)
        {
            if (edited == null) { WindowCapture.CannotSend(seam); return false; }
            e.Draft.Targets = edited;
            if (WindowCapture.Send(seam, e.Draft, e.Facility.Company))
                seam.Memory.SentTargets(ActionShapes.Key(e.Draft), edited);
            return false;
        }

        static int BodyOf(ObjectInfo o) { return o != null ? o.id : 0; }

        // ---- energy transfer ----

        internal static TargetRow[] EnergyRows(IEnergyTransfer f)
        {
            var rows = new List<TargetRow>();
            foreach (TransferTargetInfo t in f.Targets)
                rows.Add(new TargetRow { Body = t.target != null ? BodyOf(t.target.Object) : 0, Ratio = t.ratio });
            return rows.ToArray();
        }

        static Edit EnergyEdit(ICaptureSeam seam, object window, object row)
        {
            var f = HookTargets.Field<Facility>(window, "facility");
            var list = HookTargets.Field<IEnergyTransfer>(window, "energyTransferFacilityOrModule");
            if (f == null || list == null) return null;
            int at = -1;
            if (row != null) at = list.Targets.IndexOf(HookTargets.Property<TransferTargetInfo>(row, "TargetInfo"));
            return Begin(seam, ActionKind.EnergyTargets, f, EnergyRows(list), at);
        }

        static bool EnergyAdd(ICaptureSeam seam, object self, object[] args)
        {
            Edit e = EnergyEdit(seam, self, null);
            if (e == null) { WindowCapture.CannotSend(seam); return false; }
            return Finish(seam, e, TargetEdits.AddEnergyRow(e.Base));
        }

        static bool EnergyTarget(ICaptureSeam seam, object self, object[] args)
        {
            var body = args[1] as ObjectInfo;
            if (body == null) return true;   // the game logs it and does nothing
            Edit e = EnergyEdit(seam, self, args[0]);
            if (e == null) { WindowCapture.CannotSend(seam); return false; }
            return Finish(seam, e, TargetEdits.SetEnergyTarget(e.Base, e.Row, body.id));
        }

        static bool EnergyShare(ICaptureSeam seam, object self, object[] args)
        {
            Edit e = EnergyEdit(seam, self, args[0]);
            if (e == null) { WindowCapture.CannotSend(seam); return false; }
            return Finish(seam, e, TargetEdits.SetEnergyShare(e.Base, e.Row, (string)args[1]));
        }

        static bool EnergyRemove(ICaptureSeam seam, object self, object[] args)
        {
            Edit e = EnergyEdit(seam, self, args[0]);
            if (e == null) { WindowCapture.CannotSend(seam); return false; }
            return Finish(seam, e, TargetEdits.RemoveRow(e.Base, e.Row));
        }

        // ---- mirror targeting ----

        // The close button re-allocates the mirrors on this game alone (D 136186); the host's entry does it on every
        // game from the whole list. So in a session it only closes the window.
        static bool MirrorClose(ICaptureSeam seam, object self, object[] args)
        {
            MethodInfo hide = AccessTools.Method(self.GetType(), "Hide", new[] { typeof(bool) });
            if (hide == null) { WindowCapture.CannotSend(seam); return false; }
            hide.Invoke(self, new object[] { true });
            return false;
        }

        internal static TargetRow[] MirrorRows(MirrorFacility f)
        {
            var rows = new List<TargetRow>();
            foreach (MirrorFacility.MirrorTargetInfo t in f.Targets)
                rows.Add(new TargetRow { Body = t.target != null ? BodyOf(t.target.Object) : 0,
                    Count = (int)System.Math.Max(0L, System.Math.Min(t.mirrorsCount, (long)ActionLimits.MaxUnits)) });
            return rows.ToArray();
        }

        static Edit MirrorEdit(ICaptureSeam seam, object window, object row)
        {
            var f = HookTargets.Field<MirrorFacility>(window, "facility");
            if (f == null) return null;
            int at = -1;
            if (row != null)
                at = f.Targets.IndexOf(HookTargets.Property<MirrorFacility.MirrorTargetInfo>(row, "TargetInfo"));
            return Begin(seam, ActionKind.MirrorTargets, f, MirrorRows(f), at);
        }

        static bool MirrorAdd(ICaptureSeam seam, object self, object[] args)
        {
            Edit e = MirrorEdit(seam, self, null);
            if (e == null) { WindowCapture.CannotSend(seam); return false; }
            return Finish(seam, e, TargetEdits.AddMirrorRow(e.Base, e.Facility.Quantity));
        }

        static bool MirrorTarget(ICaptureSeam seam, object self, object[] args)
        {
            var body = args[1] as ObjectInfo;
            if (body == null) return true;   // the game logs it and does nothing
            Edit e = MirrorEdit(seam, self, args[0]);
            if (e == null) { WindowCapture.CannotSend(seam); return false; }
            return Finish(seam, e, TargetEdits.SetMirrorTarget(e.Base, e.Row, body.id));
        }

        static bool MirrorCount(ICaptureSeam seam, object self, object[] args)
        {
            Edit e = MirrorEdit(seam, self, args[0]);
            if (e == null) { WindowCapture.CannotSend(seam); return false; }
            return Finish(seam, e, TargetEdits.SetMirrorCount(e.Base, e.Row, (string)args[1], e.Facility.Quantity));
        }

        static bool MirrorRemove(ICaptureSeam seam, object self, object[] args)
        {
            Edit e = MirrorEdit(seam, self, args[0]);
            if (e == null) { WindowCapture.CannotSend(seam); return false; }
            return Finish(seam, e, TargetEdits.RemoveRow(e.Base, e.Row));
        }
    }
}

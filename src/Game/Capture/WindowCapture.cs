using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Company = Game.Company;
using GameManager = Manager.GameManager;
using ObjectInfo = Game.Info.ObjectInfo;
using ObjectInfoData = Game.ObjectInfoDataScripts.ObjectInfoData;

namespace SolarExpanseMultiplayer
{
    // What every window capture shares (plan "Game seams", DD1). A capture prefix asks Seam() first: null outside a
    // session and while an entry is being applied, and then the game's own code runs; otherwise the click becomes a
    // request that Send hands to the session, and the prefix returns false so the window changes nothing itself.
    // No state: the seam, and the lists it remembers, belong to the session.
    static class WindowCapture
    {
        public static ICaptureSeam Seam()
        {
            var owner = (object)Session.Commands as ICaptureOwner;
            ICaptureSeam seam = owner != null ? owner.Capture : null;
            return seam != null && seam.Active && !seam.Applying ? seam : null;
        }

        // The wire id of the agency this game plays, or null when there is no player yet.
        public static string Local()
        {
            GameManager gm = MonoBehaviourSingleton<GameManager>.Instance;
            return gm != null ? GameAgencies.Wire(gm.Player) : null;
        }

        public static Company Player()
        {
            GameManager gm = MonoBehaviourSingleton<GameManager>.Instance;
            return gm != null ? gm.Player : null;
        }

        // The click leaves as `a`, unless it is on another agency's object (`owner`; null = no owned object).
        public static bool Send(ICaptureSeam seam, ActionRequest a, Company owner)
        {
            string local = Local();
            if (local == null) return ClickGate.Refuse(seam, RefuseCodes.NotInGame, a, "no player agency yet");
            return ClickGate.Send(seam, a, owner != null ? GameAgencies.Wire(owner) : null, local);
        }

        public static bool SendAll(ICaptureSeam seam, IList<ActionRequest> list, Company owner)
        {
            string local = Local();
            if (local == null) return ClickGate.Refuse(seam, RefuseCodes.NotInGame, "no player agency yet");
            return ClickGate.SendAll(seam, list, owner != null ? GameAgencies.Wire(owner) : null, local);
        }

        // The click could not be turned into a request (a window in a state the capture does not know): the player
        // is told, and nothing changes. The log line names the capture that gave up (the compiler fills `where`).
        public static bool CannotSend(ICaptureSeam seam, [CallerMemberName] string where = null)
        {
            return ClickGate.Refuse(seam, RefuseCodes.CannotSend, "the window's state, at " + where);
        }

        public static int Id(ObjectInfo body) { return body != null ? body.id : 0; }

        // The company that owns a site's data, or null.
        public static Company OwnerOf(ObjectInfoData site) { return site != null ? site.company : null; }

        // A greyed control was used: the note, a log line, and the action does not run.
        public static void Greyed(ICaptureSeam seam, string path)
        {
            ClickGate.Greyed(seam, path);
        }
    }
}

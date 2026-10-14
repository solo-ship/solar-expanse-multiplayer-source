using System;
using UIManager = Game.UI.UIManager;

namespace SolarExpanseMultiplayer
{
    // The player's refused request in the game's own OK dialog (UIManager.ShowPopUP, the overload that does not pause
    // and that no capture patches), with CommandText's line. It goes up on the next frame, not inside the click or the
    // stream message that refused it: a refusal can come from inside another dialog's OK delegate or a capture
    // prefix. The dialog sits on top of the open window, so a refused flight's planner stays open under it. One per
    // session (CommandSession), main thread only.
    public sealed class RefusalPopup
    {
        readonly RefusalPopupGate gate = new RefusalPopupGate();
        string waiting;

        public void Post(string text)
        {
            if (gate.Take(text, NetClock.NowMs)) waiting = text;
        }

        public void Update()
        {
            if (waiting == null) return;
            string text = waiting;
            waiting = null;
            try
            {
                UIManager ui = SerializedMonoBehaviourSingleton<UIManager>.Instance;
                if (ui != null) ui.ShowPopUP(text, null, true);
            }
            catch (Exception e) { Log.Warn("command: the refusal dialog did not open: " + e.GetType().Name); }
        }
    }
}

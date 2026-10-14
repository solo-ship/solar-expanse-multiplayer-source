using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Toggle = UnityEngine.UI.Toggle;

namespace SolarExpanseMultiplayer
{
    // The dev trigger "nopause" (test seams only: DevTriggers.Take returns null otherwise; one instance, owned by
    // MenuSessions): runs one of the game's own pausing sources as the game would, so the log shows what the session
    // does with its pause (GamePauses drops it; the bankruptcy warning's still pauses).
    public sealed class PauseSourcesDev
    {
        static readonly FieldInfo PauseToggle = AccessTools.Field(typeof(Manager.NotificationManager), "pauseToggle");
        float nextDevPoll;

        // Every frame (MenuSessions.Update), outside any of the game's calls.
        public void Update()
        {
            if (DevTriggers.Every(ref nextDevPoll, Time.realtimeSinceStartup)) DevPoll();
        }

        // "nopause <case>": bankruptcy; notify (a notification for this player); lifesupport (the real daily
        // life-support step on the first of this player's outposts with more crew than habitats: it uses up that
        // outpost's supplies, on this game only); trivia (the start pop-up); report-open, report-close (the bug
        // reporter); tickon (outside a session only: the notification tick box on, as the player's click would).
        // WARNING: lifesupport and notify change this game's world (supplies used up; a notification kept in the
        // save's history), on this game only. Use them on a scratch save, solo or before a session: in a session the
        // other games never see the change, so a later drift or join comparison would differ.
        void DevPoll()
        {
            string what = DevTriggers.Take("nopause");
            if (what == null) return;
            try
            {
                string why = DevCase(what);
                if (why == null) Log.Info("dev nopause " + what + ": done (clock speed " + ClockSync.Scale + ")");
                else Log.Warn("dev nopause " + what + ": refused: " + why);
            }
            catch (Exception e) { Log.Warn("dev nopause " + what + " failed: " + (e.InnerException ?? e).Message); }
        }

        static string DevCase(string what)
        {
            if (MonoBehaviourSingleton<Manager.GameManager>.InstanceIsNull) return "no game is loaded";
            Game.Company player = MonoBehaviourSingleton<Manager.GameManager>.Instance.Player;
            switch (what)
            {
                case "bankruptcy":
                    player.SendWarningAboutImminentBankruptcy(false);
                    return null;
                case "notify":
                {
                    var nm = MonoBehaviourSingleton<Manager.NotificationManager>.Instance;
                    var data = nm.GetNotification(
                        ScriptableObjectScripts.ENotificationActionAfterClick.LifeSupportFinish);
                    // A string and a double, as every notification but one (D 114673) passes: the arguments are saved
                    // in the notification history, and the join's snapshot scan takes only those two.
                    nm.ShowNotification(data, player, player.mainObjectInfo, () => { }, "SEMP test", 0.0);
                    return null;
                }
                case "lifesupport":
                    return DevLifeSupport(player);
                case "trivia":
                    SerializedMonoBehaviourSingleton<Game.UI.UIManager>.Instance.ShowTriviaWindow(
                        TriviaWindow.ETriviaClip.tr_start);
                    return null;
                case "report-open":
                case "report-close":
                {
                    var r = UnityEngine.Object.FindObjectOfType<GameOpsReporter.ErrorReporter>();
                    if (r == null) return "no bug reporter in this scene";
                    if (what == "report-open") r.Activate(); else r.Deactivate();
                    return null;
                }
                case "tickon":
                {
                    // the box's value is saved: changed in a session, this game's save would differ from the others'
                    if (Session.Live) return "only outside a session";
                    Toggle t = MonoBehaviourSingleton<Manager.NotificationManager>.InstanceIsNull || PauseToggle == null
                        ? null
                        : PauseToggle.GetValue(MonoBehaviourSingleton<Manager.NotificationManager>.Instance) as Toggle;
                    if (t == null) return "no tick box";
                    t.isOn = true;
                    return null;
                }
                default:
                    return "unknown case (bankruptcy, notify, lifesupport, trivia, report-open, report-close, tickon)";
            }
        }

        // The game's own daily step (ObjectInfoData.UpdateLifeSupport) on an outpost where it warns and pauses: one of
        // this player's with crew, more crew than habitats and a supply demand. Its one-frame guard and its last
        // warning are cleared, and the step is run for enough days to use up its supplies, so the warning fires now.
        static string DevLifeSupport(Game.Company player)
        {
            Type t = typeof(Game.ObjectInfoDataScripts.ObjectInfoData);
            MethodInfo step = AccessTools.Method(t, "UpdateLifeSupport");
            FieldInfo guard = AccessTools.Field(t, "hackOneFrameLeesLifesupportBadtimeNotyfication");
            FieldInfo last = AccessTools.Field(t, "lastNotificationLifeSupportFinish");
            if (step == null || guard == null || last == null) return "the game's life-support members are not there";
            foreach (Game.Info.ObjectInfo body in UnityEngine.Object.FindObjectsOfType<Game.Info.ObjectInfo>())
            {
                if (body == null || body.ObjectsInfoData == null) continue;
                foreach (Game.ObjectInfoDataScripts.ObjectInfoData od in body.ObjectsInfoData)
                {
                    if (od == null || od.company != player) continue;
                    if (od.CrewResource == null || od.CrewResource.Value < 1) continue;
                    if (od.GetSupplyDemandPerDay() <= 0) continue;
                    var (people, habitats) = od.GetPopulationHabitats();
                    if (people <= habitats) continue;
                    guard.SetValue(od, true);
                    last.SetValue(od, null);
                    step.Invoke(od, new object[] { 1e6 });
                    Log.Info("dev nopause lifesupport: ran the life-support step on " + body.name);
                    return null;
                }
            }
            return "this player has no outpost with crew, more crew than habitats and a supply demand";
        }
    }
}

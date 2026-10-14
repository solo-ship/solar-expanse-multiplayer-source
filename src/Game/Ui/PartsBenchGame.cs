using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SolarExpanseMultiplayer
{
    // The bench's sets in a loaded game: the notification card, the speed marks, the pause screen and its save
    // dialog, and the parts built from the speed panel's pieces.
    public sealed partial class PartsBench
    {

        IEnumerator GameSet()
        {
            GameParts parts = GameParts.InGame();
            if (parts.Missing != null) { checks.Fail("game part missing: " + parts.Missing); yield break; }
            BenchChecks.GameState("game before");
            yield return NoticeSet(parts);
            yield return Pause(parts);
            yield return MarkSet(parts);
            yield return PauseScreenSet(parts);
            yield return StripShowcase(parts);
            BenchChecks.GameState("game after");
        }

        IEnumerator Pause(GameParts parts)
        {
            Click(((Button)parts.Original(GameParts.PSpeedButton + "0")).gameObject);   // the player's pause
            yield return Wait(0.5f);
        }

        IEnumerator NoticeSet(GameParts parts)
        {
            Manager.NotificationManager nm = MonoBehaviourSingleton<Manager.NotificationManager>.Instance;
            var show = GameParts.Field<Button>(nm, "showNotificationHistory");
            var hide = GameParts.Field<Button>(nm, "hideNotificationHistory");
            var history = GameParts.Field<Transform>(nm, "transformParentNotificationHistory");
            Func<Game.UI.MainWindow.NotificationUI> card = () => history
                .GetComponentsInChildren<Game.UI.MainWindow.NotificationUI>(false)
                .FirstOrDefault(n => n.gameObject.activeInHierarchy);
            Click(show.gameObject);   // NEW NOTIFICATIONS, as the player clicks it
            yield return Wait(1.0f);
            if (card() == null)
            {
                // none yet: let game time run at the player's x8 until one comes, then pause
                Click(hide.gameObject);
                Click(((Button)parts.Original(GameParts.PSpeedButton + "5")).gameObject);
                float until = Time.realtimeSinceStartup + 150f;
                while (Time.realtimeSinceStartup < until &&
                       Object.FindObjectsOfType<Game.UI.MainWindow.NotificationUI>(false).Length == 0)
                    yield return null;
                yield return Pause(parts);
                Click(show.gameObject);
                yield return Wait(1.0f);
            }
            Game.UI.MainWindow.NotificationUI live = card();
            if (live == null) { checks.Fail("no notification card to compare"); yield break; }
            yield return Wait(0.6f);   // its own opening animation
            string snap = BenchChecks.Snapshot(live.transform);
            HandlerCensus c0 = checks.Census();
            string t0 = BenchChecks.Clock();
            Pair p = MakePair("notice-live", live.gameObject, Side.Right);
            if (p != null) { yield return States(p); Hide(p); }
            Component prefab = (Component)parts.Original(GameParts.PNotice);
            var liveRect = (RectTransform)live.transform;
            Pair fp = MakePair("notice-prefab", prefab.gameObject, Side.Right, liveRect, OpenPoseOf);
            if (fp != null)
            {
                CopyLook(live.transform, fp.Part, true);   // the game's own markup, for the comparison only
                var li = GameParts.Field<Image>(live, "image");
                Transform mi = li != null ? Strip.FindSame(fp.Part, live.transform, li.transform) : null;
                if (mi != null) mi.GetComponent<Image>().sprite = li.sprite;
                yield return null;
                yield return Capture("notice-prefab", fp);
            }
            Clear();
            yield return null;
            checks.Judge("notice", c0, t0, checks.Census(), BenchChecks.Clock());
            checks.SameOriginal("notice card", "after the set", snap, BenchChecks.Snapshot(live.transform));
            Click(hide.gameObject);
            yield return Wait(0.5f);
        }

        static void OpenPoseOf(GameObject clone)
        {
            var anim = clone.GetComponent<Animation>();
            if (anim != null && anim.clip != null) anim.clip.SampleAnimation(clone, anim.clip.length);
        }

        // Each speed's lit mark: the game's own (set on for one frame) against the clone in its place.
        IEnumerator MarkSet(GameParts parts)
        {
            var marks = Enumerable.Range(0, 6).Select(i => (Image)parts.Original(GameParts.PMark + i)).ToArray();
            Transform root = marks[0].transform.parent.parent;
            string snap = BenchChecks.Snapshot(root);
            bool[] was = marks.Select(m => m.gameObject.activeSelf).ToArray();
            HandlerCensus c0 = checks.Census();
            string t0 = BenchChecks.Clock();
            Canvas canvas = BenchCanvas(parts.GameCanvas);
            for (int i = 0; i < 6; i++)
            {
                string problem;
                RectTransform clone = Strip.Clone(marks[i].gameObject, canvas.transform, "bench mark " + i,
                                                  out problem);
                if (clone == null) { checks.Fail("mark " + i + " refused: " + problem); continue; }
                Track(clone.gameObject);
                UiWidgets.Place(clone, (RectTransform)marks[i].transform, Vector2.zero);
                clone.gameObject.SetActive(false);
                for (int j = 0; j < 6; j++) marks[j].gameObject.SetActive(j == i);
                yield return Wait(0.2f);
                Texture2D a = null, b = null;
                yield return BenchChecks.Grab(t => a = t);
                marks[i].gameObject.SetActive(false);
                clone.gameObject.SetActive(true);
                yield return Wait(0.2f);
                yield return BenchChecks.Grab(t => b = t);
                checks.Compare("speed-mark-" + i, a, (RectTransform)marks[i].transform, b, clone);
                Destroy(a); Destroy(b);
                clone.gameObject.SetActive(false);
            }
            for (int j = 0; j < 6; j++) marks[j].gameObject.SetActive(was[j]);
            Clear();
            yield return null;
            checks.Judge("speed marks", c0, t0, checks.Census(), BenchChecks.Clock());
            checks.SameOriginal("speed buttons", "after the set", snap, BenchChecks.Snapshot(root));
        }

        // The pause screen's own Save dialog: its window, its name field and its buttons.
        IEnumerator PauseScreenSet(GameParts parts)
        {
            var pause = (Game.UI.Screens.PauseScreen)parts.Original(GameParts.PPause);
            pause.Visible = true;   // what Esc does
            yield return Wait(0.6f);
            // the pause screen's own button, while the pause window shows (the save dialog hides it)
            GameObject pauseButton = ((Button)parts.Original(GameParts.PMenuButton)).gameObject;
            string snapPause = BenchChecks.Snapshot(pauseButton.transform.parent);
            HandlerCensus p0 = checks.Census();
            string pt0 = BenchChecks.Clock();
            Pair pb = MakePair("pause-button", pauseButton, Side.Right);
            if (pb != null) { yield return States(pb); Hide(pb); }
            Clear();
            yield return null;
            checks.Judge("pause screen", p0, pt0, checks.Census(), BenchChecks.Clock());
            checks.SameOriginal("pause buttons", "after the set", snapPause,
                                BenchChecks.Snapshot(pauseButton.transform.parent));
            Click(GameParts.Field<Button>(pause, "saveGameButton").gameObject);
            yield return Wait(0.8f);
            var window = (Component)parts.Original(GameParts.PWindow);
            var field = (TMP_InputField)parts.Original(GameParts.PInput);
            if (!field.gameObject.activeInHierarchy) checks.Fail("the save dialog's name field is not shown");
            else
            {
                string snap = BenchChecks.Snapshot(window.transform);
                HandlerCensus c0 = checks.Census();
                string t0 = BenchChecks.Clock();
                Pair w = MakePair("save-window", window.gameObject, Side.Right);
                if (w != null) { yield return Capture("save-window", w); Hide(w); }
                Pair input = MakePair("input", field.gameObject, Side.Below);
                if (input != null)
                {
                    yield return States(input);
                    var ci = input.Part.GetComponent<TMP_InputField>();
                    ci.contentType = TMP_InputField.ContentType.Password;
                    ci.text = "hunter2-secret";
                    yield return Wait(0.2f);
                    Log.Info("ui: bench password field shows '" + ci.textComponent.text + "' for " + ci.text.Length +
                             " characters");
                    yield return Natural("input-password");
                    Hide(input);
                }
                yield return TextPairs(parts, new[] { TextRole.Title, TextRole.Small, TextRole.Value }, null);
                Clear();
                yield return null;
                checks.Judge("save dialog", c0, t0, checks.Census(), BenchChecks.Clock());
                checks.SameOriginal("save dialog", "after the set", snap, BenchChecks.Snapshot(window.transform));
            }
            Click(((Button)parts.Original(GameParts.PCancel)).gameObject);
            yield return Wait(0.5f);
            Click(GameParts.Field<Button>(pause, "closeButton").gameObject);
            yield return Wait(0.5f);
            yield return ShowcaseSpan(parts, "game");
        }

        // The new parts built from the speed panel's pieces: no original to compare, a screenshot for the eye.
        IEnumerator StripShowcase(GameParts parts)
        {
            Canvas canvas = BenchCanvas(parts.GameCanvas);
            SpeedStrip strip = SpeedStrip.Build(parts, canvas);
            if (strip == null) { checks.Fail("the speed strip was not built"); yield break; }
            Track(strip.Root.gameObject);
            strip.AddChip(0, "Jordan");
            strip.AddChip(2, "You");
            strip.AddChip(2, "Priya");
            strip.AddChip(5, "Sam");
            strip.Light(2);
            strip.Status.text = "PAUSED: SAM IS PLANNING A MISSION.";
            Track(strip.Root.gameObject);
            int[] pings = { -1, 45, 120, 250, 900 };
            for (int i = 0; i < pings.Length; i++)
            {
                SignalBarsWidget bars = SignalBarsWidget.Build(parts, canvas.transform);
                if (bars == null) { checks.Fail("signal bars were not built"); break; }
                Track(bars.Root.gameObject);
                bars.Root.anchorMin = bars.Root.anchorMax = new Vector2(0.5f, 0.5f);
                bars.Root.anchoredPosition = new Vector2(-200f, 200f - 40f * i);
                bars.Set(pings[i], pings[i] < 0 ? "-" : "~" + pings[i] + " ms");
                Log.Info("ui: bench signal bars for " + pings[i] + " ms: " + SignalBars.For(pings[i]) + " lit");
            }
            yield return Natural("kit-speed-strip");
        }
    }
}

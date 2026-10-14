using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine.UI;
using PauseScreen = Game.UI.Screens.PauseScreen;

namespace SolarExpanseMultiplayer
{
    // The pause screen's save buttons in a session (DD8, DD21): on a client both Save and Load game are greyed (a client never
    // saves, and the line says so once per opening), on the host Load game is greyed (loading another save mid-session would
    // need a fresh lobby). Put back the next time the screen opens outside a session. One instance (SaveGlue.Buttons), called by
    // PauseScreenEarly. Main thread only.
    public sealed class SaveButtons
    {
        static readonly FieldInfo SaveButton = AccessTools.Field(typeof(PauseScreen), "saveGameButton");
        static readonly FieldInfo LoadButton = AccessTools.Field(typeof(PauseScreen), "loadGameButton");

        public void Apply(PauseScreen pause, MenuSessions early)
        {
            try
            {
                bool live = Session.Live;
                bool client = live && Session.Host == null;
                Grey(pause, SaveButton, client);
                Grey(pause, LoadButton, live);
                if (client && early.Saves != null)
                    early.Saves.Block.Refused("the pause screen opened", early.Actions.Notices, NetClock.NowMs);
            }
            catch (Exception e) { early.PauseFailed("the save buttons failed: " + e.Message); }
        }

        void Grey(PauseScreen pause, FieldInfo f, bool off)
        {
            var b = f != null ? f.GetValue(pause) as Button : null;
            if (b == null) return;
            if (off) b.interactable = false;
            else if (!b.interactable && Greyed.Contains(b)) b.interactable = true;
            if (off) Greyed.Add(b); else Greyed.Remove(b);
        }

        // the buttons this class switched off, so only those are switched back on
        readonly System.Collections.Generic.HashSet<Button> Greyed = new System.Collections.Generic.HashSet<Button>();
    }
}

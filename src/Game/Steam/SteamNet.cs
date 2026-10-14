using System;
using System.Collections.Generic;
using Steamworks;
using UnityEngine;

namespace SolarExpanseMultiplayer
{
    // The mod's Steam side, owned by SempRoot: readiness, every transport a session side opens, the lobby (hosting,
    // invites, join requests) and the one-PC self-test (test profiles only). Stop tears all of it down; it runs on every way out (session end, quit, the
    // root's destroy) and is safe to run again. No Steamworks call is made before Ready.Up, and none after the game's
    // Steam has shut down.
    public sealed class SteamNet
    {
        public readonly SteamReady Ready = new SteamReady();
        public readonly SteamLobby Lobby;
        readonly List<SteamTransport> transports = new List<SteamTransport>();
        SteamSelfTest selfTest;
        float nextTrigger;
        bool flagApplied;
        Callback<SteamShutdown_t> shutdownCb;
        Callback<SteamServersDisconnected_t> disconnectCb;

        // Set when Steam ended the session from under us (the client closed, or its servers dropped for good): the
        // reason for the screen. Cleared by the next transport.
        public string EndedReason { get; private set; }

        // What the Multiplayer screen shows about Steam right now: the state and the player's line (empty when fine).
        public string StatusLine { get { Ready.Refresh(); return SteamStates.Line(Ready.State); } }

        byte[] salt;

        public SteamNet() { Lobby = new SteamLobby(this); }

        // A Steam id as the logs may show it (SteamIdText.Short, this install's salt): never the id itself.
        public string Short(ulong id)
        {
            if (salt == null) salt = SteamIdText.LoadOrMakeSalt(Boot.DataDir);
            return SteamIdText.Short(id, salt);
        }

        // This game's own Steam account (the host's pin in a Steam join code); 0 unless Steam is up and signed in to
        // an individual account.
        public ulong OwnId()
        {
            Ready.Refresh();
            if (!Ready.Up || !SteamReady.NativeAlive) return 0;
            try
            {
                CSteamID me = SteamUser.GetSteamID();
                return me.IsValid() && me.BIndividualAccount() ? me.m_SteamID : 0;
            }
            catch (Exception) { return 0; }
        }

        // A transport for one side of a session; null (and nothing Steam called) unless Steam is up.
        public SteamTransport NewTransport(string side, NetOptions opt)
        {
            Ready.Refresh();
            if (!Ready.Usable) { Log.Info("steam: no " + side + " transport: " + (Ready.Why ?? Ready.Problem)); return null; }
            EndedReason = null;
            Watch();
            var t = new SteamTransport(Ready, opt, side);
            transports.Add(t);
            return t;
        }

        // Steam closing under us (SteamShutdown_t) ends every Steam link at once, with the reason; losing Steam's servers
        // (SteamServersDisconnected_t) is only logged: links through the relay time out and the rejoin flow takes over.
        // Registered once a transport is wanted (never before Steam is up).
        void Watch()
        {
            if (shutdownCb != null || !SteamReady.NativeAlive) return;
            shutdownCb = Callback<SteamShutdown_t>.Create(cb =>
            {
                EndedReason = "Steam was closed, so the internet game has ended.";
                Log.Warn("steam: Steam is shutting down; ending the Steam session");
                Lobby.Forget();
                StopTransports();
            });
            disconnectCb = Callback<SteamServersDisconnected_t>.Create(cb =>
                Log.Warn("steam: lost Steam's servers (" + cb.m_eResult + "); links through the relay may drop"));
        }

        // The dev trigger "steam-state <name|off>": force a state and report what each Steam control does with it.
        void ForceFromTrigger(string value)
        {
            SteamState st;
            if (value == "off") Ready.Force(null);
            else if (SteamStates.TryParse(value, out st)) Ready.Force(st);
            else { Log.Warn("steam-state: unknown state '" + TextSanitiser.OneLine(value) + "'"); return; }
            Probe();
        }

        // What Steam options do now: the line, a transport asked for, a Begin, and that the LAN path is untouched.
        // Every outcome goes to the log as "steam-state: ..." for the test to read.
        void Probe()
        {
            Ready.Refresh();
            SteamTransport t = null;
            bool began = false;
            try
            {
                t = NewTransport("probe", new NetOptions());
                began = t != null && t.Begin();
            }
            catch (Exception e) { Log.Error("steam-state: probe threw " + e.GetType().Name); }
            finally { if (t != null) { try { t.Stop(); } catch (Exception) { } Forget(t); } }
            Log.Info("steam-state: state=" + SteamStates.Name(Ready.State) + " usable=" + Ready.Usable + " transport="
                + (t != null) + " begin=" + began + " lan=" + SteamStates.LanUsable(Ready.State) + " line=\"" + StatusLineNoRefresh() + "\"");
        }

        string StatusLineNoRefresh() { return SteamStates.Line(Ready.State); }

        public void Update()
        {
            if (!flagApplied)
            {
                flagApplied = true;
                if (Boot.Dev != null && Boot.Dev.ForcedSteam != null) Ready.Force(Boot.Dev.ForcedSteam);
            }
            if (Boot.Profile != null && DevTriggers.Every(ref nextTrigger, Time.realtimeSinceStartup))
            {
                string v = DevTriggers.Take("steam-state");   // test profiles only: Take is null without one
                if (v != null) ForceFromTrigger(v.Trim());
                if (DevTriggers.Take("steam-selftest") != null)
                {
                    if (selfTest != null && !selfTest.Done) Log.Warn("steam-selftest: already running");
                    else selfTest = new SteamSelfTest(this);
                }
            }
            if (selfTest != null && !selfTest.Done)
            {
                try { selfTest.Update(); }
                catch (Exception e) { selfTest.Abort("threw " + e.GetType().Name + ": " + e.Message); }
            }
            try { Lobby.Update(); }
            catch (Exception e) { Log.Error("steam lobby: update failed: " + e.GetType().Name + ": " + e.Message); }
            foreach (SteamTransport t in transports.ToArray())
            {
                try { t.Tick(); }
                catch (Exception e) { Log.Error("steam: asking the door again failed: " + e.GetType().Name); }
            }
            // test profiles only: lobby joins with no screens, and the invite and lobby dev triggers
            if (!devJoinChecked)
            {
                devJoinChecked = true;
                if (Boot.Profile != null && TestSeams.Open(Boot.Profile, MenuSessions.PlayerPath))
                    devJoin = new LobbyDevJoin(this);
            }
            if (devJoin != null)
            {
                try { devJoin.Update(); }
                catch (Exception e)
                {
                    Log.Error("steam lobby: dev join failed: " + e.GetType().Name + ": " + e.Message);
                }
            }
        }
        LobbyDevJoin devJoin;
        bool devJoinChecked;

        // Test profiles: the agency a lobby join claims (the dev trigger "agency"); null otherwise.
        public string DevAgency { get { return devJoin != null ? devJoin.Agency : null; } }

        internal void Forget(SteamTransport t) { transports.Remove(t); }

        // Every exit path: the lobby is left first (while Steam can still be told), then the transports close.
        public void Stop()
        {
            if (selfTest != null && !selfTest.Done) selfTest.Abort("the game is closing");
            try { Lobby.Stop(); }
            catch (Exception e) { Log.Error("steam lobby: stop failed: " + e.GetType().Name); }
            StopTransports();
            try { if (shutdownCb != null) shutdownCb.Dispose(); if (disconnectCb != null) disconnectCb.Dispose(); }
            catch (Exception) { }
            shutdownCb = null; disconnectCb = null;
        }

        void StopTransports()
        {
            foreach (SteamTransport t in transports)
            {
                try { t.Stop(); }
                catch (Exception e) { Log.Error("steam: stopping a transport failed: " + e.Message); }
            }
            transports.Clear();
        }
    }
}

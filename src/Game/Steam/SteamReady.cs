using System;
using System.Text.RegularExpressions;
using Steamworks;
using UnityEngine;

namespace SolarExpanseMultiplayer
{
    public enum RelayState { NotStarted, Waiting, Ready, Failed }

    // Whether Steam can be used, as the game set it up. The game's SteamAchievementsManager initialises Steam and pumps
    // its callbacks every frame; we never call SteamAPI.Init, Shutdown or RunCallbacks. Without Steam every Steamworks
    // call throws, so nothing Steam is called until Up says so, and after the game's Steam has shut down nothing is
    // called at all (NativeAlive). Refresh runs on the main thread (it looks at a Unity object).
    public sealed class SteamReady
    {
        const float RelayTimeoutSeconds = 30f;
        float nextLookup, relayStartedAt;
        bool startedRelay;

        public bool Up { get; private set; }
        public string Why { get; private set; }          // why not Up (for the screens and the log)
        public RelayState Relay { get; private set; }
        public SteamState State { get; private set; }    // what Steam looks like, for the player (SteamStates)
        public SteamState? Forced { get; private set; }  // a test profile's -semp-steam-state / steam-state trigger
        // Steam itself and its relay are both fine to start a connection (a slow relay only warns)
        public bool Usable { get { return SteamStates.SteamUsable(State); } }
        // Why Steam can't be used for a connection (null when it can): the player-facing line
        public string Problem { get { return Usable ? null : SteamStates.Line(State); } }
        public string RelayReason { get; private set; }  // Failed: why

        // Any thread, before every Steamworks call: false once the game has shut Steam down (Steamworks.NET's Shutdown
        // drops the dispatcher's count), so a late call from a network thread is skipped rather than made.
        public static bool NativeAlive { get { return CallbackDispatcher.IsInitialized; } }

        // Test profiles only: report this state instead of looking (null = look again). Steam itself is not touched.
        public void Force(SteamState? s)
        {
            Forced = s;
            if (s == null) { Relay = RelayState.NotStarted; RelayReason = null; }
            Log.Info("steam: " + (s == null ? "state no longer forced" : "state forced to " + SteamStates.Name(s.Value)));
            Refresh();
        }

        public void Refresh()
        {
            if (Forced != null) { ApplyForced(Forced.Value); return; }
            Up = Check();
            if (Up && Relay == RelayState.Waiting) PollRelay();
            State = !Up ? NotUpState
                : SteamStates.Decide(true, true, true, Relay == RelayState.Failed,
                                     Relay == RelayState.Waiting ? Time.realtimeSinceStartup - relayStartedAt : -1f);
        }

        SteamState NotUpState;

        void ApplyForced(SteamState s)
        {
            State = s;
            Up = s == SteamState.RelayDown || s == SteamState.RelaySlow || s == SteamState.Ready;
            Why = Up ? null : SteamStates.Line(s);
            Relay = s == SteamState.RelayDown ? RelayState.Failed : s == SteamState.RelaySlow ? RelayState.Waiting : RelayState.Ready;
            RelayReason = s == SteamState.RelayDown ? "can't reach Steam's network: forced by the test profile" : null;
        }

        bool Check()
        {
            var mgr = Manager();
            if (mgr == null) { Why = "the game's Steam manager is not there"; NotUpState = SteamState.NotRunning; return false; }
            if (!mgr.IsSteamInitialized) { Why = "Steam isn't running"; NotUpState = SteamState.NotRunning; return false; }
            if (!NativeAlive) { Why = "Steam's callbacks are not running"; NotUpState = SteamState.NotRunning; return false; }
            try
            {
                if (!SteamUser.GetSteamID().IsValid()) { Why = "not signed in to Steam"; NotUpState = SteamState.NotLoggedOn; return false; }
                if (!SteamUser.BLoggedOn()) { Why = "Steam is offline"; NotUpState = SteamState.Offline; return false; }
            }
            catch (Exception e) { Why = "Steam can't be asked (" + e.GetType().Name + ")"; NotUpState = SteamState.NotRunning; return false; }
            Why = null;
            return true;
        }

        // The instance once it exists; a lookup by type (slow) at most once a second while it doesn't.
        Manager.SteamAchievementsManager Manager()
        {
            if (!MonoBehaviourSingleton<Manager.SteamAchievementsManager>.InstanceIsNull)
                return MonoBehaviourSingleton<Manager.SteamAchievementsManager>.Instance;
            if (Time.realtimeSinceStartup < nextLookup) return null;
            nextLookup = Time.realtimeSinceStartup + 1f;
            return MonoBehaviourSingleton<Manager.SteamAchievementsManager>.Instance;
        }

        // Once per process (Steam keeps relay access up from then on): Steam starts measuring its relays.
        public void StartRelay()
        {
            Refresh();
            if (startedRelay || !Up || Forced != null) return;
            startedRelay = true;
            relayStartedAt = Time.realtimeSinceStartup;
            Relay = RelayState.Waiting;
            try
            {
                SteamNetworkingUtils.InitRelayNetworkAccess();
                Log.Info("steam: relay access requested");
            }
            catch (Exception e) { Fail("the relay request threw " + e.GetType().Name); }
        }

        void PollRelay()
        {
            float waited = Time.realtimeSinceStartup - relayStartedAt;
            SteamRelayNetworkStatus_t st;
            ESteamNetworkingAvailability a;
            try { a = SteamNetworkingUtils.GetRelayNetworkStatus(out st); }
            catch (Exception e) { Fail("the relay status threw " + e.GetType().Name); return; }
            if (a == ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_Current)
            {
                Relay = RelayState.Ready;
                Log.Info("steam: relay ready after " + waited.ToString("0.0") + " s");
                return;
            }
            // Retrying (-10) is Steam still at it; Previously, Failed and CannotTry (-100 and below) are given up
            if ((int)a <= (int)ESteamNetworkingAvailability.k_ESteamNetworkingAvailability_Previously)
            {
                Fail("Steam says " + a + " (" + Scrub(st.m_debugMsg) + ")");
                return;
            }
            if (waited > RelayTimeoutSeconds) Fail("not ready after " + RelayTimeoutSeconds + " s (" + a + ")");
        }

        void Fail(string why)
        {
            Relay = RelayState.Failed;
            RelayReason = "can't reach Steam's network: " + why;
            Log.Warn("steam: relay " + RelayReason);
        }

        // Text from Steam can hold a Steam id (17 digits): no run of 12+ digits is ever logged.
        public static string Scrub(string s)
        {
            return s == null ? "" : TextSanitiser.OneLine(Regex.Replace(s, "[0-9]{12,}", "<id>"));
        }
    }
}

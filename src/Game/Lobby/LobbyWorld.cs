using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Manager;
using ScriptableObjectScripts;
using UnityEngine.SceneManagement;

namespace SolarExpanseMultiplayer
{
    // What the host chose before its lobby opened (M8 DD6, DD7): an own save, or a new game's configuration from the
    // game's own pages (or the game's defaults), with the lobby's agencies and options made from it.
    public sealed class LobbySetup
    {
        public bool NewGame;
        public string SaveName;                                   // an own save (the game's Saves folder)
        public GameManager.StartGameConfiguration Config;         // a new game: a copy, never the menu panel's object
        public LobbyOptions Options;
        public List<LobbyAgency> Agencies = new List<LobbyAgency>();
        // a save: its player agency (the host plays it); a new game: the host's first pick, or null
        public string HostAgency;
        public string Title, Date;
    }

    // The host's world for its lobby: the setup read at the menu, and at Start the game's own load of it (the save
    // through LoadSaveManager.Load, which the setup's hooks leave alone now that nothing is armed; a new game through
    // GameManager.StaticStartGameConfig and the LoadingScreen scene, with the host's claim as the player and every
    // other agency in the game as a rival). Agencies travel as wire ids (AgencyWire). Main thread only.
    public static class LobbyWorld
    {
        // An own save's lobby: its info file read the game's own way (the save is the player's own, which the game
        // itself reads like this at its Load). Null and why when it cannot be hosted.
        public static LobbySetup ForSave(string saveName, out string why)
        {
            why = null;
            if (string.IsNullOrWhiteSpace(saveName)) { why = "host.noSave"; return null; }
            try
            {
                string info = LoadSaveManager.SaveNameToPaths(saveName).Item2;
                string world = LoadSaveManager.SaveNameToPaths(saveName).Item1;
                if (!File.Exists(info) || !File.Exists(world)) { why = "host.noSave"; return null; }
                SaveInfo s = BaseSaveGameData<SaveInfo>.Deserialize(File.ReadAllBytes(info));
                GameManager.StartGameConfiguration c = s != null ? s.startGameConfiguration : null;
                if (c == null || c.Company == null) { why = "host.loadFailed"; return null; }
                if (s.createdInDemo) { why = SetupFlow.DemoNotice; return null; }
                string player = AgencyWire.From(c.Company.ID);
                var rivals = new HashSet<string>(StringComparer.Ordinal);
                if (c.enabledRivalCorporations != null)
                    foreach (CompanyDefinition d in c.enabledRivalCorporations)
                        if (d != null) rivals.Add(AgencyWire.From(d.ID));
                var setup = new LobbySetup
                {
                    NewGame = false, SaveName = saveName, HostAgency = player, Title = saveName,
                    Date = s.gameDate.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture),
                    Options = new LobbyOptions { NewGame = false, SaveId = SaveIdOf(saveName) },
                };
                foreach (string id in Playable())
                {
                    AgencyStanding state = id == player ? AgencyStanding.Human
                        : rivals.Contains(id) ? AgencyStanding.AiRival : AgencyStanding.NotInGame;
                    setup.Agencies.Add(new LobbyAgency { Id = id, State = state, Playable = true, EpochAllows = true });
                }
                return setup;
            }
            catch (Exception e)
            {
                Log.Error("lobby: the save '" + TextSanitiser.OneLine(saveName) + "' could not be read: " +
                          e.GetType().Name);
                why = "host.loadFailed";
                return null;
            }
        }

        // A new game's lobby from the setup's kept configuration and the rival toggles (game ids): ticked = AI rival,
        // unticked = not in the game, every playable agency claimable in the lobby (the setup already refused an epoch
        // that does not allow them all).
        public static LobbySetup ForNewGame(GameManager.StartGameConfiguration kept, IList<string> ai,
            IList<string> off, string hostPick)
        {
            var aiWire = new HashSet<string>(StringComparer.Ordinal);
            if (ai != null) foreach (string a in ai) aiWire.Add(AgencyWire.From(a));
            var setup = new LobbySetup
            {
                NewGame = true, Config = kept, HostAgency = JoinCheck.IsAgencyId(hostPick) ? hostPick : null,
                Options = new LobbyOptions { NewGame = true },
            };
            foreach (string id in Playable())
                setup.Agencies.Add(new LobbyAgency
                {
                    Id = id, State = aiWire.Contains(id) ? AgencyStanding.AiRival : AgencyStanding.NotInGame,
                    Playable = true, EpochAllows = true,
                });
            return setup;
        }

        // A save's lobby id (LobbyOptions.SaveId): 16 hex digits from its name.
        static string SaveIdOf(string saveName)
        {
            return Sha256Hex.Of(Encoding.UTF8.GetBytes(saveName)).Substring(0, 16);
        }

        // This game's playable agencies as wire ids, in a stable order: every agency of its content but the World
        // Government.
        public static List<string> Playable()
        {
            var ids = new List<string>();
            foreach (CompanyDefinition d in Definitions())
            {
                string w = AgencyWire.From(d.ID);
                if (JoinCheck.IsAgencyId(w) && !ids.Contains(w)) ids.Add(w);
            }
            ids.Sort(StringComparer.Ordinal);
            return ids;
        }

        static List<CompanyDefinition> Definitions()
        {
            var list = new List<CompanyDefinition>();
            var all = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance;
            if (all == null || all.AllCompanyDefinitions == null) return list;
            foreach (CompanyDefinition d in all.AllCompanyDefinitions.ListNotEmpty)
                if (d != null && !d.IsWorldGovernment) list.Add(d);
            return list;
        }

        static CompanyDefinition Definition(string wire)
        {
            foreach (CompanyDefinition d in Definitions()) if (AgencyWire.From(d.ID) == wire) return d;
            return null;
        }

        // The host's Start: the game's own load of the host's world. Null = the load has begun; else a UiText key and
        // nothing has changed.
        public static string Begin(LobbySetup setup, LobbyRoom room)
        {
            try
            {
                if (!setup.NewGame)
                {
                    Log.Info("lobby: the host's save loads (the game's own load)");
                    bool ok = SerializedMonoBehaviourSingleton<LoadSaveManager>.Instance.Load(setup.SaveName);
                    return ok ? null : "host.loadFailed";
                }
                CompanyDefinition host = Definition(room.HostAgency);
                if (host == null || setup.Config == null) return SetupFlow.Unavailable;
                // a copy: a start that fails leaves the kept configuration as it was for the next try
                GameManager.StartGameConfiguration c = HostSetupPages.Copy(setup.Config);
                c.Company = host;
                c.enabledRivalCorporations = new List<CompanyDefinition>();
                var names = new List<string>();
                foreach (string wire in room.WorldRivals())
                {
                    CompanyDefinition d = Definition(wire);
                    if (d == null || d == host) continue;
                    c.enabledRivalCorporations.Add(d);
                    names.Add(d.ID);
                }
                c.setupAIs = true;
                c.startWithTutorial = false;   // the tutorial pauses and opens windows on one game only
                c.SetGameplayIDIfNotSet();
                GameManager.StaticStartGameConfig = c;
                Log.Info("lobby: the new game starts: player " + host.ID + ", rivals [" +
                         string.Join(",", names.ToArray()) + "], difficulty " + c.GameDifficulty);
                SceneManager.LoadScene(SetupFlow.LoadingScene);
                return null;
            }
            catch (Exception e)
            {
                Log.Error("lobby: the host's world could not be started: " + e);
                return SetupFlow.Unavailable;
            }
        }
    }
}

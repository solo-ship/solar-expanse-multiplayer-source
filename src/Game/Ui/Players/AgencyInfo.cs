using System;
using System.Collections.Generic;
using Data.ScriptableObject;
using ScriptableObjectScripts;
using UnityEngine;

namespace SolarExpanseMultiplayer
{
    // The game's own name and logo for an agency's wire id (AgencyWire), read from its CompanyDefinition: the name as
    // the game's load dialog words it (LEManager.Get of the definition's localeID, D 144836), the logo its HUD shows.
    // One instance per world (the definitions are the world's content); an id the game does not have answers null,
    // and the screens fall back to the id itself.
    public sealed class AgencyInfo
    {
        readonly Dictionary<string, CompanyDefinition> byWire = new Dictionary<string, CompanyDefinition>();
        bool read;

        public string Name(string wireId)
        {
            CompanyDefinition d = Find(wireId);
            if (d == null) return null;
            try
            {
                string n = Language.LEManager.Get(d.localeID);
                return string.IsNullOrEmpty(n) || n == d.localeID ? null : n;
            }
            catch (Exception) { return null; }
        }

        public Sprite Logo(string wireId)
        {
            CompanyDefinition d = Find(wireId);
            try { return d != null ? d.LogoImage : null; }
            catch (Exception) { return null; }
        }

        CompanyDefinition Find(string wireId)
        {
            if (string.IsNullOrEmpty(wireId)) return null;
            if (!read) Read();
            CompanyDefinition d;
            return byWire.TryGetValue(wireId, out d) ? d : null;
        }

        void Read()
        {
            read = true;
            try
            {
                var all = SerializedMonoBehaviourSingleton<Manager.AllScriptableObjectManager>.Instance;
                if (all == null || all.AllMyIDScriptableObjects == null) return;
                foreach (MyIDScriptableObject o in all.AllMyIDScriptableObjects)
                {
                    var d = o as CompanyDefinition;
                    string w = d != null ? AgencyWire.From(d.ID) : null;
                    if (w != null && !byWire.ContainsKey(w)) byWire[w] = d;
                }
            }
            catch (Exception e) { Log.Warn("ui: the agencies' names could not be read: " + e.Message); }
        }
    }
}

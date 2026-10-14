using System;
using System.Collections.Generic;
using System.Text;
using Company = Game.Company;
using ObjectInfoData = Game.ObjectInfoDataScripts.ObjectInfoData;
using ResearchManager = Manager.ResearchManager;
using ResearchDefinition = ScriptableObjectScripts.ResearchDefinition;

namespace SolarExpanseMultiplayer
{
    // What the rivals have, for the log of a solo game (the dev "speed" trigger's date lines): their facilities, finished
    // research and money, so a log shows their AI acting between two dates. Read-only; main thread only.
    public static class SoloProbe
    {
        static readonly StateField<ResearchManager, List<ResearchDefinition>> completed =
            new StateField<ResearchManager, List<ResearchDefinition>>("completedResearch");

        public static string Rivals()
        {
            var sb = new StringBuilder();
            List<Company> all = MonoBehaviourSingleton<Manager.GameManager>.Instance.Companies;
            var infos = MonoBehaviourSingleton<Manager.ObjectInfoManager>.Instance.allObjectInfos;
            foreach (Company c in new List<Company>(all))
            {
                if (c == null || c.ID == null || c.IsPlayer || !c.gameObject.activeSelf) continue;
                if (c.Definition != null && c.Definition.IsWorldGovernment) continue;
                int facilities = 0;
                foreach (Game.Info.ObjectInfo oi in infos)
                {
                    ObjectInfoData d = oi != null ? oi.GetObjectInfoData(c) : null;
                    if (d != null && d.ListFacility != null) facilities += d.ListFacility.Count;
                }
                List<ResearchDefinition> done = c.ResearchManager != null ? completed.Get(c.ResearchManager) : null;
                var mc = c.MoneyController;
                sb.Append(sb.Length > 0 ? "; " : "").Append(c.ID).Append(": facilities ").Append(facilities)
                  .Append(", research done ").Append(done != null ? done.Count : -1)
                  .Append(", money ").Append(mc != null ? mc.CurrentMoney.ToString() : "?");
            }
            return sb.Length > 0 ? sb.ToString() : "no rivals";
        }
    }
}

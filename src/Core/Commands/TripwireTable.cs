using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace SolarExpanseMultiplayer
{
    // One thing the tripwire watches: a world method an action reaches (Hooked), or a greyed path whose guard would
    // have to fail for the line to appear. `Automatic` is K13's list for a hooked method: the callers that change it
    // without a click (the day event, a finished build, the load, the AI, cargo, contract rewards, station logic),
    // written as "Type.Method" the way a stack frame names them.
    public sealed class TripwireEntry
    {
        public readonly string Name;
        public readonly bool Hooked;
        public readonly string[] Automatic;

        public TripwireEntry(string name, bool hooked, params string[] automatic)
        {
            Name = name;
            Hooked = hooked;
            Automatic = automatic ?? new string[0];
        }
    }

    // The tripwire's table (docs/research/commands.md, "K13"). A change to a human agency that is neither inside an
    // applied entry nor in one of the method's automatic contexts is a click path nothing captured: it is logged as a
    // WARN and never blocked. Every name here can also be reached by the dev trigger "trip <name>", which is how each
    // line is proved without the window that would produce it.
    public static class TripwireTable
    {
        public const string LinePrefix = "command: unrouted world change: ";

        // Where a finished or loaded build, the AI and the station logic reach the facility methods.
        static readonly string[] StackAutomatic =
        {
            "ObjectInfoData.AddToProductionFromQuery", "Facility.OnBuildInternal", "ProductionItem.Build",
            "ObjectInfoData.RocketConstructDataOnFinishConstruction",
            "InterstellarConstructionFacility.OnBuildInternal",
            "InterstellarShipFacilityFinish.OnBuildInternal", "SpaceStationConstructionFacility.CreateStartBuilding",
            "ObjectInfoData.RemoveProductionItem", "ObjectInfoData.OnLoad",
            "InterstellarConstructionFacility.OnLoadState",
            "ObjectInfoData.AddResourcesAndModules", "InfoCargoCyclicalMission.ReleaseSpaceModule",
            "Contract.GiveRewards", "ProductionItemTryToBuild.OnStart", "Offer.FullFill"
        };

        // The day event and the load run many of those callers' work inline; a frame of either clears every method.
        public static readonly string[] Everywhere =
        {
            "TimeController.DoThePeriodicEvents", "LoadSaveManager.ExtractAllFromSaveData"
        };

        // "Type.*" clears every method of the type.
        static readonly string[] ObservatoryAutomatic =
        {
            "ObservatoryFacility.*", "ObservatoryRule.*", "Company.DecreaseExoplanetSurveyCost",
            "ObjectInfoManager.NewSystemDiscover", "Facility.Scrap"
        };

        // The market calls the manager and the rivals make by themselves, and the mod's own steps (they run under an
        // applied entry, which the tripwire never sees, but a frame of theirs is a sure sign too).
        static readonly string[] MarketAutomatic =
        {
            "MarketOfferManager.*", "MarketRule.*", "CompanyBehaviour.*", "AiSteps.*", "WorldMarket.*", "MarketApply.*",
            "WorldApply.*", "WorldBuy.*"
        };

        // The contract calls the game's objectives, the unlock by date, the rivals and the mod's applies make.
        static readonly string[] ContractAutomatic =
        {
            "ContractManager.*", "Contract.*", "CompanyContractData.*", "CompanyObjectiveData.*", "CompanyBehaviour.*",
            "ContractRule.*", "WorldApply.*"
        };

        public static readonly TripwireEntry[] Entries =
        {
            // ---- world methods the 21 actions reach (hooked) ----
            new TripwireEntry("ResearchManager.StartResearch", true, "ResearchManager.AutoPickResearch",
                "ResearchManager.SetDataFromSave", "CompanyBehaviour.TryMakeNextResearch", "WaitForResearch.SetData"),
            new TripwireEntry("ResearchManager.AddToQueue", true, "ResearchManager.AutoPickResearch",
                "ResearchManager.SetDataFromSave"),
            new TripwireEntry("ResearchManager.StopResearch", true),
            new TripwireEntry("ObjectInfoData.AddFacility", true, StackAutomatic),
            new TripwireEntry("ObjectInfoData.AddProductItemToQuery", true,
                "ContractManager.LoadConstructionNotYetPay"),
            new TripwireEntry("ObjectInfoData.AddRocketToConstruct", true, "ObjectInfoData.AddToProductionFromQuery",
                "ObjectInfoData.OnLoad", "ProductionItemTryToBuild.OnStart"),
            // both rows' OnPointerEnter call it as a preview that changes nothing
            new TripwireEntry("ObjectInfoData.SetProductItemBefore", true, "UIRowFacility.OnPointerEnter",
                "UIRowRocket.OnPointerEnter"),
            new TripwireEntry("Facility.Scrap", true, "AsteroidPullingModule.OnBuildInternal",
                "ObjectInfoData.RemoveProductionItem", "ObjectInfoData.TryChangeValidChangeIfNeedSpaceStation",
                "ObjectInfoData.SendAllItems", "ObjectInfoData.VirtualDestroy", "ObjectInfo.VirtualDestroy",
                "InterstellarShipFacilityFinish.RemoveFacilityFromOrbit", "PMTabSchedule.OnClickScheduleButtonForCode",
                "Spacecraft.CreateFacility", "InfoCargoCyclicalMission..ctor", "Company.ExtractFromSaveGameData"),
            new TripwireEntry("Facility.CancelBuild", true, "ObjectInfoData.SendAllItems"),
            new TripwireEntry("SpacecraftConstructData.CancelBuild", true, "ObjectInfoData.SendAllItems"),
            new TripwireEntry("ObjectInfoData.SetPriority", true),
            new TripwireEntry("Facility.StopOperation", true, "Facility.Scrap"),
            new TripwireEntry("ObjectInfo.VirtualDestroyFromNuclearBomb", true),
            // the observatory: its stop and the three orders (O1-O3); its own steps, the host's wrappers and the
            // survey/system code re-plan it without a click
            new TripwireEntry("ObservatoryFacility.StopOperation", true, ObservatoryAutomatic),
            new TripwireEntry("ObservatoryFacility.OrderExploration", true, ObservatoryAutomatic),
            new TripwireEntry("ObservatoryFacility.OrderDiscovery", true, ObservatoryAutomatic),
            new TripwireEntry("ObservatoryFacility.AssignToExoplanetSurvey", true, ObservatoryAutomatic),
            // the market and the contracts (M7): every click path is a capture, so a human agency's offer, fill or
            // contract change outside an applied entry is a path nothing caught. The automatic callers: the manager's
            // own code (the maker, the load, the day's unlocks), the rivals' tasks, and the mod's own applies
            new TripwireEntry("MarketOfferManager.AddOffer", true, MarketAutomatic),
            new TripwireEntry("MarketOfferManager.CancelOffer", true, MarketAutomatic),
            new TripwireEntry("Offer.FullFill", true, MarketAutomatic),
            new TripwireEntry("ContractManager.ActivateContractForCompany", true, ContractAutomatic),
            new TripwireEntry("ContractManager.CancelContractForCompany", true, ContractAutomatic),
            new TripwireEntry("Contract.FinishContractForCompany", true, ContractAutomatic),
            // ---- M6: the flight world methods (DD-F4, K13's lists for flights) ----
            // A flight made by a click on the visible planner has no code-planner frame under it; every flight a
            // game makes by itself (the load, the AI, a route's leg, an accepted flight's copy, a client's applied
            // copy) is built by the hidden planners, whose schedule step is OnClickScheduleButtonForCode.
            new TripwireEntry("PMTabSchedule.CreateFly", true, "PMTabSchedule.OnClickScheduleButtonForCode"),
            // The game's own cancels: the launch moment (a rocket launcher; a patched OnLaunch is named by its nested
            // type), the impact and the routes, plus this mod's cancels, which run under an applied entry or the
            // host's decision. A click reaches it through Spacecraft.CancelMission, which is not automatic.
            new TripwireEntry("MissionInfo.Cancel", true, "RocketLauncher.*", "LaunchSpaceCraft.*",
                "Spacecraft.AsteroidImpactCancelMission", "MissionInfo.CancelAllDependsMissionInfo",
                "CycleMissionManager.*", "SpaceCraftCyclicalMissionController.*", "FlightCancels.*",
                "FlightClientEntries.*", "FlightSources.*", "FlightHost.*", "ObjectInfo.VirtualDestroy"),
            // A route is added by the load, by a build finishing and by the planner's own re-adds; removed by the
            // day event, by a destroyed object and by its ship's own update.
            new TripwireEntry("CycleMissionManager.AddCycleMission", true, "Company.LoadCyclicalMission",
                "CycleMissionManager.*"),
            new TripwireEntry("CycleMissionManager.RemoveCycleMission", true, "Spacecraft.FixedUpdate",
                "CycleMissionManager.*"),
            // an edited route is changed in place; only the route window's own edit reaches this
            new TripwireEntry("CycleMissionManager.RefreshAfterEdit", true),
            new TripwireEntry("Spacecraft.Scrap", true),
            new TripwireEntry("LaunchVehicle.Scrap", true),
            // ---- greyed paths (DD13): the handler that would run ----
            new TripwireEntry("UIRowFacility.SpaceElevatorInstanceDragAndDrop", false),
            new TripwireEntry("UIRowRocket.SpaceElevatorInstanceDragAndDrop", false),
            new TripwireEntry("SpaceCraftProjectWindow.CreatedNewProject", false),
            new TripwireEntry("SpaceCraftProjectWindow.OnClickBtnSave", false)
        };

        public static TripwireEntry Find(string name)
        {
            if (name == null) return null;
            foreach (TripwireEntry e in Entries) if (e.Name == name) return e;
            return null;
        }

        // The entry a dev trigger names: the full name, or the method part alone when only one entry has it (the
        // trigger file holds at most 64 bytes, and the longest full name is 62).
        public static TripwireEntry Resolve(string text)
        {
            TripwireEntry e = Find(text);
            if (e != null || string.IsNullOrEmpty(text)) return e;
            TripwireEntry found = null;
            foreach (TripwireEntry c in Entries)
            {
                if (!string.Equals(MethodPart(c.Name), text, StringComparison.Ordinal)) continue;
                if (found != null) return null;
                found = c;
            }
            return found;
        }

        public static string MethodPart(string name) { return name.Substring(name.LastIndexOf('.') + 1); }

        // The names the greyed buttons guard (the entries that are not world methods).
        public static string[] GreyedNames()
        {
            var names = new List<string>();
            foreach (TripwireEntry e in Entries) if (!e.Hooked) names.Add(e.Name);
            return names.ToArray();
        }

        // True when one of the stack's frames is an automatic context of the method; an unknown method is never
        // automatic, so it is reported.
        public static bool IsAutomatic(string name, IEnumerable<string> frames)
        {
            TripwireEntry e = Find(name);
            if (e == null || frames == null) return false;
            foreach (string raw in frames)
            {
                string frame = Normal(raw);
                foreach (string context in e.Automatic)
                    if (Matches(frame, context)) return true;
                foreach (string context in Everywhere)
                    if (Matches(frame, context)) return true;
            }
            return false;
        }

        static bool Matches(string frame, string context)
        {
            if (frame == null) return false;
            if (context.EndsWith(".*", StringComparison.Ordinal))
                return frame.StartsWith(context.Substring(0, context.Length - 1), StringComparison.Ordinal);
            return string.Equals(frame, context, StringComparison.Ordinal);
        }

        // A frame written as Mono prints a method Harmony rebuilt: "DynamicMethodDefinition.Ns.Type.Method_Patch1".
        static string Normal(string frame)
        {
            const string rebuilt = "DynamicMethodDefinition.";
            if (frame == null || !frame.StartsWith(rebuilt, StringComparison.Ordinal)) return frame;
            return FrameFromDynamic(frame.Substring(rebuilt.Length)) ?? frame;
        }

        // The name of a stack frame from its (outermost) declaring type and method. In Unity's Mono a patched method
        // is a MonoMod "DynamicMethodDefinition" whose method name is the original's full name with "_Patch1".
        public static string FrameOf(string outerType, string method)
        {
            if (method == null) return null;
            if (outerType == null || outerType == "DynamicMethodDefinition") return FrameFromDynamic(method);
            return FrameName(outerType, method);
        }

        // The log line: `agency` is the wire id of the human agency that changed; both parts are cleaned.
        public static string Line(string name, string agency)
        {
            return LinePrefix + TextSanitiser.OneLine(name) + " for " + TextSanitiser.OneLine(agency ?? "?");
        }

        public const string ConsolePrefix = "command: console change: ";

        // The log line for a world change a developer-console command made (never "unrouted": the console is a known
        // source, ConsoleRules says what mends it).
        public static string ConsoleLine(string command, string name, string agency)
        {
            return ConsolePrefix + TextSanitiser.OneLine(command ?? "?") + " reached " + TextSanitiser.OneLine(name) + " for " +
                   TextSanitiser.OneLine(agency ?? "?");
        }

        static readonly Regex PatchSuffix = new Regex(@"_Patch[0-9]+$", RegexOptions.CultureInvariant);

        // A frame with no declaring type is a method Harmony or MonoMod rebuilt, such as a caller another patch
        // changed: its name carries the original's, as "DMD<Ns.Type::Method>", "Ns.Type.Method_Patch1" or the like.
        // Null when no type and method can be read from it.
        public static string FrameFromDynamic(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string n = name;
            if (n.StartsWith("DMD<", StringComparison.Ordinal) && n.EndsWith(">", StringComparison.Ordinal))
                n = n.Substring(4, n.Length - 5);
            string type, method;
            int sep = n.IndexOf("::", StringComparison.Ordinal);
            if (sep >= 0) { type = n.Substring(0, sep); method = n.Substring(sep + 2); }
            else
            {
                int dot = n.LastIndexOf('.');
                if (dot <= 0) return null;
                type = n.Substring(0, dot);
                method = n.Substring(dot + 1);
            }
            int last = type.LastIndexOfAny(new[] { '.', '+' });
            if (last >= 0) type = type.Substring(last + 1);
            method = PatchSuffix.Replace(method, "");
            return type.Length == 0 || method.Length == 0 ? null : FrameName(type, method);
        }

        // A stack frame as the table names it: the outermost type and the method, with a compiler-made lambda
        // ("<Name>b__3_0") or local function ("<Name>g__Local|1_0") named after the method it was written in.
        public static string FrameName(string outerType, string method)
        {
            if (outerType == null || method == null) return null;
            string name = method;
            if (name.Length > 2 && name[0] == '<')
            {
                int close = name.IndexOf('>');
                if (close > 1) name = name.Substring(1, close - 1);
            }
            return outerType + "." + name;
        }
    }
}

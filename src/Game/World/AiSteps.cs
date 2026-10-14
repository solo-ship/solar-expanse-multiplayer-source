using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using AllScriptableObjectManager = Manager.AllScriptableObjectManager;
using Company = Game.Company;
using Facility = Game.ObjectInfoDataScripts.Facility;
using FacilityBaseDescriptor = Data.ScriptableObject.FacilityBaseDescriptor;
using MarketOfferManager = Manager.MarketOfferManager;
using MoneyController = Game.CompanyScripts.MoneyController;
using ObjectInfoData = Game.ObjectInfoDataScripts.ObjectInfoData;
using ObservatoryFacility = Game.ObjectInfoDataScripts.ObservatoryFacility;
using Offer = Game.ObjectInfoDataScripts.Offer;
using ProductionItem = Game.ObjectInfoDataScripts.ProductionItem;
using ResearchDefinition = ScriptableObjectScripts.ResearchDefinition;
using ResearchManager = Manager.ResearchManager;
using ResourceDefinition = ScriptableObjectScripts.ResourceDefinition;
using ResourcePrice = Game.UI.Windows.Elements.SpaceCraftConstructElements.ResourcePrice;
using ResourcePriceOne = Game.UI.Windows.Elements.SpaceCraftConstructElements.ResourcePriceOne;
using RowResourcesData = Game.UI.Windows.Elements.ObjectInfoElements.RowResourcesData;
using SpacecraftConstructData = Data.SpacecraftConstructData;

namespace SolarExpanseMultiplayer
{
    // DD-W6: the world methods a rival's AI task calls, recorded into the open seam record (AiSeam opens it). Only the
    // outermost call is recorded (the game's nested work is not), and a record opens at its first recorded call so the
    // counters it reads are the ones before that call. A prefix enters, a finalizer records and leaves.
    // Outside any seam record on the host (or anywhere on a client outside an applied entry) a watched call that
    // changes a rival logs "unrouted": the AI tripwire. A human agency's own calls are never the AI's.
    // The class needs a [HarmonyPatch] of its own: HarmonyX silently skips one without.
    [HarmonyPatch]
    public static class AiSteps
    {
        // 0 = nothing entered, 1 = entered inside another recorded call, 2 = the outermost call
        static int Enter(string method, Company who, bool watched)
        {
            AiRecord rec = Ensure(who, method, watched);
            return rec == null ? 0 : rec.Enter() ? 2 : 1;
        }

        // The seam record of this rival's call, opened at its first recorded call (the counters are read then). Null
        // when nothing is recorded: no session, a human agency, a load, not the host, or no seam method running (then a
        // watched call is the tripwire's).
        internal static AiRecord Ensure(Company who, string method, bool watched)
        {
            if (!HumanAgencies.Active || who == null || HumanAgencies.IsHuman(who)) return null;
            WorldRuleState st = HumanAgencies.State.World;
            if (Loading(st)) return null;
            if (HumanAgencies.Host == null || st.AiOpenDepth == 0 || st.Host == null)
            {
                if (watched) Unrouted(st, method, who);
                return null;
            }
            if (st.Ai == null)
            {
                st.Ai = new AiOpen
                {
                    Rec = new AiRecord(), Company = who, ProductionBefore = who.productionCountID,
                    OffersBefore = MarketApply.OfferCount()
                };
                st.Host.AiRecordOpen(GameAgencies.Wire(who));
            }
            if (st.Ai.Rec.Agency == null) st.Ai.Rec.Agency = GameAgencies.Wire(who);
            return st.Ai.Rec;
        }

        static void Leave(int state)
        {
            if (state == 0) return;
            WorldRuleState st = HumanAgencies.State.World;
            if (st.Ai != null) st.Ai.Rec.Leave();
        }

        public static bool Loading(WorldRuleState st)
        {
            return st.Loading || Manager.LoadSaveManager.OnExtractAllFromSaveData;
        }

        static void Unrouted(WorldRuleState st, string method, Company who)
        {
            if (HumanAgencies.Applying || (st.Maker != null && HumanAgencies.Host != null)) return;
            Log.Warn("command: unrouted AI change " + method + " for " + GameAgencies.Wire(who));
        }

        static AiRecord Rec { get { return HumanAgencies.State.World.Ai.Rec; } }

        static int BodyOf(ObjectInfoData d) { return d != null && d.ObjectInfo != null ? d.ObjectInfo.id : 0; }

        // ---- resources ----

        [HarmonyPatch(typeof(ObjectInfoData), "RemoveResource", new[] { typeof(ResourcePrice) })]
        [HarmonyPrefix]
        static void RemovePricePrefix(ObjectInfoData __instance, ref int __state)
        {
            __state = Enter("RemoveResource", __instance.company, false);
        }

        [HarmonyPatch(typeof(ObjectInfoData), "RemoveResource", new[] { typeof(ResourcePrice) })]
        [HarmonyFinalizer]
        static void RemovePriceFinalizer(ObjectInfoData __instance, ResourcePrice resourcePrice, bool __result,
            int __state)
        {
            if (__state == 0) return;
            if (__state == 2 && __result)
                foreach (ResourcePriceOne one in resourcePrice.ListResources)
                    Rec.Remove(BodyOf(__instance), one.ResourceDefinition.ID, one.Price);
            Leave(__state);
        }

        [HarmonyPatch(typeof(ObjectInfoData), "RemoveResource", new[] { typeof(ResourceDefinition), typeof(double) })]
        [HarmonyPrefix]
        static void RemoveOnePrefix(ObjectInfoData __instance, ref int __state)
        {
            __state = Enter("RemoveResource", __instance.company, false);
        }

        [HarmonyPatch(typeof(ObjectInfoData), "RemoveResource", new[] { typeof(ResourceDefinition), typeof(double) })]
        [HarmonyFinalizer]
        static void RemoveOneFinalizer(ObjectInfoData __instance, ResourceDefinition rd, double amount, bool __result,
            int __state)
        {
            if (__state == 0) return;
            if (__state == 2 && __result) Rec.Remove(BodyOf(__instance), rd.ID, amount);
            Leave(__state);
        }

        [HarmonyPatch(typeof(ObjectInfoData), "AddResources", new[] { typeof(ResourceDefinition), typeof(double) })]
        [HarmonyPrefix]
        static void AddResourcesPrefix(ObjectInfoData __instance, ref int __state)
        {
            __state = Enter("AddResources", __instance.company, false);
        }

        // Resources arriving after money was paid: BuyResources' purchase at the main object. Any other arrival is the
        // game's own bookkeeping inside a recorded call (or a mine's yield) and is not a step.
        [HarmonyPatch(typeof(ObjectInfoData), "AddResources", new[] { typeof(ResourceDefinition), typeof(double) })]
        [HarmonyFinalizer]
        static void AddResourcesFinalizer(ObjectInfoData __instance, ResourceDefinition rd, double amount, bool __result,
            int __state)
        {
            if (__state == 0) return;
            if (__state == 2 && __result) Rec.Bought(BodyOf(__instance), rd.ID, amount);
            Leave(__state);
        }

        [HarmonyPatch(typeof(MoneyController), "RemoveMoney", new[] { typeof(double) })]
        [HarmonyPrefix]
        static void PayPrefix(MoneyController __instance, ref int __state)
        {
            __state = Enter("RemoveMoney", CompanyOfMoney(__instance), false);
        }

        [HarmonyPatch(typeof(MoneyController), "RemoveMoney", new[] { typeof(double) })]
        [HarmonyFinalizer]
        static void PayFinalizer(double cost, bool __result, int __state)
        {
            if (__state == 0) return;
            if (__state == 2 && __result) Rec.Pay(cost);
            Leave(__state);
        }

        static Company CompanyOfMoney(MoneyController m) { return m != null ? m.GetComponent<Company>() : null; }

        // ---- builds ----

        [HarmonyPatch(typeof(ObjectInfoData), "AddRocketToConstruct")]
        [HarmonyPrefix]
        static void RocketPrefix(ObjectInfoData __instance, ref int __state)
        {
            __state = Enter("AddRocketToConstruct", __instance.company, true);
        }

        [HarmonyPatch(typeof(ObjectInfoData), "AddRocketToConstruct")]
        [HarmonyFinalizer]
        static void RocketFinalizer(ObjectInfoData __instance, SpacecraftConstructData spacecraftConstructData,
            int __state)
        {
            if (__state == 0) return;
            if (__state == 2 && spacecraftConstructData.ProductionItemType != null)
                Rec.Build(BodyOf(__instance), spacecraftConstructData.ProductionItemType.ID,
                    spacecraftConstructData.ID, 1);
            Leave(__state);
        }

        [HarmonyPatch(typeof(ObjectInfoData), "AddFacility")]
        [HarmonyPrefix]
        static void FacilityPrefix(ObjectInfoData __instance, ref int __state)
        {
            __state = Enter("AddFacility", __instance.company, false);
        }

        [HarmonyPatch(typeof(ObjectInfoData), "AddFacility")]
        [HarmonyFinalizer]
        static void FacilityFinalizer(ObjectInfoData __instance, FacilityBaseDescriptor facilityDescriptor, bool prebuilt,
            int count, Facility __result, int __state)
        {
            if (__state == 0) return;
            if (__state == 2 && __result != null && !prebuilt)
                Rec.Build(BodyOf(__instance), facilityDescriptor.ID, __result.ID, count);
            Leave(__state);
        }

        // ---- research ----

        [HarmonyPatch(typeof(ResearchManager), "StartResearch")]
        [HarmonyPrefix]
        static void StartPrefix(ResearchManager __instance, ref int __state)
        {
            __state = Enter("StartResearch", ResearchOwner(__instance), true);
        }

        [HarmonyPatch(typeof(ResearchManager), "StartResearch")]
        [HarmonyFinalizer]
        static void StartFinalizer(ResearchDefinition researchDefinition, bool __result, int __state)
        {
            if (__state == 0) return;
            if (__state == 2 && __result) Rec.Research(researchDefinition.ID);
            Leave(__state);
        }

        static readonly AccessTools.FieldRef<ResearchManager, Company> ResearchCompany =
            AccessTools.FieldRefAccess<ResearchManager, Company>("company");

        static Company ResearchOwner(ResearchManager m) { return m != null ? ResearchCompany(m) : null; }

        // ---- switching a facility (the base setter of the virtual Enabled, PC-7) ----

        [HarmonyPatch(typeof(Facility), "Enabled", MethodType.Setter)]
        [HarmonyPrefix]
        static void EnabledPrefix(Facility __instance, ref int __state)
        {
            __state = Enter("Facility.Enabled", __instance.Company, false);
        }

        [HarmonyPatch(typeof(Facility), "Enabled", MethodType.Setter)]
        [HarmonyFinalizer]
        static void EnabledFinalizer(Facility __instance, long value, int __state)
        {
            if (__state == 0) return;
            if (__state == 2 && __instance.ObjectInfoData != null)
                Rec.Enable(BodyOf(__instance.ObjectInfoData), __instance.facilityDescriptor.ID, __instance.ID, __instance.Enabled);
            Leave(__state);
        }

        // ---- offers ----

        [HarmonyPatch(typeof(MarketOfferManager), "AddOffer")]
        [HarmonyPrefix]
        static void AddOfferPrefix(Offer offer, ref int __state)
        {
            __state = Enter("AddOffer", offer.Company, true);
        }

        [HarmonyPatch(typeof(MarketOfferManager), "AddOffer")]
        [HarmonyFinalizer]
        static void AddOfferFinalizer(Offer offer, bool __result, int __state)
        {
            if (__state == 0) return;
            if (__state == 2 && __result)
                Rec.Op(new MarketOp
                {
                    Op = MarketOps.Add, Offer = MarketApply.KeyOf(offer), Count = offer.CountToBuySell,
                    Price = offer.PricePerUnit
                });
            Leave(__state);
        }

        [HarmonyPatch(typeof(MarketOfferManager), "CancelOffer")]
        [HarmonyPrefix]
        static void CancelOfferPrefix(Offer offer, ref int __state)
        {
            __state = Enter("CancelOffer", offer.Company, true);
        }

        [HarmonyPatch(typeof(MarketOfferManager), "CancelOffer")]
        [HarmonyFinalizer]
        static void CancelOfferFinalizer(Offer offer, bool __result, int __state)
        {
            if (__state == 0) return;
            if (__state == 2 && __result)
                Rec.Op(new MarketOp
                {
                    Op = MarketOps.Cancel, Offer = MarketApply.KeyOf(offer), Value = offer.OfferDone ? 1 : 0,
                    Count = offer.CountToBuySell, Price = offer.PricePerUnit
                });
            Leave(__state);
        }

        [HarmonyPatch(typeof(Offer), "FullFill")]
        [HarmonyPrefix]
        static void FillPrefix(Company CompanyTakeOffer, ref int __state)
        {
            __state = Enter("FullFill", CompanyTakeOffer, true);
        }

        [HarmonyPatch(typeof(Offer), "FullFill")]
        [HarmonyFinalizer]
        static void FillFinalizer(Offer __instance, Company CompanyTakeOffer, double count, bool __result, int __state)
        {
            if (__state == 0) return;
            if (__state == 2 && __result)
                Rec.Op(new MarketOp
                {
                    Op = MarketOps.Fill, Offer = MarketApply.KeyOf(__instance), Taker = GameAgencies.Wire(CompanyTakeOffer),
                    Count = count, Price = __instance.PricePerUnit
                });
            Leave(__state);
        }

        [HarmonyPatch(typeof(Offer), "IsBeingEvaluatedByCompanyAI", MethodType.Setter)]
        [HarmonyPrefix]
        static void LockPrefix(Company value, ref int __state)
        {
            // the lock is the rival's own mark on a shared offer; a free (null) mark has no company to attribute
            __state = value == null ? EnterForOpen() : Enter("IsBeingEvaluatedByCompanyAI", value, false);
        }

        // A release of the lock joins the open record without naming a company.
        static int EnterForOpen()
        {
            if (!HumanAgencies.Active) return 0;
            WorldRuleState st = HumanAgencies.State.World;
            if (HumanAgencies.Host == null || st.Ai == null || Loading(st)) return 0;
            return st.Ai.Rec.Enter() ? 2 : 1;
        }

        [HarmonyPatch(typeof(Offer), "IsBeingEvaluatedByCompanyAI", MethodType.Setter)]
        [HarmonyFinalizer]
        static void LockFinalizer(Offer __instance, Company value, int __state)
        {
            if (__state == 0) return;
            if (__state == 2)
                Rec.Op(new MarketOp
                {
                    Op = MarketOps.Lock, Offer = MarketApply.KeyOf(__instance), Value = value == null ? 0 : 1,
                    Taker = value == null ? null : GameAgencies.Wire(value)
                });
            Leave(__state);
        }

        // ---- an observatory order (kind 40 by a rival) ----

        [HarmonyPatch(typeof(ObservatoryFacility), "OrderExploration")]
        [HarmonyPrefix]
        static void OrderPrefix(ObservatoryFacility __instance, ref int __state)
        {
            __state = Enter("OrderExploration", __instance.Company, true);
            // the order draws the game's dice (ScheduleNextExplorationStep: the deposit and the step's days); a client
            // replays the entry's, so the host records them here as an observatory apply would
            if (__state == 2) OpenOrderDice();
        }

        static void OpenOrderDice()
        {
            ObservatoryDrawState dice = HumanAgencies.Dice;
            WorldRuleState st = HumanAgencies.State.World;
            if (dice == null) return;
            if (dice.BeginRecord()) st.Ai.OrderDice = true;
            else st.Host.HostFault("ai: an observatory order while another dice record is open");
        }

        static ObsDraw[] CloseOrderDice()
        {
            AiOpen open = HumanAgencies.State.World.Ai;
            if (open == null || !open.OrderDice) return null;
            open.OrderDice = false;
            return HumanAgencies.Dice.EndRecord();
        }

        [HarmonyPatch(typeof(ObservatoryFacility), "OrderExploration")]
        [HarmonyFinalizer]
        static void OrderFinalizer(ObservatoryFacility __instance, object target, bool __result, int __state)
        {
            if (__state == 0) return;
            ObsDraw[] draws = __state == 2 ? CloseOrderDice() : null;
            if (__state == 2 && __result)
            {
                var body = target as Game.Info.ObjectInfo;
                ActionRequest a = body == null ? null : ObservatoryWorld.NameOf(__instance, ActionKind.ObservatoryExamine);
                if (a == null) HumanAgencies.State.World.Host.HostFault("ai: an observatory order could not be named");
                else
                {
                    a.Agency = AgencyWire.From(a.Agency);
                    a.ToBody = body.id;
                    AiOpen open = HumanAgencies.State.World.Ai;
                    open.Order = a;
                    open.OrderNumbers = new ApplyNumbers
                    {
                        Draws = draws, After = ObservatoryWorld.AfterOf(__instance)
                    };
                }
            }
            Leave(__state);
        }

        // ---- the client's replay of a 94 (and the host's own is the game's) ----

        // Replays one rival action for `agency`, steps then ops, under the entry's keyed apply. null = applied; else
        // why this game is out of step. A Remove or BuyDirect that falls short is clamped within the tolerance.
        public static string Replay(string agency, AiStep[] steps, MarketOp[] ops)
        {
            Company c = GameAgencies.Find(agency);
            if (c == null) return "ai: no agency " + agency + " here";
            if (HumanAgencies.IsHuman(c)) return "ai: an action for a human agency";
            if (steps != null)
                foreach (AiStep s in steps)
                {
                    string problem = Step(c, s);
                    if (problem != null) return problem;
                }
            if (ops != null)
                foreach (MarketOp op in ops)
                {
                    string problem = MarketApply.Apply(op);
                    if (problem != null) return problem;
                }
            return null;
        }

        static string Step(Company c, AiStep s)
        {
            switch (s.Op)
            {
                case AiOps.Complete: return Complete(c, s.Item);
                case AiOps.Research:
                {
                    ResearchDefinition rd = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance
                        .AllResearchDefinition.GetByID(s.Item);
                    return rd != null && c.ResearchManager.StartResearch(rd, true, true) ? null
                        : "ai: this game could not start " + s.Item;
                }
                case AiOps.Remove: return Remove(c, s);
                case AiOps.Build: return Build(c, s);
                case AiOps.Enable: return Enable(c, s);
                case AiOps.BuyDirect: return Buy(c, s);
                case AiOps.Lock: return null;
                default: return "ai: unknown step " + s.Op;
            }
        }

        // A rival's topic just finished on the host: finish it here first, through the game's own path.
        static string Complete(Company c, string topic)
        {
            ResearchDefinition rd = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance
                .AllResearchDefinition.GetByID(topic);
            if (rd == null) return "ai: no research topic " + topic;
            if (c.ResearchManager.IsComplete(rd)) return null;
            var slot = c.ResearchManager.Slot1;
            if (slot == null || slot.ResearchDefinition != rd || slot.Complete)
                return "ai: " + topic + " is neither running nor done here";
            slot.AddProgress(rd.WorkHourToComplete);
            return null;
        }

        static ObjectInfoData Site(Company c, int body)
        {
            return GameAgencies.Site(GameAgencies.Body(body), c);
        }

        static string Remove(Company c, AiStep s)
        {
            ObjectInfoData site = Site(c, s.Body);
            ResourceDefinition rd = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance
                .AllResourceDefinitions.GetByID(s.Item);
            if (site == null || rd == null) return "ai: a removal names no site or resource here";
            RowResourcesData row = site.ListRowResourcesData.Find(r => r.ResourcesType == rd);
            double have = row == null ? 0 : row.Value, clamp;
            if (!WorldTolerance.Shortfall(4, have, s.Amount, out clamp))
                return "ai: " + s.Item + " is short here beyond the tolerance";
            if (clamp > 0) { site.AddResources(rd, clamp); Log.Info("shortfall: ai removal " + s.Item + " " + clamp); }
            return site.RemoveResource(rd, s.Amount) ? null : "ai: this game could not remove " + s.Item;
        }

        // Mirrors ProductionItemTryToBuild.OnStart: a craft is named with the company's counters and queued, a facility
        // is added unbuilt; the ids the host's entry carries are checked by the counters pass.
        static string Build(Company c, AiStep s)
        {
            ObjectInfoData site = Site(c, s.Body);
            if (site == null) return "ai: a build names no site here";
            var all = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance;
            Data.ScriptableObject.SpacecraftType ship = all.AllSpacecraftType.GetByID(s.Item);
            Data.ScriptableObject.LaunchVehicleType lv = ship == null ? all.AllLaunchVehicleType.GetByID(s.Item) : null;
            if (ship != null || lv != null)
            {
                var d = ship != null ? ship.spaceCraftConstructDefault : lv.spaceCraftConstructDefault;
                string unique = c.GetUniqueRocketName(d.SpaceCraftName);
                var item = new SpacecraftConstructData(d.SpaceCraftName, unique, d.ProductionItem, site,
                    site.company.productionCountID++);
                item.StartBuilding();
                site.AddRocketToConstruct(item);
                return item.ID == s.ItemId ? null : "ai: the craft took id " + item.ID + ", the host's was " + s.ItemId;
            }
            FacilityBaseDescriptor f = BuildingChecks.DescriptorOf(s.Item);
            if (f == null) return "ai: no build " + s.Item + " here";
            Facility made = null;
            for (int i = 0; i < (int)s.Amount; i++) made = site.AddFacility(f, false);
            return made != null && made.ID == s.ItemId ? null : "ai: the facility took another id than the host's";
        }

        static string Enable(Company c, AiStep s)
        {
            ObjectInfoData site = Site(c, s.Body);
            if (site == null) return "ai: a switch names no site here";
            foreach (Facility f in site.ListFacility)
                if (f.ID == s.ItemId && f.facilityDescriptor != null && f.facilityDescriptor.ID == s.Item)
                {
                    f.Enabled = (long)s.Amount;
                    return null;
                }
            return "ai: no facility " + s.Item + " " + s.ItemId + " here";
        }

        static string Buy(Company c, AiStep s)
        {
            ObjectInfoData site = Site(c, s.Body);
            ResourceDefinition rd = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance
                .AllResourceDefinitions.GetByID(s.Item);
            if (site == null || rd == null) return "ai: a purchase names no site or resource here";
            c.MoneyController.RemoveMoney(s.Price * s.Amount);
            return site.AddResources(rd, s.Amount) ? null : "ai: this game could not add " + s.Item;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using AiCompanyBehaviour = AI.CompanyBehaviour;
using AllScriptableObjectManager = Manager.AllScriptableObjectManager;
using Company = Game.Company;
using MarketOfferManager = Manager.MarketOfferManager;
using ObjectInfo = Game.Info.ObjectInfo;
using Offer = Game.ObjectInfoDataScripts.Offer;
using ResourceDefinition = ScriptableObjectScripts.ResourceDefinition;

namespace SolarExpanseMultiplayer
{
    // DD-W2: the market maker is the host's alone. The world government's CompanyBehaviour re-quotes every resource each
    // game year and again the day after one of its offers is filled; it draws random prices and sizes, so a client
    // running it would part from the host. In a session:
    //   * on a client its four entry points return at once (unless a MarketMaker entry, 90, is being applied);
    //   * on the host it runs as vanilla and a record around TriggerPriceChanges collects every cancel that removed an
    //     offer and every offer it added, then the prices' difference limits, and raises them as one entry.
    // Rivals answer a human agency's offers (OnNewOffer's player test reads "is a human agency", S10).
    // A no-op outside a session. The class needs a [HarmonyPatch] of its own: HarmonyX silently skips one without.
    [HarmonyPatch]
    public static class MarketRule
    {
        // the maker's entry points, in one place for the source test
        public const string Schedule = "ScheduleNextPriceChange", Yearly = "TriggerPriceChanges2",
            Prices = "TriggerPriceChanges", Filled = "OnOfferFullFill", NextDay = "NextDayOfferUpdate";

        // true = the game's own maker code runs (no session, or the host)
        static bool MakerRuns()
        {
            if (!HumanAgencies.Active) return true;
            if (HumanAgencies.Host != null) return true;           // the host runs the maker as vanilla
            if (!HumanAgencies.State.World.Wired) return true;
            return HumanAgencies.ApplyingKind == ActionKind.MarketMaker;
        }

        [HarmonyPatch(typeof(AiCompanyBehaviour), Schedule)]
        [HarmonyPrefix]
        static bool ScheduleOff() { return MakerRuns(); }

        [HarmonyPatch(typeof(AiCompanyBehaviour), Yearly)]
        [HarmonyPrefix]
        static bool YearlyOff() { return MakerRuns(); }

        [HarmonyPatch(typeof(AiCompanyBehaviour), Filled)]
        [HarmonyPrefix]
        static bool FilledOff() { return MakerRuns(); }

        [HarmonyPatch(typeof(AiCompanyBehaviour), NextDay)]
        [HarmonyPrefix]
        static bool NextDayOff() { return MakerRuns(); }

        // ---- the host's record ----

        [HarmonyPatch(typeof(AiCompanyBehaviour), Prices)]
        [HarmonyPrefix]
        static bool PricesPrefix(AiCompanyBehaviour __instance, ResourceDefinition singleResource, ref bool __state)
        {
            __state = false;
            if (!MakerRuns()) return false;
            if (!HumanAgencies.Active || HumanAgencies.Host == null) return true;
            WorldRuleState st = HumanAgencies.State.World;
            if (st.Maker != null || st.Host == null) return true;   // nested, or the host glue is not wired
            st.Maker = new MakerRecord
            {
                Value = singleResource == null ? 0 : 1, Maker = __instance != null ? __instance.Company : null,
                OffersBefore = MarketApply.OfferCount()
            };
            __state = true;
            return true;
        }

        // The record closes whether or not the maker threw: a record left open would swallow the next run's ops.
        [HarmonyPatch(typeof(AiCompanyBehaviour), Prices)]
        [HarmonyFinalizer]
        static void PricesFinalizer(bool __state, Exception __exception, ResourceDefinition singleResource)
        {
            if (!__state) return;
            WorldRuleState st = HumanAgencies.State.World;
            MakerRecord rec = st.Maker;
            st.Maker = null;
            if (rec == null || st.Host == null) return;
            try
            {
                if (__exception != null)
                {
                    st.Host.HostFault("market: the maker threw " + __exception.GetType().Name);
                    return;
                }
                AddPriceDifferences(rec, singleResource);
                long updated = rec.Value == 0
                    ? MonoBehaviourSingleton<Manager.TimeController>.Instance.CurrentTime.Ticks : 0;
                var counters = new[]
                {
                    new CounterRow { Which = CounterKind.OfferCount, Before = rec.OffersBefore,
                        After = MarketApply.OfferCount() }
                };
                st.Host.MarketMaker(rec.Ops.ToArray(), rec.Value, updated, counters);
            }
            catch (Exception e)
            {
                st.Host.HostFault("market: the maker's record failed: " + e.GetType().Name);
            }
        }

        // The limits are a field the maker writes, so they are read at the end for each resource it re-quoted.
        static void AddPriceDifferences(MakerRecord rec, ResourceDefinition single)
        {
            if (rec.Maker == null || rec.Maker.mainObjectInfo == null) return;
            ObjectInfo main = rec.Maker.mainObjectInfo;
            var all = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance.AllResourceDefinitions;
            IEnumerable<ResourceDefinition> list = single != null ? new[] { single }
                : (IEnumerable<ResourceDefinition>)all.ListResourceDefinitionInMarketPlaceOffer;
            foreach (ResourceDefinition rd in list)
                rec.Ops.Add(new MarketOp
                {
                    Op = MarketOps.PriceDiff, Body = main.id, Resource = rd.ID,
                    Price = main.GetResourceMarketData(rd).maxPriceDifferencePercentage
                });
        }

        // ---- the ops the maker makes ----

        [HarmonyPatch(typeof(MarketOfferManager), "CancelOffer")]
        [HarmonyPostfix]
        static void CancelPostfix(Offer offer, bool __result)
        {
            if (!__result || !HumanAgencies.Active) return;
            MakerRecord rec = HumanAgencies.State.World.Maker;
            if (rec == null) return;
            rec.Ops.Add(new MarketOp
            {
                Op = MarketOps.Cancel, Offer = MarketApply.KeyOf(offer), Value = offer.OfferDone ? 1 : 0,
                Count = offer.CountToBuySell, Price = offer.PricePerUnit
            });
        }

        [HarmonyPatch(typeof(MarketOfferManager), "AddOffer")]
        [HarmonyPostfix]
        static void AddPostfix(Offer offer, bool __result)
        {
            if (!__result || !HumanAgencies.Active) return;
            if (Manager.LoadSaveManager.OnExtractAllFromSaveData) return;
            MakerRecord rec = HumanAgencies.State.World.Maker;
            if (rec == null) return;
            rec.Ops.Add(new MarketOp
            {
                Op = MarketOps.Add, Offer = MarketApply.KeyOf(offer), Count = offer.CountToBuySell,
                Price = offer.PricePerUnit
            });
        }

        // ---- rivals answer every human agency's offers (S10) ----

        [HarmonyPatch(typeof(AiCompanyBehaviour), "OnNewOffer")]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> OnNewOfferTranspiler(IEnumerable<CodeInstruction> code)
        {
            return Il.Replace(code, "CompanyBehaviour.OnNewOffer", 1,
                (c, i) => i > 0 && Il.IsGetPlayer(c[i - 1]) && Il.Calls(c[i], typeof(object), "Equals", 1),
                () => new[] { new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(MarketRule), "IsPlayerOrHuman")) });
        }

        // The game's `offer.Company.Equals(Player)`; a human agency is a player too in a session.
        public static bool IsPlayerOrHuman(UnityEngine.Object company, object player)
        {
            if (HumanAgencies.Active && HumanAgencies.IsHuman(company as Company)) return true;
            return company.Equals(player);
        }
    }
}

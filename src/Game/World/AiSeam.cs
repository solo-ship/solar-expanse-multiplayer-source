using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using AiCompanyBehaviour = AI.CompanyBehaviour;
using Company = Game.Company;
using LoadSaveManager = Manager.LoadSaveManager;
using ResearchDefinition = ScriptableObjectScripts.ResearchDefinition;

namespace SolarExpanseMultiplayer
{
    // DD-W6: the rival AI runs on the host only, and every action it takes on the shared world is an entry. The seam is
    // the set of game methods a rival's tasks run inside: a prefix and a finalizer on each open and close a record
    // (AiSteps' recorders fill it; its close raises one AiAction, or the order a rival gave). The list is pinned: a
    // method that is not there (a game update renamed it, or a compiler-made state machine got another number) throws,
    // so the hook fails and the session is refused. Nothing opens a record inside the load (ExtractAllFromSaveData to
    // the end of its AfterLoadState loop): the load-time callers (EnableAI from SetupPlayerAndRivalAIs,
    // WaitForResearch.SetData, AfterSaveFixWrongAIVersion) run in the host's load and every joiner gets their result in
    // the snapshot. A rival's research completion always records a Complete step first (DD-W6 item 13).
    // A no-op outside a session. The class needs a [HarmonyPatch] of its own: HarmonyX silently skips one without.
    [HarmonyPatch]
    public static class AiSeam
    {
        // The seam methods, as Type::Method (the compiler-made names belong to this build; KnownBuilds re-checks them).
        public static readonly string[] Pinned =
        {
            "AI.Actions.ProductionItemTryToBuild::OnStart", "AI.Actions.ProductionItemTryToBuild::OnUpdate",
            "AI.Actions.QueueResearch::OnUpdate", "AI.Actions.TryLockOrUnlockFacilityForUsage::OnUpdate",
            "AI.Actions.FlightOrDelivery::OnUpdate", "AI.Actions.FulfillOffer::OnStart",
            "AI.Actions.LockOffer::OnUpdate", "AI.Actions.BuyResources::OnUpdate",
            "AI.Actions.OrderExploration::OnUpdate", "AI.CompanyBehaviour::OnResearchCompleted",
            "AI.CompanyBehaviour::EnableAI",
            "AI.Decorators.BuyFromOffersPriorityGate/<Calc>d__14::MoveNext",
            "AI.Decorators.MakeOfferPriorityGate/<InternalGetCost>d__8::MoveNext"
        };

        const string Completed = "OnResearchCompleted", Extract = "ExtractAllFromSaveData";

        [HarmonyTargetMethods]
        static IEnumerable<MethodBase> Targets()
        {
            foreach (string pin in Pinned) yield return Resolve(pin);
            yield return HookTargets.Method(typeof(LoadSaveManager), Extract);
        }

        // "Namespace.Type::Method" or "Namespace.Type/Nested::Method"; anything missing throws.
        static MethodBase Resolve(string pin)
        {
            string[] halves = pin.Split(new[] { "::" }, StringSplitOptions.None);
            string[] path = halves[0].Split(new[] { '/' });
            Type t = AccessTools.TypeByName(path[0]);
            if (t == null) throw new InvalidOperationException("the AI seam: no type " + path[0]);
            for (int i = 1; i < path.Length; i++)
            {
                t = AccessTools.Inner(t, path[i]);
                if (t == null) throw new InvalidOperationException("the AI seam: no nested type " + path[i]);
            }
            return HookTargets.Method(t, halves[1]);
        }

        [HarmonyPrefix]
        static void Open(MethodBase __originalMethod, object __instance, object[] __args, ref bool __state)
        {
            __state = false;
            if (!HumanAgencies.Active) return;
            WorldRuleState st = HumanAgencies.State.World;
            if (__originalMethod.Name == Extract) { st.Loading = true; __state = true; return; }
            if (HumanAgencies.Host == null || AiSteps.Loading(st)) return;
            st.AiOpenDepth++;
            __state = true;
            if (__originalMethod.Name == Completed) NoteCompleted(__instance as AiCompanyBehaviour, __args);
        }

        // Unconditionally closes what Open opened, even when the task threw: the world changed either way.
        [HarmonyFinalizer]
        static void Close(MethodBase __originalMethod, bool __state)
        {
            if (!__state) return;
            WorldRuleState st = HumanAgencies.State.World;
            if (__originalMethod.Name == Extract) { st.Loading = false; return; }
            if (st.AiOpenDepth > 0) st.AiOpenDepth--;
            if (st.AiOpenDepth == 0 && st.Ai != null) Finish(st);
        }

        static void NoteCompleted(AiCompanyBehaviour who, object[] args)
        {
            var topic = args != null && args.Length > 0 ? args[0] as ResearchDefinition : null;
            if (who == null || topic == null) return;
            AiRecord rec = AiSteps.Ensure(who.Company, "OnResearchCompleted", false);
            if (rec != null) rec.Complete(topic.ID);
        }

        // The record ends: nothing recorded raises nothing; steps or ops become one entry.
        static void Finish(WorldRuleState st)
        {
            AiOpen open = st.Ai;
            st.Ai = null;
            IWorldDecisions host = st.Host;
            if (host == null || open == null) return;
            AiRecordResult result = null;
            try
            {
                AiRecord rec = open.Rec;
                if (rec.Overflow)
                    host.HostFault("ai: " + rec.Agency + " did more than " + ActionLimits.MaxAiSteps + " things in one task call");
                else if (!rec.Empty || open.Order != null)
                    result = new AiRecordResult
                    {
                        Agency = rec.Agency, Steps = rec.Steps(), Ops = rec.Ops(), Counters = Counters(open, rec.Agency),
                        Order = open.Order, OrderNumbers = open.OrderNumbers
                    };
            }
            catch (Exception e)
            {
                host.HostFault("ai: the record could not be read: " + e.GetType().Name);
            }
            // a build the rival started counts from now, its entry's tick on every game (BuildStarts)
            if (result != null) BuildStartGlue.NoteNow(result.Agency);
            host.AiRecordClose(result);
        }

        static CounterRow[] Counters(AiOpen open, string agency)
        {
            var rows = new List<CounterRow>();
            long production = open.Company != null ? open.Company.productionCountID : 0;
            if (open.Company != null && production != open.ProductionBefore)
                rows.Add(new CounterRow
                {
                    Which = CounterKind.ProductionCountId, Agency = agency, Before = open.ProductionBefore, After = production
                });
            long offers = MarketApply.OfferCount();
            if (offers != open.OffersBefore)
                rows.Add(new CounterRow { Which = CounterKind.OfferCount, Before = open.OffersBefore, After = offers });
            return rows.ToArray();
        }
    }
}

using System.Collections.Generic;
using Company = Game.Company;
using ObjectInfo = Game.Info.ObjectInfo;
using ObjectInfoData = Game.ObjectInfoDataScripts.ObjectInfoData;
using ProductionQuery = Data.ScriptableObject.MyIDScriptableObjectProductionItemCountToBuild;
using ResourcePrice = Game.UI.Windows.Elements.SpaceCraftConstructElements.ResourcePrice;
using SpacecraftConstructData = Data.SpacecraftConstructData;

namespace SolarExpanseMultiplayer
{
    // P1-P4: the windows' own world calls for the acting agency, on the host (deciding the count and recording the
    // counters) and on a client (with the host's count; its counters were set to the host's "before" values first).
    // A name that no longer resolves, or a payment the game will not take, is a refusal on the host (nothing has
    // changed yet) and out of step on a client.
    public static partial class ActionApply
    {
        static ApplyOutcome ApplyProduction(ActionRequest a, ApplyNumbers given)
        {
            bool host = given == null;
            Company c = GameAgencies.Find(a.Agency);
            ObjectInfo body = GameAgencies.Body(a.Body);
            CraftPlan plan;
            int code = ProductionWorld.Decide(a, c, body, host, out plan);
            if (code != 0)
                return host ? Refused(code) : OutOfStep(a, "it does not resolve here: " + RefuseCodes.Name(code));
            ApplyOutcome done;
            switch (a.Kind)
            {
                case ActionKind.CraftBuild: done = BuildCraft(a, c, plan, host, given); break;
                case ActionKind.CraftQueue: done = QueueCraft(a, c, plan); break;
                case ActionKind.CraftCancel: done = CancelCraft(a, plan, body); break;
                default: done = ReorderCraft(a, plan); break;
            }
            if (done.Code == 0) ProductionWorld.Redraw(c, body);
            return done;
        }

        static ActionRequest WithCount(ActionRequest a, int count)
        {
            ActionRequest b = ActionCopy.Of(a);
            b.Count = count;
            return b;
        }

        // P1 (AddToConstruction, D 139601-139612): pay, name the ship (one CountSN step), take one item id, build.
        static ApplyOutcome BuildCraft(ActionRequest a, Company c, CraftPlan plan, bool host, ApplyNumbers given)
        {
            ObjectInfoData site = plan.Site;
            ResourcePrice price = ProductionWorld.Price(c, plan.Type);
            if (!site.RemoveResource(price * plan.Count)) return PaymentShortfall(host);
            string text = plan.Type.Data.SpaceCraftName;
            long nameBefore = NameCounters.Count(c, text);
            long idBefore = c.productionCountID;
            string name = c.GetUniqueRocketName(text);
            int id = c.productionCountID++;
            var build = new SpacecraftConstructData(text, name, plan.Type.Item, site, id);
            build.SetCountToBuild(plan.Count);
            build.StartBuilding();
            site.AddRocketToConstruct(build);
            if (!host && !ProductionCommand.NewIdMatches(given.NewIds, id))
                return OutOfStep(a, "it made item " + id + ", not the one the host's entry names");
            var numbers = new ApplyNumbers
            {
                Counters = new[]
                {
                    new CounterRow { Which = CounterKind.ProductionCountId, Agency = a.Agency, Before = idBefore,
                        After = c.productionCountID },
                    NameCounters.Row(a.Agency, plan.Type.Item.ID, nameBefore, NameCounters.Count(c, text))
                },
                NewIds = new long[] { id }
            };
            return Done(WithCount(a, plan.Count), host ? numbers : null, Builds(a.Agency, plan.BuildBody));
        }

        // P2 (the "queue it?" popup's OK, D 139271-139292): the entry goes on the build site's queue, unpaid.
        static ApplyOutcome QueueCraft(ActionRequest a, Company c, CraftPlan plan)
        {
            var entry = new ProductionQuery { MyIDScriptableObjectProductionItem = plan.Type.QueueItem,
                countToBuild = plan.Count };
            plan.Site.AddProductItemToQuery(entry);
            ObjectInfo parent = plan.BuildBody.parentObjectInfo;
            ObjectInfoData parentSite = GameAgencies.Site(parent, c);
            if (parentSite != null) parentSite.MarkIsDirty();
            return Done(a, null, Builds(a.Agency, plan.BuildBody));
        }

        // P3 (SpacecraftConstructData.CancelBuild, D 96527): every remaining unit refunded with the owner's own bonus
        // and the build's planned missions cancelled; a queued entry leaves the queue through its stand-in, which
        // also clears the same entry on the body's low orbit.
        static ApplyOutcome CancelCraft(ActionRequest a, CraftPlan plan, ObjectInfo body)
        {
            var queued = plan.Target as ProductionQuery;
            var touched = Builds(a.Agency, body);
            if (queued != null)
            {
                ProductionWorld.Placeholder(plan.Site, queued).CancelBuild();
                ObjectInfo low = body.LowOrbitCustom != null ? body.LowOrbitCustom.GetObjectInfo() : null;
                if (low != null) touched.Add(Print(ObjectKind.SiteBuilds, a.Agency, low.id));
            }
            else ((SpacecraftConstructData)plan.Target).CancelBuild();
            return Done(a, null, touched);
        }

        // P4 (UIRowRocket.OnDragAndDrop, D 167806-167829).
        static ApplyOutcome ReorderCraft(ActionRequest a, CraftPlan plan)
        {
            var dragged = (SpacecraftConstructData)plan.Target;
            int ignored;
            plan.Site.SetProductItemBefore(dragged, plan.Before, false, false, out ignored);
            plan.Site.PreUpdateBuilding();
            return Done(a, null, Builds(a.Agency, plan.BuildBody));
        }

        static List<PrintEntry> Builds(string agency, ObjectInfo body)
        {
            return new List<PrintEntry> { Print(ObjectKind.SiteBuilds, agency, body.id) };
        }
    }
}

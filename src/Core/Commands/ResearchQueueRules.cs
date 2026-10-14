using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // What the host decides about a research queue before it applies one (DD2). The game's own rebuild drops topics
    // silently, so every case the game would drop is either refused here or normalised away here, never left to it.
    public static class ResearchQueueRules
    {
        // Drops ONLY finished and unknown topics, keeping the order. A locked topic stays: queued after its
        // prerequisites it is what R3 itself produces, and OrderProblem judges the order.
        public static string[] Normalise(string[] wanted, Func<string, bool> known, Func<string, bool> finished)
        {
            var kept = new List<string>();
            if (wanted != null)
                foreach (string id in wanted)
                    if (!string.IsNullOrEmpty(id) && known(id) && !finished(id)) kept.Add(id);
            return kept.ToArray();
        }

        // null, or why the order is bad: a topic with an unfinished prerequisite that is not earlier in the list.
        // prereqsOf is each topic's own needs; a GetAllRDNeed-style list that also names the topic is fine.
        public static string OrderProblem(string[] ids, Func<string, string[]> prereqsOf, Func<string, bool> finished)
        {
            if (ids == null) return null;
            var before = new HashSet<string>();
            foreach (string id in ids)
            {
                string[] needs = prereqsOf(id);
                if (needs != null)
                    foreach (string p in needs)
                        if (p != id && !finished(p) && !before.Contains(p))
                            return "'" + id + "' comes before its prerequisite '" + p + "'";
                before.Add(id);
            }
            return null;
        }

        // R3: the unfinished, unqueued prerequisites in the order prereqsOf gives (the popup's), then the topic. A
        // topic appears once, and the topic is left out only when it is queued already.
        // prereqsOf is the popup's whole need list in its order (it may name the topic itself). The caller runs Problem
        // (MaxQueue) on the result. Task 8 rebuilds that list for the ACTING agency (agency.ResearchManager.
        // GetStageRecurrent, reversed, ready topics first, then the topic) and never calls GetAllRDNeed, which reads
        // GameManager.Player.
        public static string[] WithPrereqs(string topic, Func<string, string[]> prereqsOf, Func<string, bool> finished,
                                           ICollection<string> queued)
        {
            var list = new List<string>();
            string[] needs = prereqsOf(topic);
            if (needs != null)
                foreach (string p in needs)
                    if (p != topic && !finished(p) && (queued == null || !queued.Contains(p)) && !list.Contains(p))
                        list.Add(p);
            if (queued == null || !queued.Contains(topic)) list.Add(topic);
            return list.ToArray();
        }

        // null, or why the list cannot be a queue: missing ids, a repeat, or more than the cap.
        public static string Problem(string[] ids)
        {
            if (ids == null) return "no list";
            if (ids.Length > ActionLimits.MaxQueue) return "more than " + ActionLimits.MaxQueue + " topics";
            var seen = new HashSet<string>();
            foreach (string id in ids)
            {
                if (string.IsNullOrEmpty(id)) return "an empty topic id";
                if (!seen.Add(id)) return "'" + id + "' is listed twice";
            }
            return null;
        }

        // null, or why the head cannot start. The game's rebuild would drop a head that fails CheckCanStartResearch and
        // start a later one instead, which is not what R1 or the player asked for: refuse (Locked) rather than drop.
        public static string HeadProblem(string[] ids, Func<string, bool> canStart)
        {
            if (ids == null || ids.Length == 0) return null;
            return canStart(ids[0]) ? null : "'" + ids[0] + "' cannot start";
        }
    }
}

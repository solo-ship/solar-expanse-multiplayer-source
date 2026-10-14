using System;
using System.Reflection;

namespace SolarExpanseMultiplayer
{
    // The game's speed list lives in Economic, a SerializedScriptableObject that is not one of the definitions the
    // content check hashes: the speeds are (int)(baseTimeScale * multiTimeN) for N = 2..6 (plus the pause). Read by
    // field name, so Core does not reference the game's assembly. A field that is missing is an error, never a default:
    // the scan then fails and the join is refused.
    public static class EconomicTimes
    {
        public static readonly string[] Names =
            { "baseTimeScale", "multiTime2", "multiTime3", "multiTime4", "multiTime5", "multiTime6" };

        // The item the six values are hashed under, in the "Other" category (Type/id, like the definitions there).
        public const string ItemId = "Economic/times";

        // The six values in Names order.
        public static double[] Read(object economic)
        {
            if (economic == null) throw new InvalidOperationException("no Economic");
            var values = new double[Names.Length];
            for (int i = 0; i < Names.Length; i++)
            {
                FieldInfo f = Find(economic.GetType(), Names[i]);
                if (f == null) throw new InvalidOperationException("Economic has no field " + Names[i]);
                values[i] = Convert.ToDouble(f.GetValue(economic), System.Globalization.CultureInfo.InvariantCulture);
            }
            return values;
        }

        static FieldInfo Find(Type t, string name)
        {
            const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.DeclaredOnly;
            for (; t != null && t != typeof(object); t = t.BaseType)
            {
                FieldInfo f = t.GetField(name, Any);
                if (f != null) return f;
            }
            return null;
        }

        // The speeds the game's buttons set, as it computes them: (int)((float)baseTimeScale * multiTimeN), with 0
        // first.
        public static float[] Speeds(object economic)
        {
            double[] v = Read(economic);
            var speeds = new float[Names.Length];
            for (int i = 1; i < Names.Length; i++) speeds[i] = (int)((float)v[0] * (float)v[i]);
            return speeds;
        }

        // Hashes the six values as one item of the Other category.
        public static void Feed(DefinitionHasher h, object economic)
        {
            double[] v = Read(economic);
            h.BeginItem(ContentCategory.Other, ItemId);
            for (int i = 0; i < Names.Length; i++) h.Num(Names[i], v[i]);
            h.EndItem();
        }
    }
}

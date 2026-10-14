using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace SolarExpanseMultiplayer
{
    // One window click turned into a request. True = the game's own code runs (the click is not one this handler
    // captures, or does nothing in the game either); false = the click was captured or refused and the window
    // changes nothing.
    delegate bool CaptureHandler(ICaptureSeam seam, object self, object[] args);

    // Finding the game methods a capture class patches, and naming them for its handler table. A capture class lists
    // its targets in a [HarmonyTargetMethods] method and patches them all with one prefix; the prefix tells them apart
    // by Key. A target that is not there (a game update renamed it, a compiler-made lambda moved) throws, so the
    // class fails to patch and the session is refused, never half-captured. Stateless.
    static class HookTargets
    {
        const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
            | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        // The one method `name` the type declares; `args` pick an overload, and are needed when there are several.
        public static MethodBase Method(Type type, string name, params Type[] args)
        {
            MethodBase m = args != null && args.Length > 0 ? AccessTools.DeclaredMethod(type, name, args)
                : AccessTools.DeclaredMethod(type, name);
            if (m == null) throw new InvalidOperationException(type.Name + "." + name + " was not found");
            return m;
        }

        // The overload with no parameters.
        public static MethodBase NoArgs(Type type, string name)
        {
            MethodBase m = AccessTools.DeclaredMethod(type, name, Type.EmptyTypes);
            if (m == null) throw new InvalidOperationException(type.Name + "." + name + "() was not found");
            return m;
        }

        // A compiler-made lambda: the closure class nested in `outer` (named as the compiler names it) and its method.
        public static MethodBase Lambda(Type outer, string closure, string name)
        {
            Type nested = AccessTools.Inner(outer, closure);
            if (nested == null) throw new InvalidOperationException(outer.Name + "/" + closure + " was not found");
            foreach (MethodInfo m in nested.GetMethods(Declared))
                if (m.Name == name) return m;
            throw new InvalidOperationException(outer.Name + "/" + closure + "." + name + " was not found");
        }

        // The key a handler table uses: "Type.Method", or "Outer/Closure.Method" for a lambda.
        public static string Key(MethodBase m)
        {
            Type t = m.DeclaringType;
            if (t.DeclaringType != null) return t.DeclaringType.Name + "/" + t.Name + "." + m.Name;
            return t.Name + "." + m.Name;
        }

        public static T Field<T>(object owner, string name)
        {
            FieldInfo f = AccessTools.Field(owner.GetType(), name);
            if (f == null) throw new InvalidOperationException(owner.GetType().Name + "." + name + " was not found");
            return (T)f.GetValue(owner);
        }

        public static T Property<T>(object owner, string name)
        {
            PropertyInfo p = AccessTools.Property(owner.GetType(), name);
            if (p == null) throw new InvalidOperationException(owner.GetType().Name + "." + name + " was not found");
            return (T)p.GetValue(owner, null);
        }
    }

    // A capture class's prefix hands every call here. A handler that throws, or a method the table does not know,
    // leaves the click refused with the note: a window the capture could not read must not fall through to a local
    // change.
    static class CaptureRun
    {
        public static bool Run(Dictionary<string, CaptureHandler> table, ICaptureSeam seam, MethodBase method,
            object self, object[] args)
        {
            string key = HookTargets.Key(method);
            CaptureHandler handler;
            if (!table.TryGetValue(key, out handler))
            {
                // every method a capture class patches has a handler: one that has none is a typo in the table, and
                // the click must not fall through to a local change
                Log.Error("command: no capture handler for " + key);
                WindowCapture.CannotSend(seam);
                return false;
            }
            try { return handler(seam, self, args); }
            catch (Exception e)
            {
                Log.Error("command: the capture at " + key + " failed: " + e.GetType().Name + ": "
                    + TextSanitiser.OneLine(e.Message));
                WindowCapture.CannotSend(seam);
                return false;
            }
        }
    }
}

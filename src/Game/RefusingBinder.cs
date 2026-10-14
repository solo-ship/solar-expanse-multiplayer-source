using System;
using Sirenix.Serialization;

namespace SolarExpanseMultiplayer
{
    // The binder of the joiner's strict deserialise. The decision is Core's TypeBinding (the policy first, one bind of
    // the whole name by Odin's default binder, then the bound type's name must equal it); this class only plugs it into
    // Odin and turns a refusal into an abort. A refusal never just returns null: Odin would build the node as the
    // slot's declared type and carry on without a word. It throws, and Refused stays set, so the caller refuses the
    // snapshot even if something swallowed the exception. One instance per deserialise, on the main thread.
    public sealed class RefusingBinder : TwoWaySerializationBinder
    {
        // No debug context for the default binder: a name it cannot bind comes back as null and is refused (and named)
        // by TypeBinding, instead of a warning thrown from inside it.
        readonly TypeBinding rule =
            new TypeBinding(name => Default.BindToType(name, null), t => Default.BindToName(t, null));

        public bool Refused { get { return rule.Refused; } }
        public string FirstRefused { get { return rule.FirstRefused; } }
        public string Why { get { return rule.Why; } }
        public int Calls { get { return rule.Calls; } }

        public override Type BindToType(string typeName, DebugContext debugContext = null)
        {
            Type t = rule.Bind(typeName);
            if (t == null) throw new SerializationAbortException("the snapshot names a type that is not allowed");
            return t;
        }

        // Only used when serialising, which the join never does with this binder.
        public override string BindToName(Type type, DebugContext debugContext = null)
        {
            return Default.BindToName(type, debugContext);
        }

        public override bool ContainsType(string typeName) { return TypeBinding.Contains(typeName); }
    }
}

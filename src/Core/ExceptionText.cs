using System;
using System.Text;

namespace SolarExpanseMultiplayer
{
    // An exception as one log line: every exception of the chain (a TypeInitializationException's cause is its inner
    // one) with its message, then the innermost one's stack, frames joined by " | ", the whole capped at max
    // characters. A throw that puts a game out of step is logged with this, so it is never a bare type name again.
    public static class ExceptionText
    {
        public const int DefaultMax = 3000;

        public static string Chain(Exception e, int max = DefaultMax)
        {
            if (e == null) return "no exception";
            var sb = new StringBuilder();
            Exception inner = e;
            int depth = 0;
            for (Exception x = e; x != null && depth < 8; x = x.InnerException, depth++)
            {
                if (sb.Length > 0) sb.Append(" <- ");
                sb.Append(x.GetType().FullName).Append(": ").Append(OneLine(x.Message));
                inner = x;
            }
            Frames(sb, " || at ", inner.StackTrace, int.MaxValue, max);
            // where the outer one surfaced (the call that ran a failing static initialiser, say)
            if (!ReferenceEquals(inner, e)) Frames(sb, " || reached through ", e.StackTrace, 3, max);
            if (max < 20) max = 20;
            return sb.Length <= max ? sb.ToString() : sb.ToString(0, max - 3) + "...";
        }

        static void Frames(StringBuilder sb, string head, string stack, int most, int max)
        {
            if (string.IsNullOrEmpty(stack) || sb.Length > max) return;
            sb.Append(head);
            int n = 0;
            foreach (string line in stack.Split('\n'))
            {
                string f = line.Trim();
                if (f.StartsWith("at ", StringComparison.Ordinal)) f = f.Substring(3);
                if (f.Length == 0) continue;
                if (n > 0) sb.Append(" | ");
                sb.Append(f);
                if (++n >= most || sb.Length > max) break;
            }
        }

        static string OneLine(string s)
        {
            if (s == null) return "";
            return s.Replace('\r', ' ').Replace('\n', ' ');
        }
    }
}

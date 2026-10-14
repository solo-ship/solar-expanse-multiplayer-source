using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace SolarExpanseMultiplayer
{
    // A widget's rectangle in client pixels of the game window: x and y from the top-left corner, w and h > 0.
    public struct UiRect
    {
        public float X, Y, W, H;
        public UiRect(float x, float y, float w, float h) { X = x; Y = y; W = w; H = h; }
    }

    // Widget ids -> screen rectangles, written for the UI test tools (tools\ui-click.ps1 clicks a widget's centre).
    // Only a test seam writes it: TestSeams.Open, or a valid test profile on the player-path profile. In every other
    // case Register stores nothing and no file is written (fail closed), so a player's game never has a map.
    // The file is JSON:
    //   {"width":1280,"height":720,"widgets":{"menu.multiplayer":{"x":10,"y":20,"w":100,"h":30}}}
    // width and height are the window's client size when the map was written, so a tool can scale to the current one.
    // A widget whose centre lies outside that window is left out of the file.
    public sealed class UiMap
    {
        public const int MaxWidgets = 512;
        public const string FileName = "ui-map.json";
        static readonly Regex IdChars = new Regex(@"^[a-z0-9_.]{1,96}\z");

        readonly bool writes;
        readonly Dictionary<string, UiRect> widgets = new Dictionary<string, UiRect>(StringComparer.Ordinal);
        public int Width, Height;               // the window's client size

        // Only For and the tests make one: nothing else may decide to write.
        internal UiMap(bool writes) { this.writes = writes; }

        // The map a game uses: writable only when MayWrite says so, so no caller repeats that decision.
        public static UiMap For(string profile, bool playerPath) { return new UiMap(MayWrite(profile, playerPath)); }

        // Writing is allowed for a test profile (TestSeams.Open) and for the player-path profile with a valid name.
        public static bool MayWrite(string profile, bool playerPath)
        {
            return TestSeams.Open(profile, playerPath) || (playerPath && LaunchArgs.SafeName(profile));
        }

        public static bool ValidId(string id) { return id != null && IdChars.IsMatch(id); }

        public bool Writes { get { return writes; } }
        public int Count { get { return widgets.Count; } }

        public void SetWindow(int width, int height) { if (writes) { Width = width; Height = height; } }

        // Stores the rectangle (replacing an earlier one of the same id). False, and nothing stored, when the map
        // may not write, the id is not a plain name, the rectangle is empty or not finite, or the map is full.
        public bool Register(string id, UiRect r)
        {
            if (!writes || !ValidId(id) || !Finite(r)) return false;
            if (!(r.W > 0) || !(r.H > 0)) return false;
            if (!widgets.ContainsKey(id) && widgets.Count >= MaxWidgets) return false;
            widgets[id] = r;
            return true;
        }

        public bool Remove(string id) { return id != null && widgets.Remove(id); }

        // Removes every widget whose id starts with the prefix (a screen going away); returns how many.
        public int RemovePrefix(string prefix)
        {
            var gone = new List<string>();
            foreach (string k in widgets.Keys) if (k.StartsWith(prefix, StringComparison.Ordinal)) gone.Add(k);
            foreach (string k in gone) widgets.Remove(k);
            return gone.Count;
        }

        public bool TryGet(string id, out UiRect r)
        {
            r = new UiRect();
            return id != null && widgets.TryGetValue(id, out r);
        }

        // The point a click on the widget lands on: the middle of its rectangle.
        public bool TryCentre(string id, out float x, out float y)
        {
            UiRect r; x = 0; y = 0;
            if (!TryGet(id, out r)) return false;
            x = r.X + r.W / 2f; y = r.Y + r.H / 2f;
            return true;
        }

        // Whether a click on the widget's centre would land inside the window. With no window size set every widget
        // counts. A rectangle the game leaves behind off screen (a menu on its way out) must not reach the file:
        // a tool would scale it and aim at a point nothing is under.
        bool OnScreen(UiRect r)
        {
            if (Width <= 0 || Height <= 0) return true;
            float cx = r.X + r.W / 2f, cy = r.Y + r.H / 2f;
            return cx >= 0 && cy >= 0 && cx < Width && cy < Height;
        }

        static bool Finite(UiRect r)
        {
            return IsNum(r.X) && IsNum(r.Y) && IsNum(r.W) && IsNum(r.H);
        }

        static bool IsNum(float f) { return !float.IsNaN(f) && !float.IsInfinity(f); }

        public string ToJson()
        {
            var ids = new List<string>();
            foreach (KeyValuePair<string, UiRect> kv in widgets) if (OnScreen(kv.Value)) ids.Add(kv.Key);
            ids.Sort(StringComparer.Ordinal);
            var sb = new StringBuilder();
            sb.Append("{\"width\":").Append(Width.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"height\":").Append(Height.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"widgets\":{");
            for (int i = 0; i < ids.Count; i++)
            {
                UiRect r = widgets[ids[i]];
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(ids[i]).Append("\":{\"x\":").Append(Num(r.X)).Append(",\"y\":").Append(Num(r.Y));
                sb.Append(",\"w\":").Append(Num(r.W)).Append(",\"h\":").Append(Num(r.H)).Append('}');
            }
            return sb.Append("}}").ToString();
        }

        static string Num(float f) { return f.ToString("0.##", CultureInfo.InvariantCulture); }

        // Writes the map through a temp file so the tool never reads half of it. Does nothing (false) when the map may
        // not write. Throws what the file system throws; the caller logs it.
        public bool Save(string path)
        {
            if (!writes) return false;
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, ToJson(), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
            return true;
        }
    }
}

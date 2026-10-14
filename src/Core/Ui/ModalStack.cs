using System;
using System.Collections.Generic;

namespace SolarExpanseMultiplayer
{
    // The mod's open dialogs (DD28). Top is the one drawn on top: the highest rank (the layer it is drawn in,
    // UiLayers), the newest within a rank. A dialog counts only while isOpen says so (alive and shown), so one
    // destroyed with its scene, or hidden without being closed, never leaves the game thinking a pop-up is up (that
    // would swallow Esc and every hotkey for good). Main thread only, like the UI.
    public sealed class ModalStack<T> where T : class
    {
        readonly List<T> open = new List<T>();
        readonly Func<T, bool> isOpen;
        readonly Func<T, int> rank;

        // rank: the layer a dialog is drawn in, higher over lower; null = one layer (the newest on top).
        public ModalStack(Func<T, bool> isOpen, Func<T, int> rank = null)
        {
            if (isOpen == null) throw new ArgumentNullException("isOpen");
            this.isOpen = isOpen;
            this.rank = rank;
        }

        // Opening one that is already open moves it to the top.
        public void Open(T dialog)
        {
            if (dialog == null) return;
            open.Remove(dialog);
            open.Add(dialog);
        }

        public void Close(T dialog)
        {
            if (dialog != null) open.Remove(dialog);
        }

        public bool AnyOpen { get { Prune(); return open.Count > 0; } }

        // The dialog Esc belongs to (the one drawn on top); null when none is open.
        public T Top
        {
            get
            {
                Prune();
                T top = null;
                int best = 0;
                // newest first: one of the same rank opened earlier never takes the top from it
                for (int i = open.Count - 1; i >= 0; i--)
                {
                    int r = Rank(open[i]);
                    if (top == null || r > best) { top = open[i]; best = r; }
                }
                return top;
            }
        }

        // A rank that cannot be read is the lowest: such a dialog never takes Esc from one drawn over it.
        int Rank(T dialog)
        {
            if (rank == null) return 0;
            try { return rank(dialog); }
            catch (Exception) { return int.MinValue; }
        }

        public int Count { get { Prune(); return open.Count; } }

        public void Clear() { open.Clear(); }

        void Prune()
        {
            for (int i = open.Count - 1; i >= 0; i--)
            {
                bool keep;
                try { keep = isOpen(open[i]); }
                catch (Exception) { keep = false; }   // a dialog that cannot answer is not holding the game
                if (!keep) open.RemoveAt(i);
            }
        }
    }
}

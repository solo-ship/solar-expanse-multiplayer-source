using System;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SolarExpanseMultiplayer
{
    // The screens' one mod canvas per scene and the scene's one parts set, owned by MenuSessions (no static state) and
    // handed to every screen. Each screen draws on a layer of it (UiLayers: the HUD and the notices lowest, then the
    // screens, then the session's dialogs on top). A layer is a nested canvas with its own sorting order and
    // raycaster, so drawing and clicks follow the layers whatever was built first; ModalRegistry ranks an open dialog
    // by the same sorting order. Both belong to their scene: a world change forgets them, and the next screen to ask
    // finds the new scene's. Main thread only.
    public sealed class ScreenCanvas
    {
        sealed class SceneUi
        {
            public GameParts Parts;
            public Canvas Root;
            public readonly Canvas[] Layers = new Canvas[UiLayers.BottomToTop.Length];
        }

        readonly SceneUi menu = new SceneUi(), game = new SceneUi();

        // A save loaded or the menu came back (MenuSessions, after every screen's own reset): the old scene's canvas
        // and parts went with it; one still there is destroyed, so nothing of the old world stays drawn.
        public void Forget()
        {
            Drop(menu);
            Drop(game);
        }

        static void Drop(SceneUi s)
        {
            Canvas root = s.Root;
            s.Root = null;
            s.Parts = null;
            for (int i = 0; i < s.Layers.Length; i++) s.Layers[i] = null;
            try { if (root != null) Object.Destroy(root.gameObject); }
            catch (Exception e) { Log.Warn("ui: the old screens' canvas could not be removed: " + e.Message); }
        }

        // This scene's parts, found once. A set with a part missing is not kept: a screen that tries again later (the
        // scene may not have been ready) looks again, and each screen still refuses on Missing.
        public GameParts Parts(bool inGame)
        {
            SceneUi s = inGame ? game : menu;
            if (s.Parts != null && s.Parts.GameCanvas != null) return s.Parts;
            GameParts p = inGame ? GameParts.InGame() : GameParts.InMenu();
            s.Parts = p.Missing == null ? p : null;
            return p;
        }

        // One layer of this scene's mod canvas, the canvas and its three layers made together on first use (so their
        // order as siblings is the layers' order too); null when the scene's parts are missing.
        public Canvas Layer(bool inGame, UiLayer layer)
        {
            SceneUi s = inGame ? game : menu;
            if (s.Root == null)
            {
                GameParts p = Parts(inGame);
                if (p.Missing != null) return null;
                Canvas root = UiWidgets.ModCanvas(p.GameCanvas, inGame ? "game screens" : "menu screens");
                if (root == null) return null;
                foreach (UiLayer l in UiLayers.BottomToTop) s.Layers[(int)l] = MakeLayer(root, l);
                s.Root = root;
            }
            return s.Layers[(int)layer];
        }

        static Canvas MakeLayer(Canvas root, UiLayer layer)
        {
            RectTransform rt = UiWidgets.Box(root.transform, "layer " + layer);
            UiWidgets.Stretch(rt);
            var c = rt.gameObject.AddComponent<Canvas>();
            c.overrideSorting = true;
            c.sortingLayerID = root.sortingLayerID;
            c.sortingOrder = UiLayers.Order(root.sortingOrder, layer);
            rt.gameObject.AddComponent<GraphicRaycaster>();   // a nested canvas's graphics are hit by its own raycaster
            // the screenshot review checks no game canvas draws between the layers
            Log.Info("ui: canvas layer " + layer + " of '" + root.name + "' (order " + c.sortingOrder + ")");
            return c;
        }
    }
}

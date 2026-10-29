using System;
using Il2Cpp;
using Il2CppTMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SpellBrigadeMods.CrabLand
{
    /// <summary>A fifth button in the world panel, cloned from the base world's button. Picking it selects the
    /// base world natively and turns the Crab Land flag on.</summary>
    internal static class Menu
    {
        private const string ButtonName = "CrabLandWorldButton";
        public const string DisplayName = "Crab Land";
        private static readonly Color Sand = new Color(1f, 0.72f, 0.45f, 1f);

        private static WorldsPanel _panel;
        private static WorldSelectionButton _button;
        private static bool _clicking;
        private static IntPtr _wiredButton;
        private static int _lastClickFrame = -1;

        // The panel drives native buttons through its own subscriptions, not OnClickedAction, so the clone listens
        // on both its click event and the UI Button; one click may raise both, hence the per-frame guard.
        private static void Wire(WorldSelectionButton b)
        {
            if (b == null || _wiredButton == b.Pointer) return;
            _wiredButton = b.Pointer;
            b.OnClickedAction = new Action(() => Click("OnClickedAction"));
            b.OnHighlightAction = new Action(ShowName);
            b.add_OnClicked(new Action(() => Click("OnClicked")));
            b.add_OnHighlight(new Action(ShowName));
            if (b.Button != null) b.Button.onClick.AddListener(new Action(() => Click("Button.onClick")));
            CrabLandMod.Log.Msg($"menu: wired clone button={b.Button != null} interactable={(b.Button != null && b.Button.interactable)}");
        }

        private static void Click(string via)
        {
            CrabLandMod.Log.Msg($"menu: clone click via {via} frame={Time.frameCount}");
            if (Time.frameCount == _lastClickFrame) return;
            _lastClickFrame = Time.frameCount;
            OnCrabLandClicked();
        }

        public static void InitializeButtonsPostfix(WorldsPanel __instance)
        {
            try
            {
                _panel = __instance;
                var buttons = __instance.worldButtons;
                var n = buttons?.Count ?? 0;
                WorldSelectionButton source = null;
                var names = new System.Text.StringBuilder();
                for (var i = 0; i < n; i++)
                {
                    var b = buttons[i];
                    names.Append($"[{i}] world={b.World} go={b.gameObject.name} ");
                    if (b.World == CrabLandMod.BaseWorld && source == null) source = b;
                }
                if (source == null) { CrabLandMod.Log.Error($"menu: no base-world button among {n}: {names}"); return; }

                var parent = source.transform.parent;
                var existing = parent.Find(ButtonName);
                if (existing != null) _button = existing.GetComponent<WorldSelectionButton>();
                else
                {
                    var go = Object.Instantiate(source.gameObject, parent);
                    go.name = ButtonName;
                    go.transform.SetAsLastSibling();
                    _button = go.GetComponent<WorldSelectionButton>();
                    foreach (var img in go.GetComponentsInChildren<Image>(true)) img.color *= Sand;
                    AddLabel(go);
                }
                Wire(_button);
                CrabLandMod.Log.Msg($"menu: world buttons {n}: {names}+ go={_button.gameObject.name} name=\"{DisplayName}\" parent={parent.name}");
            }
            catch (Exception e) { CrabLandMod.Log.Error($"menu: adding Crab Land failed: {e}"); }
        }

        // Copy the panel's world-name text onto the button, minus its localizer (it would overwrite the text).
        private static void AddLabel(GameObject button)
        {
            var src = _panel.worldNameText;
            if (src == null) { CrabLandMod.Log.Warning("menu: no worldNameText to copy for the label"); return; }
            var label = Object.Instantiate(src.gameObject, button.transform);
            label.name = "CrabLandLabel";
            foreach (var c in label.GetComponents<Component>())
                if (c.GetIl2CppType().Name == "LocalizeStringEvent") Object.DestroyImmediate(c);
            var rt = label.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 0f); rt.anchorMax = new Vector2(1f, 0f);
            rt.pivot = new Vector2(0.5f, 1f); rt.anchoredPosition = new Vector2(0f, -4f);
            var tmp = label.GetComponent<TMP_Text>();
            tmp.text = DisplayName;
            tmp.alignment = TextAlignmentOptions.Center;
        }

        private static void ShowName()
        {
            if (_panel?.worldNameText != null) _panel.worldNameText.text = DisplayName;
        }

        private static void OnCrabLandClicked()
        {
            var nm = NetworkManager.Singleton;
            if (nm != null && nm.IsListening && !nm.IsServer)
            {
                CrabLandMod.Log.Msg("menu: only the host picks Crab Land");
                return;
            }
            _clicking = true;
            try { _panel.OnWorldButtonClicked(CrabLandMod.BaseWorld); }
            finally { _clicking = false; }
            CrabLandMod.Active = true;
            ShowName();
            CrabLandMod.Log.Msg("menu: Crab Land picked");
        }

        // A native world pick turns Crab Land off; our own click passes through here first.
        public static void OnWorldButtonClickedPrefix(World world)
        {
            if (_clicking || Time.frameCount == _lastClickFrame) return;   // the clone may also fire a copied native listener
            CrabLandMod.Log.Msg($"menu: native pick world={world}");
            CrabLandMod.Active = false;
        }

        /// <summary>Debug path: the same handler the button runs. False until the panel has built its buttons
        /// (it does so on its own a few seconds after the lobby loads; forcing Show() earlier throws in the game).</summary>
        public static bool ClickCrabLandButton()
        {
            if (_button == null)
            {
                var panels = Resources.FindObjectsOfTypeAll<WorldsPanel>();
                CrabLandMod.Log.Msg($"menu: WorldsPanel instances={panels.Length}; building its buttons");
                if (panels.Length > 0)
                {
                    try { panels[0].Initialize(); }
                    catch (Exception e) { CrabLandMod.Log.Warning($"menu: WorldsPanel.Initialize failed: {e.Message}"); }
                }
            }
            if (_button == null) return false;
            Click("autostart");
            return true;
        }

        /// <summary>Tint the lobby's world icon while Crab Land is on (host and mod clients).</summary>
        public static void RefreshLobbyIndicator()
        {
            try
            {
                foreach (var d in Object.FindObjectsOfType<WorldSelectionDisplayer>())
                    if (d.iconImage != null) d.iconImage.color = CrabLandMod.Active ? Sand : Color.white;
            }
            catch (Exception e) { CrabLandMod.Log.Warning($"menu: lobby indicator: {e.Message}"); }
        }
    }
}

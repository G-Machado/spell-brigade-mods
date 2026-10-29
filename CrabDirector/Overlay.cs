using System;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;

namespace SpellBrigadeMods.CrabDirector
{
    /// <summary>
    /// In-game toggle (docs/director.md): a corner panel (IMGUI) with an On/Off button and the director's
    /// state, plus hotkeys. ToggleKey flips Enabled and saves prefs; PanelKey hides the panel. Host only.
    /// Keys are read through the Input System (the game's own), with legacy Input as fallback.
    /// </summary>
    internal static class Overlay
    {
        internal static MelonPreferences_Entry<bool> ShowPanel;
        internal static MelonPreferences_Entry<string> ToggleKey, PanelKey;

        private static readonly Rect Box = new Rect(10f, 10f, 240f, 160f);
        private static string _keySource;
        private static bool _guiFailed;
        private static readonly HashSet<string> Warned = new HashSet<string>();

        public static void Init(MelonPreferences_Category cat)
        {
            ShowPanel = cat.CreateEntry("ShowPanel", true, description: "Show the Crab Director corner panel");
            ToggleKey = cat.CreateEntry("ToggleKey", "F8", description: "Key that turns the director on/off (host only); a UnityEngine.InputSystem.Key name");
            PanelKey = cat.CreateEntry("PanelKey", "F9", description: "Key that shows/hides the panel");
        }

        public static void Update()
        {
            if (Pressed(ToggleKey.Value)) Toggle("key");
            if (Pressed(PanelKey.Value))
            {
                ShowPanel.Value = !ShowPanel.Value;
                MelonPreferences.Save();
            }
        }

        /// <summary>Flip Enabled, save, log. Clients can't: the director only runs on the host.</summary>
        public static void Toggle(string source)
        {
            var host = CrabDirectorMod.IsHostOrSolo();
            if (host)
            {
                CrabDirectorMod.Enabled.Value = !CrabDirectorMod.Enabled.Value;
                MelonPreferences.Save();
            }
            CrabDirectorMod.Log.Msg($"toggle source={source} host={host} enabled={CrabDirectorMod.Enabled.Value} running={CrabDirectorMod.Running}");
            JsonLog.Write(new Dictionary<string, object>
            {
                ["ev"] = "toggle", ["t"] = Time.time, ["source"] = source, ["host"] = host,
                ["enabled"] = CrabDirectorMod.Enabled.Value, ["runningBefore"] = CrabDirectorMod.Running,
                ["directorStarted"] = Census.DirectorStarted, ["directorLive"] = Census.DirectorTotal,
            });
        }

        public static void Draw()
        {
            if (_guiFailed || !ShowPanel.Value) return;
            try
            {
                var host = CrabDirectorMod.IsHostOrSolo();
                GUI.Box(Box, "Crab Director");
                var on = CrabDirectorMod.Enabled.Value;
                var label = host ? (on ? "On  (click: off)" : "Off  (click: on)") : "host only";
                if (GUI.Button(new Rect(Box.x + 10f, Box.y + 24f, Box.width - 20f, 22f), label) && host) Toggle("button");

                string state;
                if (!host) state = "director runs on the host";
                else if (CrabDirectorMod.Running)
                    state = $"{Director.Core.Phase}  I={Director.Core.IParty:F2}  {(CrabDirectorMod.CrabLandActive() ? "crab rules" : "pacing")}\nlive {Census.DirectorTotal}  spawned {Census.DirectorStarted}";
                else state = $"idle: {CrabDirectorMod.IdleReasonText ?? "-"}";
                GUI.Label(new Rect(Box.x + 10f, Box.y + 50f, Box.width - 20f, 40f), state);
                if (host && CrabDirectorMod.Running) GUI.Label(new Rect(Box.x + 10f, Box.y + 90f, Box.width - 20f, 48f), IntentText());
                GUI.Label(new Rect(Box.x + 10f, Box.y + 138f, Box.width - 20f, 20f), $"{ToggleKey.Value} on/off   {PanelKey.Value} hide");
            }
            catch (Exception e)
            {
                // Fail soft: no panel, the hotkey still works.
                _guiFailed = true;
                CrabDirectorMod.Log.Error($"panel: IMGUI failed, hotkeys only: {e.GetType().Name}: {e.Message}");
            }
        }

        /// <summary>The wave director's current intent: theme, source, age, reason.</summary>
        private static string IntentText()
        {
            var i = Director.Core.Intent;
            var age = WaveLink.AppliedAt >= 0 ? $"{Time.time - WaveLink.AppliedAt:F0}s" : "-";
            var reason = string.IsNullOrEmpty(i.Reason) ? "" : "\n" + (i.Reason.Length > 60 ? i.Reason.Substring(0, 57) + "..." : i.Reason);
            return $"intent: {i.Theme ?? "-"} ({i.Source}, {age})\nbudget x{i.BudgetMul:F2}  relax x{i.RelaxMul:F2}{reason}";
        }

        private static bool Pressed(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            if (_keySource != "legacy")
            {
                try
                {
                    var kb = UnityEngine.InputSystem.Keyboard.current;
                    if (kb != null && Enum.TryParse<UnityEngine.InputSystem.Key>(key, true, out var k))
                    {
                        Report("inputsystem");
                        return kb[k].wasPressedThisFrame;
                    }
                    if (kb != null) { Warn($"key {key}: not a UnityEngine.InputSystem.Key"); return false; }
                }
                catch (Exception e) { Warn($"input system failed, trying legacy Input: {e.Message}"); _keySource = "legacy"; }
            }
            try
            {
                if (!Enum.TryParse<KeyCode>(key, true, out var code)) { Warn($"key {key}: not a KeyCode"); return false; }
                Report("legacy");
                return Input.GetKeyDown(code);
            }
            catch (Exception e) { Warn($"legacy Input failed too, no hotkeys: {e.Message}"); return false; }
        }

        private static void Report(string source)
        {
            if (_keySource == source) return;
            _keySource = source;
            CrabDirectorMod.Log.Msg($"keys: reading through {source}");
        }

        private static void Warn(string msg)
        {
            if (Warned.Add(msg)) CrabDirectorMod.Log.Warning(msg);
        }
    }
}

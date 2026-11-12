using OverTheCounter.Utilities;
using System;
using UnityEngine;

#if IL2CPP
using Il2CppEPOOutline;
using Il2CppScheduleOne;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.EntityFramework;
using Il2CppScheduleOne.Management;
using Il2CppScheduleOne.ObjectScripts;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.Tools;
using Il2CppScheduleOne.UI;
#else
using EPOOutline;
using ScheduleOne;
using ScheduleOne.DevUtilities;
using ScheduleOne.Economy;
using ScheduleOne.EntityFramework;
using ScheduleOne.Management;
using ScheduleOne.ObjectScripts;
using ScheduleOne.PlayerScripts;
using ScheduleOne.Tools;
using ScheduleOne.UI;
#endif

namespace OverTheCounter.UI
{
    /// <summary>
    /// Custom raycast selector for route endpoints. Detects both PlaceableStorageEntity
    /// and DeadDrop when the player aims at objects in the world — the game's built-in
    /// ObjectSelector only works with BuildableItem (which dead drops are not).
    /// </summary>
    public static class RouteEntitySelector
    {
        private static bool _isOpen;
        private static Action<PlaceableStorageEntity, DeadDrop> _callback;

        // Hover tracking
        private static PlaceableStorageEntity _highlightedPSE;
        private static DeadDrop _highlightedDD;
        private static Outlinable _ddOutlineEffect;

        // The vanilla ObjectSelector's key hints, shown while this selector is open
        private static ScheduleOne.UI.Input.InputPromptsData _prompts;

        // Cached so the same instance can be deregistered (IL2CPP wraps a new delegate per conversion)
        private static GameInput.ExitDelegate _exitListener;

        private static readonly Color HoverOutlineColor = Color.white;
        private const float RaycastRange = 5f;

        /// <summary>Exit listener priority; matches the vanilla ObjectSelector so it runs before the phone and apps.</summary>
        private const int ExitPriority = 12;

        public static bool IsOpen => _isOpen;

        /// <summary>
        /// Opens the selector. Player looks at objects and clicks to select.
        /// Callback receives (PSE, DeadDrop) — exactly one non-null, or both null on cancel.
        /// </summary>
        public static void Open(string instruction, Action<PlaceableStorageEntity, DeadDrop> callback)
        {
            if (_isOpen) Close(false);

            _isOpen = true;
            _callback = callback;
            _highlightedPSE = null;
            HideDeadDropOutline();

            try
            {
                // Close the clipboard while selecting (same as ObjectSelector does)
                Singleton<ManagementClipboard>.Instance?.CloseAndPreserveState();

                // Show instruction at top of screen
                Singleton<HUD>.Instance?.ShowTopScreenText(instruction);

                // Show the vanilla object selector's key hints
                _prompts = Singleton<ManagementInterface>.Instance?.ObjectSelector?.State?.GetDefaultInputPrompts();
                if (_prompts != null)
                    Singleton<ScheduleOne.UI.Input.InputPromptsManager>.Instance?.LoadModule(_prompts, _prompts.Position);
                else
                    OTCLog.Warning(OTCLog.Systems.Manager, "RouteEntitySelector: object selector has no input prompts, hints not shown");
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Manager, $"RouteEntitySelector Open UI setup failed: {ex.Message}");
            }

            // Escape and right-click cancel, through the game's exit listeners
            try
            {
#if IL2CPP
                _exitListener ??= (Action<ExitAction>)OnExit;
#else
                _exitListener ??= OnExit;
#endif
                GameInput.RegisterExitListener(_exitListener, ExitPriority);
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Manager, $"RouteEntitySelector exit listener registration failed: {ex.Message}");
            }
        }

        private static void OnExit(ExitAction action)
        {
            if (!_isOpen || action.Used) return;

            action.Used = true;
            Close(false);
        }

        /// <summary>
        /// Must be called every frame from EnforceUI or a patch while the selector is open.
        /// </summary>
        public static void Tick()
        {
            if (!_isOpen) return;

            try
            {
                // Raycast from player camera
                PlaceableStorageEntity hitPSE = null;
                DeadDrop hitDD = null;

                var cam = PlayerSingleton<PlayerCamera>.Instance;
                if (cam != null)
                {
                    RaycastHit hit;
                    if (cam.LookRaycast(RaycastRange, out hit, GetDetectionMask(), true, 0.1f))
                    {
                        var col = hit.collider;
                        if (col != null)
                        {
                            hitPSE = col.GetComponentInParent<PlaceableStorageEntity>();
                            if (hitPSE == null)
                                hitDD = col.GetComponentInParent<DeadDrop>();
                        }
                    }
                }

                // Update hover state
                UpdateHover(hitPSE, hitDD);

                // Check for click
                if (GameInput.GetButtonDown(GameInput.ButtonCode.PrimaryClick))
                {
                    if (hitPSE != null || hitDD != null)
                    {
                        Close(true, hitPSE, hitDD);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Manager, $"RouteEntitySelector Tick error: {ex.Message}");
                Close(false);
            }
        }

        private static void UpdateHover(PlaceableStorageEntity hitPSE, DeadDrop hitDD)
        {
            // Clear previous PSE outline if we're no longer hovering it
            if (_highlightedPSE != null && _highlightedPSE != hitPSE)
            {
                HidePSEOutline();
            }

            // Clear previous dead drop outline if we're no longer hovering it
            if (_highlightedDD != null && _highlightedDD != hitDD)
            {
                HideDeadDropOutline();
            }

            if (hitPSE != null)
            {
                // Show outline on PSE
                if (_highlightedPSE != hitPSE)
                {
                    _highlightedPSE = hitPSE;
                    try { hitPSE.ShowOutline(HoverOutlineColor); }
                    catch (Exception ex) { OTCLog.Msg(OTCLog.Systems.Manager, $"RouteEntitySelector outline show failed: {ex.Message}"); }
                }

                // Show name in crosshair
                ShowCrosshairText(GetPSEName(hitPSE));
            }
            else if (hitDD != null)
            {
                // Show outline on dead drop
                if (_highlightedDD != hitDD)
                {
                    ShowDeadDropOutline(hitDD);
                }

                ShowCrosshairText("Dead Drop (" + (hitDD.DeadDropName ?? "Unknown") + ")");
            }
            else
            {
                // Nothing hovered — hide crosshair text
                try { Singleton<HUD>.Instance?.CrosshairText?.Hide(); }
                catch (Exception ex) { OTCLog.Msg(OTCLog.Systems.Manager, $"RouteEntitySelector crosshair hide failed: {ex.Message}"); }
            }
        }

        private static void ShowCrosshairText(string text)
        {
            try { Singleton<HUD>.Instance?.CrosshairText?.Show(text, Color.white); }
            catch (Exception ex) { OTCLog.Msg(OTCLog.Systems.Manager, $"RouteEntitySelector crosshair show failed: {ex.Message}"); }
        }

        private static void HidePSEOutline()
        {
            try { _highlightedPSE.HideOutline(); }
            catch (Exception ex) { OTCLog.Msg(OTCLog.Systems.Manager, $"RouteEntitySelector outline hide failed: {ex.Message}"); }
            _highlightedPSE = null;
        }

        private static void Close(bool pushSelection, PlaceableStorageEntity pse = null, DeadDrop dd = null)
        {
            _isOpen = false;

            // Clear outlines
            if (_highlightedPSE != null)
                HidePSEOutline();
            HideDeadDropOutline();

            try
            {
                Singleton<HUD>.Instance?.HideTopScreenText();
                Singleton<HUD>.Instance?.CrosshairText?.Hide();

                if (_prompts != null)
                    Singleton<ScheduleOne.UI.Input.InputPromptsManager>.Instance?.UnloadModule(_prompts);
                _prompts = null;

                if (_exitListener != null)
                    GameInput.DeregisterExitListener(_exitListener);
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Manager, $"RouteEntitySelector close UI cleanup failed: {ex.Message}");
            }

            // Reopen clipboard
            try
            {
                var mi = Singleton<ManagementInterface>.Instance;
                if (mi?.EquippedClipboard != null)
                {
                    Singleton<ManagementClipboard>.Instance?.Open(
                        mi.Configurables, mi.EquippedClipboard);
                }
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Manager, $"RouteEntitySelector clipboard reopen failed: {ex.Message}");
            }

            // Fire callback
            if (pushSelection && _callback != null)
            {
                try { _callback(pse, dd); }
                catch (Exception ex) { OTCLog.Error(OTCLog.Systems.Manager, $"RouteEntitySelector Callback error: {ex.Message}"); }
            }
            else if (!pushSelection && _callback != null)
            {
                try { _callback(null, null); }
                catch (Exception ex) { OTCLog.Error(OTCLog.Systems.Manager, $"RouteEntitySelector Cancel callback error: {ex.Message}"); }
            }

            _callback = null;
        }

        private static void ShowDeadDropOutline(DeadDrop dd)
        {
            HideDeadDropOutline();
            _highlightedDD = dd;

            try
            {
                var go = dd.gameObject;
                _ddOutlineEffect = go.GetComponent<Outlinable>();
                if (_ddOutlineEffect == null)
                {
                    _ddOutlineEffect = go.AddComponent<Outlinable>();
                    _ddOutlineEffect.OutlineParameters.BlurShift = 0f;
                    _ddOutlineEffect.OutlineParameters.DilateShift = 0.5f;
                    _ddOutlineEffect.OutlineParameters.FillPass.Shader =
                        Resources.Load<Shader>("Easy performant outline/Shaders/Fills/ColorFill");

                    var renderers = go.GetComponentsInChildren<MeshRenderer>();
                    for (int i = 0; i < renderers.Length; i++)
                    {
                        renderers[i].allowOcclusionWhenDynamic = false;
                        var target = new OutlineTarget(renderers[i], 0);
                        _ddOutlineEffect.TryAddTarget(target);
                    }
                }

                _ddOutlineEffect.OutlineParameters.Color = HoverOutlineColor;
                Color32 fill = HoverOutlineColor;
                fill.a = 9;
                _ddOutlineEffect.OutlineParameters.FillPass.SetColor("_PublicColor", fill);
                _ddOutlineEffect.enabled = true;
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Manager, $"RouteEntitySelector dead drop outline failed: {ex.Message}");
            }
        }

        private static void HideDeadDropOutline()
        {
            if (_ddOutlineEffect != null)
            {
                try { _ddOutlineEffect.enabled = false; }
                catch (Exception ex) { OTCLog.Msg(OTCLog.Systems.Manager, $"RouteEntitySelector dead drop outline hide failed: {ex.Message}"); }
                _ddOutlineEffect = null;
            }
            _highlightedDD = null;
        }

        private static string GetPSEName(PlaceableStorageEntity pse)
        {
            try
            {
                var buildable = pse.TryCast<BuildableItem>();
                if (buildable?.ItemInstance != null)
                    return buildable.ItemInstance.Name;
            }
            catch (Exception ex)
            {
                OTCLog.Msg(OTCLog.Systems.Manager, $"RouteEntitySelector item name lookup failed: {ex.Message}");
            }
            return pse?.gameObject?.name ?? "Storage";
        }

        /// <summary>Detection mask matching the game's selectors.</summary>
        private static int GetDetectionMask() =>
            Physics.DefaultRaycastLayers;
    }
}

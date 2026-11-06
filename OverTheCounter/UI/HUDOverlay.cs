using MelonLoader;
using OverTheCounter.Utilities;
using S1API.Leveling;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

#if IL2CPP
using Il2CppInterop.Runtime.Injection;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.PlayerScripts.Health;
using Il2CppScheduleOne.UI;
using Il2CppTMPro;
#else
using ScheduleOne.DevUtilities;
using ScheduleOne.PlayerScripts;
using ScheduleOne.PlayerScripts.Health;
using ScheduleOne.UI;
using TMPro;
#endif

namespace OverTheCounter.UI
{
    [RegisterTypeInIl2Cpp]
    public class HUDOverlay : MonoBehaviour
    {
        private static readonly string[] RankNames =
        {
            "Street Rat", "Hoodlum", "Peddler", "Hustler", "Bagman",
            "Enforcer", "Shot Caller", "Block Boss", "Underlord", "Baron", "Kingpin"
        };

        private static readonly string[] RomanTiers = { "", "I", "II", "III", "IV", "V" };

        // Reused every frame by RepositionAboveHotbar() to avoid per-frame allocation
        private static readonly Vector3[] HotbarCorners = new Vector3[4];

        // Parented onto the game HUD canvas so we inherit its scaler.
        private RectTransform _canvasRect;
        private RectTransform _hotbarRect;
        private GameObject _container;
        private RectTransform _containerRect;

        // Health bar (left)
        private GameObject _healthBarObj;
        private Image _healthFill;
        private TextMeshProUGUI _healthLabel;
        private float _lastHealth = -1f;

        // Stamina bar (middle)
        private GameObject _staminaBarObj;
        private Image _staminaFill;
        private TextMeshProUGUI _staminaLabel;
        private float _lastStamina = -1f;

        // XP bar (right)
        private GameObject _xpBarObj;
        private Image _xpFill;
        private TextMeshProUGUI _xpLabel;
        private int _lastKnownXP = -1;
        private int _lastKnownTier = -1;

        // Drop animations
        private readonly List<DropLabel> _healthDrops = new List<DropLabel>();
        private readonly List<DropLabel> _staminaDrops = new List<DropLabel>();
        private readonly List<DropLabel> _xpDrops = new List<DropLabel>();

        // Selected item label repositioning
        private RectTransform _itemLabelRect;
        private float _itemLabelOriginalY;
        private bool _itemLabelMoved;

        // Game's default stamina bar
        private GameObject _gameStaminaBarObj;

        private bool _built;

        private class DropLabel
        {
            public TextMeshProUGUI Label;
            public RectTransform Rect;
            public float Timer;
            public float StartY;
            public float StartX;
            public const float Duration = 1.4f;
            public const float Rise = 40f;
        }

        private enum DropChannel
        {
            Health,
            Stamina,
            XP
        }

        public static void Register()
        {
#if IL2CPP
            ClassInjector.RegisterTypeInIl2Cpp<HUDOverlay>();
#endif
        }

        private void Update()
        {
            try
            {
                if (Player.Local == null) return;

                if (_built && (_container == null || _containerRect == null || _canvasRect == null || _hotbarRect == null))
                    _built = false;

                if (!_built)
                    TryBuild();

                if (!_built) return;

                // Hide when game HUD is hidden (pause, checkout, watering, dying, arrest, etc.)
                bool gameHudHidden = Singleton<HUD>.InstanceExists && !Singleton<HUD>.Instance.canvas.enabled;
                if (gameHudHidden)
                {
                    if (_container != null && _container.activeSelf)
                        _container.SetActive(false);
                    return;
                }
                if (_container != null && !_container.activeSelf)
                    _container.SetActive(true);

                RepositionAboveHotbar();

                bool hpEnabled = Config.HUDShowHealth.Value;
                bool stamEnabled = Config.HUDShowStamina.Value;
                bool xpEnabled = Config.HUDShowRankXP.Value;

                if (_healthBarObj != null)
                    _healthBarObj.SetActive(hpEnabled);
                if (_staminaBarObj != null)
                    _staminaBarObj.SetActive(stamEnabled);
                if (_xpBarObj != null)
                    _xpBarObj.SetActive(xpEnabled);

                // Hide game's default stamina bar when ours is active
                if (_gameStaminaBarObj != null)
                    _gameStaminaBarObj.SetActive(!stamEnabled);

                if (hpEnabled) UpdateHealthBar();
                if (stamEnabled) UpdateStaminaBar();
                if (xpEnabled) UpdateXPBar();

                UpdateDrops(_healthDrops);
                UpdateDrops(_staminaDrops);
                UpdateDrops(_xpDrops);

                // Only move item label when at least one bar is visible
                bool anyVisible = hpEnabled || stamEnabled || xpEnabled;
                UpdateItemLabelPosition(anyVisible);
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch, $"HUDOverlay: {ex.Message}");
            }
        }

        private void TryBuild()
        {
            if (!Singleton<HUD>.InstanceExists) return;
            var hud = Singleton<HUD>.Instance;
            if (hud == null || hud.canvas == null || hud.canvasRect == null) return;

            var hotbar = hud.HotbarContainer;
            if (hotbar == null) return;

            // Parent onto the game HUD canvas so we inherit its CanvasScaler
            // and the player's Interface Scale. We do NOT parent onto
            // HotbarContainer itself because it has layout-group / sizing
            // behavior that compresses arbitrary children.
            _canvasRect = hud.canvasRect;
            _hotbarRect = hotbar;

            float barHeight = 22f;
            float barWidth = 920f; // reference-pixel width, hotbar-sized at 1080p

            // Container: bottom-center of game HUD canvas. Its Y is updated
            // each frame by RepositionAboveHotbar() using the hotbar's real
            // world-space top edge, so the offset is correct at every
            // resolution and Interface Scale setting.
            _container = new GameObject("OTC_HUDBars");
            // Add RectTransform before parenting into the canvas hierarchy
            // so IL2CPP interop never tries to cast a plain Transform.
            _containerRect = _container.AddComponent<RectTransform>();
            _containerRect.SetParent(_canvasRect, false);
            _containerRect.anchorMin = new Vector2(0.5f, 0f);
            _containerRect.anchorMax = new Vector2(0.5f, 0f);
            _containerRect.pivot = new Vector2(0.5f, 0f);
            _containerRect.sizeDelta = new Vector2(barWidth, barHeight);
            _containerRect.anchoredPosition = new Vector2(0f, 0f);

            // Three bars: 31% each with 3.5% gaps between
            float barFrac = 0.31f;
            float barGap = 0.035f;

            // Health bar (left) - muted red
            _healthBarObj = CreateBar(_containerRect, "HealthBar", 0f, barFrac, barHeight,
                new Color(0.6f, 0.2f, 0.2f), out _healthFill, out _healthLabel);

            // XP bar (middle) - muted green
            float midLeft = barFrac + barGap;
            _xpBarObj = CreateBar(_containerRect, "XPBar", midLeft, midLeft + barFrac, barHeight,
                new Color(0.2f, 0.55f, 0.25f), out _xpFill, out _xpLabel);

            // Stamina bar (right) - muted amber
            float rightLeft = midLeft + barFrac + barGap;
            _staminaBarObj = CreateBar(_containerRect, "StaminaBar", rightLeft, rightLeft + barFrac, barHeight,
                new Color(0.7f, 0.55f, 0.15f), out _staminaFill, out _staminaLabel);

            // Capture item label reference for repositioning
            var itemLabel = hud.selectedItemLabel;
            if (itemLabel != null)
            {
                _itemLabelRect = itemLabel.GetComponent<RectTransform>();
                _itemLabelOriginalY = _itemLabelRect.anchoredPosition.y;
            }

            // Find the game's default stamina bar so we can hide it
            var gameStaminaBar = UnityEngine.Object.FindObjectOfType<StaminaBar>();
            if (gameStaminaBar != null)
                _gameStaminaBarObj = gameStaminaBar.gameObject;

            _built = true;
            RepositionAboveHotbar();
        }

        /// <summary>
        /// Places the container directly above the hotbar by setting its
        /// world-space <c>position</c> from <see cref="_hotbarRect"/>'s
        /// live corners. Unity computes anchored position internally, so
        /// this works regardless of the canvas pivot or scaler settings.
        /// Gap scales with hotbar height so it stays proportional at any
        /// resolution or Interface Scale setting.
        /// </summary>
        private void RepositionAboveHotbar()
        {
            if (_container == null || _containerRect == null || _hotbarRect == null) return;

            _hotbarRect.GetWorldCorners(HotbarCorners);
            // corners: [0]=BL, [1]=TL, [2]=TR, [3]=BR (world / screen px)
            Vector3 topCenter = (HotbarCorners[1] + HotbarCorners[2]) * 0.5f;
            float hotbarWorldHeight = HotbarCorners[1].y - HotbarCorners[0].y;
            // Gap = ~15% of hotbar height, so it scales naturally.
            float gap = Mathf.Max(8f, hotbarWorldHeight * 0.15f);

            // Our pivot is bottom-center; set world position of that pivot
            // to hotbar top-center + gap. Unity handles anchored recompute.
            // Keep the hotbar's z so this holds on camera-space canvases too.
            _containerRect.position = new Vector3(topCenter.x, topCenter.y + gap, topCenter.z);
        }

        private void UpdateItemLabelPosition(bool barsVisible)
        {
            if (_itemLabelRect == null) return;

            if (barsVisible && !_itemLabelMoved)
            {
                _itemLabelRect.anchoredPosition = new Vector2(
                    _itemLabelRect.anchoredPosition.x,
                    _itemLabelOriginalY + 28f);
                _itemLabelMoved = true;
            }
            else if (!barsVisible && _itemLabelMoved)
            {
                _itemLabelRect.anchoredPosition = new Vector2(
                    _itemLabelRect.anchoredPosition.x,
                    _itemLabelOriginalY);
                _itemLabelMoved = false;
            }
        }

        private static Sprite _roundedSprite;

        private static Sprite GetRoundedSprite()
        {
            if (_roundedSprite != null) return _roundedSprite;

            int size = 32;
            int radius = 3;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = Mathf.Max(0, Mathf.Max(radius - x, x - (size - 1 - radius)));
                    float dy = Mathf.Max(0, Mathf.Max(radius - y, y - (size - 1 - radius)));
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    tex.SetPixel(x, y, dist <= radius ? Color.white : Color.clear);
                }
            }

            tex.Apply();
            _roundedSprite = Sprite.Create(tex, new Rect(0, 0, size, size),
                new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect,
                new Vector4(radius, radius, radius, radius));
            return _roundedSprite;
        }

        private GameObject CreateBar(RectTransform parent, string name,
            float anchorLeft, float anchorRight, float height,
            Color fillColor, out Image fill, out TextMeshProUGUI label)
        {
            var sprite = GetRoundedSprite();

            // Border (faint semi-transparent, matching hotbar slot style)
            var border = new GameObject(name);
            border.transform.SetParent(parent, false);
            var borderImg = border.AddComponent<Image>();
            borderImg.sprite = sprite;
            borderImg.type = Image.Type.Sliced;
            borderImg.color = new Color(0.75f, 0.75f, 0.75f, 0.2f);
            borderImg.raycastTarget = false;

            var borderRect = border.GetComponent<RectTransform>();
            borderRect.anchorMin = new Vector2(anchorLeft, 0f);
            borderRect.anchorMax = new Vector2(anchorRight, 1f);
            borderRect.offsetMin = Vector2.zero;
            borderRect.offsetMax = Vector2.zero;

            // Inner with semi-transparent background (matching hotbar empty slots)
            float borderThickness = 1.5f;
            var root = new GameObject("Inner");
            root.transform.SetParent(border.transform, false);
            var bgImg = root.AddComponent<Image>();
            bgImg.sprite = sprite;
            bgImg.type = Image.Type.Sliced;
            bgImg.color = new Color(0.1f, 0.1f, 0.1f, 0.4f);
            bgImg.raycastTarget = false;

            var rootRect = root.GetComponent<RectTransform>();
            rootRect.anchorMin = Vector2.zero;
            rootRect.anchorMax = Vector2.one;
            rootRect.offsetMin = new Vector2(borderThickness, borderThickness);
            rootRect.offsetMax = new Vector2(-borderThickness, -borderThickness);

            // Fill bar (anchored left, width driven by anchorMax.x)
            var fillObj = new GameObject("Fill");
            fillObj.transform.SetParent(root.transform, false);
            fill = fillObj.AddComponent<Image>();
            fill.sprite = sprite;
            fill.type = Image.Type.Sliced;
            fill.color = fillColor;
            fill.raycastTarget = false;

            var fillRect = fillObj.GetComponent<RectTransform>();
            fillRect.anchorMin = Vector2.zero;
            fillRect.anchorMax = new Vector2(0f, 1f);
            fillRect.offsetMin = Vector2.zero;
            fillRect.offsetMax = Vector2.zero;


            // Text label (fills entire bar, with dark outline for readability)
            label = TMPFactory.Text(name + "Label", "", root.transform, 15,
                TextAlignmentOptions.Center, FontStyles.Bold);
            label.raycastTarget = false;
            label.outlineWidth = 0.2f;
            label.outlineColor = new Color32(0, 0, 0, 180);
            var labelRect = label.GetComponent<RectTransform>();
            labelRect.offsetMin = new Vector2(4, 0);
            labelRect.offsetMax = new Vector2(-4, 0);

            return border;
        }

        private void UpdateHealthBar()
        {
            if (_healthFill == null || _healthLabel == null) return;

            try
            {
                var health = Player.Local?.Health;
                if (health == null) return;

                float current = health.CurrentHealth;
                float max = PlayerHealth.MAX_HEALTH;
                float ratio = max > 0 ? Mathf.Clamp01(current / max) : 1f;

                _healthFill.rectTransform.anchorMax = new Vector2(ratio, 1f);
                _healthLabel.text = $"{Mathf.RoundToInt(current)} Health";

                if (_lastHealth >= 0f)
                {
                    float delta = current - _lastHealth;
                    if (Mathf.Abs(delta) >= 0.5f)
                        SpawnDrop(_healthBarObj, DropChannel.Health, delta, "Health");
                }
                _lastHealth = current;
            }
            catch { }
        }

        private void UpdateStaminaBar()
        {
            if (_staminaFill == null || _staminaLabel == null) return;

            try
            {
                var movement = PlayerSingleton<PlayerMovement>.Instance;
                if (movement == null) return;

                float current = movement.CurrentStaminaReserve;
                float max = PlayerMovement.StaminaReserveMax;
                float ratio = max > 0 ? Mathf.Clamp01(current / max) : 1f;

                _staminaFill.rectTransform.anchorMax = new Vector2(ratio, 1f);
                _staminaLabel.text = $"{Mathf.RoundToInt(current)} Stamina";

                if (_lastStamina >= 0f)
                {
                    float delta = current - _lastStamina;
                    if (Mathf.Abs(delta) >= 2f)
                        SpawnDrop(_staminaBarObj, DropChannel.Stamina, delta, "Stamina",
                            new Color(0.7f, 0.55f, 0.15f, 1f));
                }
                _lastStamina = current;
            }
            catch { }
        }

        private void UpdateXPBar()
        {
            if (_xpFill == null || _xpLabel == null) return;

            try
            {
                if (!LevelManager.Exists) return;

                var rank = LevelManager.Rank;
                int tier = LevelManager.Tier;
                int rankIdx = (int)rank;
                string rankName = rankIdx >= 0 && rankIdx < RankNames.Length
                    ? RankNames[rankIdx]
                    : rank.ToString();
                string tierStr = tier >= 1 && tier <= 5 ? RomanTiers[tier] : tier.ToString();

                int xp = LevelManager.XP;
                float xpToNext = LevelManager.XPToNextTier;
                float ratio = xpToNext > 0 ? Mathf.Clamp01(xp / xpToNext) : 1f;

                _xpFill.rectTransform.anchorMax = new Vector2(ratio, 1f);
                _xpLabel.text = $"{rankName} {tierStr} ({xp}/{Mathf.RoundToInt(xpToNext)} XP)";

                // XP drop detection
                bool tierChanged = _lastKnownTier >= 0 && tier > _lastKnownTier;
                int tierDelta = tierChanged ? tier - _lastKnownTier : 0;

                if (_lastKnownXP >= 0 && xp > _lastKnownXP)
                    SpawnXPDrop(xp - _lastKnownXP, tierChanged, tierDelta);
                else if (tierChanged)
                    SpawnXPDrop(0, true, tierDelta);

                _lastKnownXP = xp;
                _lastKnownTier = tier;
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch, $"HUD overlay XP bar update: {ex.Message}");
            }
        }

        private void SpawnDrop(GameObject barObj, DropChannel channel, float delta, string suffix, Color? negativeColor = null)
        {
            if (barObj == null || _canvasRect == null) return;
            int rounded = Mathf.RoundToInt(delta);
            if (rounded == 0) return;

            var barRect = barObj.GetComponent<RectTransform>();
            var dropObj = new GameObject(suffix + "Drop");
            dropObj.transform.SetParent(_canvasRect, false);

            var label = dropObj.AddComponent<TextMeshProUGUI>();
            label.fontSize = 15;
            label.fontStyle = FontStyles.Bold;
            label.alignment = TextAlignmentOptions.Center;
            label.raycastTarget = false;

            if (rounded > 0)
            {
                label.color = new Color(0.4f, 1f, 0.4f, 1f);
                label.text = $"+{rounded}";
            }
            else
            {
                label.color = negativeColor ?? new Color(1f, 0.3f, 0.3f, 1f);
                label.text = $"{rounded}";
            }

            PositionDrop(dropObj, barRect, channel);
        }

        private void SpawnXPDrop(int delta, bool levelUp = false, int levels = 1)
        {
            if (_xpBarObj == null || _canvasRect == null) return;
            if (delta == 0 && !levelUp) return;

            var barRect = _xpBarObj.GetComponent<RectTransform>();

            var dropObj = new GameObject("XPDrop");
            dropObj.transform.SetParent(_canvasRect, false);

            var label = dropObj.AddComponent<TextMeshProUGUI>();
            label.fontSize = 15;
            label.fontStyle = FontStyles.Bold;
            label.alignment = TextAlignmentOptions.Center;
            label.raycastTarget = false;

            if (delta > 0 && levelUp)
            {
                label.color = new Color(0.4f, 1f, 0.4f, 1f);
                label.richText = true;
                string lvlPart = levels == 1 ? "+1 Level" : $"+{levels} Levels";
                label.text = $"+{delta} XP <color=#9B40E8>{lvlPart}</color>";
            }
            else if (delta > 0)
            {
                label.color = new Color(0.4f, 1f, 0.4f, 1f);
                label.text = $"+{delta} XP";
            }
            else
            {
                label.color = new Color(0.6f, 0.3f, 1f, 1f);
                label.text = levels == 1 ? "+1 Level" : $"+{levels} Levels";
            }

            PositionDrop(dropObj, barRect, DropChannel.XP);
        }

        private void PositionDrop(GameObject dropObj, RectTransform barRect, DropChannel channel)
        {
            var dropRect = dropObj.GetComponent<RectTransform>();

            // Convert bar's screen position to our canvas local space
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                _canvasRect, (Vector2)barRect.position, null, out Vector2 localPos);

            float barHeight = barRect.rect.height;
            float barWidth = barRect.rect.width;
            float startX = localPos.x;
            float startY = localPos.y + barHeight / 2f + 4f;

            dropRect.anchorMin = new Vector2(0.5f, 0.5f);
            dropRect.anchorMax = new Vector2(0.5f, 0.5f);
            dropRect.pivot = new Vector2(0.5f, 0.5f);
            dropRect.sizeDelta = new Vector2(barWidth, 20f);
            dropRect.anchoredPosition = new Vector2(startX, startY);

            var entry = new DropLabel
            {
                Label = dropObj.GetComponent<TextMeshProUGUI>(),
                Rect = dropRect,
                Timer = 0f,
                StartY = startY,
                StartX = startX
            };

            switch (channel)
            {
                case DropChannel.Health:
                    _healthDrops.Add(entry);
                    break;
                case DropChannel.Stamina:
                    _staminaDrops.Add(entry);
                    break;
                case DropChannel.XP:
                    _xpDrops.Add(entry);
                    break;
            }
        }

        private static void UpdateDrops(List<DropLabel> drops)
        {
            for (int i = drops.Count - 1; i >= 0; i--)
            {
                var drop = drops[i];
                drop.Timer += Time.deltaTime;
                float t = Mathf.Clamp01(drop.Timer / DropLabel.Duration);

                drop.Rect.anchoredPosition = new Vector2(drop.StartX, drop.StartY + DropLabel.Rise * t);

                float alpha = t < 0.4f ? 1f : Mathf.Clamp01(1f - (t - 0.4f) / 0.6f);
                var c = drop.Label.color;
                drop.Label.color = new Color(c.r, c.g, c.b, alpha);

                if (drop.Timer >= DropLabel.Duration)
                {
                    Destroy(drop.Label.gameObject);
                    drops.RemoveAt(i);
                }
            }
        }

        private void OnDestroy()
        {
            foreach (var d in _healthDrops)
                if (d.Label != null) Destroy(d.Label.gameObject);
            _healthDrops.Clear();

            foreach (var d in _staminaDrops)
                if (d.Label != null) Destroy(d.Label.gameObject);
            _staminaDrops.Clear();

            foreach (var d in _xpDrops)
                if (d.Label != null) Destroy(d.Label.gameObject);
            _xpDrops.Clear();

            // Restore game's default stamina bar
            if (_gameStaminaBarObj != null)
                _gameStaminaBarObj.SetActive(true);

            // Restore item label position
            if (_itemLabelRect != null && _itemLabelMoved)
            {
                _itemLabelRect.anchoredPosition = new Vector2(
                    _itemLabelRect.anchoredPosition.x,
                    _itemLabelOriginalY);
                _itemLabelMoved = false;
            }

            if (_container != null)
                Destroy(_container);
            _container = null;
            _containerRect = null;
            _canvasRect = null;
            _hotbarRect = null;

            _built = false;
            _lastHealth = -1f;
            _lastStamina = -1f;
            _lastKnownXP = -1;
            _lastKnownTier = -1;
        }
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;
#if IL2CPP
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppScheduleOne.AvatarFramework;
using Il2CppScheduleOne.Core.Avatar;
using Il2CppScheduleOne.NPCs;
using AvatarLayer = Il2CppScheduleOne.AvatarFramework.AvatarLayer;
using AvatarObject = Il2CppScheduleOne.Core.Avatar.AvatarObject;
#else
using ScheduleOne.AvatarFramework;
using ScheduleOne.Core.Avatar;
using ScheduleOne.NPCs;
using AvatarLayer = ScheduleOne.AvatarFramework.AvatarLayer;
using AvatarObject = ScheduleOne.Core.Avatar.AvatarObject;
#endif

namespace OverTheCounter.Utilities
{
    /// <summary>
    /// Puts an <see cref="AvatarSettings"/> look on an NPC. The game dresses avatars from a
    /// <see cref="NakedAppearance"/> (body, face, hair) plus an outfit of worn objects, and every
    /// legacy layer, hair and accessory asset points at its replacement through
    /// <c>AvatarObjectEquivalent</c>, so OTC's seeded generators keep producing AvatarSettings and
    /// this translates them.
    /// </summary>
    public static class AvatarBridge
    {
        /// <summary>The game scales the avatar by NakedAppearance.Height / this; AvatarSettings.Height was the scale itself.</summary>
        private const float HeightScale = 1.875f;

        private const string AppearanceName = "OTC_Appearance";
        private const string OutfitName = "OTC_Outfit";

        /// <summary>Paths already reported as missing, so a respawning NPC doesn't repeat the warning.</summary>
        private static readonly HashSet<string> ReportedMissing = new();

        /// <summary>
        /// Applies <paramref name="settings"/> to the NPC's avatar and stores the result as the
        /// appearance in its runtime NPCData, so a re-initialization and the mugshot renderer see
        /// the same look. Parts with no new-system equivalent are left off and logged once.
        /// </summary>
        /// <param name="npc">The NPC to dress.</param>
        /// <param name="settings">The look to apply. Not kept; the caller owns it.</param>
        public static void Apply(NPC npc, AvatarSettings settings)
        {
            if (npc?.Avatar == null || settings == null) return;

            var appearance = npc.Avatar.Appearance;
            if (appearance == null)
            {
                OTCLog.Warning(OTCLog.Systems.NPC, $"{npc.gameObject.name}: avatar has no AvatarAppearance, look not applied");
                return;
            }

            var worn = new List<SerializedAvatarObject>();
            var naked = CreateNakedAppearance(settings, worn);

            var nakedObject = ScriptableObject.CreateInstance<NakedAppearanceObject>();
            nakedObject.name = AppearanceName;
            nakedObject.Appearance = naked;

            var outfit = ScriptableObject.CreateInstance<Outfit>();
            outfit.name = OutfitName;
#if IL2CPP
            var objects = new Il2CppReferenceArray<SerializedAvatarObject>(worn.Count);
            for (int i = 0; i < worn.Count; i++)
                objects[i] = worn[i];
            outfit.AvatarObjects = objects;
#else
            outfit.AvatarObjects = worn.ToArray();
#endif

            if (npc.HasNPCData)
            {
                // The runtime copy's Appearance is its own object, but the assets it points at may be
                // the source NPC's, so only destroy what OTC created
                var data = npc.NPCData.Appearance;
                DestroyOwned(data.DefaultAppearance, AppearanceName);
                DestroyOwned(data.DefaultOutfit, OutfitName);
                data.DefaultAppearance = nakedObject;
                data.DefaultOutfit = outfit;
            }

            appearance.ApplyNakedAppearance(naked);
            appearance.ApplyOutfit(outfit);
        }

        /// <summary>
        /// Body, eyes and the natural objects (face, hair, natural layers). Worn objects resolved
        /// along the way are added to <paramref name="worn"/>.
        /// </summary>
        private static NakedAppearance CreateNakedAppearance(AvatarSettings settings, List<SerializedAvatarObject> worn)
        {
            var naked = new NakedAppearance
            {
                Height = (settings.Height > 0f ? settings.Height : 1f) * HeightScale,
                Gender = settings.Gender,
                Weight = settings.Weight,
                SkinColor = settings.SkinColor,
            };

            var brow = naked.LeftEyebrowSettings;
            brow.Scale = settings.EyebrowScale;
            brow.Thickness = settings.EyebrowThickness;
            brow.RestingHeight = settings.EyebrowRestingHeight;
            brow.RestingAngle = settings.EyebrowRestingAngle;
            naked.LeftEyebrowSettings = brow;
            naked.RightEyebrowSettings = brow;

            var eye = naked.LeftEyeSettings;
            eye.EyeballColor = settings.EyeBallTint;
            eye.PupilDilation = settings.PupilDilation;
            naked.LeftEyeSettings = eye;
            naked.RightEyeSettings = eye;

            naked.LeftEyelidSettings = WithRestingState(naked.LeftEyelidSettings, settings.LeftEyeRestingState);
            naked.RightEyelidSettings = WithRestingState(naked.RightEyelidSettings, settings.RightEyeRestingState);

            // AvatarSettings has no eyebrow color; eyebrows match the hair
            naked.SetHairAndEyebrowColor(settings.HairColor);

            if (!string.IsNullOrEmpty(settings.HairPath))
                AddObject(naked, worn, settings.HairPath, settings.HairColor, true);

            var faces = settings.FaceLayerSettings;
            for (int i = 0; faces != null && i < faces.Count; i++)
            {
                // The first face layer is the expression, which keeps its own colors (as the game's
                // own legacy converter does); later ones (facial hair, details) take their tint
                AddObject(naked, worn, faces[i].layerPath, faces[i].layerTint, i > 0);
            }

            var bodies = settings.BodyLayerSettings;
            for (int i = 0; bodies != null && i < bodies.Count; i++)
                AddObject(naked, worn, bodies[i].layerPath, bodies[i].layerTint, true);

            var accessories = settings.AccessorySettings;
            for (int i = 0; accessories != null && i < accessories.Count; i++)
            {
                var accessory = accessories[i];
                if (accessory != null)
                    AddObject(naked, worn, accessory.path, accessory.color, true);
            }

            return naked;
        }

        private static EyelidSettings WithRestingState(EyelidSettings lid, Eye.EyeLidConfiguration rest)
        {
            var position = lid.RestingState;
            position.TopLidOpenness = Mathf.Clamp01(rest.topLidOpen);
            position.BottomLidOpenness = Mathf.Clamp01(rest.bottomLidOpen);
            lid.RestingState = position;
            return lid;
        }

        /// <summary>
        /// Resolves a legacy asset path and adds its object to the outfit when the object is worn,
        /// otherwise to the naked appearance.
        /// </summary>
        private static void AddObject(NakedAppearance naked, List<SerializedAvatarObject> worn,
            string path, Color color, bool tinted)
        {
            if (string.IsNullOrEmpty(path)) return;

            var avatarObject = FindEquivalent(path);
            if (avatarObject == null)
            {
                if (ReportedMissing.Add(path))
                    OTCLog.Warning(OTCLog.Systems.NPC, $"Avatar part '{path}' has no equivalent in the new avatar system, left off");
                return;
            }

            var serialized = tinted ? avatarObject.SerializeWithPrimaryColor(color) : avatarObject.Serialize();
            if (avatarObject.PrimaryType == AvatarObject.EType.Worn)
                worn.Add(serialized);
            else
                naked.AddAvatarObject(serialized);
        }

        /// <summary>
        /// The new-system object a legacy path loads to: hair and accessories are prefabs with an
        /// Accessory component, layers are AvatarLayer assets.
        /// </summary>
        private static AvatarObject FindEquivalent(string path)
        {
            var prefab = Resources.Load<GameObject>(path);
            var accessory = prefab != null ? prefab.GetComponent<Accessory>() : null;
            if (accessory != null && accessory.AvatarObjectEquivalent != null)
                return accessory.AvatarObjectEquivalent;

            var layer = Resources.Load<AvatarLayer>(path);
            return layer != null ? layer.AvatarObjectEquivalent : null;
        }

        private static void DestroyOwned(UnityEngine.Object asset, string ownedName)
        {
            if (asset != null && asset.name == ownedName)
                UnityEngine.Object.Destroy(asset);
        }
    }
}

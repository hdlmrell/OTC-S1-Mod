using MelonLoader;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

#if IL2CPP
using Il2CppScheduleOne.Core.Avatar;
using Il2CppScheduleOne.NPCs;
#else
using ScheduleOne.Core.Avatar;
using ScheduleOne.NPCs;
#endif

namespace OverTheCounter.Utilities
{
    /// <summary>
    /// Renders mugshots of OTC NPCs, one at a time, with S1API's portrait pipeline: its mugshot
    /// generator (the scene's, or the lit rig S1API builds when the scene has none), its lock on
    /// that generator, and its portrait camera. The game's own MugshotGenerator.GenerateMugshot
    /// aborts any capture in progress, so going through S1API's lock is what keeps OTC from
    /// breaking S1API's and other mods' portraits. Those members are internal to S1API and are
    /// reached by reflection; when they can't be found, mugshots are disabled with one warning
    /// and NPCs keep their generic icons.
    /// </summary>
    internal static class MugshotUtility
    {
        private const string CompatibilityTypeName = "S1API.Internal.Compatibility.AvatarCompatibility";
        private const int PortraitSize = 512;

        /// <summary>Frames to wait for the generator to exist, to be free, and for a capture to finish.</summary>
        private const int FrameBudget = 600;

        private static readonly Queue<MugshotRequest> Requests = new();
        private static bool _isProcessing;

        // Incremented on scene transitions. MelonCoroutines persist across scenes,
        // so in-flight coroutines check this to self-abort.
        private static int _sessionId;

        private static bool _s1apiResolved;
        private static bool _s1apiMissing;
        private static MethodInfo _findGenerator;
        private static MethodInfo _acquireGenerator;
        private static MethodInfo _releaseGenerator;
        private static MethodInfo _startCapture;
        private static MethodInfo _resizePortrait;

        private struct MugshotRequest
        {
            public NPC GameNpc;
            public string Label;
            public Action<Sprite> OnComplete;
        }

        /// <summary>
        /// Resets session state. Call on scene transitions.
        /// </summary>
        public static void ResetSession()
        {
            _sessionId++;
            Requests.Clear();
            _isProcessing = false;
        }

        /// <summary>
        /// Enqueues a mugshot of the look stored in the NPC's data (see <see cref="AvatarBridge"/>).
        /// <paramref name="onComplete"/> fires with the generated Sprite, or null on failure.
        /// </summary>
        public static void Generate(NPC gameNpc, string label, Action<Sprite> onComplete)
        {
            Requests.Enqueue(new MugshotRequest
            {
                GameNpc = gameNpc,
                Label = label,
                OnComplete = onComplete
            });

            if (!_isProcessing)
            {
                _isProcessing = true;
                MelonCoroutines.Start(ProcessQueue());
            }
        }

        private static IEnumerator ProcessQueue()
        {
            int mySession = _sessionId;

            if (!ResolveS1Api())
            {
                DrainQueue();
                yield break;
            }

            while (Requests.Count > 0 && mySession == _sessionId)
            {
                var req = Requests.Dequeue();
                Sprite sprite = null;

                if (req.GameNpc != null)
                {
                    // Stepped by hand rather than yielded as a nested coroutine, so it behaves the
                    // same under MelonLoader's Mono and IL2CPP coroutine hosts
                    var capture = Capture(req, mySession, result => sprite = result);
                    while (capture.MoveNext())
                        yield return capture.Current;
                }

                if (mySession != _sessionId) yield break;

                if (Config.VerboseLogging.Value)
                    OTCLog.Msg(OTCLog.Systems.NPC, $"{req.Label}: mugshot {(sprite != null ? "ready" : "failed")}");

                try
                {
                    req.OnComplete?.Invoke(sprite);
                }
                catch (Exception ex)
                {
                    OTCLog.Warning(OTCLog.Systems.NPC, $"{req.Label}: mugshot callback failed: {ex.Message}");
                }
            }

            if (mySession == _sessionId)
                _isProcessing = false;
        }

        /// <summary>
        /// One capture: wait for the generator and its lock, render, release, scale to
        /// <see cref="PortraitSize"/>. Reports null on any failure.
        /// </summary>
        private static IEnumerator Capture(MugshotRequest req, int session, Action<Sprite> report)
        {
            var avatar = req.GameNpc.Avatar;

            object generator = null;
            for (int frame = 0; generator == null && frame < FrameBudget; frame++)
            {
                generator = Invoke(req, "find the mugshot generator", _findGenerator, avatar);
                if (generator != null) break;
                yield return null;
                if (session != _sessionId) yield break;
            }
            if (generator == null)
            {
                OTCLog.Warning(OTCLog.Systems.NPC, $"{req.Label}: no mugshot generator available, keeping the generic icon");
                yield break;
            }

            bool held = false;
            for (int frame = 0; !held && frame < FrameBudget; frame++)
            {
                held = Invoke(req, "take the mugshot generator", _acquireGenerator, generator) is true;
                if (held) break;
                yield return null;
                if (session != _sessionId) yield break;
            }
            if (!held)
            {
                OTCLog.Warning(OTCLog.Systems.NPC, $"{req.Label}: mugshot generator stayed busy, keeping the generic icon");
                yield break;
            }

            Texture2D captured = null;
            try
            {
                // Read the look only now: the NPC may have been re-dressed while this waited
                if (!TryGetLook(req.GameNpc, out var naked, out var outfit))
                {
                    OTCLog.Warning(OTCLog.Systems.NPC, $"{req.Label}: NPC has no stored look, keeping the generic icon");
                    yield break;
                }

                bool done = false;
                Action<Texture2D> onCaptured = texture =>
                {
                    captured = texture;
                    done = true;
                };
                if (Invoke(req, "start the capture", _startCapture, generator, naked, outfit, onCaptured) == null)
                    done = true;

                for (int frame = 0; !done && frame < FrameBudget; frame++)
                {
                    yield return null;
                    if (session != _sessionId) yield break;
                }
            }
            finally
            {
                Invoke(req, "release the mugshot generator", _releaseGenerator);
            }

            report(ToSprite(req, captured));
        }

        /// <summary>
        /// The look <see cref="AvatarBridge"/> stored in the NPC's runtime data.
        /// </summary>
        private static bool TryGetLook(NPC npc, out NakedAppearance naked, out Outfit outfit)
        {
            naked = null;
            outfit = null;
            if (npc == null || !npc.HasNPCData) return false;

            var appearance = npc.NPCData.Appearance;
            naked = appearance?.DefaultAppearance?.Appearance;
            outfit = appearance?.DefaultOutfit;
            return naked != null && outfit != null;
        }

        private static Sprite ToSprite(MugshotRequest req, Texture2D source)
        {
            if (source == null) return null;

            try
            {
                source.Apply();
                var portrait = Invoke(req, "resize the portrait", _resizePortrait, source, PortraitSize) as Texture2D;
                UnityEngine.Object.Destroy(source);
                if (portrait == null) return null;

                if (!HasContent(portrait))
                {
                    OTCLog.Warning(OTCLog.Systems.NPC, $"{req.Label}: mugshot came out empty, keeping the generic icon");
                    UnityEngine.Object.Destroy(portrait);
                    return null;
                }

                return Sprite.Create(portrait, new Rect(0, 0, portrait.width, portrait.height), Vector2.zero);
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.NPC, $"{req.Label}: sprite creation failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// The portrait background is transparent; an avatar shows up as opaque pixels around the
        /// center and upper half (face and shoulders).
        /// </summary>
        private static bool HasContent(Texture2D texture)
        {
            int w = texture.width, h = texture.height;
            Color[] samples =
            {
                texture.GetPixel(w / 2, h / 2),
                texture.GetPixel(w / 2, (int)(h * 0.75f)),
                texture.GetPixel((int)(w * 0.4f), (int)(h * 0.6f)),
                texture.GetPixel((int)(w * 0.6f), (int)(h * 0.6f)),
                texture.GetPixel(w / 2, (int)(h * 0.3f))
            };

            foreach (var s in samples)
            {
                if (s.a > 0.01f) return true;
            }
            return false;
        }

        /// <summary>
        /// Looks up S1API's internal portrait members once. Missing members disable mugshots
        /// with one warning.
        /// </summary>
        private static bool ResolveS1Api()
        {
            if (_s1apiResolved) return true;
            if (_s1apiMissing) return false;

            try
            {
                var compat = typeof(S1API.Entities.NPCAppearance).Assembly.GetType(CompatibilityTypeName);
                const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
                _findGenerator = compat?.GetMethod("FindMugshotGenerator", flags);
                _acquireGenerator = compat?.GetMethod("TryAcquireMugshotGenerator", flags);
                _releaseGenerator = compat?.GetMethod("ReleaseMugshotGenerator", flags);
                _startCapture = compat?.GetMethod("StartPortraitCapture", flags);
                _resizePortrait = compat?.GetMethod("ResizePortrait", flags);
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.NPC, $"S1API portrait lookup failed: {ex.Message}");
            }

            if (_findGenerator == null || _acquireGenerator == null || _releaseGenerator == null
                || _startCapture == null || _resizePortrait == null)
            {
                OTCLog.Warning(OTCLog.Systems.NPC,
                    "Mugshots disabled: S1API's portrait capture (AvatarCompatibility) not found in this S1API version");
                _s1apiMissing = true;
                return false;
            }

            _s1apiResolved = true;
            return true;
        }

        /// <summary>
        /// Calls an S1API member, logging (with the inner exception) instead of throwing.
        /// Returns null on failure.
        /// </summary>
        private static object Invoke(MugshotRequest req, string what, MethodInfo method, params object[] args)
        {
            try
            {
                return method.Invoke(null, args);
            }
            catch (Exception ex)
            {
                var inner = (ex as TargetInvocationException)?.InnerException ?? ex;
                OTCLog.Warning(OTCLog.Systems.NPC, $"{req.Label}: could not {what}: {inner.Message}");
                return null;
            }
        }

        private static void DrainQueue()
        {
            while (Requests.Count > 0)
                Requests.Dequeue().OnComplete?.Invoke(null);
            _isProcessing = false;
        }
    }
}

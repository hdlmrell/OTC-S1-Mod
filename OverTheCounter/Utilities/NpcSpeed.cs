#if IL2CPP
using Il2CppScheduleOne.NPCs;
#else
using ScheduleOne.NPCs;
#endif
using UnityEngine;

namespace OverTheCounter.Utilities
{
    /// <summary>
    /// OTC speed values are a walk→run blend: 0 = 1.8 m/s walk, 1 = 7 m/s run.
    /// The game's speed controls take normalized (× 7 m/s) or absolute m/s values,
    /// so blends are converted to absolute m/s here.
    /// </summary>
    public static class NpcSpeed
    {
        private const float WalkSpeed = 1.8f;
        private const float RunSpeed = 7f;

        /// <summary>
        /// Adds (or updates, if <paramref name="id"/> already exists) a speed control
        /// running the NPC at the given walk→run blend. Higher priority wins.
        /// </summary>
        /// <param name="controller">The NPC's speed controller.</param>
        /// <param name="id">Speed control id; reuse it to update or remove the control.</param>
        /// <param name="priority">Stack priority; the highest-priority control sets the speed.</param>
        /// <param name="blend">0 = walk (1.8 m/s), 1 = run (7 m/s).</param>
        public static void AddBlendSpeedControl(this NPCSpeedController controller, string id, int priority, float blend)
        {
            float speed = Mathf.Lerp(WalkSpeed, RunSpeed, blend);
            controller.AddSpeedControl(id, speed, priority, SpeedControl.EType.Absolute);
        }
    }
}

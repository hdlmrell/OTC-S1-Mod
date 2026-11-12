#if IL2CPP
using Il2CppScheduleOne.Messaging;
using Il2CppScheduleOne.NPCs;
#else
using ScheduleOne.Messaging;
using ScheduleOne.NPCs;
#endif

namespace OverTheCounter.Utilities
{
    /// <summary>
    /// Phone texts from game NPCs. The game no longer has an NPC-level send; texts go through
    /// the NPC's <see cref="MSGConversation"/>.
    /// </summary>
    public static class NpcMessaging
    {
        /// <summary>
        /// Sends <paramref name="message"/> as a text from <paramref name="npc"/>, notified and
        /// networked. Does nothing when the NPC has no conversation.
        /// </summary>
        /// <param name="npc">The sender.</param>
        /// <param name="message">The text.</param>
        public static void SendTextMessage(this NPC npc, string message)
        {
            var conversation = npc?.MSGConversation;
            if (conversation == null)
            {
                OTCLog.Warning(OTCLog.Systems.NPC, $"{npc?.gameObject.name ?? "null NPC"}: no conversation, text not sent");
                return;
            }

            conversation.SendMessage(new Message(message, Message.ESenderType.Other, true,
                UnityEngine.Random.Range(int.MinValue, int.MaxValue)));
        }
    }
}

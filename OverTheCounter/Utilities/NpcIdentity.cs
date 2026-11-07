using System;
#if IL2CPP
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.Messaging;
using Il2CppScheduleOne.NPCs;
using NPCData = Il2CppScheduleOne.NPCs.Framework.NPCData;
#else
using ScheduleOne.Economy;
using ScheduleOne.Messaging;
using ScheduleOne.NPCs;
using NPCData = ScheduleOne.NPCs.Framework.NPCData;
#endif

namespace OverTheCounter.Utilities
{
    /// <summary>
    /// Gives cloned NPCs their OTC identity. The game reads ID, names and mugshot from
    /// <c>NPC.NPCData</c>, so identity is applied by re-initializing the NPC with an edited
    /// deep copy of its data (the same way vanilla initializes special customers).
    /// </summary>
    public static class NpcIdentity
    {
        /// <summary>
        /// Re-initializes <paramref name="npc"/> with a deep copy of its current data (or a vanilla
        /// civilian's when the clone has none), carrying the given identity plus any extra edits.
        /// Call after the NPC's GameObject has been activated (Awake must have run).
        /// </summary>
        /// <param name="npc">The cloned NPC.</param>
        /// <param name="id">NPC ID.</param>
        /// <param name="firstName">First name.</param>
        /// <param name="lastName">Last name; empty means the NPC has no last name.</param>
        /// <param name="configure">Optional extra edits to the copied data before it is applied.</param>
        /// <returns>False when no data was available to copy or initialization threw; the NPC
        /// is then unusable (its ID and names read from missing data) and should be discarded.</returns>
        public static bool Apply(NPC npc, string id, string firstName, string lastName,
            Action<NPCData> configure = null)
        {
            if (npc == null) return false;

            NPCData data = null;
            int desiredSlots = 0;
            try
            {
                var source = npc.NPCData;
                bool fromTemplate = source == null;
                if (fromTemplate)
                    source = FindTemplateData(npc);
                if (source == null)
                {
                    OTCLog.Warning(OTCLog.Systems.NPC, $"{id}: no NPCData to copy, identity not applied");
                    return false;
                }

                data = source.GetDeepCopy();
                data.BasicInfo.ID = id;
                data.BasicInfo.FirstName = firstName;
                data.BasicInfo.HasLastName = !string.IsNullOrEmpty(lastName);
                data.BasicInfo.LastName = lastName ?? string.Empty;
                // A template's mugshot is another NPC's face
                if (fromTemplate)
                    data.Appearance.Mugshot = null;
                configure?.Invoke(data);

                // Initialize adds InventorySlotCount new slots on top of any the NPC already has
                desiredSlots = data.Inventory.InventorySlotCount;
                int existingSlots = npc.Inventory?.ItemSlots?.Count ?? 0;
                data.Inventory.InventorySlotCount = Math.Max(0, desiredSlots - existingSlots);

                // Drop the conversation inherited from the clone source; it carries the source's name
                npc.UnassignConversationMessage();
                npc.InitializeNPC(data);
                return true;
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.NPC, $"{id}: identity not applied: {ex.Message}");
                return false;
            }
            finally
            {
                if (data != null)
                    data.Inventory.InventorySlotCount = desiredSlots;
            }
        }

        /// <summary>
        /// Creates and assigns a known message conversation for an NPC whose data has
        /// <c>Messaging.CreateConversationOnStart</c> disabled. The conversation ID is the NPC ID,
        /// so host and clients agree on it.
        /// </summary>
        public static MSGConversation CreateConversation(NPC npc)
        {
            var conversation = new MSGConversation(new MessageContactInfo(npc.NPCData), npc.ID);
            npc.SetMSGConversation(conversation);
            conversation.SetIsKnown(true);
            return conversation;
        }

        /// <summary>
        /// Sets the NPC's mugshot (shown in phone apps and map markers).
        /// </summary>
        public static void SetMugshot(NPC npc, UnityEngine.Sprite sprite)
        {
            if (npc?.NPCData != null)
                npc.NPCData.Appearance.Mugshot = sprite;
        }

        // Clones whose prefab carries no default data borrow a registered vanilla civilian's.
        private static NPCData FindTemplateData(NPC exclude)
        {
            var registry = NPCManager.NPCRegistry;
            if (registry == null) return null;

            for (int i = 0; i < registry.Count; i++)
            {
                var other = registry[i];
                if (other == null || other == exclude || !other.HasNPCData) continue;
                if (other.GetComponent<Customer>() == null) continue;
                return other.NPCData;
            }
            return null;
        }
    }
}

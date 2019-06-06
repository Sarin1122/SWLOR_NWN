﻿using System;
using System.Collections.Generic;
using NWN;
using SWLOR.Game.Server.Bioware;
using SWLOR.Game.Server.Data.Entity;
using SWLOR.Game.Server.Enumeration;
using SWLOR.Game.Server.Event.Module;
using SWLOR.Game.Server.GameObject;
using SWLOR.Game.Server.Messaging;
using SWLOR.Game.Server.NWN.Events.Module;
using SWLOR.Game.Server.NWNX;

using static NWN._;

namespace SWLOR.Game.Server.Service
{
    public static class PlayerMigrationService
    {
        public class SerializedObjectData
        {
            public string Data { get; }
            public List<ItemProperty> ItemPropertiesToAdd { get; }

            public SerializedObjectData(string data, List<ItemProperty> itemProperties)
            {
                Data = data;
                ItemPropertiesToAdd = itemProperties;
            }
        }

        public static void SubscribeEvents()
        {
            MessageHub.Instance.Subscribe<OnModuleEnter>(message => OnModuleEnter());
        }

        private static void OnModuleEnter()
        {
            NWPlayer player = _.GetEnteringObject();
            if (!player.IsPlayer) return;

            var dbPlayer = DataService.Get<Player>(player.GlobalID);

            // VERSION 2: Background items are no longer plot because item level no longer dictates your skill XP gain.
            if (dbPlayer.VersionNumber < 2) 
            {
                string[] resrefs =
                {
                    "blaster_s",
                    "rifle_s",
                    "powerglove_t",
                    "baton_s",
                    "doubleaxe_z",
                    "kukri_d",
                    "greatsword_s",
                    "scanner_r_h",
                    "harvest_r_h",
                    "man_armor"
                };

                foreach (var resref in resrefs)
                {
                    NWItem item = _.GetItemPossessedBy(player, resref);
                    if (item.IsValid)
                    {
                        item.IsPlot = false;
                    }
                }

                dbPlayer.VersionNumber = 2;
            }

            // VERSION 3: Force feats need to be removed since force powers were reworked.
            if (dbPlayer.VersionNumber < 3)
            {
                // These IDs come from the Feat.2da file.
                NWNXCreature.RemoveFeat(player, 1135); // Force Breach
                NWNXCreature.RemoveFeat(player, 1136); // Force Lightning
                NWNXCreature.RemoveFeat(player, 1137); // Force Heal
                NWNXCreature.RemoveFeat(player, 1138); // Dark Heal
                NWNXCreature.RemoveFeat(player, 1143); // Force Spread
                NWNXCreature.RemoveFeat(player, 1144); // Dark Spread
                NWNXCreature.RemoveFeat(player, 1145); // Force Push
                NWNXCreature.RemoveFeat(player, 1125); // Force Aura
                NWNXCreature.RemoveFeat(player, 1152); // Drain Life
                NWNXCreature.RemoveFeat(player, 1134); // Chainspell

                dbPlayer.VersionNumber = 3;
            }

            // VERSION 4: Give the Uncanny Dodge 1 feat to all characters.
            if (dbPlayer.VersionNumber < 4)
            {
                NWNXCreature.AddFeatByLevel(player, FEAT_UNCANNY_DODGE_1, 1);
                dbPlayer.VersionNumber = 4;
            }

            // VERSION 5: We're doing another Force rework, so remove any force feats the player may have acquired.
            if (dbPlayer.VersionNumber < 5)
            {
                NWNXCreature.RemoveFeat(player, 1135); // Force Breach
                NWNXCreature.RemoveFeat(player, 1136); // Force Lightning
                NWNXCreature.RemoveFeat(player, 1137); // Force Heal I
                NWNXCreature.RemoveFeat(player, 1140); // Absorption Field
                NWNXCreature.RemoveFeat(player, 1143); // Force Spread
                NWNXCreature.RemoveFeat(player, 1145); // Force Push
                NWNXCreature.RemoveFeat(player, 1125); // Force Aura
                NWNXCreature.RemoveFeat(player, 1152); // Drain Life
                NWNXCreature.RemoveFeat(player, 1134); // Chainspell
                NWNXCreature.RemoveFeat(player, 1162); // Force Heal II
                NWNXCreature.RemoveFeat(player, 1163); // Force Heal III
                NWNXCreature.RemoveFeat(player, 1164); // Force Heal IV

                dbPlayer.VersionNumber = 5;
            }

            // VERSION 6: Remove AC from all items the player is carrying. If possible,
            // grant +1 durability and +1 max durability for every 2 AC the item has.
            if (dbPlayer.VersionNumber < 6)
            {
                ProcessVersion6ItemChanges(player);
                dbPlayer.VersionNumber = 6;
            }

            DataService.SubmitDataChange(dbPlayer, DatabaseActionType.Update);
        }
        
        private static void ProcessVersion6ItemChanges(NWPlayer player)
        {
            List<SerializedObjectData> serializedItems = new List<SerializedObjectData>();

            // Start with equipped items.
            foreach (var item in player.EquippedItems)
            {
                ProcessVersion6RemoveACFromItem(item);
                var data = ProcessVersion6LightsaberItem(item);
                if(data.Data != null)
                    serializedItems.Add(data);
            }
            // Next do all inventory items.
            foreach (var item in player.InventoryItems)
            {
                ProcessVersion6RemoveACFromItem(item);
                var data = ProcessVersion6LightsaberItem(item);
                if(data.Data != null)
                    serializedItems.Add(data);
            }

            // Deserialize all items onto the player now.
            foreach (var serialized in serializedItems)
            {
                var item = SerializationService.DeserializeItem(serialized.Data, player);
                BiowareXP2.IPRemoveAllItemProperties(item, DURATION_TYPE_PERMANENT);
                foreach (var ip in serialized.ItemPropertiesToAdd)
                {
                    BiowareXP2.IPSafeAddItemProperty(item, ip, 0.0f, AddItemPropertyPolicy.ReplaceExisting, false, false);
                }
            }
        }

        public static void ProcessVersion6RemoveACFromItem(NWItem item)
        {
            // Start by pulling the custom AC off the item and halving it.
            // Durability is +1 for every 2 AC on the item.
            int amount = item.CustomAC / 2;
            if (amount > 0)
            {
                float newMax = DurabilityService.GetMaxDurability(item) + amount;
                float newCurrent = DurabilityService.GetDurability(item) + amount;
                DurabilityService.SetMaxDurability(item, newMax);
                DurabilityService.SetDurability(item, newCurrent);
            }
            
            item.CustomAC = 0;

            // Check all item properties. If the IP is a component Armor Class Bonus, remove it and replace with an increase to durability.
            foreach (var ip in item.ItemProperties)
            {
                if (_.GetItemPropertyType(ip) == (int) CustomItemPropertyType.ComponentBonus)
                {
                    // Check the sub-type. If it's AC, then do the replacement.
                    if (GetItemPropertySubType(ip) == (int) ComponentBonusType.ACUp)
                    {
                        amount = GetItemPropertyCostTableValue(ip) / 2;
                        // Grant the durability up property if amount > 0
                        if (amount > 0)
                        {
                            // Unpack the IP we're working with. Adjust its type and value, then reapply it.
                            var unpacked = NWNXItemProperty.UnpackIP(ip);
                            unpacked.SubType = (int) ComponentBonusType.DurabilityUp;
                            unpacked.CostTableValue = amount;
                            var packed = NWNXItemProperty.PackIP(unpacked);
                            BiowareXP2.IPSafeAddItemProperty(item, packed, 0.0f, AddItemPropertyPolicy.IgnoreExisting, true, true);
                        }

                        _.RemoveItemProperty(item, ip);
                    }
                }
            }
        }

        private static SerializedObjectData ProcessVersion6LightsaberItem(NWItem item)
        {
            if (item.CustomItemType != CustomItemType.Lightsaber &&
                item.CustomItemType != CustomItemType.Saberstaff) return new SerializedObjectData(null, null);

            NWPlaceable storage = _.GetObjectByTag("MIGRATION_STORAGE");
            NWItem newVersion = _.CreateItemOnObject(item.Resref, storage);
            List<ItemProperty> ipsToAdd = new List<ItemProperty>();
            
            // There's a quirk with NWN in how it handles removing of item properties.
            // IPs don't get removed immediately - instead, they get removed after the script exits.
            // Because we're serializing during this process, it causes us to get duplicate item properties
            // since they haven't actually been removed yet.
            // To work around this, we return both the serialized item as well as the item properties we need
            // to add to the item once it's been deserialized.
            // Nasty workaround, but it does work!
            foreach (var ip in item.ItemProperties)
            {
                ipsToAdd.Add(ip);
            }

            // Copy all local variables from old to new version.
            LocalVariableService.CopyVariables(item, newVersion);

            // Destroy the old item.
            item.Destroy();

            // We return the serialized value. Be sure we do this before destroying the object.
            // The reason for this is to ensure we don't hit an infinite loop. The calling method uses a loop iterating
            // over the player's inventory. Creating an item will cause an infinite loop to happen.
            string retVal = SerializationService.Serialize(newVersion);

            // Destroy the copy on the container.
            newVersion.Destroy();
            
            return new SerializedObjectData(retVal, ipsToAdd);
        }
    }
}

﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Oxide.Core;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Furnace Splitter", "Skipcast", "1.0.1", ResourceId = 2406)]
    [Description("Splits up resources in furnaces automatically and shows useful furnace information.")]
    public class FurnaceSplitter : RustPlugin
    {
        private static class Extensions
        {
            public static T GetBaseEntity<T>(EntityComponent<T> entityComponent) where T : BaseEntity
            {
                var propertyInfo = entityComponent.GetType().GetProperty("baseEntity", BindingFlags.Instance | BindingFlags.NonPublic);
                return (T)propertyInfo.GetValue(entityComponent, null);
            }

            public static float GetWorkTemperature(BaseOven oven)
            {
                var property = oven.GetType().GetProperty("cookingTemperature", BindingFlags.Instance | BindingFlags.NonPublic);
                return (float)property.GetValue(oven, null);
            }

            public static IEnumerable<T> ToIEnumerable<T>(IEnumerator<T> enumerator)
            {
                while (enumerator.MoveNext())
                {
                    yield return enumerator.Current;
                }
            }
        }

        private class OvenSlot
        {
            /// <summary>The item in this slot. May be null.</summary>
            public Item Item;

            /// <summary>The slot position</summary>
            public int? Position;

            /// <summary>The slot's index in the itemList list.</summary>
            public int Index;

            /// <summary>How much should be added/removed from stack</summary>
            public int DeltaAmount;
        }

        private class OvenInfo
        {
            public float ETA;
            public float FuelNeeded;
        }

        private class StoredData
        {
            public Dictionary<ulong, PlayerOptions> AllPlayerOptions { get; private set; } = new Dictionary<ulong, PlayerOptions>();
        }

        private class PlayerOptions
        {
            public bool Enabled;
            public Dictionary<string, int> TotalStacks = new Dictionary<string, int>();
        }

        private StoredData storedData;
        private Dictionary<ulong, PlayerOptions> allPlayerOptions => storedData.AllPlayerOptions;

        private readonly Dictionary<ulong, string> openUis = new Dictionary<ulong, string>();
        private readonly Dictionary<BaseOven, List<BasePlayer>> looters = new Dictionary<BaseOven, List<BasePlayer>>();
        private readonly Stack<BaseOven> queuedUiUpdates = new Stack<BaseOven>();

        private readonly string[] compatibleOvens =
        {
            "furnace",
            "furnace.large",
            "campfire",
            "refinery_small_deployed"
        };
        
        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            DestroyUI(player);
        }

        void OnPlayerInit(BasePlayer player)
        {
            InitPlayer(player);
        }

        private void Loaded()
        {
            storedData = Interface.Oxide.DataFileSystem.ReadObject<StoredData>("FurnaceSplitterOptions");
        }

        private void OnServerInitialized()
        {
            foreach (var player in Player.Players)
            {
                InitPlayer(player);
            }

            lang.RegisterMessages(new Dictionary<string, string>
            {
                { "turnon", "Turn On" },
                { "turnoff", "Turn Off" },
                { "title", "Furnace Splitter" },
                { "eta", "ETA" },
                { "totalstacks", "Total stacks" }
            }, this, "en");
        }

        private void OnServerSave()
        {
            SaveData();
        }

        private void SaveData()
        {
            Interface.Oxide.DataFileSystem.WriteObject("FurnaceSplitterOptions", storedData);
        }

        private void InitPlayer(BasePlayer player)
        {
            if (!allPlayerOptions.ContainsKey(player.userID))
            {
                allPlayerOptions[player.userID] = new PlayerOptions
                {
                    Enabled = true,
                    TotalStacks = new Dictionary<string, int>
                    {
                        { "furnace", 4 },
                        { "furnace.large", 15 },
                        { "campfire", 2 },
                        { "refinery_small_deployed", 4 }
                    }
                };
            }
        }

        private void OnTick()
        {
            while (queuedUiUpdates.Count > 0)
            {
                var oven = queuedUiUpdates.Pop();
                var ovenInfo = GetOvenInfo(oven);

                GetLooters(oven)?.ForEach(plr =>
                {
                    if (plr && oven && !plr.IsDestroyed && !oven.IsDestroyed)
                    {
                        CreateUi(plr, oven, ovenInfo);
                    }
                });
            }
        }

        private OvenInfo GetOvenInfo(BaseOven oven)
        {
            using (TimeWarning.New("FurnaceSplitter.GetOvenInfo", 0.01f))
            {
                var result = new OvenInfo();
                var smeltTimes = GetSmeltTimes(oven);

                if (smeltTimes.Count > 0)
                {
                    var longestStack = smeltTimes.OrderByDescending(kv => kv.Value).First();
                    var fuelUnits = oven.fuelType.GetComponent<ItemModBurnable>().fuelAmount;
                    var ovenTemperature = Extensions.GetWorkTemperature(oven);
                    float neededFuel = (float) Math.Ceiling((longestStack.Value * (ovenTemperature / 200.0f)) / fuelUnits);

                    result.FuelNeeded = neededFuel;
                    result.ETA = longestStack.Value;
                }

                return result;
            }
        }

        private void Unload()
        {
            SaveData();

            foreach (var kv in openUis.ToDictionary(kv => kv.Key, kv => kv.Value))
            {
                var player = BasePlayer.FindByID(kv.Key);
                DestroyUI(player);
            }
        }

        private bool GetEnabled(BasePlayer player)
        {
            return allPlayerOptions[player.userID].Enabled;
        }

        private void SetEnabled(BasePlayer player, bool enabled)
        {
            allPlayerOptions[player.userID].Enabled = enabled;
            CreateUiIfFurnaceOpen(player);
        }
        
        private bool IsSlotCompatible(Item item, BaseOven oven, ItemDefinition itemDefinition)
        {
            var cookable = item.info.GetComponent<ItemModCookable>();

            if (item.amount < item.info.stackable && item.info == itemDefinition)
                return true;

            if (oven.allowByproductCreation && oven.fuelType.GetComponent<ItemModBurnable>().byproductItem == item.info)
                return true;

            if (cookable == null || cookable.becomeOnCooked == itemDefinition)
                return true;

            if (CanCook(cookable, oven))
                return true;

            return false;
        }

        private void OnConsumeFuel(BaseOven oven, Item fuel, ItemModBurnable burnable)
        {
            if (compatibleOvens.Contains(oven.ShortPrefabName))
                queuedUiUpdates.Push(oven);
        }

        private List<BasePlayer> GetLooters(BaseOven oven)
        {
            if (looters.ContainsKey(oven))
                return looters[oven];

            return null;
        }

        private void AddLooter(BaseOven oven, BasePlayer player)
        {
            if (!looters.ContainsKey(oven))
                looters[oven] = new List<BasePlayer>();

            var list = looters[oven];
            list.Add(player);
        }

        private void RemoveLooter(BaseOven oven, BasePlayer player)
        {
            if (!looters.ContainsKey(oven))
                return;

            looters[oven].Remove(player);
        }

        private object CanMoveItem(Item item, PlayerInventory playerLoot, uint targetContainer, int targetSlot)
        {
            Func<object> splitFunc = () =>
            {
                using (TimeWarning.New("FurnaceSplitter.CanMoveItem", 0.005f))
                {
                    var player = Extensions.GetBaseEntity(playerLoot.loot);
                    var container = playerLoot.FindContainer(targetContainer);
                    var originalContainer = item.GetRootContainer();

                    if (player == null || !GetEnabled(player))
                        return null;

                    var playerOptions = allPlayerOptions[player.userID];

                    if (container == null || container == item.GetRootContainer())
                        return null;

                    var oven = container.entityOwner as BaseOven;
                    var cookable = item.info.GetComponent<ItemModCookable>();

                    if (oven == null || cookable == null)
                        return null;

                    int totalSlots = 2 + (oven.allowByproductCreation ? 1 : 0);

                    if (playerOptions.TotalStacks.ContainsKey(oven.ShortPrefabName))
                    {
                        totalSlots = playerOptions.TotalStacks[oven.ShortPrefabName];
                    }

                    float workTemperature = Extensions.GetWorkTemperature(oven);

                    if (cookable.lowTemp > workTemperature || cookable.highTemp < workTemperature)
                        return null;

                    int invalidItemsCount = container.itemList.Count(slotItem => !IsSlotCompatible(slotItem, oven, item.info));
                    int numOreSlots = Math.Min(container.capacity - invalidItemsCount, totalSlots);
                    int totalMoved = 0;
                    int totalAmount = Math.Min(item.amount + container.itemList.Where(slotItem => slotItem.info == item.info).Take(numOreSlots).Sum(slotItem => slotItem.amount), item.info.stackable * numOreSlots);
                    
                    if (numOreSlots <= 0)
                    {
                        return true;
                    }

                    //Puts("---------------------------");
                    
                    int totalStackSize = Math.Min(totalAmount / numOreSlots, item.info.stackable);
                    int remaining = totalAmount - (totalAmount / numOreSlots) * numOreSlots;
                    
                    List<int> addedSlots = new List<int>();

                    //Puts("total: {0}, remaining: {1}, totalStackSize: {2}", totalAmount, remaining, totalStackSize);
                    
                    List<OvenSlot> ovenSlots = new List<OvenSlot>();

                    for (int i = 0; i < numOreSlots; ++i)
                    {
                        Item existingItem;
                        var slot = FindMatchingSlotIndex(container, out existingItem, item.info, addedSlots);

                        if (slot == -1) // full
                        {
                            return true;
                        }

                        addedSlots.Add(slot);

                        var ovenSlot = new OvenSlot
                        {
                            Position = existingItem?.position,
                            Index = slot,
                            Item = existingItem
                        };
                        
                        int currentAmount = existingItem?.amount ?? 0;
                        int missingAmount = totalStackSize - currentAmount + (i < remaining ? 1 : 0);
                        ovenSlot.DeltaAmount = missingAmount;
                        
                        //Puts("[{0}] current: {1}, delta: {2}, total: {3}", slot, currentAmount, ovenSlot.DeltaAmount, currentAmount + missingAmount);

                        if (currentAmount + missingAmount <= 0)
                            continue;

                        ovenSlots.Add(ovenSlot);
                    }
                    
                    foreach (var slot in ovenSlots)
                    {
                        if (slot.Item == null)
                        {
                            var newItem = ItemManager.Create(item.info, slot.DeltaAmount, item.skin);
                            slot.Item = newItem;
                            newItem.MoveToContainer(container, slot.Position ?? slot.Index);
                        }
                        else
                        {
                            slot.Item.amount += slot.DeltaAmount;
                        }

                        totalMoved += slot.DeltaAmount;
                    }
                    
                    if (totalMoved >= item.amount)
                        item.Remove();
                    else
                        item.amount -= totalMoved;

                    container.MarkDirty();
                    originalContainer.MarkDirty();
                    return true;
                }
            };

            object returnValue = splitFunc();

            {
                var container = playerLoot.FindContainer(targetContainer);
                var oven = container?.entityOwner as BaseOven ?? item.GetRootContainer().entityOwner as BaseOven;

                if (oven != null && compatibleOvens.Contains(oven.ShortPrefabName))
                    queuedUiUpdates.Push(oven);
            }

            return returnValue;
        }

        private int FindMatchingSlotIndex(ItemContainer container, out Item existingItem, ItemDefinition itemType, List<int> indexBlacklist)
        {
            existingItem = null;
            int firstIndex = -1;
            Dictionary<int, Item> existingItems = new Dictionary<int, Item>();

            for (int i = 0; i < container.capacity; ++i)
            {
                if (indexBlacklist.Contains(i))
                    continue;

                Item itemSlot = container.GetSlot(i);
                if (itemSlot == null || (itemType != null && itemSlot.info == itemType))
                {
                    if (itemSlot != null)
                        existingItems.Add(i, itemSlot);

                    if (firstIndex == -1)
                    {
                        existingItem = itemSlot;
                        firstIndex = i;
                    }
                }
            }

            if (existingItems.Count <= 0 && firstIndex != -1)
            {
                return firstIndex;
            }
            else if (existingItems.Count > 0)
            {
                var largestStackItem = existingItems.OrderByDescending(kv => kv.Value.amount).First();
                existingItem = largestStackItem.Value;
                return largestStackItem.Key;
            }

            existingItem = null;
            return -1;
        }

        private void OnLootEntity(BasePlayer player, BaseEntity entity)
        {
            var oven = entity as BaseOven;

            if (oven == null || !compatibleOvens.Contains(oven.ShortPrefabName))
                return;
            
            AddLooter(oven, player);
            queuedUiUpdates.Push(oven);
        }

        private void OnLootEntityEnd(BasePlayer player, BaseCombatEntity entity)
        {
            var oven = entity as BaseOven;

            if (oven == null || !compatibleOvens.Contains(oven.ShortPrefabName))
                return;

            DestroyUI(player);
            RemoveLooter(oven, player);
        }

        private void OnOvenToggle(BaseOven oven, BasePlayer player)
        {
            if (compatibleOvens.Contains(oven.ShortPrefabName))
                queuedUiUpdates.Push(oven);
        }

        private void CreateUiIfFurnaceOpen(BasePlayer player)
        {
            var oven = player.inventory.loot?.entitySource as BaseOven;

            if (oven != null && compatibleOvens.Contains(oven.ShortPrefabName))
                queuedUiUpdates.Push(oven);
        }

        private CuiElementContainer CreateUi(BasePlayer player, BaseOven oven, OvenInfo ovenInfo)
        {
            var options = allPlayerOptions[player.userID];
            int totalSlots = GetTotalStacksOption(player, oven) ?? oven.inventory.capacity - (oven.allowByproductCreation ? 1 : 2);
            string remainingTimeStr;
            string neededFuelStr;

            if (ovenInfo.ETA <= 0)
            {
                remainingTimeStr = "0s";
                neededFuelStr = "0";
            }
            else
            {
                remainingTimeStr = FormatTime(ovenInfo.ETA);
                neededFuelStr = ovenInfo.FuelNeeded.ToString("##,###");
            }
            
            string contentColor = "0.7 0.7 0.7 1.0";
            int contentSize = 10;
            string toggleStateStr = (!options.Enabled).ToString();
            string toggleButtonColor = !options.Enabled
                                        ? "0.415 0.5 0.258 0.4"
                                        : "0.8 0.254 0.254 0.4";
            string toggleButtonTextColor = !options.Enabled
                                            ? "0.607 0.705 0.431"
                                            : "0.705 0.607 0.431";
            string buttonColor = "0.75 0.75 0.75 0.1";
            string buttonTextColor = "0.77 0.68 0.68 1";

            int nextDecrementSlot = totalSlots - 1;
            int nextIncrementSlot = totalSlots + 1;

            DestroyUI(player);

            var result = new CuiElementContainer();
            var rootPanelName = result.Add(new CuiPanel
            {
                Image = new CuiImageComponent
                {
                    Color = "0 0 0 0"
                },
                RectTransform =
                {
                    AnchorMin = "0.6505 0.022",
                    AnchorMax = "0.829 0.133"
                }
            }, "Hud.Menu");

            var headerPanel = result.Add(new CuiPanel
            {
                Image = new CuiImageComponent
                {
                    Color = "0.75 0.75 0.75 0.1"
                },
                RectTransform =
                {
                    AnchorMin = "0 0.775",
                    AnchorMax = "1 1"
                }
            }, rootPanelName);

            // Header label
            result.Add(new CuiLabel
            {
                RectTransform =
                {
                    AnchorMin = "0.051 0",
                    AnchorMax = "1 0.95"
                },
                Text =
                {
                    Text = lang.GetMessage("title", this, player.UserIDString),
                    Align = TextAnchor.MiddleLeft,
                    Color = "0.77 0.68 0.68 1",
                    FontSize = 13
                }
            }, headerPanel);

            var contentPanel = result.Add(new CuiPanel
            {
                Image = new CuiImageComponent
                {
                    Color = "0.65 0.65 0.65 0.06"
                },
                RectTransform =
                {
                    AnchorMin = "0 0",
                    AnchorMax = "1 0.74"
                }
            }, rootPanelName);

            // ETA label
            result.Add(new CuiLabel
            {
                RectTransform =
                {
                    AnchorMin = "0.02 0.7",
                    AnchorMax = "0.98 1"
                },
                Text =
                {
                    Text = string.Format("{0}: " + (ovenInfo.ETA > 0 ? "~" : "") + remainingTimeStr + " (" + neededFuelStr +  " " + oven.fuelType.displayName.english.ToLower() + ")", lang.GetMessage("eta", this, player.UserIDString)),
                    Align = TextAnchor.MiddleLeft,
                    Color = contentColor,
                    FontSize = contentSize
                }
            }, contentPanel);

            // Toggle button
            result.Add(new CuiButton
            {
                RectTransform =
                {
                    AnchorMin = "0.02 0.4",
                    AnchorMax = "0.25 0.7"
                },
                Button =
                {
                    Command = "furnacesplitter.enabled " + toggleStateStr,
                    Color = toggleButtonColor
                },
                Text =
                {
                    Align = TextAnchor.MiddleCenter,
                    Text = options.Enabled ? lang.GetMessage("turnoff", this, player.UserIDString) : lang.GetMessage("turnon", this, player.UserIDString),
                    Color = toggleButtonTextColor,
                    FontSize = 11
                }
            }, contentPanel);

            // Decrease stack button
            result.Add(new CuiButton
            {
                RectTransform =
                {
                    AnchorMin = "0.02 0.05",
                    AnchorMax = "0.07 0.35"
                },
                Button =
                {
                    Command = "furnacesplitter.totalstacks " + nextDecrementSlot,
                    Color = buttonColor
                },
                Text =
                {
                    Align = TextAnchor.MiddleCenter,
                    Text = "<",
                    Color = buttonTextColor,
                    FontSize = contentSize
                }
            }, contentPanel);

            // Empty slots label
            result.Add(new CuiLabel
            {
                RectTransform =
                {
                    AnchorMin = "0.08 0.05",
                    AnchorMax = "0.19 0.35"
                },
                Text =
                {
                    Align = TextAnchor.MiddleCenter,
                    Text = totalSlots.ToString(),
                    Color = contentColor,
                    FontSize = contentSize
                }
            }, contentPanel);

            // Increase stack button
            result.Add(new CuiButton
            {
                RectTransform =
                {
                    AnchorMin = "0.19 0.05",
                    AnchorMax = "0.25 0.35"
                },
                Button =
                {
                    Command = "furnacesplitter.totalstacks " + nextIncrementSlot,
                    Color = buttonColor
                },
                Text =
                {
                    Align = TextAnchor.MiddleCenter,
                    Text = ">",
                    Color = buttonTextColor,
                    FontSize = contentSize
                }
            }, contentPanel);

            // Stack itemType label
            result.Add(new CuiLabel
            {
                RectTransform =
                {
                    AnchorMin = "0.27 0.05",
                    AnchorMax = "1 0.35"
                },
                Text =
                {
                    Align = TextAnchor.MiddleLeft,
                    Text = string.Format("({0})", lang.GetMessage("totalstacks", this, player.UserIDString)),
                    Color = contentColor,
                    FontSize = contentSize
                }
            }, contentPanel);

            openUis.Add(player.userID, rootPanelName);
            CuiHelper.AddUi(player, result);
            return result;
        }

        private string FormatTime(float totalSeconds)
        {
            var hours = Math.Floor(totalSeconds / 3600);
            var minutes = Math.Floor((totalSeconds / 60) % 60);
            var seconds = totalSeconds % 60;

            if (hours <= 0 && minutes <= 0)
                return seconds + "s";
            else if (hours <= 0)
                return minutes + "m" + seconds + "s";
            else
                return hours + "h" + minutes + "m" + seconds + "s";
        }

        private Dictionary<ItemDefinition, float> GetSmeltTimes(BaseOven oven)
        {
            var container = oven.inventory;
            var cookables = container.itemList.Where(item =>
            {
                var cookable = item.info.GetComponent<ItemModCookable>();
                return cookable != null && CanCook(cookable, oven);
            }).ToList();

            if (cookables.Count == 0)
                return new Dictionary<ItemDefinition, float>();

            var distinctCookables = cookables.GroupBy(item => item.info, item => item).ToList();
            Dictionary<ItemDefinition, int> amounts = new Dictionary<ItemDefinition, int>();

            foreach (var group in distinctCookables)
            {
                int biggestAmount = Extensions.ToIEnumerable(group.GetEnumerator()).Max(item => item.amount);
                amounts.Add(group.Key, biggestAmount);
            }

            var smeltTimes = amounts.ToDictionary(kv => kv.Key, kv => GetSmeltTime(kv.Key.GetComponent<ItemModCookable>(), kv.Value));
            return smeltTimes;
        }

        private bool CanCook(ItemModCookable cookable, BaseOven oven)
        {
            float workTemperature = Extensions.GetWorkTemperature(oven);
            return workTemperature >= cookable.lowTemp && workTemperature <= cookable.highTemp;
        }

        private float GetSmeltTime(ItemModCookable cookable, int amount)
        {
            float smeltTime = cookable.cookTime * amount;
            return smeltTime;
        }

        private int? GetTotalStacksOption(BasePlayer player, BaseOven oven)
        {
            var options = allPlayerOptions[player.userID];

            if (options.TotalStacks.ContainsKey(oven.ShortPrefabName))
                return options.TotalStacks[oven.ShortPrefabName];

            return null;
        }

        private void DestroyUI(BasePlayer player)
        {
            if (!openUis.ContainsKey(player.userID))
                return;

            var uiName = openUis[player.userID];
            CuiHelper.DestroyUi(player, uiName);
            openUis.Remove(player.userID);
        }

        [ConsoleCommand("furnacesplitter.enabled")]
        private void ConsoleCommand_Toggle(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();

            if (!arg.HasArgs())
            {
                player.ConsoleMessage(GetEnabled(player).ToString());
                return;
            }

            bool enabled = arg.GetBool(0);
            SetEnabled(player, enabled);
            CreateUiIfFurnaceOpen(player);
        }

        [ConsoleCommand("furnacesplitter.totalstacks")]
        private void ConsoleCommand_TotalStacks(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            var lootSource = player.inventory.loot?.entitySource as BaseOven;

            if (lootSource == null || !compatibleOvens.Contains(lootSource.ShortPrefabName))
            {
                player.ConsoleMessage("Current loot source invalid");
                return;
            }

            string ovenName = lootSource.ShortPrefabName;
            var playerOption = allPlayerOptions[player.userID];

            if (playerOption.TotalStacks.ContainsKey(ovenName))
            {
                if (!arg.HasArgs())
                {
                    player.ConsoleMessage(playerOption.TotalStacks[ovenName].ToString());
                }
                else
                {
                    var newValue = (int) Mathf.Clamp(arg.GetInt(0), 0, lootSource.inventory.capacity);
                    playerOption.TotalStacks[ovenName] = newValue;
                }
            }
            else
            {
                player.ConsoleMessage("Unsupported furnace '" + ovenName + "'");
            }

            CreateUiIfFurnaceOpen(player);
        }
    }
}
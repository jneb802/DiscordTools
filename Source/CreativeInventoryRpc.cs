using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace DiscordTools
{
    internal static class CreativeInventoryRpc
    {
        private const int ProtocolVersion = 1;
        private const string ExtraSlotsApiTypeName = "ExtraSlots.API";

        public static void OnRequest(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || ZNet.instance.IsServer())
            {
                return;
            }

            string requestId = string.Empty;
            ZDOID characterId = ZDOID.None;
            bool includeItems = true;

            try
            {
                int version = pkg.ReadInt();
                if (version != ProtocolVersion)
                {
                    SendUnavailable(sender, requestId, characterId, $"Unsupported creative inventory protocol version {version}.");
                    return;
                }

                requestId = pkg.ReadString();
                characterId = pkg.ReadZDOID();
                includeItems = pkg.ReadBool();

                CreativeInventorySnapshot snapshot = BuildSnapshot(characterId, includeItems);
                SendResponse(sender, requestId, characterId, snapshot);
            }
            catch (Exception ex)
            {
                DiscordToolsPlugin.Log.LogWarning($"Failed to answer creative inventory request {requestId}: {ex}");
                SendUnavailable(sender, requestId, characterId, "Failed to read client inventory.");
            }
        }

        private static CreativeInventorySnapshot BuildSnapshot(ZDOID expectedCharacterId, bool includeItems)
        {
            Player player = Player.m_localPlayer;
            if (player == null || player.m_nview == null || !player.m_nview.IsValid())
            {
                return CreativeInventorySnapshot.Unavailable("Local player is not available.");
            }

            ZDO playerZdo = player.m_nview.GetZDO();
            if (playerZdo == null)
            {
                return CreativeInventorySnapshot.Unavailable("Local player character is not available.");
            }

            if (!expectedCharacterId.IsNone() && playerZdo.m_uid != expectedCharacterId)
            {
                return CreativeInventorySnapshot.Unavailable("Local player character did not match the requested character.");
            }

            Inventory inventory = player.GetInventory();
            if (inventory == null)
            {
                return CreativeInventorySnapshot.Unavailable("Local player inventory is not available.");
            }

            List<ItemDrop.ItemData> playerInventoryItems = inventory.GetAllItems()
                .Where(item => item != null)
                .ToList();

            ExtraSlotsReadResult extraSlots = ReadExtraSlotsItems();
            if (!extraSlots.Available && extraSlots.ModLoaded)
            {
                return CreativeInventorySnapshot.Unavailable(extraSlots.Error);
            }

            List<ItemDrop.ItemData> uniqueItems = new();
            AddUnique(uniqueItems, playerInventoryItems);
            AddUnique(uniqueItems, extraSlots.Items);

            CreativeInventorySnapshot snapshot = new()
            {
                Available = true,
                Error = string.Empty,
                PlayerId = player.GetPlayerID(),
                PlayerName = player.GetPlayerName(),
                PlayerInventoryCount = playerInventoryItems.Count,
                ExtraSlotsAvailable = extraSlots.Available,
                ExtraSlotsLoaded = extraSlots.ModLoaded,
                ExtraSlotsCount = extraSlots.Items.Count,
                TotalUniqueCount = uniqueItems.Count
            };

            if (includeItems)
            {
                snapshot.Items.AddRange(playerInventoryItems.Select(item => CreativeInventoryItem.FromItem("player", item)));
                snapshot.Items.AddRange(extraSlots.Items.Select(item => CreativeInventoryItem.FromItem("extraSlots", item)));
            }

            return snapshot;
        }

        private static ExtraSlotsReadResult ReadExtraSlotsItems()
        {
            Type? apiType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(ExtraSlotsApiTypeName, throwOnError: false))
                .FirstOrDefault(type => type != null);

            if (apiType == null)
            {
                return ExtraSlotsReadResult.NotLoaded();
            }

            MethodInfo? method = apiType.GetMethod("GetAllExtraSlotsItems", BindingFlags.Public | BindingFlags.Static);
            if (method == null)
            {
                return ExtraSlotsReadResult.Failed("ExtraSlots API did not expose GetAllExtraSlotsItems.");
            }

            try
            {
                object? result = method.Invoke(null, Array.Empty<object>());
                if (result is not IEnumerable enumerable)
                {
                    return ExtraSlotsReadResult.Failed("ExtraSlots API returned no item list.");
                }

                List<ItemDrop.ItemData> items = new();
                foreach (object value in enumerable)
                {
                    if (value is ItemDrop.ItemData item)
                    {
                        items.Add(item);
                    }
                }

                return ExtraSlotsReadResult.Loaded(items);
            }
            catch (Exception ex)
            {
                return ExtraSlotsReadResult.Failed("ExtraSlots API read failed: " + ex.Message);
            }
        }

        private static void AddUnique(List<ItemDrop.ItemData> target, IEnumerable<ItemDrop.ItemData> items)
        {
            foreach (ItemDrop.ItemData item in items)
            {
                if (!target.Contains(item))
                {
                    target.Add(item);
                }
            }
        }

        private static void SendUnavailable(long target, string requestId, ZDOID characterId, string error)
        {
            SendResponse(target, requestId, characterId, CreativeInventorySnapshot.Unavailable(error));
        }

        private static void SendResponse(long target, string requestId, ZDOID characterId, CreativeInventorySnapshot snapshot)
        {
            ZPackage response = new();
            response.Write(ProtocolVersion);
            response.Write(requestId);
            response.Write(characterId);
            response.Write(snapshot.Available);
            response.Write(snapshot.Error);
            response.Write(snapshot.PlayerId);
            response.Write(snapshot.PlayerName);
            response.Write(snapshot.PlayerInventoryCount);
            response.Write(snapshot.ExtraSlotsLoaded);
            response.Write(snapshot.ExtraSlotsAvailable);
            response.Write(snapshot.ExtraSlotsCount);
            response.Write(snapshot.TotalUniqueCount);
            response.Write(snapshot.Items.Count);

            foreach (CreativeInventoryItem item in snapshot.Items)
            {
                response.Write(item.Source);
                response.Write(item.PrefabName);
                response.Write(item.SharedName);
                response.Write(item.Stack);
                response.Write(item.Quality);
                response.Write(item.Equipped);
                response.Write(item.GridX);
                response.Write(item.GridY);
            }

            ZRoutedRpc.instance.InvokeRoutedRPC(target, RpcNames.CreativeInventoryResponse, response);
        }

        private sealed class CreativeInventorySnapshot
        {
            public bool Available { get; set; }
            public string Error { get; set; } = string.Empty;
            public long PlayerId { get; set; }
            public string PlayerName { get; set; } = string.Empty;
            public int PlayerInventoryCount { get; set; }
            public bool ExtraSlotsLoaded { get; set; }
            public bool ExtraSlotsAvailable { get; set; }
            public int ExtraSlotsCount { get; set; }
            public int TotalUniqueCount { get; set; }
            public List<CreativeInventoryItem> Items { get; } = new();

            public static CreativeInventorySnapshot Unavailable(string error)
            {
                return new CreativeInventorySnapshot
                {
                    Available = false,
                    Error = error
                };
            }
        }

        private sealed class CreativeInventoryItem
        {
            public string Source { get; set; } = string.Empty;
            public string PrefabName { get; set; } = string.Empty;
            public string SharedName { get; set; } = string.Empty;
            public int Stack { get; set; }
            public int Quality { get; set; }
            public bool Equipped { get; set; }
            public int GridX { get; set; }
            public int GridY { get; set; }

            public static CreativeInventoryItem FromItem(string source, ItemDrop.ItemData item)
            {
                return new CreativeInventoryItem
                {
                    Source = source,
                    PrefabName = item.m_dropPrefab != null ? item.m_dropPrefab.name : string.Empty,
                    SharedName = item.m_shared?.m_name ?? string.Empty,
                    Stack = item.m_stack,
                    Quality = item.m_quality,
                    Equipped = item.m_equipped,
                    GridX = item.m_gridPos.x,
                    GridY = item.m_gridPos.y
                };
            }
        }

        private sealed class ExtraSlotsReadResult
        {
            public bool ModLoaded { get; private set; }
            public bool Available { get; private set; }
            public string Error { get; private set; } = string.Empty;
            public List<ItemDrop.ItemData> Items { get; } = new();

            public static ExtraSlotsReadResult NotLoaded()
            {
                return new ExtraSlotsReadResult
                {
                    ModLoaded = false,
                    Available = true
                };
            }

            public static ExtraSlotsReadResult Loaded(IEnumerable<ItemDrop.ItemData> items)
            {
                ExtraSlotsReadResult result = new()
                {
                    ModLoaded = true,
                    Available = true
                };
                result.Items.AddRange(items);
                return result;
            }

            public static ExtraSlotsReadResult Failed(string error)
            {
                return new ExtraSlotsReadResult
                {
                    ModLoaded = true,
                    Available = false,
                    Error = error
                };
            }
        }
    }
}

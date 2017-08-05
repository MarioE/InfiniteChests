using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Terraria;
using TShockAPI;

namespace InfiniteChests.Database
{
    /// <summary>
    ///     Represents a chest.
    /// </summary>
    public sealed class Chest
    {
        private static readonly object ChestLock = new object();

        private DateTime _lastRefill = DateTime.UtcNow;

        /// <summary>
        ///     Initializes a new instance of the <see cref="Chest" /> class with the specified coordinates, name, and owner name.
        /// </summary>
        /// <param name="x">The X coordinate.</param>
        /// <param name="y">The Y coordinate.</param>
        /// <param name="name">The name, which must not be <c>null</c>.</param>
        /// <param name="ownerName">The owner name.</param>
        public Chest(int x, int y, string name, string ownerName)
        {
            Debug.Assert(name != null, "Name must not be null.");

            X = x;
            Y = y;
            Name = name;
            OwnerName = ownerName;
        }

        /// <summary>
        ///     Gets the set of allowed group names.
        /// </summary>
        public ISet<string> AllowedGroupNames { get; } = new HashSet<string>();

        /// <summary>
        ///     Gets the set of allowed usernames.
        /// </summary>
        public ISet<string> AllowedUsernames { get; } = new HashSet<string>();

        /// <summary>
        ///     Gets a value indicating whether the chest can be used.
        /// </summary>
        public bool CanUse => !IsInUse || IsMultiUse;

        /// <summary>
        ///     Determines if the chest is empty. If the chest can refill, then the original items are checked instead.
        /// </summary>
        public bool IsEmpty => (RefillTime == null ? Items : OriginalItems).All(i => i.Stack == 0);

        /// <summary>
        ///     Gets or sets a value indicating whether the chest is in use.
        /// </summary>
        public bool IsInUse { get; set; }

        /// <summary>
        ///     Gets or sets a value indicating whether the chest is multiuse.
        /// </summary>
        public bool IsMultiUse { get; set; }

        /// <summary>
        ///     Gets or sets a value indicating whether the chest is public.
        /// </summary>
        public bool IsPublic { get; set; }

        /// <summary>
        ///     Gets the items.
        /// </summary>
        public NetItem[] Items { get; } = new NetItem[Terraria.Chest.maxItems];

        /// <summary>
        ///     Gets or sets the name.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        ///     Gets the only item ID contained in the chest, or -1 if this is not the case.
        /// </summary>
        public int OnlyItemId
        {
            get
            {
                var items = Items.Where(i => i.NetId > 0 && i.Stack > 0).ToList();
                return items.Count == 1 && items[0].Stack == 1 ? items[0].NetId : -1;
            }
        }

        /// <summary>
        ///     Gets the original items.
        /// </summary>
        public NetItem[] OriginalItems { get; } = new NetItem[Terraria.Chest.maxItems];

        /// <summary>
        ///     Gets or sets the owner name.
        /// </summary>
        public string OwnerName { get; set; }

        /// <summary>
        ///     Gets or sets the refill time.
        /// </summary>
        public TimeSpan? RefillTime { get; set; }

        /// <summary>
        ///     Gets the X coordinate.
        /// </summary>
        public int X { get; }

        /// <summary>
        ///     Gets the Y coordinate.
        /// </summary>
        public int Y { get; }

        /// <summary>
        ///     Determines if the specified player is allowed.
        /// </summary>
        /// <param name="player">The player, which must not be <c>null</c>.</param>
        /// <returns><c>true</c> if the player is allowed; otherwise, <c>false</c>.</returns>
        public bool IsAllowed(TSPlayer player)
        {
            Debug.Assert(player != null);

            if (player.HasPermission("infchests.admin"))
            {
                return true;
            }

            var username = player.User?.Name;
            var groupName = player.Group.Name;
            return IsPublic || OwnerName == username || AllowedUsernames.Contains(username) ||
                   AllowedGroupNames.Contains(groupName);
        }

        /// <summary>
        ///     Determines if the specified player can act like the owner.
        /// </summary>
        /// <param name="player">The player, which must not be <c>null</c>.</param>
        /// <returns><c>true</c> if the player can act like the owner; otherwise, <c>false</c>.</returns>
        public bool IsOwner(TSPlayer player)
        {
            Debug.Assert(player != null);

            return player.HasPermission("infchests.admin") || OwnerName == player.User?.Name;
        }

        /// <summary>
        ///     Shows the chest to the specified player.
        /// </summary>
        /// <param name="player">The player, which must not be <c>null</c>.</param>
        /// <param name="newChestId">A new chest ID to use for showing the chest.</param>
        public void ShowTo(TSPlayer player, int newChestId)
        {
            if (RefillTime != null && DateTime.UtcNow - _lastRefill > RefillTime)
            {
                Debug.WriteLine($"DEBUG: Chest at {X}, {Y} was refilled");
                _lastRefill = DateTime.UtcNow;
                for (var i = 0; i < Terraria.Chest.maxItems; ++i)
                {
                    Items[i] = OriginalItems[i];
                }
            }

            var session = player.GetSession();
            if (!session.ChestToId.TryGetValue(this, out var chestId))
            {
                chestId = newChestId;

                // Remove all mappings of the old chest ID.
                foreach (var kvp in session.ChestToId.Where(kvp => kvp.Value == chestId).ToList())
                {
                    session.ChestToId.Remove(kvp);
                }
                session.IdToChest.Remove(chestId);
            }

            session.ChestToId[this] = chestId;
            session.IdToChest[chestId] = this;

            Debug.WriteLine($"DEBUG: {player.Name} accessing chest at {X}, {Y} (ID: {chestId})");
            lock (ChestLock)
            {
                var terrariaChest = new Terraria.Chest {x = X, y = Y, name = Name};
                Main.chest[chestId] = terrariaChest;
                player.SendData(PacketTypes.ChestName, "", chestId, X, Y);
                for (var i = 0; i < Terraria.Chest.maxItems; ++i)
                {
                    var item = new Item();
                    item.SetDefaults(Items[i].NetId);
                    item.stack = Items[i].Stack;
                    item.prefix = Items[i].PrefixId;
                    terrariaChest.item[i] = item;
                    player.SendData(PacketTypes.ChestItem, "", chestId, i);
                }
                player.SendData(PacketTypes.ChestOpen, "", chestId);
                Main.chest[chestId] = null;
            }
            player.TPlayer.chest = chestId;

            // Sync player chest indices for everyone. We have to ensure that each player only receives the chest ID
            // that they received for the chest.
            foreach (var player2 in TShock.Players.Where(p => p?.Active == true && p != player))
            {
                var session2 = player2.GetSession();
                if (session2.ChestToId.TryGetValue(this, out var chestId2))
                {
                    player2.SendData(PacketTypes.SyncPlayerChestIndex, "", player.Index, chestId2);
                }
            }
        }
    }
}

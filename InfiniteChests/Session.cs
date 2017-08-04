using System;
using System.Collections.Generic;
using Terraria;
using Chest = InfiniteChests.Database.Chest;

namespace InfiniteChests
{
    /// <summary>
    ///     Holds session information.
    /// </summary>
    public sealed class Session
    {
        private int _nextChestId;

        /// <summary>
        ///     Gets a mapping from chests to IDs.
        /// </summary>
        public IDictionary<Chest, int> ChestToId { get; } = new Dictionary<Chest, int>();

        /// <summary>
        ///     Gets a mapping from IDs to chests.
        /// </summary>
        public IDictionary<int, Chest> IdToChest { get; } = new Dictionary<int, Chest>();

        /// <summary>
        ///     Gets or sets the pending chest action.
        /// </summary>
        public ChestAction PendingChestAction { get; set; }

        /// <summary>
        ///     Gets or sets the pending group name.
        /// </summary>
        public string PendingGroupName { get; set; }

        /// <summary>
        ///     Gets or sets the pending refill time.
        /// </summary>
        public TimeSpan? PendingRefillTime { get; set; }

        /// <summary>
        ///     Gets or sets the pending username.
        /// </summary>
        public string PendingUsername { get; set; }

        /// <summary>
        ///     Gets the next chest ID, which rotates around.
        /// </summary>
        /// <returns>The next chest ID.</returns>
        public int GetNextChestId()
        {
            ++_nextChestId;
            _nextChestId %= Main.maxChests;
            return _nextChestId;
        }
    }
}

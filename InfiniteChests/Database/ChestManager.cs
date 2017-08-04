using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using Mono.Data.Sqlite;
using Terraria;
using TShockAPI;
using TShockAPI.DB;

namespace InfiniteChests.Database
{
    /// <summary>
    ///     Represents a chest manager.
    /// </summary>
    public sealed class ChestManager : IDisposable
    {
        private readonly List<Chest> _chests = new List<Chest>();
        private readonly HashSet<Chest> _chestsToAdd = new HashSet<Chest>();
        private readonly HashSet<Chest> _chestsToRemove = new HashSet<Chest>();
        private readonly HashSet<Chest> _chestsToUpdate = new HashSet<Chest>();
        private readonly IDbConnection _connection;
        private readonly object _lock = new object();

        /// <summary>
        ///     Initializes a new instance of the <see cref="ChestManager" /> class with the specified connection.
        /// </summary>
        /// <param name="connection">The connection, which must not be <c>null</c>.</param>
        public ChestManager(IDbConnection connection)
        {
            Debug.Assert(connection != null, "Connection must not be null.");

            _connection = connection;
            _connection.Query("CREATE TABLE IF NOT EXISTS Chests (" +
                              "  X          INTEGER," +
                              "  Y          INTEGER," +
                              "  WorldId    INTEGER," +
                              "  Name       TEXT," +
                              "  OwnerName  TEXT," +
                              "  IsPublic   INTEGER DEFAULT 0," +
                              "  IsMultiUse INTEGER DEFAULT 0," +
                              "  RefillTime TEXT," +
                              "  UNIQUE(X, Y, WorldId) ON CONFLICT REPLACE)");
            _connection.Query("CREATE TABLE IF NOT EXISTS ChestHasItem (" +
                              "  X          INTEGER," +
                              "  Y          INTEGER," +
                              "  WorldId    INTEGER," +
                              "  ItemIndex  INTEGER," +
                              "  ItemId     INTEGER," +
                              "  StackSize  INTEGER," +
                              "  PrefixId   INTEGER," +
                              "  UNIQUE(X, Y, WorldId, ItemIndex) ON CONFLICT REPLACE," +
                              "  FOREIGN KEY(X, Y, WorldId) REFERENCES Chests(X, Y, WorldId) ON DELETE CASCADE)");
            _connection.Query("CREATE TABLE IF NOT EXISTS ChestHasGroup (" +
                              "  X          INTEGER," +
                              "  Y          INTEGER," +
                              "  WorldId    INTEGER," +
                              "  GroupName  TEXT," +
                              "  UNIQUE(X, Y, WorldId, GroupName) ON CONFLICT REPLACE," +
                              "  FOREIGN KEY(X, Y, WorldId) REFERENCES Chests(X, Y, WorldId) ON DELETE CASCADE)");
            _connection.Query("CREATE TABLE IF NOT EXISTS ChestHasUser (" +
                              "  X          INTEGER," +
                              "  Y          INTEGER," +
                              "  WorldId    INTEGER," +
                              "  Username   TEXT," +
                              "  UNIQUE(X, Y, WorldId, Username) ON CONFLICT REPLACE," +
                              "  FOREIGN KEY(X, Y, WorldId) REFERENCES Chests(X, Y, WorldId) ON DELETE CASCADE)");
        }

        public void Dispose()
        {
            _connection.Dispose();
        }

        /// <summary>
        ///     Adds a chest with the specified coordinates, name, and owner name. This will add the chest to a queue.
        /// </summary>
        /// <param name="x">The X coordinate.</param>
        /// <param name="y">The Y coordinate.</param>
        /// <param name="name">The name.</param>
        /// <param name="ownerName">The owner name.</param>
        /// <returns>The resulting chest.</returns>
        public Chest Add(int x, int y, string name, string ownerName)
        {
            lock (_lock)
            {
                var chest = new Chest(x, y, name, ownerName);

                // We have to remove chests in the same position, similar to the way the insert works.
                _chests.RemoveAll(c => c.X == x && c.Y == y);
                _chests.Add(chest);
                _chestsToAdd.Add(chest);
                return chest;
            }
        }

        /// <summary>
        ///     Flushes the queue.
        /// </summary>
        public void FlushQueue()
        {
            lock (_lock)
            {
                foreach (var chest in _chestsToAdd)
                {
                    _connection.Query("INSERT INTO Chests (X, Y, WorldId, Name, OwnerName) VALUES (@0, @1, @2, @3, @4)",
                                      chest.X, chest.Y, Main.worldID, chest.Name, chest.OwnerName);
                }
                _chestsToAdd.Clear();

                foreach (var chest in _chestsToRemove)
                {
                    _connection.Query("DELETE FROM Chests WHERE X = @0 AND Y = @1 AND WorldId = @2",
                                      chest.X, chest.Y, Main.worldID);
                }
                _chestsToRemove.Clear();

                foreach (var chest in _chestsToUpdate)
                {
                    _connection.Query("UPDATE Chests SET Name = @0, OwnerName = @1, IsPublic = @2, IsMultiUse = @3," +
                                      "  RefillTime = @4 " +
                                      "WHERE X = @5 AND Y = @6 AND WorldId = @7",
                                      chest.Name, chest.OwnerName, chest.IsPublic ? 1 : 0, chest.IsMultiUse ? 1 : 0,
                                      chest.RefillTime?.ToString(), chest.X, chest.Y, Main.worldID);
                    _connection.Query("DELETE FROM ChestHasGroup WHERE X = @0 AND Y = @1 AND WorldId = @2",
                                      chest.X, chest.Y, Main.worldID);
                    _connection.Query("DELETE FROM ChestHasUser WHERE X = @0 AND Y = @1 AND WorldId = @2",
                                      chest.X, chest.Y, Main.worldID);
                    using (var db = _connection.CloneEx())
                    {
                        db.Open();
                        using (var transaction = db.BeginTransaction())
                        {
                            // Only perform item updates for chests that never refill.
                            if (chest.RefillTime == null)
                            {
                                using (var command = (SqliteCommand)db.CreateCommand())
                                {
                                    command.CommandText = "INSERT INTO ChestHasItem (X, Y, WorldId, ItemIndex," +
                                                          "  ItemId, StackSize, PrefixId)" +
                                                          "VALUES (@0, @1, @2, @3, @4, @5, @6)";
                                    for (var i = 0; i <= 6; ++i)
                                    {
                                        command.AddParameter($"@{i}", null);
                                    }
                                    command.Parameters["@0"].Value = chest.X;
                                    command.Parameters["@1"].Value = chest.Y;
                                    command.Parameters["@2"].Value = Main.worldID;

                                    for (var i = 0; i < Terraria.Chest.maxItems; ++i)
                                    {
                                        command.Parameters["@3"].Value = i;
                                        command.Parameters["@4"].Value = chest.Items[i].NetId;
                                        command.Parameters["@5"].Value = chest.Items[i].Stack;
                                        command.Parameters["@6"].Value = chest.Items[i].PrefixId;
                                        command.ExecuteNonQuery();
                                    }
                                }
                            }
                            using (var command = (SqliteCommand)db.CreateCommand())
                            {
                                command.CommandText = "INSERT INTO ChestHasGroup (X, Y, WorldId, GroupName)" +
                                                      "VALUES (@0, @1, @2, @3)";
                                for (var i = 0; i <= 3; ++i)
                                {
                                    command.AddParameter($"@{i}", null);
                                }
                                command.Parameters["@0"].Value = chest.X;
                                command.Parameters["@1"].Value = chest.Y;
                                command.Parameters["@2"].Value = Main.worldID;

                                foreach (var groupName in chest.AllowedGroupNames)
                                {
                                    command.Parameters["@3"].Value = groupName;
                                    command.ExecuteNonQuery();
                                }
                            }
                            using (var command = (SqliteCommand)db.CreateCommand())
                            {
                                command.CommandText = "INSERT INTO ChestHasUser (X, Y, WorldId, Username)" +
                                                      "VALUES (@0, @1, @2, @3)";
                                for (var i = 0; i <= 3; ++i)
                                {
                                    command.AddParameter($"@{i}", null);
                                }
                                command.Parameters["@0"].Value = chest.X;
                                command.Parameters["@1"].Value = chest.Y;
                                command.Parameters["@2"].Value = Main.worldID;

                                foreach (var username in chest.AllowedUsernames)
                                {
                                    command.Parameters["@3"].Value = username;
                                    command.ExecuteNonQuery();
                                }
                            }
                            transaction.Commit();
                        }
                    }
                }
                _chestsToUpdate.Clear();
            }
        }

        /// <summary>
        ///     Gets all of the chests.
        /// </summary>
        /// <returns>The chests.</returns>
        public IEnumerable<Chest> GetAll()
        {
            lock (_lock)
            {
                return _chests.ToList();
            }
        }

        /// <summary>
        ///     Gets or converts the chest at the specified coordinates.
        /// </summary>
        /// <param name="x">The X coordinate.</param>
        /// <param name="y">The Y coordinate.</param>
        /// <returns>The chest, or <c>null</c> if everything failed.</returns>
        public Chest GetOrConvert(int x, int y)
        {
            lock (_lock)
            {
                // First, check if there's a Terraria chest in the way. We're essentially doing lazy conversions.
                var terrariaChest = Main.chest.FirstOrDefault(c => c?.x == x && c.y == y);
                if (terrariaChest == null)
                {
                    return _chests.FirstOrDefault(c => c.X == x && c.Y == y);
                }

                Debug.WriteLine($"DEBUG: Converting chest at {x}, {y}");
                // Make sure to null out the Terraria chest, so that it doesn't interfere with us.
                for (var i = 0; i < Main.maxChests; ++i)
                {
                    if (Main.chest[i] == terrariaChest)
                    {
                        Main.chest[i] = null;
                    }
                }

                var chest = Add(x, y, "", null);
                chest.IsPublic = true;
                for (var i = 0; i < Terraria.Chest.maxItems; ++i)
                {
                    var item = (NetItem)terrariaChest.item[i];
                    chest.OriginalItems[i] = item;
                    chest.Items[i] = item;
                }
                Update(chest);
                return chest;
            }
        }

        /// <summary>
        ///     Loads the chests.
        /// </summary>
        public void Load()
        {
            lock (_lock)
            {
                _chests.Clear();
                using (var reader = _connection.QueryReader("SELECT * FROM Chests WHERE WorldId = @0", Main.worldID))
                {
                    while (reader.Read())
                    {
                        var x = reader.Get<int>("X");
                        var y = reader.Get<int>("Y");
                        var name = reader.Get<string>("Name");
                        var ownerName = reader.Get<string>("OwnerName");
                        var isPublic = reader.Get<int>("IsPublic") == 1;
                        var isMultiUse = reader.Get<int>("IsMultiUse") == 1;
                        var refillTime = reader.Get<string>("RefillTime");

                        var chest = new Chest(x, y, name, ownerName)
                        {
                            IsPublic = isPublic,
                            IsMultiUse = isMultiUse,
                            RefillTime = string.IsNullOrEmpty(refillTime) ? (TimeSpan?)null : TimeSpan.Parse(refillTime)
                        };
                        using (var reader2 = _connection.QueryReader(
                            "SELECT * FROM ChestHasItem WHERE X = @0 AND Y = @1 AND WorldId = @2", x, y, Main.worldID))
                        {
                            while (reader2.Read())
                            {
                                var index = reader2.Get<int>("ItemIndex");
                                var itemId = reader2.Get<int>("ItemId");
                                var stackSize = reader2.Get<int>("StackSize");
                                var prefixId = reader2.Get<byte>("PrefixId");
                                chest.Items[index] = new NetItem(itemId, stackSize, prefixId);
                                chest.OriginalItems[index] = new NetItem(itemId, stackSize, prefixId);
                            }
                        }
                        using (var reader2 = _connection.QueryReader(
                            "SELECT * FROM ChestHasGroup WHERE X = @0 AND Y = @1 AND WorldId = @2", x, y, Main.worldID))
                        {
                            while (reader2.Read())
                            {
                                var groupName = reader2.Get<string>("GroupName");
                                chest.AllowedGroupNames.Add(groupName);
                            }
                        }
                        using (var reader2 = _connection.QueryReader(
                            "SELECT * FROM ChestHasUser WHERE X = @0 AND Y = @1 AND WorldId = @2", x, y, Main.worldID))
                        {
                            while (reader2.Read())
                            {
                                var username = reader2.Get<string>("Username");
                                chest.AllowedUsernames.Add(username);
                            }
                        }
                        _chests.Add(chest);
                    }
                }
            }
        }

        /// <summary>
        ///     Removes the specified chest. This will add the chest to a queue.
        /// </summary>
        /// <param name="chest">The chest, which must not be <c>null</c>.</param>
        public void Remove(Chest chest)
        {
            Debug.Assert(chest != null, "Chest must not be null.");

            lock (_lock)
            {
                _chests.Remove(chest);
                _chestsToRemove.Add(chest);

                // Remove this chest from all players' ID caches to prevent memory leaks.
                foreach (var player in TShock.Players.Where(p => p?.Active == true))
                {
                    var session = player.GetSession();
                    session.ChestToId.Remove(chest);
                    foreach (var kvp in session.IdToChest.Where(kvp => kvp.Value == chest).ToList())
                    {
                        session.IdToChest.Remove(kvp);
                    }
                }
            }
        }

        /// <summary>
        ///     Updates the specified chest. This will add the chest to a queue.
        /// </summary>
        /// <param name="chest">The chest, which must not be <c>null</c>.</param>
        public void Update(Chest chest)
        {
            Debug.Assert(chest != null, "Chest must not be null.");

            lock (_lock)
            {
                _chestsToUpdate.Add(chest);
            }
        }
    }
}

using System;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using InfiniteChests.Database;
using Microsoft.Xna.Framework;
using Mono.Data.Sqlite;
using MySql.Data.MySqlClient;
using Terraria;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.DB;
using Chest = Terraria.Chest;

namespace InfiniteChests
{
    [ApiVersion(2, 1)]
    public sealed class InfiniteChestsPlugin : TerrariaPlugin
    {
        private static readonly string SqlitePath = Path.Combine("infchests", "db.sqlite");

        private DbConnection _connection;
        private ChestManager _database;

        public InfiniteChestsPlugin(Main game) : base(game)
        {
        }

        public override string Author => "MarioE";
        public override string Description => "Allows for infinite chests.";
        public override string Name => "InfiniteChests";
        public override Version Version => Assembly.GetExecutingAssembly().GetName().Version;

        public override void Initialize()
        {
#if DEBUG
            Debug.Listeners.Add(new TextWriterTraceListener(Console.Out));
#endif

            Directory.CreateDirectory("infchests");
            _connection = new SqliteConnection($"uri=file://{SqlitePath},Version=3");
            _database = new ChestManager(_connection);

            ServerApi.Hooks.NetGetData.Register(this, OnNetGetData);
            ServerApi.Hooks.GamePostInitialize.Register(this, OnGamePostInitialize);
            ServerApi.Hooks.WorldSave.Register(this, OnWorldSave);

            Commands.ChatCommands.Add(new Command("infchests.chest", ChestCmd, "chest"));
            Commands.ChatCommands.Add(new Command("infchests.importchests", ImportChests, "importchests"));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _database.Dispose();

                ServerApi.Hooks.NetGetData.Deregister(this, OnNetGetData);
                ServerApi.Hooks.GamePostInitialize.Deregister(this, OnGamePostInitialize);
                ServerApi.Hooks.WorldSave.Deregister(this, OnWorldSave);
            }
            base.Dispose(disposing);
        }

        private void ChestCmd(CommandArgs args)
        {
            var parameters = args.Parameters;
            var player = args.Player;
            var session = player.GetSession();
            var subcommand = parameters.Count > 0 ? parameters[0] : "";
            if (subcommand.Equals("allow", StringComparison.OrdinalIgnoreCase))
            {
                if (!player.HasPermission("infchests.chest.allow"))
                {
                    player.SendErrorMessage("You do not have access to this command.");
                    return;
                }

                if (parameters.Count != 2)
                {
                    player.SendErrorMessage($"Syntax: {Commands.Specifier}chest allow <user-name>");
                    return;
                }

                session.PendingChestAction = ChestAction.AllowUser;
                var inputUsername = parameters[1];
                if (TShock.Users.GetUserByName(inputUsername) == null)
                {
                    player.SendErrorMessage($"Invalid user '{inputUsername}'.");
                    return;
                }

                session.PendingUsername = inputUsername;
                player.SendInfoMessage($"Open a chest to allow {inputUsername}.");
            }
            else if (subcommand.Equals("allowg", StringComparison.OrdinalIgnoreCase))
            {
                if (!player.HasPermission("infchests.chest.allowg"))
                {
                    player.SendErrorMessage("You do not have access to this command.");
                    return;
                }

                if (parameters.Count != 2)
                {
                    player.SendErrorMessage($"Syntax: {Commands.Specifier}chest allowg <group-name>");
                    return;
                }

                session.PendingChestAction = ChestAction.AllowGroup;
                var inputGroupName = parameters[1];
                if (TShock.Groups.GetGroupByName(inputGroupName) == null)
                {
                    player.SendErrorMessage($"Invalid group '{inputGroupName}'.");
                    return;
                }

                session.PendingGroupName = inputGroupName;
                player.SendInfoMessage($"Open a chest to allow the {inputGroupName} group.");
            }
            else if (subcommand.Equals("claim", StringComparison.OrdinalIgnoreCase))
            {
                if (!player.HasPermission("infchests.chest.claim"))
                {
                    player.SendErrorMessage("You do not have access to this command.");
                    return;
                }

                session.PendingChestAction = ChestAction.Claim;
                player.SendInfoMessage("Open a chest to claim it.");
            }
            else if (subcommand.Equals("disallow", StringComparison.OrdinalIgnoreCase))
            {
                if (!player.HasPermission("infchests.chest.disallow"))
                {
                    player.SendErrorMessage("You do not have access to this command.");
                    return;
                }

                if (parameters.Count != 2)
                {
                    player.SendErrorMessage($"Syntax: {Commands.Specifier}chest disallow <user-name>");
                    return;
                }

                session.PendingChestAction = ChestAction.DisallowUser;
                var inputUsername = parameters[1];
                if (TShock.Users.GetUserByName(inputUsername) == null)
                {
                    player.SendErrorMessage($"Invalid user '{inputUsername}'.");
                    return;
                }

                session.PendingUsername = inputUsername;
                player.SendInfoMessage($"Open a chest to disallow {inputUsername}.");
            }
            else if (subcommand.Equals("disallowg", StringComparison.OrdinalIgnoreCase))
            {
                if (!player.HasPermission("infchests.chest.disallowg"))
                {
                    player.SendErrorMessage("You do not have access to this command.");
                    return;
                }

                if (parameters.Count != 2)
                {
                    player.SendErrorMessage($"Syntax: {Commands.Specifier}chest disallowg <group-name>");
                    return;
                }

                session.PendingChestAction = ChestAction.DisallowGroup;
                var inputGroupName = parameters[1];
                if (TShock.Groups.GetGroupByName(inputGroupName) == null)
                {
                    player.SendErrorMessage($"Invalid group '{inputGroupName}'.");
                    return;
                }

                session.PendingGroupName = inputGroupName;
                player.SendInfoMessage($"Open a chest to disallow the {inputGroupName} group.");
            }
            else if (subcommand.Equals("info", StringComparison.OrdinalIgnoreCase))
            {
                if (!player.HasPermission("infchests.chest.info"))
                {
                    player.SendErrorMessage("You do not have access to this command.");
                    return;
                }

                session.PendingChestAction = ChestAction.GetInfo;
                player.SendInfoMessage("Open a chest to get its information.");
            }
            else if (subcommand.Equals("multiuse", StringComparison.OrdinalIgnoreCase))
            {
                if (!player.HasPermission("infchests.chest.multiuse"))
                {
                    player.SendErrorMessage("You do not have access to this command.");
                    return;
                }

                session.PendingChestAction = ChestAction.ToggleMultiuse;
                player.SendInfoMessage("Open a chest to toggle its multiuse status.");
            }
            else if (subcommand.Equals("public", StringComparison.OrdinalIgnoreCase))
            {
                if (!player.HasPermission("infchests.chest.public"))
                {
                    player.SendErrorMessage("You do not have access to this command.");
                    return;
                }

                session.PendingChestAction = ChestAction.TogglePublic;
                player.SendInfoMessage("Open a chest to toggle its public status.");
            }
            else if (subcommand.Equals("refill", StringComparison.OrdinalIgnoreCase))
            {
                if (!player.HasPermission("infchests.chest.refill"))
                {
                    player.SendErrorMessage("You do not have access to this command.");
                    return;
                }

                if (parameters.Count != 2)
                {
                    player.SendErrorMessage($"Syntax: {Commands.Specifier}chest refill <refill-time|none>");
                    return;
                }

                var inputRefillTime = parameters[1];
                if (inputRefillTime.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    session.PendingChestAction = ChestAction.SetRefill;
                    session.PendingRefillTime = null;
                    player.SendInfoMessage("Open a chest to disable its refilling.");
                    return;
                }

                if (!TimeSpan.TryParse(inputRefillTime, out var refillTime) || refillTime < TimeSpan.Zero)
                {
                    player.SendErrorMessage($"Invalid refill time '{inputRefillTime}'.");
                    return;
                }

                session.PendingChestAction = ChestAction.SetRefill;
                session.PendingRefillTime = refillTime;
                player.SendInfoMessage("Open a chest to set its refill time.");
            }
            else if (subcommand.Equals("unclaim", StringComparison.OrdinalIgnoreCase))
            {
                if (!player.HasPermission("infchests.chest.unclaim"))
                {
                    player.SendErrorMessage("You do not have access to this command.");
                    return;
                }

                session.PendingChestAction = ChestAction.Unclaim;
                player.SendInfoMessage("Open a chest to unclaim it.");
            }
            else
            {
                if (player.HasPermission("infchests.chest.allow"))
                {
                    player.SendErrorMessage($"Syntax: {Commands.Specifier}chest allow <user-name>");
                }
                if (player.HasPermission("infchests.chest.allowg"))
                {
                    player.SendErrorMessage($"Syntax: {Commands.Specifier}chest allowg <group-name>");
                }
                if (player.HasPermission("infchests.chest.claim"))
                {
                    player.SendErrorMessage($"Syntax: {Commands.Specifier}chest claim");
                }
                if (player.HasPermission("infchests.chest.disallow"))
                {
                    player.SendErrorMessage($"Syntax: {Commands.Specifier}chest disallow <user-name>");
                }
                if (player.HasPermission("infchests.chest.disallowg"))
                {
                    player.SendErrorMessage($"Syntax: {Commands.Specifier}chest disallowg <group-name>");
                }
                if (player.HasPermission("infchests.chest.info"))
                {
                    player.SendErrorMessage($"Syntax: {Commands.Specifier}chest info");
                }
                if (player.HasPermission("infchests.chest.multiuse"))
                {
                    player.SendErrorMessage($"Syntax: {Commands.Specifier}chest multiuse");
                }
                if (player.HasPermission("infchests.chest.public"))
                {
                    player.SendErrorMessage($"Syntax: {Commands.Specifier}chest public");
                }
                if (player.HasPermission("infchests.chest.refill"))
                {
                    player.SendErrorMessage($"Syntax: {Commands.Specifier}chest refill <refill-time|none>");
                }
                if (player.HasPermission("infchests.chest.unclaim"))
                {
                    player.SendErrorMessage($"Syntax: {Commands.Specifier}chest unclaim");
                }
            }
        }

        private void ImportChests(CommandArgs args)
        {
            IDbConnection connection;
            switch (TShock.Config.StorageType.ToLower())
            {
                case "mysql":
                    var dbHost = TShock.Config.MySqlHost.Split(':');
                    connection = new MySqlConnection
                    {
                        ConnectionString =
                            $"Server={dbHost[0]}; " +
                            $"Port={(dbHost.Length == 1 ? "3306" : dbHost[1])}; " +
                            $"Database={TShock.Config.MySqlDbName}; " +
                            $"Uid={TShock.Config.MySqlUsername}; " +
                            $"Pwd={TShock.Config.MySqlPassword};"
                    };
                    break;
                case "sqlite":
                    var path = Path.Combine(TShock.SavePath, "InfChests3.sqlite");
                    connection = new SqliteConnection($"uri=file://{path},Version=3");
                    break;
                default:
                    throw new InvalidOperationException();
            }

            var player = args.Player;
            player.SendWarningMessage("Importing chests. This may take a while...");
            using (var reader = connection.QueryReader("SELECT * FROM InfChests3 WHERE WorldID = @0", Main.worldID))
            {
                while (reader.Read())
                {
                    var userId = reader.Get<int>("UserID");
                    var x = reader.Get<int>("X");
                    var y = reader.Get<int>("Y");
                    var items = reader.Get<string>("Items");
                    var isPublic = reader.Get<int>("Public") == 1;
                    var users = reader.Get<string>("Users");
                    var groups = reader.Get<string>("Groups");
                    TimeSpan? refillTime = TimeSpan.FromSeconds(reader.Get<int>("RefillTime"));
                    if (refillTime < TimeSpan.Zero)
                    {
                        refillTime = null;
                    }

                    var ownerName = TShock.Users.GetUserByID(userId)?.Name;
                    var chest = _database.Add(x, y, "", ownerName);
                    chest.IsPublic = isPublic;
                    chest.RefillTime = refillTime;

                    var index = 0;
                    foreach (var itemString in items.Split('~').Skip(1))
                    {
                        var components = itemString.Split(',');
                        var itemId = int.Parse(components[0]);
                        var stackSize = int.Parse(components[0]);
                        var prefixId = byte.Parse(components[0]);
                        chest.Items[index] = new NetItem(itemId, stackSize, prefixId);
                        ++index;
                    }
                    foreach (var userId2 in users.Split(',').Select(int.Parse))
                    {
                        var username = TShock.Users.GetUserByID(userId2)?.Name;
                        if (username != null)
                        {
                            chest.AllowedUsernames.Add(username);
                        }
                    }
                    foreach (var groupName in groups.Split(','))
                    {
                        chest.AllowedGroupNames.Add(groupName);
                    }
                    _database.Update(chest);
                }
            }
        }

        private void OnGamePostInitialize(EventArgs args)
        {
            _database.Load();
        }

        private void OnNetGetData(GetDataEventArgs args)
        {
            if (args.Handled)
            {
                return;
            }

            var player = TShock.Players[args.Msg.whoAmI];
            using (var reader = new BinaryReader(new MemoryStream(args.Msg.readBuffer, args.Index, args.Length)))
            {
                switch (args.MsgID)
                {
                    case PacketTypes.ChestGetContents:
                        OnPlayerChestGet(player, reader);
                        args.Handled = true;
                        return;
                    case PacketTypes.ChestItem:
                        OnPlayerChestItem(player, reader);
                        args.Handled = true;
                        return;
                    case PacketTypes.ChestOpen:
                        OnPlayerChestSet(player, reader);
                        args.Handled = true;
                        return;
                    case PacketTypes.TileKill:
                        args.Handled = OnPlayerChestPlaceOrRemove(player, reader);
                        return;
                    case PacketTypes.ForceItemIntoNearestChest:
                        OnPlayerQuickStack(player, reader);
                        args.Handled = true;
                        return;
                }
            }
        }

        private void OnPlayerChestGet(TSPlayer player, BinaryReader reader)
        {
            var x = reader.ReadInt16();
            var y = reader.ReadInt16();

            var session = player.GetSession();
            var chest = _database.GetOrConvert(x, y);
            if (chest == null)
            {
                player.SendErrorMessage("This chest is corrupted.");
                session.PendingChestAction = ChestAction.None;
                return;
            }

            switch (session.PendingChestAction)
            {
                case ChestAction.GetInfo:
                    Debug.WriteLine($"DEBUG: {player.Name} obtained info about chest at {x}, {y}");
                    player.SendInfoMessage(
                        $"X: {x}, Y: {y}, Owner: {chest.OwnerName ?? "N/A"} {(chest.IsPublic ? "(Public)" : "")}");
                    if (chest.RefillTime != null)
                    {
                        player.SendInfoMessage($"Refill time: {chest.RefillTime}");
                    }
                    if (chest.AllowedUsernames.Count > 0)
                    {
                        player.SendInfoMessage($"Allowed users: {string.Join(", ", chest.AllowedUsernames)}");
                    }
                    if (chest.AllowedGroupNames.Count > 0)
                    {
                        player.SendInfoMessage($"Allowed groups: {string.Join(", ", chest.AllowedGroupNames)}");
                    }
                    break;
                case ChestAction.TogglePublic:
                    if (!chest.IsOwner(player))
                    {
                        Debug.WriteLine(
                            $"DEBUG: {player.Name} attempted to toggle public status for chest at {x}, {y}");
                        player.SendErrorMessage("This chest is protected.");
                        break;
                    }

                    Debug.WriteLine($"DEBUG: {player.Name} toggled public status for chest at {x}, {y}");
                    chest.IsPublic = !chest.IsPublic;
                    _database.Update(chest);
                    player.SendInfoMessage($"This chest is now {(chest.IsPublic ? "public" : "private")}.");
                    break;
                case ChestAction.ToggleMultiuse:
                    if (!chest.IsOwner(player))
                    {
                        Debug.WriteLine(
                            $"DEBUG: {player.Name} attempted to toggle multiuse status for chest at {x}, {y}");
                        player.SendErrorMessage("This chest is protected.");
                        break;
                    }

                    Debug.WriteLine($"DEBUG: {player.Name} toggled multiuse status for chest at {x}, {y}");
                    chest.IsMultiUse = !chest.IsMultiUse;
                    _database.Update(chest);
                    player.SendInfoMessage(
                        $"This can {(chest.IsMultiUse ? "now" : "no longer")} be used by multiple players.");
                    break;
                case ChestAction.SetRefill:
                    if (!chest.IsOwner(player))
                    {
                        Debug.WriteLine($"DEBUG: {player.Name} attempted to set refill time for chest at {x}, {y}");
                        player.SendErrorMessage("This chest is protected.");
                        break;
                    }

                    Debug.WriteLine($"DEBUG: {player.Name} set refill time for chest at {x}, {y}");
                    chest.RefillTime = session.PendingRefillTime;
                    _database.Update(chest);
                    player.SendInfoMessage(chest.RefillTime == null
                                               ? "This chest will no longer refill."
                                               : $"This chest will now refill every {chest.RefillTime}.");
                    break;
                case ChestAction.AllowUser:
                    if (!chest.IsOwner(player))
                    {
                        Debug.WriteLine($"DEBUG: {player.Name} attempted to allow a user for chest at {x}, {y}");
                        player.SendErrorMessage("This chest is protected.");
                        break;
                    }

                    Debug.WriteLine($"DEBUG: {player.Name} allowed a user for chest at {x}, {y}");
                    chest.AllowedUsernames.Add(session.PendingUsername);
                    _database.Update(chest);
                    player.SendInfoMessage($"Allowed {session.PendingUsername} to edit this chest.");
                    break;
                case ChestAction.DisallowUser:
                    if (!chest.IsOwner(player))
                    {
                        Debug.WriteLine($"DEBUG: {player.Name} attempted to disallow a user for chest at {x}, {y}");
                        player.SendErrorMessage("This chest is protected.");
                        break;
                    }

                    Debug.WriteLine($"DEBUG: {player.Name} disallowed a user for chest at {x}, {y}");
                    chest.AllowedUsernames.Remove(session.PendingUsername);
                    _database.Update(chest);
                    player.SendInfoMessage($"Disallowed {session.PendingUsername} from editing this chest.");
                    break;
                case ChestAction.AllowGroup:
                    if (!chest.IsOwner(player))
                    {
                        Debug.WriteLine($"DEBUG: {player.Name} attempted to allow a group for chest at {x}, {y}");
                        player.SendErrorMessage("This chest is protected.");
                        break;
                    }

                    Debug.WriteLine($"DEBUG: {player.Name} allowed a group for chest at {x}, {y}");
                    chest.AllowedGroupNames.Add(session.PendingGroupName);
                    _database.Update(chest);
                    player.SendInfoMessage($"Allowed the {session.PendingGroupName} group to edit this chest.");
                    break;
                case ChestAction.DisallowGroup:
                    if (!chest.IsOwner(player))
                    {
                        Debug.WriteLine($"DEBUG: {player.Name} attempted to disallow a group for chest at {x}, {y}");
                        player.SendErrorMessage("This chest is protected.");
                        break;
                    }

                    Debug.WriteLine($"DEBUG: {player.Name} disallowed a group for chest at {x}, {y}");
                    chest.AllowedGroupNames.Remove(session.PendingGroupName);
                    _database.Update(chest);
                    player.SendInfoMessage($"Disallowed the {session.PendingGroupName} group from editing this chest.");
                    break;
                case ChestAction.Claim:
                    if (!string.IsNullOrEmpty(chest.OwnerName))
                    {
                        Debug.WriteLine($"DEBUG: {player.Name} attempted to claim a chest at {x}, {y}");
                        player.SendErrorMessage("This chest is already claimed.");
                        return;
                    }

                    Debug.WriteLine($"DEBUG: {player.Name} claimed a chest at {x}, {y}");
                    chest.OwnerName = player.User?.Name;
                    _database.Update(chest);
                    player.SendInfoMessage("Claimed chest.");
                    break;
                case ChestAction.Unclaim:
                    if (!chest.IsOwner(player))
                    {
                        Debug.WriteLine($"DEBUG: {player.Name} attempted to unclaim a chest at {x}, {y}");
                        player.SendErrorMessage("This chest is protected.");
                        return;
                    }

                    Debug.WriteLine($"DEBUG: {player.Name} unclaimed a chest at {x}, {y}");
                    chest.OwnerName = null;
                    _database.Update(chest);
                    player.SendInfoMessage("Unclaimed chest.");
                    break;
                default:
                    if (!chest.IsAllowed(player))
                    {
                        Debug.WriteLine($"DEBUG: {player.Name} attempted to access chest at {x}, {y}");
                        player.SendErrorMessage("This chest is protected.");
                        break;
                    }
                    if (!chest.CanUse)
                    {
                        Debug.WriteLine($"DEBUG: {player.Name} attempted to access chest at {x}, {y}");
                        player.SendErrorMessage("This chest is currently in use.");
                        break;
                    }

                    chest.IsInUse = true;
                    chest.ShowTo(player, session.GetNextChestId());
                    break;
            }
            session.PendingChestAction = ChestAction.None;
        }

        private void OnPlayerChestItem(TSPlayer player, BinaryReader reader)
        {
            var session = player.GetSession();
            var chestId = reader.ReadInt16();
            if (!session.IdToChest.TryGetValue(chestId, out var chest))
            {
                Debug.WriteLine($"DEBUG: {player.Name} attempted to modify chest (ID: {chestId})");
                return;
            }

            Debug.WriteLine($"DEBUG: {player.Name} modified chest at {chest.X}, {chest.Y} (ID: {chestId})");
            var index = reader.ReadByte();
            var stackSize = reader.ReadInt16();
            var prefixId = reader.ReadByte();
            var itemId = reader.ReadInt16();
            chest.Items[index] = new NetItem(itemId, stackSize, prefixId);
            _database.Update(chest);
        }

        private bool OnPlayerChestPlaceOrRemove(TSPlayer player, BinaryReader reader)
        {
            var action = reader.ReadByte();
            var x = reader.ReadInt16();
            var y = reader.ReadInt16();
            var style = reader.ReadInt16();
            if (!TShock.Regions.CanBuild(x, y, player))
            {
                return false;
            }

            // Chest and dresser placement
            if (action == 0 || action == 2 || action == 4)
            {
                Debug.WriteLine($"DEBUG: {player.Name} placed chest at {x}, {y}");
                var tileId = action == 0 ? TileID.Containers : action == 2 ? TileID.Dressers : TileID.Containers2;
                var chestId = WorldGen.PlaceChest(x, y, tileId, false, style);
                Main.chest[chestId] = null;
                _database.Add(action == 2 ? x - 1 : x, y - 1, "", player.User?.Name);
                // We don't send a chest creation packet, as the players have to "discover" the chest themselves.
                player.SendTileSquare(x, y, 3);
            }
            // Chest and dresser removal
            else if (action == 1 || action == 3)
            {
                var tile = Main.tile[x, y];
                // Chests and dressers have different widths, so we have to compensate.
                var width = action == 1 ? 36 : 54;
                x -= (short)(tile.frameX % width / 18);
                y -= (short)(tile.frameY % 36 / 18);

                var chest = _database.GetOrConvert(x, y);
                if (chest == null)
                {
                    Debug.WriteLine($"DEBUG: {player.Name} removed corrupted chest at {x}, {y}");
                    return false;
                }

                if (!chest.IsOwner(player))
                {
                    Debug.WriteLine($"DEBUG: {player.Name} attempted to remove chest at {chest.X}, {chest.Y}");
                    player.SendErrorMessage("This chest is protected.");
                }
                else if (!chest.IsEmpty)
                {
                    Debug.WriteLine($"DEBUG: {player.Name} attempted to remove chest at {chest.X}, {chest.Y}");
                    player.SendErrorMessage("This chest isn't empty.");
                }
                else
                {
                    Debug.WriteLine($"DEBUG: {player.Name} removed chest at {chest.X}, {chest.Y}");
                    // Sync chest removals for everyone. We have to ensure that each player deletes the chest ID that
                    // they received for the chest.
                    foreach (var player2 in TShock.Players.Where(p => p?.Active == true && p != player))
                    {
                        var session2 = player2.GetSession();
                        if (session2.ChestToId.TryGetValue(chest, out var chestId))
                        {
                            player2.SendData(PacketTypes.TileKill, "", action, x, y, 0, chestId);
                        }
                    }
                    WorldGen.KillTile(x, y);
                    _database.Remove(chest);
                }
                player.SendTileSquare(x, y, 3);
            }
            return true;
        }

        private void OnPlayerChestSet(TSPlayer player, BinaryReader reader)
        {
            var chestId = reader.ReadInt16();
            reader.ReadInt16();
            reader.ReadInt16();
            var length = reader.ReadByte();
            var name = "";
            if (length > 0 && length <= 20)
            {
                name = reader.ReadString();
            }
            else if (length != 255)
            {
                length = 0;
            }

            var tplayer = player.TPlayer;
            var session = player.GetSession();
            var oldChestId = tplayer.chest;
            if (session.IdToChest.TryGetValue(oldChestId, out var oldChest))
            {
                oldChest.IsInUse = false;
                if (length != 0)
                {
                    Debug.WriteLine(
                        $"DEBUG: {player.Name} renamed chest at {oldChest.X}, {oldChest.Y} (ID: {chestId})");
                    oldChest.Name = name;
                    _database.Update(oldChest);

                    // Sync chest names for everyone. We have to ensure that each player recieves the chest ID that they
                    // received for the chest.
                    foreach (var player2 in TShock.Players.Where(p => p?.Active == true && p != player))
                    {
                        var session2 = player2.GetSession();
                        if (session2.ChestToId.TryGetValue(oldChest, out var chestId2))
                        {
                            player2.SendData(PacketTypes.ChestName, "", chestId2, oldChest.X, oldChest.Y);
                        }
                    }
                }
            }

            tplayer.chest = chestId;
            // Sync player chest indices for everyone. If the chest ID is positive, we have to ensure that each player
            // receives the chest ID that they received for the chest.
            if (chestId >= 0 && session.IdToChest.TryGetValue(chestId, out var chest))
            {
                foreach (var player2 in TShock.Players.Where(p => p?.Active == true && p != player))
                {
                    var session2 = player2.GetSession();
                    if (session2.ChestToId.TryGetValue(chest, out var chestId2))
                    {
                        player2.SendData(PacketTypes.SyncPlayerChestIndex, "", player.Index, chestId2);
                    }
                }
            }
            else if (chestId == -1)
            {
                Debug.WriteLine($"DEBUG: {player.Name} finished accessing chest (ID: {oldChestId})");
                NetMessage.SendData((int)PacketTypes.SyncPlayerChestIndex, -1, player.Index, null, player.Index, -1);
            }
        }

        private void OnPlayerQuickStack(TSPlayer player, BinaryReader reader)
        {
            var slot = reader.ReadByte();
            var item = player.TPlayer.inventory[slot];
            foreach (var chest in _database.GetAll())
            {
                var position = new Vector2(16 * chest.X + 16, 16 * chest.Y + 16);
                if ((position - player.TPlayer.Center).Length() >= 200.0 || !chest.IsAllowed(player) || !chest.CanUse)
                {
                    continue;
                }

                // First, check to see if we can stack anything.
                var foundStack = false;
                for (var i = 0; i < Chest.maxItems; ++i)
                {
                    var chestItem = chest.Items[i];
                    if (chestItem.NetId == item.type)
                    {
                        Debug.WriteLine($"DEBUG: {player.Name} quick stacking into chest at {chest.X}, {chest.Y}");
                        foundStack = true;
                        var space = item.maxStack - chestItem.Stack;
                        var addition = Math.Min(space, item.stack);
                        chest.Items[i] =
                            new NetItem(chestItem.NetId, chestItem.Stack + addition, chestItem.PrefixId);
                        item.stack -= addition;
                        _database.Update(chest);
                    }
                }

                // Then, place the item at the end if needed.
                if (foundStack && item.stack > 0)
                {
                    for (var i = 0; i < Chest.maxItems; ++i)
                    {
                        var chestItem = chest.Items[i];
                        if (chestItem.NetId == 0 || chestItem.Stack == 0)
                        {
                            chest.Items[i] = (NetItem)item;
                            item.stack = 0;
                            _database.Update(chest);
                            break;
                        }
                    }
                }
                if (item.stack <= 0)
                {
                    item.SetDefaults();
                }
            }

            player.SendData(PacketTypes.PlayerSlot, "", player.Index, slot, item.prefix);
        }

        private void OnWorldSave(WorldSaveEventArgs args)
        {
            if (!args.Handled)
            {
                Debug.WriteLine("DEBUG: Saving chests");
                _database.FlushQueue();
            }
        }
    }
}

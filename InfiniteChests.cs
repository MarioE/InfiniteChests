using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Community.CsharpSqlite.SQLiteClient;
using Hooks;
using MySql.Data.MySqlClient;
using Terraria;
using TShockAPI;
using TShockAPI.DB;

namespace InfiniteChests
{
    [APIVersion(1, 12)]
    public class InfiniteChests : TerrariaPlugin
    {
        public static ChestAction[] Action = new ChestAction[256];
        public override string Author
        {
            get { return "MarioE"; }
        }
        public static Vector2[] ChestPosition = new Vector2[256];
        public static IDbConnection Database;
        public override string Description
        {
            get { return "Allows for infinite chests, and supports all chest control commands."; }
        }
        public override string Name
        {
            get { return "InfiniteChests"; }
        }
        public override Version Version
        {
            get { return Assembly.GetExecutingAssembly().GetName().Version; }
        }

        public InfiniteChests(Main game)
            : base(game)
        {
            Order = -1;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                NetHooks.GetData -= OnGetData;
                GameHooks.Initialize -= OnInitialize;
                ServerHooks.Leave -= OnLeave;
            }
        }

        public override void Initialize()
        {
            NetHooks.GetData += OnGetData;
            GameHooks.Initialize += OnInitialize;
            ServerHooks.Leave += OnLeave;
        }

        void OnGetData(GetDataEventArgs e)
        {
            if (!e.Handled)
            {
                switch (e.MsgID)
                {
                    case PacketTypes.ChestGetContents:
                        {
                            int X = BitConverter.ToInt32(e.Msg.readBuffer, e.Index);
                            int Y = BitConverter.ToInt32(e.Msg.readBuffer, e.Index + 4);
                            ChestPosition[e.Msg.whoAmI] = new Vector2(X, Y);
                            ThreadPool.QueueUserWorkItem(GetChestCallback,
                                new ChestArgs { plr = TShock.Players[e.Msg.whoAmI], loc = new Vector2(X, Y) });
                            e.Handled = true;
                        }
                        break;
                    case PacketTypes.ChestItem:
                        {
                            byte slot = e.Msg.readBuffer[e.Index + 2];
                            if (slot > 20)
                            {
                                return;
                            }
                            byte stack = e.Msg.readBuffer[e.Index + 3];
                            byte prefix = e.Msg.readBuffer[e.Index + 4];
                            int netID = BitConverter.ToInt16(e.Msg.readBuffer, e.Index + 5);
                            ThreadPool.QueueUserWorkItem(ModChestCallback,
                                new ChestItemArgs { plr = TShock.Players[e.Msg.whoAmI], netID = netID, stack = stack, prefix = prefix, slot = slot });
                            e.Handled = true;
                        }
                        break;
                    case PacketTypes.Tile:
                        {
                            if (e.Msg.readBuffer[e.Index] == 1 && e.Msg.readBuffer[e.Index + 9] == 21)
                            {
                                int X = BitConverter.ToInt32(e.Msg.readBuffer, e.Index + 1);
                                int Y = BitConverter.ToInt32(e.Msg.readBuffer, e.Index + 5);
                                if (TShock.Regions.CanBuild(X, Y, TShock.Players[e.Msg.whoAmI]))
                                {
                                    ThreadPool.QueueUserWorkItem(PlaceChestCallback,
                                        new ChestArgs { plr = TShock.Players[e.Msg.whoAmI], loc = new Vector2(X, Y - 1) });
                                    WorldGen.PlaceChest(X, Y, 21, false, e.Msg.readBuffer[e.Index + 10]);
                                    NetMessage.SendData((int)PacketTypes.Tile, -1, e.Msg.whoAmI, "", 1, X, Y, 21, e.Msg.readBuffer[e.Index + 10]);
                                    e.Handled = true;
                                }
                            }
                        }
                        break;
                    case PacketTypes.TileKill:
                        {
                            int X = BitConverter.ToInt32(e.Msg.readBuffer, e.Index);
                            int Y = BitConverter.ToInt32(e.Msg.readBuffer, e.Index + 4);
                            if (TShock.Regions.CanBuild(X, Y, TShock.Players[e.Msg.whoAmI]) && Main.tile[X, Y].type == 21)
                            {
                                if (Main.tile[X, Y].frameY != 0)
                                {
                                    Y--;
                                }
                                if (Main.tile[X, Y].frameX % 36 != 0)
                                {
                                    X--;
                                }
                                ThreadPool.QueueUserWorkItem(KillChestCallback,
                                    new ChestArgs { plr = TShock.Players[e.Msg.whoAmI], loc = new Vector2(X, Y) });
                                TShock.Players[e.Msg.whoAmI].SendTileSquare(X, Y, 3);
                                e.Handled = true;
                            }
                        }
                        break;
                }
            }
        }
        void OnInitialize()
        {
            Commands.ChatCommands.Add(new Command("protectchest", Deselect, "cdeselect"));
            Commands.ChatCommands.Add(new Command("showchestinfo", Info, "cinfo"));
            Commands.ChatCommands.Add(new Command("maintenance", ConvertChests, "convchests"));
            Commands.ChatCommands.Add(new Command("protectchest", Protect, "cprotect"));
            Commands.ChatCommands.Add(new Command("refillchest", Refill, "crefill"));
            Commands.ChatCommands.Add(new Command("protectchest", Unprotect, "cunprotect"));
            Commands.ChatCommands.Add(new Command("refillchest", Unrefill, "cunrefill"));

            switch (TShock.Config.StorageType.ToLower())
            {
                case "mysql":
                    string[] host = TShock.Config.MySqlHost.Split(':');
                    Database = new MySqlConnection()
                    {
                        ConnectionString = string.Format("Server={0}; Port={1}; Database={2}; Uid={3}; Pwd={4};",
                            host[0],
                            host.Length == 1 ? "3306" : host[1],
                            TShock.Config.MySqlDbName,
                            TShock.Config.MySqlUsername,
                            TShock.Config.MySqlPassword)
                    };
                    break;
                case "sqlite":
                    string sql = Path.Combine(TShock.SavePath, "chests.sqlite");
                    Database = new SqliteConnection(string.Format("uri=file://{0},Version=3", sql));
                    break;
            }
            SqlTableCreator sqlcreator = new SqlTableCreator(Database,
                Database.GetSqlType() == SqlType.Sqlite ? (IQueryBuilder)new SqliteQueryCreator() : new MysqlQueryCreator());
            sqlcreator.EnsureExists(new SqlTable("Chests",
                new SqlColumn("X", MySqlDbType.Int32),
                new SqlColumn("Y", MySqlDbType.Int32),
                new SqlColumn("Account", MySqlDbType.Text),
                new SqlColumn("Items", MySqlDbType.Text),
                new SqlColumn("Flags", MySqlDbType.Int32),
                new SqlColumn("WorldID", MySqlDbType.Int32)));
        }
        void OnLeave(int index)
        {
            Action[index] = ChestAction.NONE;
            ChestPosition[index] = Vector2.Zero;
        }

        void ConvertCallback(object t)
        {
            Database.QueryReader("DELETE FROM Chests WHERE WorldID = @0", Main.worldID);
            int converted = 0;
            foreach (Terraria.Chest c in Main.chest)
            {
                if (c != null)
                {
                    StringBuilder items = new StringBuilder();
                    for (int i = 0; i < 20; i++)
                    {
                        items.Append(c.item[i].netID + "," + c.item[i].stack + "," + c.item[i].prefix);
                        if (i != 20)
                        {
                            items.Append(",");
                        }
                    }
                    Database.Query("INSERT INTO Chests (X, Y, Account, Items, Flags, WorldID) VALUES (@0, @1, '', @2, 0, @3)",
                        c.x, c.y, items.ToString(), Main.worldID);
                    converted++;
                }
            }
            ((ChestArgs)t).plr.SendMessage("Converted " + converted + " chests.");
        }
        void GetChestCallback(object t)
        {
            ChestArgs c = (ChestArgs)t;

            using (QueryResult query = Database.QueryReader("SELECT Account, Items, Flags FROM Chests WHERE X = @0 AND Y = @1 AND WorldID = @2",
                (int)c.loc.X, (int)c.loc.Y, Main.worldID))
            {
                while (query.Read())
                {
                    Chest chest = new Chest
                    {
                        account = query.Get<string>("Account"),
                        flags = (ChestFlags)query.Get<int>("Flags"),
                        items = query.Get<string>("Items")
                    };
                    switch (Action[c.plr.Index])
                    {
                        case ChestAction.INFO:
                            c.plr.SendMessage(string.Format("X: {0} Y: {1} Account: {2} Refill: {3}",
                                (int)c.loc.X, (int)c.loc.Y, chest.account == "" ? "N/A" : chest.account,
                                (chest.flags & ChestFlags.REFILL) != 0), Color.Yellow);
                            break;
                        case ChestAction.PROTECT:
                            if (chest.account != "")
                            {
                                c.plr.SendMessage("This chest is already protected.", Color.Red);
                                break;
                            }
                            Database.Query("UPDATE Chests SET Account = @0 WHERE X = @1 AND Y = @2 AND WorldID = @3",
                                c.plr.UserAccountName, (int)c.loc.X, (int)c.loc.Y, Main.worldID);
                            c.plr.SendMessage("This chest is now protected.");
                            break;
                        case ChestAction.REFILL:
                            Database.Query("UPDATE Chests SET Flags = @0 WHERE X = @1 AND Y = @2 AND WorldID = @3",
                                (int)(chest.flags | ChestFlags.REFILL), (int)c.loc.X, (int)c.loc.Y, Main.worldID);
                            c.plr.SendMessage("This chest will now refill.");
                            break;
                        case ChestAction.UNPROTECT:
                            if (chest.account == "")
                            {
                                c.plr.SendMessage("This chest is not protected.", Color.Red);
                                break;
                            }
                            if (chest.account != c.plr.UserAccountName &&
                                !c.plr.Group.HasPermission("removechestprotection"))
                            {
                                c.plr.SendMessage("This chest is not yours.");
                                break;
                            }
                            Database.Query("UPDATE Chests SET Account = '' WHERE X = @0 AND Y = @1 AND WorldID = @2",
                                (int)c.loc.X, (int)c.loc.Y, Main.worldID);
                            c.plr.SendMessage("This chest is now unprotected.");
                            break;
                        case ChestAction.UNREFILL:
                            Database.Query("UPDATE Chests SET Flags = @0 WHERE X = @1 AND Y = @2 AND WorldID = @3",
                                (int)(chest.flags & ~ChestFlags.REFILL), (int)c.loc.X, (int)c.loc.Y, Main.worldID);
                            c.plr.SendMessage("This chest will no longer refill.");
                            break;
                        default:
                            if (chest.account != c.plr.UserAccountName &&
                                chest.account != "" && !c.plr.Group.HasPermission("openallchests"))
                            {
                                c.plr.SendMessage("This chest is protected.", Color.Red);
                                break;
                            }
                            try
                            {
                                int[] itemArgs = new int[60];
                                string[] split = chest.items.Split(',');
                                for (int i = 0; i < 60; i++)
                                {
                                    itemArgs[i] = Convert.ToInt32(split[i]);
                                }
                                byte[] raw = new byte[] { 8, 0, 0, 0, 32, 0, 0, 255, 255, 255, 255, 255 };
                                for (int i = 0; i < 20; i++)
                                {
                                    raw[7] = (byte)i;
                                    raw[8] = (byte)itemArgs[i * 3 + 1];
                                    raw[9] = (byte)itemArgs[i * 3 + 2];
                                    Buffer.BlockCopy(BitConverter.GetBytes((short)itemArgs[i * 3]), 0, raw, 10, 2);
                                    c.plr.SendRawData(raw);
                                }
                                byte[] raw2 = new byte[] { 11, 0, 0, 0, 33, 0, 0, 255, 255, 255, 255, 255, 255, 255, 255 };
                                Buffer.BlockCopy(BitConverter.GetBytes((int)c.loc.X), 0, raw2, 7, 4);
                                Buffer.BlockCopy(BitConverter.GetBytes((int)c.loc.Y), 0, raw2, 11, 4);
                                c.plr.SendRawData(raw2);
                                ChestPosition[c.plr.Index] = c.loc;
                            }
                            catch
                            {
                            }
                            break;
                    }
                    Action[c.plr.Index] = ChestAction.NONE;
                }
            }
        }
        void KillChestCallback(object t)
        {
            ChestArgs c = (ChestArgs)t;
            using (QueryResult query = Database.QueryReader("SELECT Account, Items, Flags FROM Chests WHERE X = @0 AND Y = @1 AND WorldID = @2",
                (int)c.loc.X, (int)c.loc.Y, Main.worldID))
            {
                while (query.Read())
                {
                    Chest chest = new Chest
                    {
                        account = query.Get<string>("Account"),
                        flags = (ChestFlags)query.Get<int>("Flags"),
                        items = query.Get<string>("Items")
                    };
                    if (chest.account != c.plr.UserAccountName && chest.account != "")
                    {
                        c.plr.SendMessage("This chest is protected.", Color.Red);
                        c.plr.SendTileSquare((int)c.loc.X, (int)c.loc.Y, 3);
                        return;
                    }
                    if (chest.items !=
                        "0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0")
                    {
                        return;
                    }
                    Database.Query("DELETE FROM Chests WHERE X = @0 AND Y = @1 AND WorldID = @2", (int)c.loc.X, (int)c.loc.Y, Main.worldID);
                    WorldGen.KillTile((int)c.loc.X, (int)c.loc.Y);
                    TSPlayer.All.SendData(PacketTypes.Tile, "", 0, (int)c.loc.X, (int)c.loc.Y + 1);
                    TSPlayer.All.SendTileSquare((int)c.loc.X, (int)c.loc.Y, 1);
                    return;
                }
            }
        }
        void ModChestCallback(object t)
        {
            ChestItemArgs ci = (ChestItemArgs)t;
            using (QueryResult query = Database.QueryReader("SELECT Account, Items, Flags FROM Chests WHERE X = @0 AND Y = @1 AND WorldID = @2",
                (int)ChestPosition[ci.plr.Index].X, (int)ChestPosition[ci.plr.Index].Y, Main.worldID))
            {
                while (query.Read())
                {
                    Chest chest = new Chest
                    {
                        account = query.Get<string>("Account"),
                        flags = (ChestFlags)query.Get<int>("Flags"),
                        items = query.Get<string>("Items")
                    };
                    if ((chest.flags & ChestFlags.REFILL) != 0)
                    {
                        return;
                    }
                    int[] itemArgs = new int[60];
                    string[] split = chest.items.Split(',');
                    for (int i = 0; i < 60; i++)
                    {
                        itemArgs[i] = Convert.ToInt32(split[i]);
                    }
                    itemArgs[ci.slot * 3] = ci.netID;
                    itemArgs[ci.slot * 3 + 1] = ci.stack;
                    itemArgs[ci.slot * 3 + 2] = ci.prefix;
                    StringBuilder newItems = new StringBuilder();
                    for (int i = 0; i < 60; i++)
                    {
                        newItems.Append(itemArgs[i]);
                        if (i != 59)
                        {
                            newItems.Append(',');
                        }
                    }
                    Database.Query("UPDATE Chests SET Items = @0 WHERE X = @1 AND Y = @2 AND WorldID = @3",
                        newItems.ToString(), (int)ChestPosition[ci.plr.Index].X, (int)ChestPosition[ci.plr.Index].Y, Main.worldID);
                    return;
                }
            }
        }
        void PlaceChestCallback(object t)
        {
            ChestArgs c = (ChestArgs)t;
            Database.Query("INSERT INTO Chests (X, Y, Account, Items, Flags, WorldID) VALUES (@0, @1, @2, @3, 0, @4)",
                (int)c.loc.X, (int)c.loc.Y, c.plr.IsLoggedIn ? c.plr.UserAccountName : "",
                "0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0", Main.worldID);
            Main.chest[999] = null;
        }

        void ConvertChests(CommandArgs e)
        {
            e.Player.SendMessage("Converting all chests into the new storage format; this may take a while.");
            ThreadPool.QueueUserWorkItem(ConvertCallback, new ChestArgs { plr = e.Player });
        }
        void Deselect(CommandArgs e)
        {
            Action[e.Player.Index] = ChestAction.NONE;
            e.Player.SendMessage("Stopped selecting a chest.");
        }
        void Info(CommandArgs e)
        {
            Action[e.Player.Index] = ChestAction.INFO;
            e.Player.SendMessage("Open a chest to get its info.");
        }
        void Protect(CommandArgs e)
        {
            Action[e.Player.Index] = ChestAction.PROTECT;
            e.Player.SendMessage("Open a chest to protect it.");
        }
        void Refill(CommandArgs e)
        {
            Action[e.Player.Index] = ChestAction.REFILL;
            e.Player.SendMessage("Open a chest to make it refill.");
        }
        void Unprotect(CommandArgs e)
        {
            Action[e.Player.Index] = ChestAction.UNPROTECT;
            e.Player.SendMessage("Open a chest to unprotect it.");
        }
        void Unrefill(CommandArgs e)
        {
            Action[e.Player.Index] = ChestAction.UNREFILL;
            e.Player.SendMessage("Open a chest to make it not refill.");
        }

        private class ChestArgs
        {
            public Vector2 loc;
            public TSPlayer plr;
        }
        private class ChestItemArgs
        {
            public int netID;
            public TSPlayer plr;
            public byte prefix;
            public byte slot;
            public byte stack;
        }
    }
}

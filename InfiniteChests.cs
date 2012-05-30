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
        public override string Author
        {
            get { return "MarioE"; }
        }
        public static IDbConnection Database;
        public override string Description
        {
            get { return "Allows for infinite chests, and supports all chest control commands."; }
        }
        public static PlayerInfo[] infos = new PlayerInfo[256];
        public override string Name
        {
            get { return "InfiniteChests"; }
        }
        public static Dictionary<Point, int> Timer = new Dictionary<Point, int>();
        public static System.Timers.Timer TimerDec = new System.Timers.Timer(1000);
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
                TimerDec.Dispose();
            }
        }

        public override void Initialize()
        {
            NetHooks.GetData += OnGetData;
            GameHooks.Initialize += OnInitialize;
            ServerHooks.Leave += OnLeave;

            TimerDec.Elapsed += OnElapsed;
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
                            infos[e.Msg.whoAmI].loc = new Point(X, Y);
                            ThreadPool.QueueUserWorkItem(GetChestCallback,
                                new ChestArgs { plr = TShock.Players[e.Msg.whoAmI], loc = new Point(X, Y) });
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
                                        new ChestArgs { plr = TShock.Players[e.Msg.whoAmI], loc = new Point(X, Y - 1) });
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
                                    new ChestArgs { plr = TShock.Players[e.Msg.whoAmI], loc = new Point(X, Y) });
                                TShock.Players[e.Msg.whoAmI].SendTileSquare(X, Y, 3);
                                e.Handled = true;
                            }
                        }
                        break;
                }
            }
        }
        void OnElapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            List<Point> dec = new List<Point>();
            foreach (Point p in Timer.Keys)
            {
                dec.Add(p);
            }
            foreach (Point p in dec)
            {
                if (Timer[p] == 0)
                {
                    Timer.Remove(p);
                }
                else
                {
                    Timer[p]--;
                }
            }
        }
        void OnInitialize()
        {
            Commands.ChatCommands.Add(new Command("protectchest", Deselect, "ccset"));
            Commands.ChatCommands.Add(new Command("showchestinfo", Info, "cinfo"));
            Commands.ChatCommands.Add(new Command("maintenance", ConvertChests, "convchests"));
            Commands.ChatCommands.Add(new Command("protectchest", PublicProtect, "cpset"));
            Commands.ChatCommands.Add(new Command("refillchest", Refill, "crefill"));
            Commands.ChatCommands.Add(new Command("protectchest", RegionProtect, "crset"));
            Commands.ChatCommands.Add(new Command("protectchest", Protect, "cset"));
            Commands.ChatCommands.Add(new Command("protectchest", Unprotect, "cunset"));

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
            infos[index] = new PlayerInfo();
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
            ((ChestArgs)t).plr.SendMessage(string.Format("Converted {0} chests.", converted));
        }
        void GetChestCallback(object t)
        {
            ChestArgs c = (ChestArgs)t;
            using (QueryResult query = Database.QueryReader("SELECT Account, Items, Flags FROM Chests WHERE X = @0 AND Y = @1 AND WorldID = @2",
                c.loc.X, c.loc.Y, Main.worldID))
            {
                while (query.Read())
                {
                    Chest chest = new Chest
                    {
                        account = query.Get<string>("Account"),
                        flags = (ChestFlags)query.Get<int>("Flags"),
                        items = query.Get<string>("Items")
                    };
                    switch (infos[c.plr.Index].action)
                    {
                        case ChestAction.INFO:
                            c.plr.SendMessage(string.Format("X: {0} Y: {1} Account: {2} {3} Refill: {4} Delay: {5} second(s) Region: {6}",
                                c.loc.X, c.loc.Y, chest.account == "" ? "N/A" : chest.account, ((chest.flags & ChestFlags.PUBLIC) != 0) ? "(public)" : "",
                                (chest.flags & ChestFlags.REFILL) != 0, (int)chest.flags >> 3, (chest.flags & ChestFlags.REGION) != 0), Color.Yellow);
                            break;
                        case ChestAction.PROTECT:
                            if (chest.account != "")
                            {
                                c.plr.SendMessage("This chest is already protected.", Color.Red);
                                break;
                            }
                            Database.Query("UPDATE Chests SET Account = @0 WHERE X = @1 AND Y = @2 AND WorldID = @3",
                                c.plr.UserAccountName, c.loc.X, c.loc.Y, Main.worldID);
                            c.plr.SendMessage("This chest is now protected.");
                            break;
                        case ChestAction.PUBLIC:
                            if (chest.account != c.plr.UserAccountName && chest.account != "")
                            {
                                c.plr.SendMessage("This chest is not yours.");
                                break;
                            }
                            Database.Query("UPDATE Chests SET Flags = @0 WHERE X = @1 AND Y = @2 AND WorldID = @3",
                                (int)(chest.flags ^ ChestFlags.PUBLIC), c.loc.X, c.loc.Y, Main.worldID);
                            if ((chest.flags & ChestFlags.PUBLIC) == 0)
                            {
                                c.plr.SendMessage("This chest is now public.");
                            }
                            else
                            {
                                c.plr.SendMessage("This chest is now private.");
                            }
                            break;
                        case ChestAction.REFILL:
                            if (infos[c.plr.Index].time > 0)
                            {
                                Database.Query("UPDATE Chests SET Flags = @0 WHERE X = @1 AND Y = @2 AND WorldID = @3",
                                    ((int)chest.flags & 3) + (infos[c.plr.Index].time << 3) + 4, c.loc.X, c.loc.Y, Main.worldID);
                                c.plr.SendMessage(string.Format("This chest will now refill with a delay of {0} second(s).", infos[c.plr.Index].time));
                            }
                            else
                            {
                                Database.Query("UPDATE Chests SET Flags = @0 WHERE X = @1 AND Y = @2 AND WorldID = @3",
                                    (int)(chest.flags ^ ChestFlags.REFILL) & 3, c.loc.X, c.loc.Y, Main.worldID);
                                if ((chest.flags & ChestFlags.REFILL) == 0)
                                {
                                    c.plr.SendMessage("This chest will now refill.");
                                }
                                else
                                {
                                    c.plr.SendMessage("This chest will no longer refill.");
                                }
                            }
                            break;
                        case ChestAction.REGION:
                            if (chest.account != c.plr.UserAccountName && chest.account != "")
                            {
                                c.plr.SendMessage("This chest is not yours.");
                                break;
                            }
                            Database.Query("UPDATE Chests SET Flags = @0 WHERE X = @1 AND Y = @2 AND WorldID = @3",
                                (int)(chest.flags ^ ChestFlags.REGION), c.loc.X, c.loc.Y, Main.worldID);
                            if ((chest.flags & ChestFlags.REGION) == 0)
                            {
                                c.plr.SendMessage("This chest is now region shared.");
                            }
                            else
                            {
                                c.plr.SendMessage("This chest is no longer region shared.");
                            }
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
                                c.loc.X, c.loc.Y, Main.worldID);
                            c.plr.SendMessage("This chest is no longer protected.");
                            break;
                        default:
                            if ((chest.flags & ChestFlags.PUBLIC) != 0 && ((chest.account != c.plr.UserAccountName &&
                                chest.account != "" && !c.plr.Group.HasPermission("openallchests") && (chest.flags & ChestFlags.REGION) == 0)
                                || ((chest.flags & ChestFlags.REGION) != 0 && !TShock.Regions.CanBuild(c.loc.X, c.loc.Y, c.plr))))
                            {
                                c.plr.SendMessage("This chest is protected.", Color.Red);
                                break;
                            }
                            int timeLeft;
                            if (Timer.TryGetValue(new Point(c.loc.X, c.loc.Y), out timeLeft))
                            {
                                c.plr.SendMessage(string.Format("This chest will refill in {0} second(s).", (int)timeLeft), Color.Red);
                                break;
                            }
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
                            Buffer.BlockCopy(BitConverter.GetBytes(c.loc.X), 0, raw2, 7, 4);
                            Buffer.BlockCopy(BitConverter.GetBytes(c.loc.Y), 0, raw2, 11, 4);
                            c.plr.SendRawData(raw2);
                            infos[c.plr.Index].loc = c.loc;
                            break;
                    }
                    infos[c.plr.Index].action = ChestAction.NONE;
                }
            }
        }
        void KillChestCallback(object t)
        {
            ChestArgs c = (ChestArgs)t;
            using (QueryResult query = Database.QueryReader("SELECT Account, Items FROM Chests WHERE X = @0 AND Y = @1 AND WorldID = @2",
                c.loc.X, c.loc.Y, Main.worldID))
            {
                while (query.Read())
                {
                    Chest chest = new Chest
                    {
                        account = query.Get<string>("Account"),
                        items = query.Get<string>("Items")
                    };
                    if (chest.account != c.plr.UserAccountName && chest.account != "")
                    {
                        c.plr.SendMessage("This chest is protected.", Color.Red);
                        c.plr.SendTileSquare(c.loc.X, c.loc.Y, 3);
                        return;
                    }
                    if (chest.items !=
                        "0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0")
                    {
                        return;
                    }
                    Database.Query("DELETE FROM Chests WHERE X = @0 AND Y = @1 AND WorldID = @2", c.loc.X, c.loc.Y, Main.worldID);
                    break;
                }
                WorldGen.KillTile(c.loc.X, c.loc.Y);
                TSPlayer.All.SendData(PacketTypes.Tile, "", 0, c.loc.X, c.loc.Y + 1);
                TSPlayer.All.SendTileSquare(c.loc.X, c.loc.Y, 1);
                return;
            }
        }
        void ModChestCallback(object t)
        {
            ChestItemArgs ci = (ChestItemArgs)t;
            using (QueryResult query = Database.QueryReader("SELECT Account, Items, Flags FROM Chests WHERE X = @0 AND Y = @1 AND WorldID = @2",
                infos[ci.plr.Index].loc.X, infos[ci.plr.Index].loc.Y, Main.worldID))
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
                        Timer.Add(new Point(infos[ci.plr.Index].loc.X, infos[ci.plr.Index].loc.Y), (int)chest.flags >> 3);
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
                        newItems.ToString(), infos[ci.plr.Index].loc.X, infos[ci.plr.Index].loc.Y, Main.worldID);
                    return;
                }
            }
        }
        void PlaceChestCallback(object t)
        {
            ChestArgs c = (ChestArgs)t;
            Database.Query("INSERT INTO Chests (X, Y, Account, Items, Flags, WorldID) VALUES (@0, @1, @2, @3, 0, @4)",
                c.loc.X, c.loc.Y, c.plr.IsLoggedIn ? c.plr.UserAccountName : "",
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
            infos[e.Player.Index].action = ChestAction.NONE;
            e.Player.SendMessage("Stopped selecting a chest.");
        }
        void Info(CommandArgs e)
        {
            infos[e.Player.Index].action = ChestAction.INFO;
            e.Player.SendMessage("Open a chest to get its info.");
        }
        void Protect(CommandArgs e)
        {
            infos[e.Player.Index].action = ChestAction.PROTECT;
            e.Player.SendMessage("Open a chest to protect it.");
        }
        void Refill(CommandArgs e)
        {
            if (e.Parameters.Count > 1)
            {
                e.Player.SendMessage("Syntax: /crefill [<interval>]", Color.Red);
                return;
            }
            infos[e.Player.Index].time = 0;
            if (e.Parameters.Count == 1)
            {
                int time;
                if (int.TryParse(e.Parameters[0], out time) && time > 0)
                {
                    infos[e.Player.Index].action = ChestAction.REFILL;
                    infos[e.Player.Index].time = time;
                    e.Player.SendMessage(string.Format("Open a chest to make it refill with an interval of {0} second(s).", time));
                    return;
                }
                e.Player.SendMessage("Invalid interval!", Color.Red);
            }
            else
            {
                infos[e.Player.Index].action = ChestAction.REFILL;
                e.Player.SendMessage("Open a chest to toggle its refill status.");
            }
        }
        void PublicProtect(CommandArgs e)
        {
            infos[e.Player.Index].action = ChestAction.PUBLIC;
            e.Player.SendMessage("Open a chest to toggle its public status.");
        }
        void RegionProtect(CommandArgs e)
        {
            infos[e.Player.Index].action = ChestAction.REGION;
            e.Player.SendMessage("Open a chest to toggle its region share status.");
        }
        void Unprotect(CommandArgs e)
        {
            infos[e.Player.Index].action = ChestAction.UNPROTECT;
            e.Player.SendMessage("Open a chest to unprotect it.");
        }

        private class ChestArgs
        {
            public Point loc;
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

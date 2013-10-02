using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Mono.Data.Sqlite;
using MySql.Data.MySqlClient;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.DB;

namespace InfiniteChests
{
	[ApiVersion(1, 14)]
	public class InfiniteChests : TerrariaPlugin
	{
		public override string Author
		{
			get { return "MarioE"; }
		}
		private IDbConnection Database;
		public override string Description
		{
			get { return "Allows for infinite chests, and supports all chest control commands."; }
		}
		private PlayerInfo[] Infos = new PlayerInfo[256];
		private DateTime LastCheck = DateTime.UtcNow;
		public override string Name
		{
			get { return "InfiniteChests"; }
		}
		private Dictionary<Point, int> Timer = new Dictionary<Point, int>();
		public override Version Version
		{
			get { return Assembly.GetExecutingAssembly().GetName().Version; }
		}

		public InfiniteChests(Main game)
			: base(game)
		{
			for (int i = 0; i < 256; i++)
			{
				Infos[i] = new PlayerInfo();
			}
			Order = 1;
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				ServerApi.Hooks.NetGetData.Deregister(this, OnGetData);
				ServerApi.Hooks.GameInitialize.Deregister(this, OnInitialize);
				ServerApi.Hooks.GameUpdate.Deregister(this, OnUpdate);
				ServerApi.Hooks.ServerLeave.Deregister(this, OnLeave);

				Database.Dispose();
			}
		}
		public override void Initialize()
		{
			ServerApi.Hooks.NetGetData.Register(this, OnGetData);
			ServerApi.Hooks.GameInitialize.Register(this, OnInitialize);
			ServerApi.Hooks.GameUpdate.Register(this, OnUpdate);
			ServerApi.Hooks.ServerLeave.Register(this, OnLeave);
		}

		void OnGetData(GetDataEventArgs e)
		{
			if (!e.Handled)
			{
				int index = e.Msg.whoAmI;
				switch (e.MsgID)
				{
					case PacketTypes.ChestGetContents:
						{
							int X = BitConverter.ToInt32(e.Msg.readBuffer, e.Index);
							int Y = BitConverter.ToInt32(e.Msg.readBuffer, e.Index + 4);
							GetChest(X, Y, index);
							e.Handled = true;
						}
						break;
					case PacketTypes.ChestItem:
						{
							byte slot = e.Msg.readBuffer[e.Index + 2];
							if (slot > 40)
							{
								return;
							}
							short stack = BitConverter.ToInt16(e.Msg.readBuffer, e.Index + 3);
							byte prefix = e.Msg.readBuffer[e.Index + 5];
							int netID = BitConverter.ToInt16(e.Msg.readBuffer, e.Index + 6);
							ModChest(index, slot, netID, stack, prefix);
							e.Handled = true;
						}
						break;
					case PacketTypes.Tile:
						{
							if (e.Msg.readBuffer[e.Index] == 1 && e.Msg.readBuffer[e.Index + 9] == 21)
							{
								int X = BitConverter.ToInt32(e.Msg.readBuffer, e.Index + 1);
								int Y = BitConverter.ToInt32(e.Msg.readBuffer, e.Index + 5);
								if ((TShock.Utils.TilePlacementValid(X, Y + 1) && Main.tile[X, Y + 1].type == 138) ||
									(TShock.Utils.TilePlacementValid(X + 1, Y + 1) && Main.tile[X + 1, Y + 1].type == 138))
								{
									TShock.Players[index].SendTileSquare(X, Y, 3);
									e.Handled = true;
									return;
								}
								if (TShock.Regions.CanBuild(X, Y, TShock.Players[index]))
								{
									PlaceChest(X, Y, index);
									WorldGen.PlaceChest(X, Y, 21, false, e.Msg.readBuffer[e.Index + 10]);
									NetMessage.SendData((int)PacketTypes.Tile, -1, index, "", 1, X, Y, 21, e.Msg.readBuffer[e.Index + 10]);
									e.Handled = true;
								}
							}
						}
						break;
					case PacketTypes.TileKill:
						{
							int X = BitConverter.ToInt32(e.Msg.readBuffer, e.Index);
							int Y = BitConverter.ToInt32(e.Msg.readBuffer, e.Index + 4);
							if (TShock.Regions.CanBuild(X, Y, TShock.Players[index]) && Main.tile[X, Y].type == 21)
							{
								if (Main.tile[X, Y].frameY != 0)
								{
									Y--;
								}
								if (Main.tile[X, Y].frameX % 36 != 0)
								{
									X--;
								}
								KillChest(X, Y, index);
								TShock.Players[index].SendTileSquare(X, Y, 3);
								e.Handled = true;
							}
						}
						break;
				}
			}
		}
		void OnInitialize(EventArgs e)
		{
			Commands.ChatCommands.Add(new Command("infchests.chest.deselect", Deselect, "ccset"));
			Commands.ChatCommands.Add(new Command("infchests.admin.info", Info, "cinfo"));
			Commands.ChatCommands.Add(new Command("infchests.chest.lock", Lock, "clock") { DoLog = false });
			Commands.ChatCommands.Add(new Command("infchests.admin.convert", ConvertChests, "convchests"));
			Commands.ChatCommands.Add(new Command("infchests.chest.public", PublicProtect, "cpset"));
			Commands.ChatCommands.Add(new Command("infchests.admin.refill", Refill, "crefill"));
			Commands.ChatCommands.Add(new Command("infchests.chest.region", RegionProtect, "crset"));
			Commands.ChatCommands.Add(new Command("infchests.chest.protect", Protect, "cset"));
			Commands.ChatCommands.Add(new Command("infchests.chest.unlock", Unlock, "cunlock") { DoLog = false });
			Commands.ChatCommands.Add(new Command("infchests.chest.unprotect", Unprotect, "cunset"));

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
				new SqlColumn("Password", MySqlDbType.Text),
				new SqlColumn("WorldID", MySqlDbType.Int32)));
		}
		void OnLeave(LeaveEventArgs e)
		{
			Infos[e.Who] = new PlayerInfo();
		}
		void OnUpdate(EventArgs e)
		{
			if ((DateTime.UtcNow - LastCheck).TotalSeconds > 1)
			{
				LastCheck = DateTime.UtcNow;
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
		}

		void GetChest(int X, int Y, int plr)
		{
			Chest chest = null;
			using (QueryResult reader = Database.QueryReader("SELECT Account, Flags, Items, Password FROM Chests WHERE X = @0 AND Y = @1 and WorldID = @2",
				X, Y, Main.worldID))
			{
				if (reader.Read())
				{
					chest = new Chest
					{
						account = reader.Get<string>("Account"),
						flags = (ChestFlags)reader.Get<int>("Flags"),
						items = reader.Get<string>("Items"),
						password = reader.Get<string>("Password")
					};
				}
			}
			TSPlayer player = TShock.Players[plr];

			if (chest != null)
			{
				switch (Infos[plr].action)
				{
					case ChestAction.INFO:
						player.SendInfoMessage("X: {0} Y: {1} Account: {2} {3}Refill: {4} ({5} second{6}) Region: {7}",
							X, Y, chest.account == "" ? "N/A" : chest.account, ((chest.flags & ChestFlags.PUBLIC) != 0) ? "(public) " : "",
							(chest.flags & ChestFlags.REFILL) != 0, (int)chest.flags / 8, (int)chest.flags / 8 == 1 ? "" : "s",
							(chest.flags & ChestFlags.REGION) != 0);
						break;
					case ChestAction.PROTECT:
						if (chest.account != "")
						{
							player.SendErrorMessage("This chest is already protected.");
							break;
						}
						Database.Query("UPDATE Chests SET Account = @0 WHERE X = @1 AND Y = @2 AND WorldID = @3",
							player.UserAccountName, X, Y, Main.worldID);
						player.SendSuccessMessage("This chest is now protected.");
						break;
					case ChestAction.PUBLIC:
						if (chest.account == "")
						{
							player.SendErrorMessage("This chest is not protected.");
							break;
						}
						if (chest.account != player.UserAccountName && !player.Group.HasPermission("infchests.admin.editall"))
						{
							player.SendErrorMessage("This chest is not yours.");
							break;
						}
						Database.Query("UPDATE Chests SET Flags = ((~(Flags & 1)) & (Flags | 1)) WHERE X = @0 AND Y = @1 AND WorldID = @2",
							X, Y, Main.worldID);
						player.SendSuccessMessage("This chest is now {0}.",
							(chest.flags & ChestFlags.PUBLIC) != 0 ? "private" : "public");
						break;
					case ChestAction.REFILL:
						if (chest.account != player.UserAccountName && chest.account != "" && !player.Group.HasPermission("infchests.admin.editall"))
						{
							player.SendErrorMessage("This chest is not yours.");
							break;
						}
						if (Infos[plr].time > 0)
						{
							Database.Query("UPDATE Chests SET Flags = @0 WHERE X = @1 AND Y = @2 AND WorldID = @3",
								((int)chest.flags & 3) + (Infos[plr].time * 8) + 4, X, Y, Main.worldID);
							player.SendSuccessMessage(string.Format("This chest will now refill with a delay of {0} second{1}.", Infos[plr].time,
								Infos[plr].time == 1 ? "" : "s"));
						}
						else
						{
							Database.Query("UPDATE Chests SET Flags = ((~(Flags & 4)) & (Flags | 4)) & 7 WHERE X = @0 AND Y = @1 AND WorldID = @2",
								X, Y, Main.worldID);
							player.SendSuccessMessage("This chest will {0} refill.",
								(chest.flags & ChestFlags.REFILL) != 0 ? "no longer" : "now");
						}
						break;
					case ChestAction.REGION:
						if (chest.account == "")
						{
							player.SendErrorMessage("This chest is not protected.");
							break;
						}
						if (chest.account != player.UserAccountName && !player.Group.HasPermission("infchests.admin.editall"))
						{
							player.SendErrorMessage("This chest is not yours.");
							break;
						}
						Database.Query("UPDATE Chests SET Flags = ((~(Flags & 2)) & (Flags | 2)) WHERE X = @0 AND Y = @1 AND WorldID = @2",
							X, Y, Main.worldID);
						player.SendSuccessMessage("This chest is {0} region shared.",
							(chest.flags & ChestFlags.REGION) != 0 ? "no longer" : "now");
						break;
					case ChestAction.SETPASS:
						if (chest.account == "")
						{
							player.SendErrorMessage("This chest is not protected.");
							break;
						}
						if (chest.account != player.UserAccountName && !player.Group.HasPermission("infchests.admin.editall"))
						{
							player.SendErrorMessage("This chest is not yours.");
							break;
						}
						if (Infos[plr].password.ToLower() == "remove")
						{
							Database.Query("UPDATE Chests SET Password = '' WHERE X = @0 AND Y = @1 AND WorldID = @2",
								X, Y, Main.worldID);
						}
						else
						{
							Database.Query("UPDATE Chests SET Password = @0 WHERE X = @1 AND Y = @2 AND WorldID = @3",
								TShock.Utils.HashPassword(Infos[plr].password), X, Y, Main.worldID);
						}
						player.SendSuccessMessage("This chest is now password protected.");
						break;
					case ChestAction.UNPROTECT:
						if (chest.account == "")
						{
							player.SendErrorMessage("This chest is not protected.");
							break;
						}
						if (chest.account != player.UserAccountName && !player.Group.HasPermission("infchests.admin.editall"))
						{
							player.SendErrorMessage("This chest is not yours.");
							break;
						}
						Database.Query("UPDATE Chests SET Account = '' WHERE X = @0 AND Y = @1 AND WorldID = @2",
							X, Y, Main.worldID);
						player.SendSuccessMessage("This chest is now un-protected.");
						break;
					default:
						bool isFree = chest.account == "";
						bool isOwner = chest.account == player.UserAccountName || player.Group.HasPermission("infchests.admin.editall");
						bool isPub = (chest.flags & ChestFlags.PUBLIC) == ChestFlags.PUBLIC;
						bool isRegion = (chest.flags & ChestFlags.REGION) == ChestFlags.REGION && TShock.Regions.CanBuild(X, Y, player);
						if (!isFree && !isOwner && !isPub && !isRegion)
						{
							if (String.IsNullOrEmpty(chest.password))
							{
								player.SendErrorMessage("This chest is protected.");
								break;
							}
							else if (TShock.Utils.HashPassword(Infos[plr].password) != chest.password)
							{
								player.SendErrorMessage("This chest is password protected.");
								break;
							}
							else
							{
								player.SendSuccessMessage("Chest unlocked.");
								Infos[plr].password = "";
							}
						}
						int timeLeft;
						if (Timer.TryGetValue(new Point(X, Y), out timeLeft) && timeLeft > 0)
						{
							player.SendErrorMessage("This chest will refill in {0} second{1}.", timeLeft, timeLeft == 1 ? "" : "s");
							break;
						}

						int[] itemArgs = new int[120];
						string[] split = chest.items.Split(',');
						for (int i = 0; i < 120; i++)
						{
							itemArgs[i] = Convert.ToInt32(split[i]);
						}
                        
						byte[] raw = new byte[] { 9, 0, 0, 0, 32, 0, 0, 255, 255, 255, 255, 255, 255 };
						for (int i = 0; i < 40; i++)
						{
							raw[7] = (byte)i;
							raw[8] = (byte)itemArgs[i * 3 + 1];
							raw[9] = (byte)(itemArgs[i * 3 + 1] >> 8);
							raw[10] = (byte)itemArgs[i * 3 + 2];
							raw[11] = (byte)itemArgs[i * 3];
							raw[12] = (byte)(itemArgs[i * 3] >> 8);
							player.SendRawData(raw);
						}

						byte[] raw2 = new byte[] { 11, 0, 0, 0, 33, 0, 0, 255, 255, 255, 255, 255, 255, 255, 255 };
						Buffer.BlockCopy(BitConverter.GetBytes(X), 0, raw2, 7, 4);
						Buffer.BlockCopy(BitConverter.GetBytes(Y), 0, raw2, 11, 4);
						player.SendRawData(raw2);
						Infos[plr].x = X;
						Infos[plr].y = Y;
						break;
				}
				Infos[plr].action = ChestAction.NONE;
			}
		}
		void KillChest(int X, int Y, int plr)
		{
			Chest chest = null;
			using (QueryResult reader = Database.QueryReader("SELECT Account, Items FROM Chests WHERE X = @0 AND Y = @1 AND WorldID = @2",
				X, Y, Main.worldID))
			{
				if (reader.Read())
				{
					chest = new Chest { account = reader.Get<string>("Account"), items = reader.Get<string>("Items") };
				}
			}
			TSPlayer player = TShock.Players[plr];

			if (chest != null && chest.account != player.UserAccountName && chest.account != "" && !player.Group.HasPermission("infchests.admin.editall"))
			{
				player.SendErrorMessage("This chest is protected.");
				player.SendTileSquare(X, Y, 3);
			}
			else if (chest != null && chest.items !=
				"0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0," +
				"0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0")
			{
				player.SendTileSquare(X, Y, 3);
			}
			else
			{
				WorldGen.KillTile(X, Y);
				Database.Query("DELETE FROM Chests WHERE X = @0 AND Y = @1 and WorldID = @2", X, Y, Main.worldID);
				TSPlayer.All.SendData(PacketTypes.Tile, "", 0, X, Y + 1);
			}
		}
		void ModChest(int plr, byte slot, int ID, int stack, byte prefix)
		{
			Chest chest = null;
            using (QueryResult reader = Database.QueryReader("SELECT Account, Flags, Items FROM Chests WHERE X = @0 AND Y = @1 AND WorldID = @2",
				Infos[plr].x, Infos[plr].y, Main.worldID))
			{
				if (reader.Read())
				{
                    chest = new Chest { flags = (ChestFlags)reader.Get<int>("Flags"), items = reader.Get<string>("Items"), account = reader.Get<string>("Account") };
				}
			}
			TSPlayer player = TShock.Players[plr];

			if (chest != null)
			{
				if ((chest.flags & ChestFlags.REFILL) != 0)
				{
					if (!Timer.ContainsKey(new Point(Infos[plr].x, Infos[plr].y)))
					{
						Timer.Add(new Point(Infos[plr].x, Infos[plr].y), (int)chest.flags >> 3);
					}
				}
				else
				{
					int[] itemArgs = new int[120];
                    string[] split = chest.items.Split(',');
					for (int i = 0; i < 120; i++)
                    {
                        itemArgs[i] = Convert.ToInt32(split[i]);
                    }
					itemArgs[slot * 3] = ID;
					itemArgs[slot * 3 + 1] = stack;
					itemArgs[slot * 3 + 2] = prefix;
					StringBuilder newItems = new StringBuilder();
					for (int i = 0; i < 120; i++)
					{
						newItems.Append(itemArgs[i]);
						if (i != 119)
						{
							newItems.Append(',');
						}
					}
					Database.Query("UPDATE Chests SET Items = @0 WHERE X = @1 AND Y = @2 AND WorldID = @3",
						newItems.ToString(), Infos[plr].x, Infos[plr].y, Main.worldID);

					for (int i = 0; i < 256; i++)
					{
						if (Infos[i].x == Infos[plr].x && Infos[i].y == Infos[plr].y && i != plr)
						{
							byte[] raw = new byte[] { 9, 0, 0, 0, 32, 0, 0, slot, (byte)stack, (byte)(stack >> 8), prefix, (byte)ID, (byte)(ID >> 8) };
							TShock.Players[i].SendRawData(raw);
						}
					}
				}
			}
		}
		void PlaceChest(int X, int Y, int plr)
		{
			TSPlayer player = TShock.Players[plr];
			Database.Query("INSERT INTO Chests (X, Y, Account, Flags, Items, Password, WorldID) VALUES (@0, @1, @2, @3, @4, \'\', @5)",
				X, Y - 1, (player.IsLoggedIn && player.Group.HasPermission("infchests.chest.protect")) ? player.UserAccountName : "", 0,
				"0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0," +
				"0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0", Main.worldID);
			Main.chest[0] = null;
		}

		void ConvertChests(CommandArgs e)
		{
			Database.Query("DELETE FROM Chests WHERE WorldID = @0", Main.worldID);
			int converted = 0;
			StringBuilder items = new StringBuilder();
			for (int i = 0; i < 1000; i++)
			{
				Terraria.Chest c = Main.chest[i];
				if (c != null)
				{
					for (int j = 0; j < 40; j++)
					{
						items.Append(c.item[j].netID + "," + c.item[j].stack + "," + c.item[j].prefix);
						if (j != 39)
						{
							items.Append(",");
						}
					}
					Database.Query("INSERT INTO Chests (X, Y, Account, Items, WorldID) VALUES (@0, @1, '', @2, @3)",
						c.x, c.y, items.ToString(), Main.worldID);
					converted++;
					items.Clear();
					Main.chest[i] = null;
				}
			}
			e.Player.SendSuccessMessage("Converted {0} chest{1}.", converted, converted == 1 ? "" : "s");
		}
		void Deselect(CommandArgs e)
		{
			Infos[e.Player.Index].action = ChestAction.NONE;
			e.Player.SendInfoMessage("Stopped selecting a chest.");
		}
		void Info(CommandArgs e)
		{
			Infos[e.Player.Index].action = ChestAction.INFO;
			e.Player.SendInfoMessage("Open a chest to get its info.");
		}
		void Lock(CommandArgs e)
		{
			if (e.Parameters.Count != 1)
			{
				e.Player.SendErrorMessage("Invalid syntax! Proper syntax: /clock <password>");
				return;
			}

			Infos[e.Player.Index].action = ChestAction.SETPASS;
			Infos[e.Player.Index].password = e.Parameters[0];
			if (e.Parameters[0].ToLower() == "remove")
			{
				e.Player.SendInfoMessage("Open chest to disable a password on it.");
			}
			else
			{
				e.Player.SendInfoMessage("Open chest to enable a password on it.");
			}
		}
		void Protect(CommandArgs e)
		{
			Infos[e.Player.Index].action = ChestAction.PROTECT;
			e.Player.SendInfoMessage("Open a chest to protect it.");
		}
		void Refill(CommandArgs e)
		{
			if (e.Parameters.Count > 1)
			{
				e.Player.SendErrorMessage("Invalid syntax! Proper syntax: : /crefill [interval]");
				return;
			}
			Infos[e.Player.Index].time = 0;
			if (e.Parameters.Count == 1)
			{
				int time;
				if (int.TryParse(e.Parameters[0], out time) && time > 0)
				{
					Infos[e.Player.Index].action = ChestAction.REFILL;
					Infos[e.Player.Index].time = time;
					e.Player.SendInfoMessage("Open a chest to make it refill with an interval of {0} second{1}.", time, time == 1 ? "" : "s");
					return;
				}
				e.Player.SendErrorMessage("Invalid interval!");
			}
			else
			{
				Infos[e.Player.Index].action = ChestAction.REFILL;
				e.Player.SendInfoMessage("Open a chest to toggle its refill status.");
			}
		}
		void PublicProtect(CommandArgs e)
		{
			Infos[e.Player.Index].action = ChestAction.PUBLIC;
			e.Player.SendInfoMessage("Open a chest to toggle its public status.");
		}
		void RegionProtect(CommandArgs e)
		{
			Infos[e.Player.Index].action = ChestAction.REGION;
			e.Player.SendInfoMessage("Open a chest to toggle its region share status.");
		}
		void Unlock(CommandArgs e)
		{
			if (e.Parameters.Count != 1)
			{
				e.Player.SendErrorMessage("Invalid syntax! Proper syntax: /cunlock <password>");
				return;
			}
			Infos[e.Player.Index].password = e.Parameters[0];
			e.Player.SendInfoMessage("Open chest to unlock it.");
		}
		void Unprotect(CommandArgs e)
		{
			Infos[e.Player.Index].action = ChestAction.UNPROTECT;
			e.Player.SendInfoMessage("Open a chest to unprotect it.");
		}
	}
}
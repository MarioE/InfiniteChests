using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.IO.Streams;
using Mono.Data.Sqlite;
using MySql.Data.MySqlClient;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.DB;

namespace InfiniteChests
{
	[ApiVersion(1, 16)]
	public class InfiniteChests : TerrariaPlugin
	{
		IDbConnection Database;
		PlayerInfo[] Infos = new PlayerInfo[256];
		System.Timers.Timer Timer = new System.Timers.Timer(1000);
		Dictionary<Point, int> Timers = new Dictionary<Point, int>();

		public override string Author
		{
			get { return "MarioE"; }
		}
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
				ServerApi.Hooks.GamePostInitialize.Deregister(this, OnPostInitialize);
				ServerApi.Hooks.ServerLeave.Deregister(this, OnLeave);

				Database.Dispose();
				Timer.Dispose();
			}
		}
		public override void Initialize()
		{
			ServerApi.Hooks.NetGetData.Register(this, OnGetData);
			ServerApi.Hooks.GameInitialize.Register(this, OnInitialize);
			ServerApi.Hooks.GamePostInitialize.Register(this, OnPostInitialize);
			ServerApi.Hooks.ServerLeave.Register(this, OnLeave);

			Timer.Elapsed += OnElapsed;
			Timer.Start();
		}

		void OnElapsed(object o, ElapsedEventArgs e)
		{
			lock (Timers)
			{
				var newTimers = new Dictionary<Point, int>(Timers);
				foreach (Point p in Timers.Keys)
				{
					if (newTimers[p] == 0)
						newTimers.Remove(p);
					else
						newTimers[p]--;
				}
				Timers = newTimers;
			}
		}
		void OnGetData(GetDataEventArgs e)
		{
			if (!e.Handled)
			{
				int plr = e.Msg.whoAmI;
				using (var reader = new MemoryStream(e.Msg.readBuffer, e.Index, e.Length))
				{
					switch (e.MsgID)
					{
						case PacketTypes.ChestGetContents:
							{
								int x = reader.ReadInt16();
								int y = reader.ReadInt16();
								Task.Factory.StartNew(() => GetChest(x, y, plr));
								e.Handled = true;
							}
							break;
						case PacketTypes.ChestItem:
							{
								reader.ReadInt16();
								byte slot = (byte)reader.ReadByte();
								if (slot > 40)
									return;

								int stack = reader.ReadInt16();
								byte prefix = (byte)reader.ReadByte();
								int netID = reader.ReadInt16();
								Task.Factory.StartNew(() => ModChest(plr, slot, netID, stack, prefix));
								e.Handled = true;
							}
							break;
						case PacketTypes.ChestOpen:
							{
								reader.ReadInt16();
								reader.ReadInt16();
								reader.ReadInt16();
								string name = reader.ReadString();

								if (name.Length > 0)
									Task.Factory.StartNew(() => NameChest(Infos[plr].x, Infos[plr].y, plr, name));
							}
							break;
						case PacketTypes.TileKill:
							{
								byte action = (byte)reader.ReadByte();
								int x = reader.ReadInt16();
								int y = reader.ReadInt16();
								int style = reader.ReadInt16();

								if (action == 0)
								{
									if (TShock.Regions.CanBuild(x, y, TShock.Players[plr]))
									{
										Task.Factory.StartNew(() => PlaceChest(x, y, plr));
										WorldGen.PlaceChest(x, y, 21, false, style);
										NetMessage.SendData((int)PacketTypes.TileKill, -1, plr, "", 0, x, y, style, 1);
										NetMessage.SendData((int)PacketTypes.TileKill, plr, -1, "", 0, x, y, style, 0);
										e.Handled = true;
									}
								}
								else if (TShock.Regions.CanBuild(x, y, TShock.Players[plr]) && Main.tile[x, y].type == 21)
								{
									if (Main.tile[x, y].frameY % 36 != 0)
										y--;
									if (Main.tile[x, y].frameX % 36 != 0)
										x--;
									Task.Factory.StartNew(() => KillChest(x, y, plr));
									e.Handled = true;
								}
							}
							break;
					}
				}
			}
		}
		void OnInitialize(EventArgs e)
		{
			Commands.ChatCommands.Add(new Command("infchests.chest.deselect", Deselect, "ccset"));
			Commands.ChatCommands.Add(new Command("infchests.admin.info", Info, "cinfo"));
			Commands.ChatCommands.Add(new Command("infchests.chest.lock", Lock, "clock") { DoLog = false });
			Commands.ChatCommands.Add(new Command("infchests.admin.convert", ConvertChests, "convchests"));
			Commands.ChatCommands.Add(new Command("infchests.admin.prune", Prune, "prunechests"));
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
				new SqlColumn("Name", MySqlDbType.Text),
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
		void OnPostInitialize(EventArgs e)
		{
			int converted = 0;
			StringBuilder items = new StringBuilder();
			for (int i = 0; i < 1000; i++)
			{
				Terraria.Chest c = Main.chest[i];
				if (c != null)
				{
					for (int j = 0; j < 40; j++)
						items.Append("," + c.item[j].netID + "," + c.item[j].stack + "," + c.item[j].prefix);
					Database.Query("INSERT INTO Chests (X, Y, Account, Items, WorldID) VALUES (@0, @1, '', @2, @3)",
						c.x, c.y, items.ToString().Substring(1), Main.worldID);
					converted++;
					items.Clear();
					Main.chest[i] = null;
				}
			}

			if (converted > 0)
			{
				TSPlayer.Server.SendSuccessMessage("[InfiniteChests] Converted {0} chest{1}.", converted, converted == 1 ? "" : "s");
				WorldFile.saveWorld();
			}
		}

		void GetChest(int x, int y, int plr)
		{
			try
			{
				Chest chest = null;
				using (QueryResult reader = Database.QueryReader("SELECT Account, Flags, Items, Name, Password FROM Chests WHERE X = @0 AND Y = @1 and WorldID = @2",
					x, y, Main.worldID))
				{
					if (reader.Read())
					{
						chest = new Chest
						{
							account = reader.Get<string>("Account"),
							flags = (ChestFlags)reader.Get<int>("Flags"),
							items = reader.Get<string>("Items"),
							name = reader.Get<string>("Name"),
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
								x, y, chest.account == "" ? "N/A" : chest.account, ((chest.flags & ChestFlags.PUBLIC) != 0) ? "(public) " : "",
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
								player.UserAccountName, x, y, Main.worldID);
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
								x, y, Main.worldID);
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
									((int)chest.flags & 3) + (Infos[plr].time * 8) + 4, x, y, Main.worldID);
								player.SendSuccessMessage(string.Format("This chest will now refill with a delay of {0} second{1}.", Infos[plr].time,
									Infos[plr].time == 1 ? "" : "s"));
							}
							else
							{
								Database.Query("UPDATE Chests SET Flags = ((~(Flags & 4)) & (Flags | 4)) & 7 WHERE X = @0 AND Y = @1 AND WorldID = @2",
									x, y, Main.worldID);
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
								x, y, Main.worldID);
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
									x, y, Main.worldID);
							}
							else
							{
								Database.Query("UPDATE Chests SET Password = @0 WHERE X = @1 AND Y = @2 AND WorldID = @3",
									TShock.Utils.HashPassword(Infos[plr].password), x, y, Main.worldID);
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
								x, y, Main.worldID);
							player.SendSuccessMessage("This chest is now un-protected.");
							break;
						default:
							bool isFree = chest.account == "";
							bool isOwner = chest.account == player.UserAccountName || player.Group.HasPermission("infchests.admin.editall");
							bool isPub = (chest.flags & ChestFlags.PUBLIC) == ChestFlags.PUBLIC;
							bool isRegion = (chest.flags & ChestFlags.REGION) == ChestFlags.REGION && TShock.Regions.CanBuild(x, y, player);
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
							lock (Timers)
							{
								if (Timers.TryGetValue(new Point(x, y), out timeLeft) && timeLeft > 0)
								{
									player.SendErrorMessage("This chest will refill in {0} second{1}.", timeLeft, timeLeft == 1 ? "" : "s");
									break;
								}
							}

							int[] itemArgs = new int[120];
							string[] split = chest.items.Split(',');
							for (int i = 0; i < 120; i++)
								itemArgs[i] = Convert.ToInt32(split[i]);


							byte[] raw = new byte[] { 11, 0, 32, 0, 0, 255, 255, 255, 255, 255, 255 };

							for (int i = 0; i < 40; i++)
							{
								raw[5] = (byte)i;
								raw[6] = (byte)itemArgs[i * 3 + 1];
								raw[7] = (byte)(itemArgs[i * 3 + 1] >> 8);
								raw[8] = (byte)itemArgs[i * 3 + 2];
								raw[9] = (byte)itemArgs[i * 3];
								raw[10] = (byte)(itemArgs[i * 3] >> 8);
								player.SendRawData(raw);
							}

							byte[] raw2 = new byte[] { 9, 0, 33, 0, 0, 255, 255, 255, 255 };
							Buffer.BlockCopy(BitConverter.GetBytes((short)x), 0, raw2, 5, 2);
							Buffer.BlockCopy(BitConverter.GetBytes((short)y), 0, raw2, 7, 2);
							player.SendRawData(raw2);

							player.SendData(PacketTypes.ChestName, chest.name ?? "Chest", 0, x, y);

							Infos[plr].x = x;
							Infos[plr].y = y;
							break;
					}
					Infos[plr].action = ChestAction.NONE;
				}
			}
			catch (Exception ex)
			{
				Log.Error("InfiniteChests: " + ex.Message);
			}
		}
		void KillChest(int x, int y, int plr)
		{
			Chest chest = null;
			using (QueryResult reader = Database.QueryReader("SELECT Account, Items FROM Chests WHERE X = @0 AND Y = @1 AND WorldID = @2",
				x, y, Main.worldID))
			{
				if (reader.Read())
					chest = new Chest { account = reader.Get<string>("Account"), items = reader.Get<string>("Items") };
			}

			TSPlayer player = TShock.Players[plr];
			if (chest != null && chest.account != player.UserAccountName && chest.account != "" && !player.Group.HasPermission("infchests.admin.editall"))
			{
				player.SendErrorMessage("This chest is protected.");
				player.SendTileSquare(x, y, 3);
			}
			else if (chest != null && chest.items !=
				"0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0," +
				"0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0")
			{
				player.SendTileSquare(x, y, 3);
			}
			else
			{
				WorldGen.KillTile(x, y);
				Database.Query("DELETE FROM Chests WHERE X = @0 AND Y = @1 and WorldID = @2", x, y, Main.worldID);
				TSPlayer.All.SendData(PacketTypes.Tile, "", 0, x, y + 1);
			}
		}
		void NameChest(int x, int y, int plr, string name)
		{
			Chest chest = null;
			using (QueryResult reader = Database.QueryReader("SELECT Account FROM Chests WHERE X = @0 AND Y = @1 AND WorldID = @2",
				x, y, Main.worldID))
			{
				if (reader.Read())
					chest = new Chest { account = reader.Get<string>("Account") };
			}

			TSPlayer player = TShock.Players[plr];
			if (chest != null && chest.account != player.UserAccountName && chest.account != "" && !player.Group.HasPermission("infchests.admin.editall"))
			{
				player.SendErrorMessage("This chest is protected.");
				player.SendTileSquare(x, y, 3);
			}
			else
				Database.Query("UPDATE Chests SET Name = @0 WHERE X = @1 AND Y = @2 AND WorldID = @3", name, x, y, Main.worldID);
		}
		void ModChest(int plr, byte slot, int ID, int stack, byte prefix)
		{
			lock (Database)
			{
				Chest chest = null;
				using (QueryResult reader = Database.QueryReader("SELECT Account, Flags, Items FROM Chests WHERE X = @0 AND Y = @1 AND WorldID = @2",
					Infos[plr].x, Infos[plr].y, Main.worldID))
				{
					if (reader.Read())
						chest = new Chest { flags = (ChestFlags)reader.Get<int>("Flags"), items = reader.Get<string>("Items"), account = reader.Get<string>("Account") };
				}
				TSPlayer player = TShock.Players[plr];

				if (chest != null)
				{
					if ((chest.flags & ChestFlags.REFILL) != 0)
					{
						lock (Timers)
						{
							if (!Timers.ContainsKey(new Point(Infos[plr].x, Infos[plr].y)))
								Timers.Add(new Point(Infos[plr].x, Infos[plr].y), (int)chest.flags >> 3);
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
							newItems.Append("," + itemArgs[i]);
						Database.Query("UPDATE Chests SET Items = @0 WHERE X = @1 AND Y = @2 AND WorldID = @3",
							newItems.ToString().Substring(1), Infos[plr].x, Infos[plr].y, Main.worldID);

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
		}
		void PlaceChest(int x, int y, int plr)
		{
			TSPlayer player = TShock.Players[plr];
			Database.Query("INSERT INTO Chests (X, Y, Name, Account, Flags, Items, Password, WorldID) VALUES (@0, @1, @2, @3, @4, @5, \'\', @6)",
				x, y - 1, "Chest", (player.IsLoggedIn && player.Group.HasPermission("infchests.chest.protect")) ? player.UserAccountName : "", 0,
				"0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0," +
				"0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0", Main.worldID);
			Main.chest[999] = null;
		}

		void ConvertChests(CommandArgs e)
		{
			Task.Factory.StartNew(() => 
				{
					int converted = 0;
					StringBuilder items = new StringBuilder();
					for (int i = 0; i < 1000; i++)
					{
						Terraria.Chest c = Main.chest[i];
						if (c != null)
						{
							for (int j = 0; j < 40; j++)
								items.Append("," + c.item[j].netID + "," + c.item[j].stack + "," + c.item[j].prefix);
							Database.Query("INSERT INTO Chests (X, Y, Account, Items, WorldID) VALUES (@0, @1, '', @2, @3)",
								c.x, c.y, items.ToString().Substring(1), Main.worldID);
							converted++;
							items.Clear();
							Main.chest[i] = null;
						}
					}

					e.Player.SendSuccessMessage("[InfiniteChests] Converted {0} chest{1}.", converted, converted == 1 ? "" : "s");
					if (converted > 0)
						WorldFile.saveWorld();
				});
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
		void Prune(CommandArgs e)
		{
			Task.Factory.StartNew(() =>
				{
					using (var reader = Database.QueryReader("SELECT X, Y FROM Chests WHERE Items = @0 AND WorldID = @1",
						"0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0," +
						"0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0",
						Main.worldID))
					{
						while (reader.Read())
						{
							int x = reader.Get<int>("X");
							int y = reader.Get<int>("Y");
							WorldGen.KillTile(x, y);
							TSPlayer.All.SendTileSquare(x, y, 3);
						}
					}

					int count = Database.Query("DELETE FROM Chests WHERE Items = @0 AND WorldID = @1",
						"0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0," +
						"0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0",
						Main.worldID);
					e.Player.SendSuccessMessage("Pruned {0} chest{1}.", count, count == 1 ? "" : "s");
					if (count > 0)
						WorldFile.saveWorld();
				});
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
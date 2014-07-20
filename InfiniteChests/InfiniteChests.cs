using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Mono.Data.Sqlite;
using MySql.Data.MySqlClient;
using Terraria;
using Terraria.ID;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.DB;

namespace InfiniteChests
{
	[ApiVersion(1, 16)]
	public class InfiniteChests : TerrariaPlugin
	{
		private IDbConnection Database;
		private PlayerInfo[] Infos = new PlayerInfo[256];
		private System.Timers.Timer Timer = new System.Timers.Timer(1000);
		private Dictionary<Point, int> Timers = new Dictionary<Point, int>();

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
				Infos[i] = new PlayerInfo() { Index = i };
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
				using (var reader = new BinaryReader(new MemoryStream(e.Msg.readBuffer, e.Index, e.Length)))
				{
					switch (e.MsgID)
					{
						case PacketTypes.ChestGetContents:
							{
								int x = reader.ReadInt16();
								int y = reader.ReadInt16();
								Task.Factory.StartNew(() => GetChest(x, y, plr)).LogExceptions();
								e.Handled = true;
							}
							break;
						case PacketTypes.ChestItem:
							{
								reader.ReadInt16();
								byte slot = reader.ReadByte();
								if (slot > 40)
									return;

								int stack = reader.ReadInt16();
								byte prefix = reader.ReadByte();
								int netID = reader.ReadInt16();
								Task.Factory.StartNew(() => ModChest(plr, slot, netID, stack, prefix)).LogExceptions();
								e.Handled = true;
							}
							break;
						case PacketTypes.ChestOpen:
							{
								if (reader.ReadInt16() == -1)
								{
									Infos[plr].X = -1;
									Infos[plr].Y = -1;
								}
								int x = reader.ReadInt16();
								int y = reader.ReadInt16();
								int length = reader.ReadByte();

								if (length != 0 && length <= 20 && length != 255)
									TShock.Players[plr].SendData(PacketTypes.ChestName, "", 0, x, y);
							}
							break;
						case PacketTypes.TileKill:
							{
								byte action = reader.ReadByte();
								int x = reader.ReadInt16();
								int y = reader.ReadInt16();
								int style = reader.ReadInt16();

								if (action == 0)
								{
									if (TShock.Regions.CanBuild(x, y, TShock.Players[plr]))
									{
										Task.Factory.StartNew(() => PlaceChest(x, y, plr)).LogExceptions();
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
									Task.Factory.StartNew(() => KillChest(x, y, plr)).LogExceptions();
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
			Commands.ChatCommands.Add(new Command("infchests.chest.bank", Bank, "cbank")
			{
				DoLog = false,
				HelpText = "Toggles a chests's bank status when selected."
			});
			Commands.ChatCommands.Add(new Command("infchests.chest.deselect", Deselect, "ccset")
			{
				AllowServer = false,
				HelpText = "Cancels a chest selection."
			});
			Commands.ChatCommands.Add(new Command("infchests.admin.info", Info, "cinfo")
			{
				AllowServer = false,
				HelpText = "Gets information about a chest when selected."
			});
			Commands.ChatCommands.Add(new Command("infchests.chest.lock", Lock, "clock")
			{
				DoLog = false,
				HelpText = "Locks a chest with a password. Use remove as the password to remove it."
			});
			Commands.ChatCommands.Add(new Command("infchests.admin.convert", ConvertChests, "convchests")
			{
				HelpText = "Converts Terraria chests to InfiniteChests chests."
			});
			Commands.ChatCommands.Add(new Command("infchests.admin.prune", Prune, "prunechests")
			{
				HelpText = "Prunes empty chests."
			});
			Commands.ChatCommands.Add(new Command("infchests.chest.public", PublicProtect, "cpset")
			{
				AllowServer = false,
				HelpText = "Toggles a chest's publicity when selected."
			});
			Commands.ChatCommands.Add(new Command("infchests.admin.refill", Refill, "crefill")
			{
				AllowServer = false,
				HelpText = "Toggles a chest's refill status (with optional refill time) when selected."
			});
			Commands.ChatCommands.Add(new Command("infchests.chest.region", RegionProtect, "crset")
			{
				AllowServer = false,
				HelpText = "Toggles a chest's region sharing status when selected."
			});
			Commands.ChatCommands.Add(new Command("infchests.admin.rconvert", ReverseConvertChests, "rconvchests")
			{
				HelpText = "Converts InfiniteChests chests to Terraria chests."
			});
			Commands.ChatCommands.Add(new Command("infchests.chest.protect", Protect, "cset")
			{
				AllowServer = false,
				HelpText = "Protects an unprotected chest when selected."
			});
			Commands.ChatCommands.Add(new Command("infchests.chest.unlock", Unlock, "cunlock")
			{
				DoLog = false,
				HelpText = "Unlocks a chest with a password."
			});
			Commands.ChatCommands.Add(new Command("infchests.chest.unprotect", Unprotect, "cunset")
			{
				HelpText = "Unprotects a chest when selected."
			});

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
				new SqlColumn("ID", MySqlDbType.Int32) { AutoIncrement = true, Primary = true },
				new SqlColumn("X", MySqlDbType.Int32),
				new SqlColumn("Y", MySqlDbType.Int32),
				new SqlColumn("Account", MySqlDbType.Text),
				new SqlColumn("Items", MySqlDbType.Text),
				new SqlColumn("Flags", MySqlDbType.Int32),
				new SqlColumn("BankID", MySqlDbType.Int32),
				new SqlColumn("RefillTime", MySqlDbType.Int32),
				new SqlColumn("Password", MySqlDbType.Text),
				new SqlColumn("WorldID", MySqlDbType.Int32)));

			sqlcreator.EnsureExists(new SqlTable("BankChests",
				new SqlColumn("ID", MySqlDbType.Int32) { AutoIncrement = true, Primary = true },
				new SqlColumn("Account", MySqlDbType.Text),
				new SqlColumn("BankID", MySqlDbType.Int32),
				new SqlColumn("Items", MySqlDbType.Text)));
		}
		void OnLeave(LeaveEventArgs e)
		{
			Infos[e.Who] = new PlayerInfo();
		}
		void OnPostInitialize(EventArgs e)
		{
			int converted = 0;
			var items = new StringBuilder();
			for (int i = 0; i < 1000; i++)
			{
				Terraria.Chest c = Main.chest[i];
				if (c != null)
				{
					for (int j = 0; j < 40; j++)
						items.Append(c.item[j].netID).Append(",").Append(c.item[j].stack).Append(",").Append(c.item[j].prefix).Append(",");
					Database.Query("INSERT INTO Chests (X, Y, Account, Items, WorldID) VALUES (@0, @1, '', @2, @3)",
						c.x, c.y, items.ToString(0, items.Length - 1), Main.worldID);
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
			Chest chest = null;
			using (QueryResult reader = Database.QueryReader("SELECT Account, BankID, Flags, ID, Items, Password, RefillTime FROM Chests WHERE X = @0 AND Y = @1 AND WorldID = @2",
				x, y, Main.worldID))
			{
				if (reader.Read())
				{
					chest = new Chest
					{
						Account = reader.Get<string>("Account"),
						BankID = reader.Get<int>("BankID"),
						Flags = (ChestFlags)reader.Get<int>("Flags"),
						ID = reader.Get<int>("ID"),
						Items = reader.Get<string>("Items"),
						HashedPassword = reader.Get<string>("Password"),
						RefillTime = reader.Get<int>("RefillTime")
					};
				}
			}
			using (QueryResult reader = Database.QueryReader("SELECT Items FROM BankChests WHERE Account = @0 AND BankID = @1",
				chest.Account, chest.BankID))
			{
				if (reader.Read())
					chest.Items = reader.Get<string>("Items");
			}

			var info = Infos[plr];
			TSPlayer player = TShock.Players[plr];

			if (chest != null)
			{
				switch (info.Action)
				{
					case ChestAction.GetInfo:
						player.SendInfoMessage("X: {0} Y: {1} Account: {2} {3}Bank: {4} Refill: {5} ({6} second{7}) Region: {8}",
							x, y, chest.Account ?? "N/A", chest.Flags.HasFlag(ChestFlags.Public) ? "(public) " : "",
							chest.BankID, chest.Flags.HasFlag(ChestFlags.Refill), chest.RefillTime,
							chest.RefillTime == 1 ? "" : "s", chest.Flags.HasFlag(ChestFlags.Region));
						break;
					case ChestAction.Protect:
						if (!String.IsNullOrEmpty(chest.Account))
						{
							player.SendErrorMessage("This chest is already protected.");
							break;
						}
						Database.Query("UPDATE Chests SET Account = @0 WHERE ID = @1", player.UserAccountName, chest.ID);
						player.SendSuccessMessage("This chest is now protected.");
						break;
					case ChestAction.TogglePublic:
						if (String.IsNullOrEmpty(chest.Account))
						{
							player.SendErrorMessage("This chest is not protected.");
							break;
						}
						if (chest.Account != player.UserAccountName && !player.Group.HasPermission("infchests.admin.editall"))
						{
							player.SendErrorMessage("This chest is not yours.");
							break;
						}
						Database.Query("UPDATE Chests SET Flags = ((~(Flags & 1)) & (Flags | 1)) WHERE ID = @0", chest.ID);
						player.SendSuccessMessage("This chest is now p{0}.", chest.IsPublic ? "rivate" : "ublic");
						break;
					case ChestAction.ToggleRegion:
						if (String.IsNullOrEmpty(chest.Account))
						{
							player.SendErrorMessage("This chest is not protected.");
							break;
						}
						if (chest.Account != player.UserAccountName && !player.Group.HasPermission("infchests.admin.editall"))
						{
							player.SendErrorMessage("This chest is not yours.");
							break;
						}
						Database.Query("UPDATE Chests SET Flags = ((~(Flags & 2)) & (Flags | 2)) WHERE ID = @0", chest.ID);
						player.SendSuccessMessage("This chest is no{0} region shared.", chest.IsRegion ? " longer" : "w");
						break;
					case ChestAction.SetBank:
						if (String.IsNullOrEmpty(chest.Account))
						{
							player.SendErrorMessage("This chest is not protected.");
							break;
						}
						if (chest.Account != player.UserAccountName && !player.Group.HasPermission("infchests.admin.editall"))
						{
							player.SendErrorMessage("This chest is not yours.");
							break;
						}
						if (info.BankID == -1)
						{
							Database.Query("UPDATE Chests SET BankID = 0, Flags = Flags & 7 WHERE ID = @0", chest.ID);
							player.SendSuccessMessage("This chest is no longer a bank chest.", chest.BankID);
						}
						else
						{
							bool exists = false;
							using (var reader = Database.QueryReader("SELECT * FROM BankChests WHERE Account = @0 AND BankID = @1",
								chest.Account, info.BankID))
							{
								exists = reader.Read();
							}

							if (!exists)
							{
								Database.Query("INSERT INTO BankChests (Account, BankID, Items) VALUES (@0, @1, @2)",
									chest.Account, info.BankID,
									"0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0," +
									"0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0");
							}

							Database.Query("UPDATE Chests SET BankID = @0, Flags = Flags | 8 WHERE ID = @1", info.BankID, chest.ID);
							player.SendSuccessMessage("This chest is now bank ID {0}.", info.BankID);
						}
						break;
					case ChestAction.SetPassword:
						if (String.IsNullOrEmpty(chest.Account))
						{
							player.SendErrorMessage("This chest is not protected.");
							break;
						}
						if (chest.Account != player.UserAccountName && !player.Group.HasPermission("infchests.admin.editall"))
						{
							player.SendErrorMessage("This chest is not yours.");
							break;
						}
						if (String.Equals(info.Password, "remove", StringComparison.CurrentCultureIgnoreCase))
						{
							Database.Query("UPDATE Chests SET Password = '' WHERE ID = @0", chest.ID);
							player.SendSuccessMessage("This chest is no longer password protected.", info.Password);
						}
						else
						{
							Database.Query("UPDATE Chests SET Password = @0 WHERE ID = @1", TShock.Utils.HashPassword(info.Password), chest.ID);
							player.SendSuccessMessage("This chest is now password protected with password '{0}'.", info.Password);
						}
						break;
					case ChestAction.SetRefill:
						if (String.IsNullOrEmpty(chest.Account))
						{
							player.SendErrorMessage("This chest is not protected.");
							break;
						}
						if (chest.Account != player.UserAccountName && !player.Group.HasPermission("infchests.admin.editall"))
						{
							player.SendErrorMessage("This chest is not yours.");
							break;
						}
						if (info.RefillTime > 0)
						{
							Database.Query("UPDATE Chests SET Flags = Flags | 4, RefillTime = @0 WHERE ID = @1", info.RefillTime, chest.ID);
							player.SendSuccessMessage("This chest will now refill with a delay of {0} second{1}.", info.RefillTime, info.RefillTime == 1 ? "" : "s");
						}
						else
						{
							Database.Query("UPDATE Chests SET Flags = (~(Flags & 4)) & (Flags | 4) WHERE ID = @0", chest.ID);
							player.SendSuccessMessage("This chest will no{0} refill.", chest.IsRefill ? " longer" : "w");
						}
						break;
					case ChestAction.Unprotect:
						if (String.IsNullOrEmpty(chest.Account))
						{
							player.SendErrorMessage("This chest is not protected.");
							break;
						}
						if (chest.Account != player.UserAccountName && !player.Group.HasPermission("infchests.admin.editall"))
						{
							player.SendErrorMessage("This chest is not yours.");
							break;
						}
						Database.Query("UPDATE Chests SET Account = NULL, BankID = 0, Flags = 0 WHERE ID = @0", chest.ID);
						player.SendSuccessMessage("This chest is now un-protected.");
						break;
					default:
#if !MULTI_USE
						if (Infos.Any(p => p.X == x && p.Y == y))
						{
							player.SendErrorMessage("This chest is already in use.");
							return;
						}
#endif

						bool isFree = String.IsNullOrEmpty(chest.Account);
						bool isOwner = chest.Account == player.UserAccountName || player.Group.HasPermission("infchests.admin.editall");
						bool isRegion = chest.IsRegion && TShock.Regions.CanBuild(x, y, player);
						if (!isFree && !isOwner && !chest.IsPublic && !isRegion)
						{
							if (String.IsNullOrEmpty(chest.HashedPassword))
							{
								player.SendErrorMessage("This chest is protected.");
								break;
							}
							else if (TShock.Utils.HashPassword(info.Password) != chest.HashedPassword)
							{
								player.SendErrorMessage("This chest is password protected.");
								break;
							}
							else
							{
								player.SendSuccessMessage("Chest unlocked.");
								info.Password = "";
							}
						}

						lock (Timers)
						{
							int timeLeft;
							if (Timers.TryGetValue(new Point(x, y), out timeLeft) && timeLeft > 0)
							{
								player.SendErrorMessage("This chest will refill in {0} second{1}.", timeLeft, timeLeft == 1 ? "" : "s");
								break;
							}
						}

						int[] itemArgs = new int[120];
						string[] split = chest.Items.Split(',');
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

						byte[] raw2 = new byte[] { 10, 0, 33, 0, 0, 255, 255, 255, 255, 0 };
						Buffer.BlockCopy(BitConverter.GetBytes((short)x), 0, raw2, 5, 2);
						Buffer.BlockCopy(BitConverter.GetBytes((short)y), 0, raw2, 7, 2);
						player.SendRawData(raw2);
						player.SendData(PacketTypes.ChestName, "", 0, x, y);

						info.X = x;
						info.Y = y;
						break;
				}
				info.Action = ChestAction.None;
			}
			else
				player.SendErrorMessage("This chest is corrupted. Please destroy it.");
		}
		void KillChest(int x, int y, int plr)
		{
			Chest chest = null;
			using (QueryResult reader = Database.QueryReader("SELECT Account, Flags, ID, Items FROM Chests WHERE X = @0 AND Y = @1 AND WorldID = @2",
				x, y, Main.worldID))
			{
				if (reader.Read())
				{
					chest = new Chest
					{
						Account = reader.Get<string>("Account"),
						ID = reader.Get<int>("ID"),
						Flags = (ChestFlags)reader.Get<int>("Flags"),
						Items = reader.Get<string>("Items")
					};
				}
			}

			TSPlayer player = TShock.Players[plr];
			if (chest == null)
			{
				WorldGen.KillTile(x, y);
				TSPlayer.All.SendData(PacketTypes.Tile, "", 0, x, y + 1);
			}
			else if (chest.Account != player.UserAccountName && !String.IsNullOrEmpty(chest.Account) && !player.Group.HasPermission("infchests.admin.editall"))
			{
				player.SendErrorMessage("This chest is protected.");
				player.SendTileSquare(x, y, 3);
			}
			else if (chest.IsBank)
			{
				player.SendErrorMessage("This chest is a bank chest.");
				player.SendTileSquare(x, y, 3);
			}
			else if (chest.Items !=
				"0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0," +
				"0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0")
			{
				player.SendTileSquare(x, y, 3);
			}
			else
			{
				WorldGen.KillTile(x, y);
				Database.Query("DELETE FROM Chests WHERE ID = @0", chest.ID);
				TSPlayer.All.SendData(PacketTypes.Tile, "", 0, x, y + 1);
			}
		}
		void ModChest(int plr, byte slot, int ID, int stack, byte prefix)
		{
			lock (Database)
			{
				Chest chest = null;
				using (QueryResult reader = Database.QueryReader("SELECT Account, BankID, Flags, ID, Items, RefillTime FROM Chests WHERE X = @0 AND Y = @1 AND WorldID = @2",
					Infos[plr].X, Infos[plr].Y, Main.worldID))
				{
					if (reader.Read())
					{
						chest = new Chest
						{
							Account = reader.Get<string>("Account"),
							BankID = reader.Get<int>("BankID"),
							Flags = (ChestFlags)reader.Get<int>("Flags"),
							ID = reader.Get<int>("ID"),
							Items = reader.Get<string>("Items"),
							RefillTime = reader.Get<int>("RefillTime")
						};
					}
				}

				TSPlayer player = TShock.Players[plr];
				if (player == null)
					return;

				if (chest == null)
				{
					player.SendErrorMessage("This chest is corrupted. Please remove it.");
					return;
				}

				if (chest.IsBank)
				{
					using (QueryResult reader = Database.QueryReader("SELECT Items FROM BankChests WHERE Account = @0 AND BankID = @1",
						chest.Account, chest.BankID))
					{
						if (reader.Read())
							chest.Items = reader.Get<string>("Items");
						else
						{
							player.SendErrorMessage("This bank chest was corrupted.");
							return;
						}
					}
				}

				var info = Infos[plr];
				if (chest.IsRefill)
				{
					lock (Timers)
					{
						if (!Timers.ContainsKey(new Point(info.X, info.Y)))
							Timers.Add(new Point(info.X, info.Y), chest.RefillTime);
					}
				}
				else
				{
					int[] itemArgs = new int[120];
					string[] split = chest.Items.Split(',');
					for (int i = 0; i < 120; i++)
						itemArgs[i] = Convert.ToInt32(split[i]);
					itemArgs[slot * 3] = ID;
					itemArgs[slot * 3 + 1] = stack;
					itemArgs[slot * 3 + 2] = prefix;
					StringBuilder newItems = new StringBuilder();
					for (int i = 0; i < 120; i++)
						newItems.Append(itemArgs[i]).Append(",");

					if (chest.IsBank)
					{
						Database.Query("UPDATE BankChests SET Items = @0 WHERE Account = @1 AND BankID = @2",
							newItems.ToString(0, newItems.Length - 1), chest.Account, chest.BankID);
					}
					else
					{
						Database.Query("UPDATE Chests SET Items = @0 WHERE ID = @1", newItems.ToString(0, newItems.Length - 1), chest.ID);
#if MULTI_USE
						byte[] raw = new byte[] { 11, 0, 32, 0, 0, slot, (byte)stack, (byte)(stack >> 8), prefix, (byte)ID, (byte)(ID >> 8) };
						foreach (int i in Infos.Where(i => i.X == info.X && i.Y == info.Y && i != info).Select(i => i.Index))
							TShock.Players[i].SendRawData(raw);
#endif
					}
				}
			}
		}
		void PlaceChest(int x, int y, int plr)
		{
			TSPlayer player = TShock.Players[plr];
			Database.Query("INSERT INTO Chests (X, Y, Account, Flags, Items, Password, WorldID) VALUES (@0, @1, @2, 0, @3, NULL, @4)",
				x, y - 1, (player.IsLoggedIn && player.Group.HasPermission("infchests.chest.protect")) ? player.UserAccountName : null,
				"0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0," +
				"0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0", Main.worldID);
			Main.chest[999] = null;
		}

		void Bank(CommandArgs e)
		{
			if (e.Parameters.Count != 1)
			{
				e.Player.SendErrorMessage("Invalid syntax! Proper syntax: /cbank <ID or remove>");
				return;
			}

			int bankID;
			if (!int.TryParse(e.Parameters[0], out bankID) || bankID <= 0)
			{
				if (String.Equals(e.Parameters[0], "remove", StringComparison.CurrentCultureIgnoreCase))
				{
					Infos[e.Player.Index].Action = ChestAction.SetBank;
					Infos[e.Player.Index].BankID = -1;
					e.Player.SendInfoMessage("Open a chest to remove its bank ID.", e.Parameters[0].ToLower());
				}
				else
					e.Player.SendErrorMessage("Invalid bank ID.");
				return;
			}

			if (!e.Player.Group.HasPermission("infchests.chest.bank.*"))
			{
				int maxBankIDs = 0;
				foreach (string permission in e.Player.Group.permissions)
				{
					Match Match = Regex.Match(permission, @"infchests\.chest\.bank\.(\d+)");
					if (Match.Success && Match.Value == permission)
					{
						maxBankIDs = Convert.ToInt32(Match.Groups[1].Value);
						break;
					}
				}

				if (bankID > maxBankIDs)
				{
					e.Player.SendErrorMessage("You have exceeded your maximum number of bank IDs.");
					return;
				}
			}

			Infos[e.Player.Index].Action = ChestAction.SetBank;
			Infos[e.Player.Index].BankID = bankID;
			e.Player.SendInfoMessage("Open a chest to set its bank ID to {0}.", bankID);
		}
		void ConvertChests(CommandArgs e)
		{
			Task.Factory.StartNew(() => 
			{
				int converted = 0;
				var items = new StringBuilder();
				for (int i = 0; i < 1000; i++)
				{
					Terraria.Chest c = Main.chest[i];
					if (c != null)
					{
						for (int j = 0; j < 40; j++)
							items.Append(c.item[j].netID).Append(",").Append(c.item[j].stack).Append(",").Append(c.item[j].prefix).Append(",");
						Database.Query("INSERT INTO Chests (X, Y, Account, Items, WorldID) VALUES (@0, @1, '', @2, @3)",
							c.x, c.y, items.ToString(0, items.Length - 1), Main.worldID);
						converted++;
						items.Clear();
						Main.chest[i] = null;
					}
				}

				e.Player.SendSuccessMessage("Converted {0} chest{1}.", converted, converted == 1 ? "" : "s");
				if (converted > 0)
					WorldFile.saveWorld();
			}).LogExceptions();
		}
		void Deselect(CommandArgs e)
		{
			var info = Infos[e.Player.Index];
			info.Action = ChestAction.None;
			info.BankID = 0;
			info.Password = null;
			e.Player.SendInfoMessage("Stopped selecting a chest.");
		}
		void Info(CommandArgs e)
		{
			Infos[e.Player.Index].Action = ChestAction.GetInfo;
			e.Player.SendInfoMessage("Open a chest to get its info.");
		}
		void Lock(CommandArgs e)
		{
			if (e.Parameters.Count != 1)
			{
				e.Player.SendErrorMessage("Invalid syntax! Proper syntax: /clock <password>");
				return;
			}

			Infos[e.Player.Index].Action = ChestAction.SetPassword;
			Infos[e.Player.Index].Password = e.Parameters[0];
			if (e.Parameters[0].ToLower() == "remove")
				e.Player.SendInfoMessage("Open a chest to disable a password on it.");
			else
				e.Player.SendInfoMessage("Open a chest to set its password to '{0}'.", e.Parameters[0]);
		}
		void Protect(CommandArgs e)
		{
			Infos[e.Player.Index].Action = ChestAction.Protect;
			e.Player.SendInfoMessage("Open a chest to protect it.");
		}
		void Prune(CommandArgs e)
		{
			Task.Factory.StartNew(() =>
				{
					int corrupted = 0;
					int empty = 0;
					var pruneID = new List<int>();
					for (int i = 0; i < Main.maxTilesX; i++)
					{
						for (int j = 0; j < Main.maxTilesY; j++)
						{
							if (Main.tile[i, j].type == TileID.Containers)
							{
								int x = i;
								int y = j;
								if (Main.tile[x, y].frameY % 36 != 0)
									y--;
								if (Main.tile[x, y].frameX % 36 != 0)
									x--;

								using (var reader = Database.QueryReader("SELECT ID, Items FROM Chests WHERE X = @0 AND Y = @1 AND WorldID = @2", x, y, Main.worldID))
								{
									if (reader.Read())
									{
										if (reader.Get<string>("Items") ==
											"0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0," +
											"0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0")
										{
											empty++;
											WorldGen.KillTile(x, y);
											TSPlayer.All.SendTileSquare(x, y, 3);
											pruneID.Add(reader.Get<int>("ID"));
										}
									}
									else
									{
										corrupted++;
										WorldGen.KillTile(x, y);
										TSPlayer.All.SendTileSquare(x, y, 3);
									}
								}
							}
						}
					}

					e.Player.SendSuccessMessage("Pruned {0} empty chest{1}.", empty, empty == 1 ? "" : "s");

					using (var reader = Database.QueryReader("SELECT ID, X, Y FROM Chests WHERE WorldID = @0", Main.worldID))
					{
						while (reader.Read())
						{
							int x = reader.Get<int>("X");
							int y = reader.Get<int>("Y");
							if (Main.tile[x, y].type != TileID.Containers)
							{
								corrupted++;
								WorldGen.KillTile(x, y);
								TSPlayer.All.SendTileSquare(x, y, 3);
								pruneID.Add(reader.Get<int>("ID"));
							}
						}
					}

					for (int i = 0; i < pruneID.Count; i++)
						Database.Query("DELETE FROM Chests WHERE ID = @0", pruneID[i]);

					e.Player.SendSuccessMessage("Pruned {0} corrupted chest{1}.", corrupted, corrupted == 1 ? "" : "s");
					if (corrupted + empty > 0)
						WorldFile.saveWorld();
				}).LogExceptions();
		}
		void Refill(CommandArgs e)
		{
			if (e.Parameters.Count > 1)
			{
				e.Player.SendErrorMessage("Invalid syntax! Proper syntax: /crefill [interval]");
				return;
			}
			Infos[e.Player.Index].RefillTime = 0;
			if (e.Parameters.Count == 1)
			{
				int time;
				if (int.TryParse(e.Parameters[0], out time) && time > 0)
				{
					Infos[e.Player.Index].Action = ChestAction.SetRefill;
					Infos[e.Player.Index].RefillTime = time;
					e.Player.SendInfoMessage("Open a chest to make it refill with an interval of {0} second{1}.", time, time == 1 ? "" : "s");
					return;
				}
				e.Player.SendErrorMessage("Invalid interval!");
			}
			else
			{
				Infos[e.Player.Index].Action = ChestAction.SetRefill;
				e.Player.SendInfoMessage("Open a chest to toggle its refill status.");
			}
		}
		void PublicProtect(CommandArgs e)
		{
			Infos[e.Player.Index].Action = ChestAction.TogglePublic;
			e.Player.SendInfoMessage("Open a chest to toggle its public status.");
		}
		void RegionProtect(CommandArgs e)
		{
			Infos[e.Player.Index].Action = ChestAction.ToggleRegion;
			e.Player.SendInfoMessage("Open a chest to toggle its region share status.");
		}
		void ReverseConvertChests(CommandArgs e)
		{
			Task.Factory.StartNew(() =>
			{
				using (var reader = Database.QueryReader("SELECT COUNT(*) AS Count FROM Chests"))
				{
					reader.Read();
					if (reader.Get<int>("Count") > 1000)
					{
						e.Player.SendErrorMessage("The chests cannot be reverse-converted without losing data.");
						return;
					}
				}

				int i = 0;
				using (var reader = Database.QueryReader("SELECT Items, X, Y FROM Chests WHERE WorldID = @0", Main.worldID))
				{
					while (reader.Read())
					{
						var chest = (Main.chest[i++] = new Terraria.Chest());

						string items = reader.Get<string>("Items");
						chest.name = "";
						chest.x = reader.Get<int>("X");
						chest.y = reader.Get<int>("Y");

						int[] itemArgs = new int[120];
						string[] split = items.Split(',');
						for (int j = 0; j < 40; j++)
						{
							int netID = Convert.ToInt32(split[3 * j]);
							int prefix = Convert.ToInt32(split[3 * j + 2]);
							int stack = Convert.ToInt32(split[3 * j + 1]);
							chest.item[j] = new Item();
							chest.item[j].netDefaults(netID);
							chest.item[j].Prefix(prefix);
							chest.item[j].stack = stack;
						}
					}
				}
				Database.Query("DELETE FROM Chests WHERE WorldID = @0", Main.worldID);
				e.Player.SendSuccessMessage("Reverse converted {0} chests.", i);
				if (i > 0)
					WorldFile.saveWorld();
			}).LogExceptions();
		}
		void Unlock(CommandArgs e)
		{
			if (e.Parameters.Count != 1)
			{
				e.Player.SendErrorMessage("Invalid syntax! Proper syntax: /cunlock <password>");
				return;
			}
			Infos[e.Player.Index].Password = e.Parameters[0];
			e.Player.SendInfoMessage("Open a chest to unlock it.");
		}
		void Unprotect(CommandArgs e)
		{
			Infos[e.Player.Index].Action = ChestAction.Unprotect;
			e.Player.SendInfoMessage("Open a chest to unprotect it.");
		}
	}

	public static class TaskExt
	{
		public static Task LogExceptions(this Task task)
		{
			task.ContinueWith(t => { }, TaskContinuationOptions.OnlyOnFaulted);
			return task;
		}
	}
}

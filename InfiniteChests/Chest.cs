using System;

namespace InfiniteChests
{
	public class Chest
	{
		public string Account = "";
		public int BankID;
		public ChestFlags Flags;
		public string HashedPassword = "";
		public int ID;
		public string Items;
		public Point Location;
		public int RefillTime;

		public bool IsBank
		{
			get { return Flags.HasFlag(ChestFlags.Bank); }
		}
		public bool IsPublic
		{
			get { return Flags.HasFlag(ChestFlags.Public); }
		}
		public bool IsRefill
		{
			get { return Flags.HasFlag(ChestFlags.Refill); }
		}
		public bool IsRegion
		{
			get { return Flags.HasFlag(ChestFlags.Region); }
		}
	}
}
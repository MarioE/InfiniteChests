using System;

namespace InfiniteChests
{
	public class Chest
	{
		public string Account = "";
		public string BankName;
		public ChestFlags Flags;
		public string HashedPassword = "";
		public string Items;
		public Point Location;
		public int RefillTime;

		public bool IsBank
		{
			get { return !String.IsNullOrEmpty(BankName); }
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
using System;

namespace InfiniteChests
{
	public class Chest
	{
		public string account = "";
		public ChestFlags flags;
		public string items;
		public Point loc;
		public string password = "";
	}

	[Flags]
	public enum ChestFlags
	{
		PUBLIC = 1,
		REGION = 2,
		REFILL = 4
	}

	public enum ChestAction : byte
	{
		NONE,
		PROTECT,
		UNPROTECT,
		REFILL,
		REGION,
		PUBLIC,
		INFO,
		SETPASS
	}

	public class PlayerInfo
	{
		public ChestAction action;
		public string password = "";
		public int time;
		public int x;
		public int y;
	}
}
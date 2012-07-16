using System;

namespace InfiniteChests
{
    public class Chest
    {
        public string account = "";
        public ChestFlags flags;
        public string items;
        public Point loc;
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
        INFO
    }

    public struct PlayerInfo
    {
        public ChestAction action;
        public int time;
        public int x;
        public int y;
    }
}

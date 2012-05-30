using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace InfiniteChests
{
    public class Chest
    {
        public string account;
        public ChestFlags flags;
        public string items;
        public Vector2 loc;
    }

    public enum ChestFlags : byte
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
        public Point loc;
        public int time;
    }
}

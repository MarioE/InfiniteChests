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
        REFILL = 1,
        REGION = 2
    }

    public enum ChestAction : byte
    {
        NONE,
        PROTECT,
        UNPROTECT,
        REFILL,
        REGION,
        INFO
    }

    public struct PlayerInfo
    {
        public ChestAction action;
        public Point loc;
        public int time;
    }
}

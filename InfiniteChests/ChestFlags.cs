using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace InfiniteChests
{
	[Flags]
	public enum ChestFlags
	{
		Public = 1,
		Region = 2,
		Refill = 4
	}
}

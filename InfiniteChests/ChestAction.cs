using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace InfiniteChests
{
	public enum ChestAction : byte
	{
		None,
		GetInfo,
		Protect,
		SetPassword,
		SetRefill,
		SetBank,
		ToggleRegion,
		TogglePublic,
		Unprotect
	}
}

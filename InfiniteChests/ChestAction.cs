namespace InfiniteChests
{
    /// <summary>
    ///     Describes a pending chest action.
    /// </summary>
    public enum ChestAction
    {
        None = 0,
        GetInfo = 1,
        TogglePublic = 2,
        ToggleMultiuse = 3,
        SetRefill = 4,
        AllowUser = 5,
        DisallowUser = 6,
        AllowGroup = 7,
        DisallowGroup = 8,
        Claim = 9,
        Unclaim = 10
    }
}

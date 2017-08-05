namespace InfiniteChests
{
    /// <summary>
    ///     Describes a pending chest action.
    /// </summary>
    public enum ChestAction
    {
        /// <summary>
        ///     Specifies no action.
        /// </summary>
        None = 0,

        /// <summary>
        ///     Specifies that the player is retrieving information.
        /// </summary>
        GetInfo = 1,

        /// <summary>
        ///     Specifies that the player is toggling whether the chest is public.
        /// </summary>
        TogglePublic = 2,

        /// <summary>
        ///     Specifies that the player is toggling whether the chest allows multiuse.
        /// </summary>
        ToggleMultiuse = 3,

        /// <summary>
        ///     Specifies that the player is setting the refill time.
        /// </summary>
        SetRefill = 4,

        /// <summary>
        ///     Specifies that the player is allowing a user.
        /// </summary>
        AllowUser = 5,

        /// <summary>
        ///     Specifies that the player is disallowing a user.
        /// </summary>
        DisallowUser = 6,

        /// <summary>
        ///     Specifies that the player is allowing a group.
        /// </summary>
        AllowGroup = 7,

        /// <summary>
        ///     Specifies that the player is disallowing a group.
        /// </summary>
        DisallowGroup = 8,

        /// <summary>
        ///     Specifies that the player is claiming the chest.
        /// </summary>
        Claim = 9,

        /// <summary>
        ///     Specifies that the player is unclaiming the chest.
        /// </summary>
        Unclaim = 10
    }
}

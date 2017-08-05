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
        ///     Specifies that the player is toggling user access.
        /// </summary>
        ToggleUser = 5,
        
        /// <summary>
        ///     Specifies that the player is toggling group access.
        /// </summary>
        ToggleGroup = 6,
        
        /// <summary>
        ///     Specifies that the player is claiming the chest.
        /// </summary>
        Claim = 7,

        /// <summary>
        ///     Specifies that the player is unclaiming the chest.
        /// </summary>
        Unclaim = 8
    }
}

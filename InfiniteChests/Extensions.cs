using System.Diagnostics;
using TShockAPI;

namespace InfiniteChests
{
    /// <summary>
    ///     Provides extensions.
    /// </summary>
    public static class Extensions
    {
        private const string SessionKey = "InfiniteChests_Session";

        /// <summary>
        ///     Gets the session associated with the specified player.
        /// </summary>
        /// <param name="player">The player, which must not be <c>null</c>.</param>
        /// <returns>The session.</returns>
        public static Session GetSession(this TSPlayer player)
        {
            Debug.Assert(player != null, "Player must not be null.");

            var session = player.GetData<Session>(SessionKey);
            if (session == null)
            {
                session = new Session();
                player.SetData(SessionKey, session);
            }
            return session;
        }
    }
}

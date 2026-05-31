using System.Collections.Generic;

namespace LobbyKit.Features.Anticheat
{
    internal static class KickAnnouncer
    {
        private static readonly Dictionary<int, string> _pendingReasons = [];

        internal static void Enqueue(int clientId, string reason)
        {
            _pendingReasons[clientId] = reason;
        }

        internal static bool TryConsume(int clientId, out string reason)
        {
            if (_pendingReasons.TryGetValue(clientId, out reason))
            {
                _pendingReasons.Remove(clientId);
                return true;
            }
            return false;
        }
    }
}

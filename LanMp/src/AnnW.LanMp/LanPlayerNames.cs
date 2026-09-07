using AnnW.LanMp.Protocol;

namespace AnnW.LanMp
{
    /// <summary>
    /// Resolve lobby usernames for LAN seats (replaces vanilla localized 「人类」).
    /// Prefers draft.occupantName, then live Net peer display name.
    /// </summary>
    internal static class LanPlayerNames
    {
        /// <summary>Human seat display name, or null to keep vanilla (AI / unknown).</summary>
        public static string TryResolveSeatDisplayName(int seatIndex, string ownerPeerId = null)
        {
            var plugin = LanMpPlugin.Instance;
            if (plugin == null)
                return null;

            var draft = plugin.Lobby?.Draft;
            if (draft?.seats != null && seatIndex >= 0 && seatIndex < draft.seats.Length)
            {
                var seat = draft.seats[seatIndex];
                if (seat != null)
                {
                    if (!string.IsNullOrWhiteSpace(seat.occupantName))
                        return seat.occupantName.Trim();

                    if (!string.IsNullOrEmpty(seat.peerId))
                    {
                        var fromPeer = NameForPeer(plugin, seat.peerId, draft);
                        if (!string.IsNullOrEmpty(fromPeer))
                            return fromPeer;
                    }
                }
            }

            if (!string.IsNullOrEmpty(ownerPeerId))
            {
                var fromOwner = NameForPeer(plugin, ownerPeerId, draft);
                if (!string.IsNullOrEmpty(fromOwner))
                    return fromOwner;
            }

            try
            {
                var peer = plugin.Authority?.GetOwnerPeerIdForSeat(seatIndex);
                if (!string.IsNullOrEmpty(peer))
                {
                    var fromAuth = NameForPeer(plugin, peer, draft);
                    if (!string.IsNullOrEmpty(fromAuth))
                        return fromAuth;
                }
            }
            catch { /* ignore */ }

            return null;
        }

        public static string ResolveSeatLabel(int seatIndex, string ownerPeerId = null, string cachedDisplayName = null)
        {
            if (!string.IsNullOrWhiteSpace(cachedDisplayName))
                return cachedDisplayName.Trim();

            var name = TryResolveSeatDisplayName(seatIndex, ownerPeerId);
            if (!string.IsNullOrEmpty(name))
                return name;

            if (!string.IsNullOrEmpty(ownerPeerId) && ownerPeerId.Length > 6)
                return "席" + seatIndex + " @" + ownerPeerId.Substring(0, 6);
            if (!string.IsNullOrEmpty(ownerPeerId))
                return "席" + seatIndex + " @" + ownerPeerId;
            return "席" + seatIndex;
        }

        /// <summary>Stamp human usernames onto MatchEnd rows while draft/Net still available.</summary>
        public static void StampResultDisplayNames(SeatMatchResultDto[] results)
        {
            if (results == null)
                return;
            for (var i = 0; i < results.Length; i++)
            {
                var r = results[i];
                if (r == null)
                    continue;
                if (!string.IsNullOrWhiteSpace(r.displayName))
                    continue;
                var n = TryResolveSeatDisplayName(r.playerIndex, r.ownerPeerId);
                if (!string.IsNullOrEmpty(n))
                    r.displayName = n;
            }
        }

        /// <summary>Fill empty human occupantName from Net before LobbyStart broadcast.</summary>
        public static void EnsureDraftOccupantNames(LobbyDraftDto draft, NetSession net)
        {
            if (draft?.seats == null || net == null)
                return;
            for (var i = 0; i < draft.seats.Length; i++)
            {
                var s = draft.seats[i];
                if (s == null || LobbySeatLogic.GetState(s) != LobbySeatState.HumanSeated)
                    continue;

                // Always refresh local seat to current LocalDisplayName.
                if (!string.IsNullOrEmpty(s.peerId) && s.peerId == net.LocalPeerId &&
                    !string.IsNullOrWhiteSpace(net.LocalDisplayName))
                {
                    s.occupantName = net.LocalDisplayName.Trim();
                    if (s.peerId == draft.hostPeerId)
                        draft.hostDisplayName = s.occupantName;
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(s.occupantName))
                    continue;

                var n = net.GetPeerDisplayName(s.peerId);
                if (string.IsNullOrWhiteSpace(n) && !string.IsNullOrEmpty(s.peerId) &&
                    s.peerId == net.LocalPeerId)
                    n = net.LocalDisplayName;
                if (!string.IsNullOrWhiteSpace(n))
                    s.occupantName = n.Trim();
            }
            LobbySeatLogic.RefreshLegacyGuestFields(draft);
        }

        private static string NameForPeer(LanMpPlugin plugin, string peerId, LobbyDraftDto draft)
        {
            if (string.IsNullOrEmpty(peerId))
                return null;

            try
            {
                var live = plugin.Net?.GetPeerDisplayName(peerId);
                if (!string.IsNullOrWhiteSpace(live))
                    return live.Trim();
            }
            catch { /* ignore */ }

            if (plugin.Net != null && peerId == plugin.Net.LocalPeerId &&
                !string.IsNullOrWhiteSpace(plugin.Net.LocalDisplayName))
                return plugin.Net.LocalDisplayName.Trim();

            if (draft != null)
            {
                if (peerId == draft.hostPeerId && !string.IsNullOrWhiteSpace(draft.hostDisplayName))
                    return draft.hostDisplayName.Trim();
                if (peerId == draft.guestPeerId && !string.IsNullOrWhiteSpace(draft.guestDisplayName))
                    return draft.guestDisplayName.Trim();
            }

            if (!string.IsNullOrWhiteSpace(plugin.DisplayName?.Value) &&
                plugin.Net != null && peerId == plugin.Net.LocalPeerId)
                return plugin.DisplayName.Value.Trim();

            return null;
        }
    }
}

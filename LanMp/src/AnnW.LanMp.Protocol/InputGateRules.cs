namespace AnnW.LanMp.Protocol
{
    /// <summary>Pure gate rules (M03) — no Unity / no network.</summary>
    public static class InputGateRules
    {
        public static bool IsLocalPlayersTurn(
            bool inLanBattle,
            bool gatesArmed,
            int currentPlayerIndex,
            int localHumanSlotIndex)
        {
            if (!inLanBattle || !gatesArmed)
                return true;
            return currentPlayerIndex == localHumanSlotIndex;
        }

        public static bool ShouldBlockLocalInput(
            bool inLanBattle,
            bool gatesArmed,
            bool applyingRemoteCommand,
            bool isLocalPlayersTurn)
        {
            if (!inLanBattle || !gatesArmed)
                return false;
            // Remote apply during a foreign seat must stay spectating (banner + idle hide).
            if (!isLocalPlayersTurn)
                return true;
            return false;
        }

        /// <summary>
        /// Block selecting/acting on a unit that is not owned by the local human,
        /// even on the local player's turn (cannot control ally/enemy units).
        /// </summary>
        public static bool ShouldBlockUnitControl(
            bool inLanBattle,
            bool gatesArmed,
            bool applyingRemoteCommand,
            bool isLocalPlayersTurn,
            int unitOwnerPlayerIndex,
            int localHumanSlotIndex)
        {
            if (!inLanBattle || !gatesArmed)
                return false;
            if (!isLocalPlayersTurn)
                return true;
            if (unitOwnerPlayerIndex < 0)
                return true;
            return unitOwnerPlayerIndex != localHumanSlotIndex;
        }

        public static bool MayAuthorizeStart(bool isHost, bool canStart, bool gatesArmed)
        {
            return isHost && canStart && gatesArmed;
        }

        public static string BlockReasonNotYourTurn => "非你的回合（观战中）";
        public static string BlockReasonNotYourUnit => "只能操作自己的单位";
        public static string WaitingHostConfirm => "等待主机确认…";
    }
}

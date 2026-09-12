namespace AnnW.LanMp.Protocol
{
    /// <summary>
    /// Pure gate rules (M03) — no Unity / no network.
    /// Dual-state table (P-Learn PL1) mirrors XingyiStarry <c>InputGate</c> semantics without
    /// adopting their full-command replay model — see <see cref="ShouldRunOriginal"/> /
    /// <see cref="MaySubmit"/>.
    /// </summary>
    public static class InputGateRules
    {
        /// <summary>
        /// Xingyi <c>InputGate.ShouldRunOriginal</c> analogue: when multiplayer is active,
        /// the vanilla game body runs only during authoritative execution (Host Accept /
        /// remote Command Apply). Otherwise Prefixes capture Intent / block UX.
        /// </summary>
        public static bool ShouldRunOriginal(bool multiplayerActive, bool authoritativeExecution)
        {
            if (!multiplayerActive)
                return true;
            return authoritativeExecution;
        }

        /// <summary>
        /// Fallthrough for Intent-capture Prefixes whose Accept body is driven by
        /// <c>ApplyingRemoteCommand</c> / apply enumerator / skill cast — <em>not</em>
        /// Suppress-only (that flag may belong to a different Accept command).
        /// Use for Undo / CastSkill UX. MannualEndTurn Accept uses Suppress-only entry —
        /// check Suppress||Applying directly. Surrender/RestartLevel: never fall through.
        /// </summary>
        public static bool AllowApplyDrivenVanillaBody(
            bool multiplayerActive,
            bool applyingRemoteCommand,
            bool inApplyEnumerator,
            bool skillCastInProgress)
        {
            if (!multiplayerActive)
                return true;
            return applyingRemoteCommand || inApplyEnumerator || skillCastInProgress;
        }

        /// <summary>
        /// Player UX while Accept holds Suppress for a <em>different</em> command must block
        /// (toast), not mutate under a silenced Bus. Skill-cast Accept keeps this false so
        /// SetUX/proc run while Host spectates the remote seat.
        /// </summary>
        public static bool ShouldBlockUxForAuthoritative(
            bool multiplayerActive,
            bool suppressNetworkEmit,
            bool applyingRemoteCommand,
            bool skillCastInProgress)
        {
            if (!multiplayerActive)
                return false;
            if (skillCastInProgress)
                return false;
            return suppressNetworkEmit || applyingRemoteCommand;
        }

        /// <summary>
        /// Xingyi <c>InputGate.MaySubmit</c> analogue: local seat may emit Intent (Guest) or
        /// drive Host-local apply entry only when it is their act window and we are not
        /// already inside authoritative execution.
        /// </summary>
        public static bool MaySubmit(
            bool multiplayerActive,
            bool localSeatMayAct,
            bool authoritativeExecution)
        {
            if (!multiplayerActive || !localSeatMayAct)
                return false;
            return !authoritativeExecution;
        }

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

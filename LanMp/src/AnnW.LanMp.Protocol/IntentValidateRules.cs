namespace AnnW.LanMp.Protocol
{
    /// <summary>Pure intent validation (M04) — no Unity.</summary>
    public static class IntentValidateRules
    {
        public static bool TryValidateBasics(
            bool inLanBattle,
            bool gatesArmed,
            string expectedBattleId,
            IntentDto intent,
            int currentTurn,
            int currentPlayerIndex,
            out string error)
        {
            error = null;
            if (intent == null)
            {
                error = "null-intent";
                return false;
            }
            if (!inLanBattle || !gatesArmed)
            {
                error = "gates-inactive";
                return false;
            }
            if (string.IsNullOrEmpty(intent.kind))
            {
                error = "missing-kind";
                return false;
            }
            if (!string.IsNullOrEmpty(expectedBattleId) &&
                !string.IsNullOrEmpty(intent.battleId) &&
                intent.battleId != expectedBattleId)
            {
                error = "battle-mismatch";
                return false;
            }
            if (intent.turn >= 0 && currentTurn >= 0 && intent.turn != currentTurn)
            {
                error = "turn-mismatch";
                return false;
            }
            if (intent.playerIndex >= 0 && currentPlayerIndex >= 0 &&
                intent.playerIndex != currentPlayerIndex)
            {
                error = "not-current-player";
                return false;
            }
            return true;
        }

        public static bool TryValidateUnitOwner(
            string kind,
            int unitOwnerPlayerIndex,
            int actingPlayerIndex,
            out string error)
        {
            error = null;
            if (kind != "DoAction" && kind != "UnitMoved" && kind != "Undo")
                return true;
            if (unitOwnerPlayerIndex < 0)
            {
                error = "unit-missing";
                return false;
            }
            if (unitOwnerPlayerIndex != actingPlayerIndex)
            {
                error = "unit-not-owned";
                return false;
            }
            return true;
        }

        /// <summary>Mirror Host spent-unit checks — block duplicate Guest Intent before send.</summary>
        public static bool IsUnitSpentForIntent(string kind, bool moved, bool actioned)
        {
            if (kind == "UnitMoved")
                return moved;
            if (kind == "DoAction")
                return actioned;
            return false;
        }

        /// <summary>Host must have an undo batch before Accepting Undo Intent.</summary>
        public static bool CanAcceptUndo(int hostUndoStackDepth) =>
            hostUndoStackDepth > 0;
    }
}

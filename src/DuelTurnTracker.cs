namespace DuelLinksAccess
{
    public sealed class DuelTurnTracker
    {
        public int CurrentPlayer { get; private set; } = -1;
        public int TurnNumber { get; private set; }

        public void ObservePhase(int player)
        {
            if (!IsPlayer(player) || CurrentPlayer >= 0) return;

            CurrentPlayer = player;
            TurnNumber = 1;
        }

        public void ObserveTurnChange(int player)
        {
            if (!IsPlayer(player)) return;

            CurrentPlayer = player;
            TurnNumber = TurnNumber == 0 ? 1 : TurnNumber + 1;
        }

        public void Reset()
        {
            CurrentPlayer = -1;
            TurnNumber = 0;
        }

        private static bool IsPlayer(int player)
        {
            return player == 0 || player == 1;
        }
    }
}

namespace DuelLinksAccess
{
    public sealed class TurnAnnouncementTracker
    {
        private int _lastTurn = -1;
        private int _lastPlayer = -1;

        public bool ShouldAnnounce(int turn, int player)
        {
            if (turn == _lastTurn && player == _lastPlayer) return false;

            _lastTurn = turn;
            _lastPlayer = player;
            return true;
        }

        public void Reset()
        {
            _lastTurn = -1;
            _lastPlayer = -1;
        }
    }
}

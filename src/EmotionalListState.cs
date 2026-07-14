namespace DuelLinksAccess
{
    public sealed class EmotionalListState
    {
        public bool IsActive { get; set; }
        public int Index { get; set; }
        public int Count { get; set; }
        public bool IsHandled { get; set; }
        public float HandledUntil { get; set; }

        /// <summary>
        /// True for view-style lists (selectMaxNum == 0 with an active
        /// Confirm button), e.g. "look at the top N cards of your deck".
        /// The user reviews the cards and confirms; nothing is selected.
        /// </summary>
        public bool ViewOnly { get; set; }

        public void MarkHandled(float now, float timeout)
        {
            IsHandled = true;
            HandledUntil = now + timeout;
        }

        public void Reset()
        {
            IsActive = false;
            Index = 0;
            Count = 0;
            IsHandled = false;
            HandledUntil = 0f;
            ViewOnly = false;
        }
    }
}

namespace DuelLinksAccess
{
    public sealed class EmotionalListState
    {
        public bool IsActive { get; set; }
        public int Index { get; set; }
        public int Count { get; set; }
        public bool IsHandled { get; set; }
        public float HandledUntil { get; set; }

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
        }
    }
}

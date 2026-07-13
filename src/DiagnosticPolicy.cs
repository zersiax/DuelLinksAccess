namespace DuelLinksAccess
{
    public static class DiagnosticPolicy
    {
        public static bool ShouldCollect(bool debugMode)
        {
            return debugMode;
        }
    }
}

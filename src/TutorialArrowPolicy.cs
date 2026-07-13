namespace DuelLinksAccess
{
    public enum TutorialArrowShape
    {
        // No specific target or handlers. Safe delayed cutscene advance.
        ClickToContinue,
        // A UI target or handler that requires user-driven activation.
        UISelectablePointer,
        // A 3D target rendered by a non-main camera.
        WorldColliderPointer,
    }

    public static class TutorialArrowPolicy
    {
        public static TutorialArrowShape SafeDefault
            => TutorialArrowShape.UISelectablePointer;

        public static TutorialArrowShape Classify(
            bool hasTarget,
            bool hasIpclick,
            bool worldCamera,
            bool hasUiGraphic)
        {
            if (!hasTarget)
            {
                return hasIpclick
                    ? TutorialArrowShape.UISelectablePointer
                    : TutorialArrowShape.ClickToContinue;
            }

            return worldCamera && !hasUiGraphic
                ? TutorialArrowShape.WorldColliderPointer
                : TutorialArrowShape.UISelectablePointer;
        }
    }
}

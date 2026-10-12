using System;

namespace DuelLinksAccess
{
    public enum GameScreen
    {
        Unknown,
        Title,
        Home,
        Duel,
        Deck,
        Shop,
        Dialog,
        CardDetail,
        Gate,
        Store,
        Notices,
        DuelTrials,
        Other,
    }

    public static class ScreenClassificationPolicy
    {
        public static GameScreen Classify(
            string name, bool fromDialogManager)
        {
            if (string.IsNullOrEmpty(name)) return GameScreen.Other;

            // Anything hosted by DialogManager is a modal dialog on top of the screen
            if (fromDialogManager)
            {
                if (name == "TutorialArrowPart")
                    return GameScreen.Dialog;
                return GameScreen.Dialog;
            }

            // Names indicating dialogs or prompts take precedence over screen keywords
            if (name.Contains("Dialog") || name.Contains("Confirm")
                || name.Contains("AgeVerification") || name.Contains("Tutorial"))
            {
                if (name == "TutorialArrowPart")
                    return GameScreen.Other;
                return GameScreen.Dialog;
            }

            return ClassifyName(name);
        }

        private static GameScreen ClassifyName(string name)
        {
            if (name.Contains("Home") || name == "Single")
                return GameScreen.Home;
            if (name.Contains("Title"))
                return GameScreen.Title;
            if (name == "DuelClient")
                return GameScreen.Duel;
            if (name.Contains("CardDetail"))
                return GameScreen.CardDetail;
            if (name.Contains("Shop"))
                return GameScreen.Shop;
            if (name.Contains("Gate"))
                return GameScreen.Gate;
            if (name == "Store")
                return GameScreen.Store;
            if (name == "HtjsonPage" || name == "Standby")
                return GameScreen.Notices;
            if (name.Contains("School") || name.Contains("DuelQuest"))
                return GameScreen.DuelTrials;
            if (name.Contains("Deck"))
                return GameScreen.Deck;
            return GameScreen.Other;
        }
    }
}

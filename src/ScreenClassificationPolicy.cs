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

            GameScreen screen = ClassifyName(name);
            if (screen == GameScreen.Other && fromDialogManager)
                return GameScreen.Dialog;
            if (screen == GameScreen.Dialog
                && name == "TutorialArrowPart" && !fromDialogManager)
            {
                return GameScreen.Other;
            }
            return screen;
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
            if (name.Contains("Deck"))
                return GameScreen.Deck;
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
            if (name.Contains("Dialog") || name.Contains("Confirm")
                || name.Contains("AgeVerification") || name.Contains("Tutorial"))
            {
                return GameScreen.Dialog;
            }
            return GameScreen.Other;
        }
    }
}

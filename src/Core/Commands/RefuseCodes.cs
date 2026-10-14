namespace SolarExpanseMultiplayer
{
    // Why the host (or the client's own send budget) refused an action. A refusal carries the code only; the player
    // reads CommandText.Refused(code) on their own machine, never text from the other side.
    public static class RefuseCodes
    {
        public const int NotInGame = 1, NotYourAgency = 2, AgencyAway = 3, TooMany = 4, Superseded = 5, Busy = 6,
            TooLarge = 7,
            CannotSend = 8,   // local only: the capture built a request Validate refuses
            BadTarget = 10, TargetChanged = 11, CannotAfford = 12, Locked = 13, AlreadyDone = 14, NotAllowedHere = 15,
            TooManyUnits = 16, InUse = 17, NotInMultiplayer = 18, GameOver = 19, AgencyStopped = 20,
            // M6 flights
            TooCloseToLaunch = 21, AlreadyLaunched = 22, CraftBusy = 23,
            PlanFailed = 24,   // "That flight plan isn't valid": a FlightSanity failure
            TargetGone = 25, PresetsFull = 26, TargetFull = 27,
            // M7 shared world
            PriceChanged = 30, NoSuchOffer = 31,   // NoSuchOffer: gone, or the key matches none or several
            OfferNotYours = 32, ContractState = 33,   // not available / not active / objective done
            NotABomb = 34, NotOnHome = 35,   // NotOnHome: the make-offer window's instant button only (source 1)
            FlightsDueToday = 36, MarketClosed = 37,   // MarketClosed: the resource is not in the market list
            OwnOffer = 38, OffersFull = 39,
            OfferSmaller = 40;   // a take asked for more than the offer has left (the dialog's count was stale)

        public static bool IsKnown(int c) { return (c >= NotInGame && c <= CannotSend) || (c >= BadTarget
            && c <= TargetFull) || (c >= PriceChanged && c <= OfferSmaller); }

        // For logs: the line "command: player 2 req 7 Build refused CannotAfford" uses it.
        public static string Name(int c)
        {
            switch (c)
            {
                case NotInGame: return "NotInGame";
                case NotYourAgency: return "NotYourAgency";
                case AgencyAway: return "AgencyAway";
                case TooMany: return "TooMany";
                case Superseded: return "Superseded";
                case Busy: return "Busy";
                case TooLarge: return "TooLarge";
                case CannotSend: return "CannotSend";
                case BadTarget: return "BadTarget";
                case TargetChanged: return "TargetChanged";
                case CannotAfford: return "CannotAfford";
                case Locked: return "Locked";
                case AlreadyDone: return "AlreadyDone";
                case NotAllowedHere: return "NotAllowedHere";
                case TooManyUnits: return "TooManyUnits";
                case InUse: return "InUse";
                case NotInMultiplayer: return "NotInMultiplayer";
                case GameOver: return "GameOver";
                case AgencyStopped: return "AgencyStopped";
                case TooCloseToLaunch: return "TooCloseToLaunch";
                case AlreadyLaunched: return "AlreadyLaunched";
                case CraftBusy: return "CraftBusy";
                case PlanFailed: return "PlanFailed";
                case TargetGone: return "TargetGone";
                case PresetsFull: return "PresetsFull";
                case TargetFull: return "TargetFull";
                case PriceChanged: return "PriceChanged";
                case NoSuchOffer: return "NoSuchOffer";
                case OfferNotYours: return "OfferNotYours";
                case ContractState: return "ContractState";
                case NotABomb: return "NotABomb";
                case NotOnHome: return "NotOnHome";
                case FlightsDueToday: return "FlightsDueToday";
                case MarketClosed: return "MarketClosed";
                case OwnOffer: return "OwnOffer";
                case OffersFull: return "OffersFull";
                case OfferSmaller: return "OfferSmaller";
                default: return "Code" + c;
            }
        }
    }
}

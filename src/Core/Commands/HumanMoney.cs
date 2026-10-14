using System;

namespace SolarExpanseMultiplayer
{
    // DD8: the money a human agency plays with. The host owns it: the agency's current money when that is a real
    // amount (a seat is claimed once per session, so a finite value is never replaced), otherwise its opening money,
    // the start data's money for it times the difficulty's start-money multiplier. One place, so M8's lobby option (a
    // host-chosen amount) replaces Opening and nothing else.
    public static class HumanMoney
    {
        // A real amount: finite, and within what a message may carry (money goes negative).
        public static bool IsReal(double money)
        {
            return !double.IsNaN(money) && !double.IsInfinity(money) && Math.Abs(money) <= ActionLimits.MaxMoney;
        }

        // Throws when the game cannot give a real amount: a made-up opening would be a split between the games that
        // nobody could see, so the caller's fault path (a new stream) is the honest answer.
        public static double Opening(double startMoney, double multiplier)
        {
            if (!IsReal(startMoney) || double.IsNaN(multiplier) || double.IsInfinity(multiplier) || multiplier < 0)
                throw new InvalidOperationException("no opening money: start " + startMoney + " x " + multiplier);
            double money = startMoney * multiplier;
            if (!IsReal(money)) throw new InvalidOperationException("the opening money " + money + " is not real");
            return money;
        }

        // SeatClaimed's amount and AgencyState's money. The opening is read only when it is needed (a game that has
        // a real amount never asks for it). opened: the opening was used.
        public static double For(double current, Func<double> opening, out bool opened)
        {
            if (opening == null) throw new ArgumentNullException("opening");
            opened = !IsReal(current);
            return opened ? opening() : current;
        }
    }
}

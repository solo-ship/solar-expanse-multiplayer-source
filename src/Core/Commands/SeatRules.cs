namespace SolarExpanseMultiplayer
{
    // What a released seat's agency goes back to, apart from the game types (DD5's SeatReleased, on the host).
    public static class SeatRules
    {
        // Its AI comes on exactly as the game's own setup would switch it on (GameManager.SetupPlayerAndRivalAIs,
        // D 81237-81248): a rival the start configuration enabled, when the configuration sets up AIs, or the World
        // Government; and never while Company.BlockEnableAI holds every AI off. An idle or disabled rival stays off.
        public static bool AiOnAfterRelease(bool setupAis, bool enabledRival, bool worldGovernment, bool blockEnableAi)
        {
            if (blockEnableAi) return false;
            return (setupAis && enabledRival) || worldGovernment;
        }
    }
}

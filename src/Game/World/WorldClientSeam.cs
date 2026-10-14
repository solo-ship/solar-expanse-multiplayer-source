using GameManager = Manager.GameManager;

namespace SolarExpanseMultiplayer
{
    // The client half of the world rules' seam (IWorldClientSeam, M7 Task 12): the year event's in-event apply on the
    // client driver, this game's day, out of step, and the local player's UI-only objectives sent to the host as an 88.
    // Set by the client glue at every rules start (CommandSession.RegisterWorld). Main thread.
    public sealed class WorldClientSeam : IWorldClientSeam
    {
        readonly CommandClientGlue glue;

        public WorldClientSeam(CommandClientGlue glue) { this.glue = glue; }

        public string ApplyInEventNow(int kind, long localDay) { return glue.Client.ApplyInEventNow(kind, localDay); }

        public GrowthBook Growth { get { return glue.Client.Growth; } }

        public string CloseInEvent(int kind) { return glue.Client.CloseInEvent(kind); }

        public long LocalDay
        {
            get { long day; return glue.LocalDay(out day) ? day : -1; }
        }

        public void OutOfStep(string why) { glue.Client.OutOfStepNow(why); }

        // The objective's tick on this player's own screen: the host completes it for every game (a 92 after the 88).
        public void ObjectiveUi(ContractRef objective)
        {
            GameManager m = MonoBehaviourSingleton<GameManager>.Instance;
            if (objective == null || m == null || m.Player == null) return;
            glue.Click(new ActionRequest { Kind = ActionKind.ObjectiveUi, Agency = GameAgencies.Wire(m.Player),
                Contract = new ContractRef { Contract = objective.Contract, Objective = objective.Objective } });
        }
    }
}

using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Contract = Game.ContractsObjectives.Contract;
using ContractManager = Manager.ContractManager;
using ContractRow = Game.UI.Windows.Elements.ContractsElement.ContractRow;

namespace SolarExpanseMultiplayer
{
    // Accepting and declining a contract (M7 DD-W7; kinds 86 and 87): the contract window's two buttons and the
    // contract list row's one button, which accepts an available contract and declines an active one (a finished one
    // only opens its info, as the game wrote it). The game's own call acts for the local player and is held in a
    // session, so nothing changes until the host's entry arrives; the host repeats the window's tests.
    // The class needs a [HarmonyPatch] of its own: HarmonyX (BepInEx) silently skips a patch class without one.
    [HarmonyPatch]
    public static class CaptureContracts
    {
        static readonly Dictionary<string, CaptureHandler> Handlers = new Dictionary<string, CaptureHandler>
        {
            { "ContractWindowInfo.OnClickAccept", Accept },
            { "ContractWindowInfo.OnClickDecline", Decline },
            { "ContractRow.OnButtonClick", RowButton }
        };

        [HarmonyTargetMethods]
        static IEnumerable<MethodBase> Targets()
        {
            yield return HookTargets.Method(typeof(ContractWindowInfo), "OnClickAccept");
            yield return HookTargets.Method(typeof(ContractWindowInfo), "OnClickDecline");
            yield return HookTargets.Method(typeof(ContractRow), "OnButtonClick");
        }

        [HarmonyPrefix]
        static bool Prefix(MethodBase __originalMethod, object __instance, object[] __args)
        {
            ICaptureSeam seam = WindowCapture.Seam();
            if (seam == null) return true;
            return CaptureRun.Run(Handlers, seam, __originalMethod, __instance, __args);
        }

        static ActionRequest Draft(int kind, Contract contract)
        {
            return new ActionRequest { Kind = kind, Agency = WindowCapture.Local(),
                Contract = new ContractRef { Contract = contract.ContractDefinition.ID, Objective = -1 } };
        }

        static bool Accept(ICaptureSeam seam, object self, object[] args)
        {
            var contract = HookTargets.Field<Contract>(self, "contract");
            if (contract == null || contract.ContractDefinition == null) { WindowCapture.CannotSend(seam); return false; }
            WindowCapture.Send(seam, Draft(ActionKind.ContractAccept, contract), null);
            return false;
        }

        static bool Decline(ICaptureSeam seam, object self, object[] args)
        {
            var contract = HookTargets.Field<Contract>(self, "contract");
            if (contract == null || contract.ContractDefinition == null) { WindowCapture.CannotSend(seam); return false; }
            WindowCapture.Send(seam, Draft(ActionKind.ContractCancel, contract), null);
            return false;
        }

        // The list row: available accepts, active declines, anything else is the game's own (it opens the info).
        static bool RowButton(ICaptureSeam seam, object self, object[] args)
        {
            var contract = HookTargets.Field<Contract>(self, "contract");
            if (contract == null || contract.ContractDefinition == null) return true;
            ContractManager.EContractState state = contract.ContractStateForPlayer;
            if (state != ContractManager.EContractState.Available && state != ContractManager.EContractState.Active)
                return true;
            int kind = state == ContractManager.EContractState.Available ? ActionKind.ContractAccept
                : ActionKind.ContractCancel;
            WindowCapture.Send(seam, Draft(kind, contract), null);
            return false;
        }
    }
}

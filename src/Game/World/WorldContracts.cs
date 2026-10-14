using System.Collections.Generic;
using Company = Game.Company;
using CompanyContractData = Game.ContractsObjectives.CompanyContractData;
using CompanyObjectiveData = Game.ContractsObjectives.CompanyObjectiveData;
using Contract = Game.ContractsObjectives.Contract;
using ContractManager = Manager.ContractManager;
using EObjectiveType = Data.ScriptableObject.EObjectiveType;
using Objective = Game.ContractsObjectives.Objective;

namespace SolarExpanseMultiplayer
{
    // Contracts as the shared world needs them (M7 DD-W7): finding a contract and an agency's data in it, which
    // objectives are trusted from a player's own screen, and what one completion set off, found by comparing the
    // agency's contracts before and after (the same comparison on every game, so the chains are comparable). Main thread.
    public static class WorldContracts
    {
        public static Contract Find(string id)
        {
            if (id == null) return null;
            List<Contract> all = ContractManager.Instance.allContracts;
            for (int i = 0; i < all.Count; i++)
                if (all[i] != null && all[i].ContractDefinition != null && all[i].ContractDefinition.ID == id) return all[i];
            return null;
        }

        public static CompanyContractData DataOf(Contract c, Company company)
        {
            CompanyContractData d;
            return c != null && company != null && c.PerCompanyContractData.TryGetValue(company, out d) ? d : null;
        }

        public static ContractManager.EContractState StateOf(Contract c, Company company)
        {
            return c.ContractStateForCompany(company);
        }

        // The objectives only the local player's own windows can reach (DD-W7): ticked on a player's screen, never
        // by a world event, so the host trusts them from the owner and for nothing else.
        public static bool UiOnly(Objective o)
        {
            if (o == null) return false;
            switch (o.objectiveType)
            {
                case EObjectiveType.SelectLayer: case EObjectiveType.CalculateFly: case EObjectiveType.AddModuleToCargo:
                case EObjectiveType.SelectObject: case EObjectiveType.SelectTarget: return true;
                default: return false;
            }
        }

        // A note is still to be raised: the objective is not complete (a chain may have completed it meanwhile), or the
        // contract is still Available for the agency.
        public static bool Due(WorldNote n)
        {
            Company company = GameAgencies.Find(n.Agency);
            if (company == null || company.BlockContract) return false;
            Contract c = WorldContracts.Find(n.Contract);
            if (n.Activation)
                return c == null ? Definition(n.Contract) : StateOf(c, company) == ContractManager.EContractState.Available;
            CompanyContractData d = DataOf(c, company);
            if (d == null || n.Objective < 0 || n.Objective >= d.ObjectivesDataList.Count) return false;
            CompanyObjectiveData od = d.ObjectivesDataList[n.Objective];
            return od != null && !od.IsCompleteUI;
        }

        static bool Definition(string id)
        {
            var all = SerializedMonoBehaviourSingleton<Manager.AllScriptableObjectManager>.Instance.AllContract;
            return all != null && all.GetByID(id) != null;
        }

        // The chain of one completion is the contract rule's (ContractRule.BeginChain/TakeChain): what the game did,
        // in its order, on every game alike.
    }
}

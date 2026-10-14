using System;
using System.Collections.Generic;
using System.Text;

namespace SolarExpanseMultiplayer
{
    // Rule E7 (docs\research\save-allowlist.md section 10.3): Odin matches a node's names against a map of the type's members
    // (FormatterUtilities.FindSerializableMembersMap) and adds every [FormerlySerializedAs] old name to the same map.
    // A save that holds a member's own name and one of its old names therefore has two keys for one member, and Odin keeps the last.
    // Odin never writes an old name, so a real save does not hold one; OdinScan refuses a key that is an alias of the node's type.
    //
    // The table is per type, never global (a name that is an alias on one type is a real member of another), and holds the aliases of
    // the whole base chain, because Odin builds the map over the type and its bases. Everything between the markers is GENERATED
    // from the game's Assembly-CSharp.dll for every type on the allowlist:  tools\save-types.ps1 -GenAliases  prints it, and
    //   tools\save-types.ps1 -CheckAliases  compares it with this file and fails when they differ (SaveAliasesTests runs it when the
    // vanilla test copy is present), so a game update cannot drift the table unseen. Both read the beta in the vanilla test copy, never the
    // main Steam install (its branch changes); GameAssemblyCSharpSha256 pins the Assembly-CSharp the table was made from.
    //
    // Entries: "Odin type name|alias|alias|...", sorted by type name.
    public static class SaveAliases
    {
        // BEGIN GENERATED (tools\save-types.ps1 -GenAliases)
        public const string GameAssemblyCSharpSha256 = "d7cc4b107d686b46be260b5066699314af3ba8b4ce3ad21b5ec0a4da0754ca0b";
        public static readonly string[] Entries = new string[]
        {
            "AsteroidPullingFacility, Assembly-CSharp|facilityType|groundFacilityDescriptor",
            "AsteroidPullingModule, Assembly-CSharp|facilityType|groundFacilityDescriptor",
            "CameraControl.MyCameraController+StartCameraPosition, Assembly-CSharp|currentZoom",
            "CreateNbodyFromJSON+AddObjectInfoData, Assembly-CSharp|planetComposition",
            "Data.SystemDiscoveryData, Assembly-CSharp|solarSystemData",
            "EnergyStorage, Assembly-CSharp|facilityType|groundFacilityDescriptor",
            "EnergyStorageModule, Assembly-CSharp|facilityType|groundFacilityDescriptor",
            "Game.ObjectInfoDataScripts.CustomFacilitiesAndModules.CommSatFacility, Assembly-CSharp|facilityType|groundFacilityDescriptor",
            "Game.ObjectInfoDataScripts.CustomFacilitiesAndModules.ConstructionEquipmentModule, Assembly-CSharp|facilityType|groundFacilityDescriptor",
            "Game.ObjectInfoDataScripts.CustomFacilitiesAndModules.EnergyProductionFacility, Assembly-CSharp|facilityType|groundFacilityDescriptor",
            "Game.ObjectInfoDataScripts.CustomFacilitiesAndModules.EnergyProductionModule, Assembly-CSharp|facilityType|groundFacilityDescriptor",
            "Game.ObjectInfoDataScripts.CustomFacilitiesAndModules.EnergyReceiverFacility, Assembly-CSharp|facilityType|groundFacilityDescriptor",
            "Game.ObjectInfoDataScripts.CustomFacilitiesAndModules.EnergyReceiverModule, Assembly-CSharp|facilityType|groundFacilityDescriptor",
            "Game.ObjectInfoDataScripts.CustomFacilitiesAndModules.EnergyTransferFacility, Assembly-CSharp|facilityType|groundFacilityDescriptor",
            "Game.ObjectInfoDataScripts.CustomFacilitiesAndModules.EnergyTransferModule, Assembly-CSharp|facilityType|groundFacilityDescriptor",
            "Game.ObjectInfoDataScripts.CustomFacilitiesAndModules.FacilityBonus, Assembly-CSharp|facilityType|groundFacilityDescriptor",
            "Game.ObjectInfoDataScripts.CustomFacilitiesAndModules.InterstellarShipFacilityFinish, Assembly-CSharp|facilityType|groundFacilityDescriptor",
            "Game.ObjectInfoDataScripts.CustomFacilitiesAndModules.LabFacility, Assembly-CSharp|facilityType|groundFacilityDescriptor",
            "Game.ObjectInfoDataScripts.CustomFacilitiesAndModules.RefineryFacility, Assembly-CSharp|facilityType|groundFacilityDescriptor",
            "Game.ObjectInfoDataScripts.CustomFacilitiesAndModules.RefineryModule, Assembly-CSharp|facilityType|groundFacilityDescriptor",
            "Game.ObjectInfoDataScripts.CustomFacilitiesAndModules.SpaceMirrorOrShadeFacility, Assembly-CSharp|facilityType|groundFacilityDescriptor",
            "Game.ObjectInfoDataScripts.CustomFacilitiesAndModules.VehicleAssemblyFacility, Assembly-CSharp|facilityType|groundFacilityDescriptor",
            "Game.ObjectInfoDataScripts.Facility, Assembly-CSharp|facilityType|groundFacilityDescriptor",
            "Game.ObjectInfoDataScripts.FacilityToConstructNotYetPay, Assembly-CSharp|facilityType|groundFacilityDescriptor",
            "Game.ObjectInfoDataScripts.MiningEquipmentModule, Assembly-CSharp|facilityType|groundFacilityDescriptor",
            "Game.ObjectInfoDataScripts.MiningFacility, Assembly-CSharp|facilityType|groundFacilityDescriptor",
            "Game.ObjectInfoDataScripts.ObjectInfoData, Assembly-CSharp|constructionEquipment",
            "Game.ObjectInfoDataScripts.ObservatoryFacility, Assembly-CSharp|facilityType|groundFacilityDescriptor|scheduledExplorationDate",
            "Game.ObjectInfoDataScripts.ProbeSpaceModule, Assembly-CSharp|facilityType|groundFacilityDescriptor",
            "Game.ObjectInfoDataScripts.SpaceModule, Assembly-CSharp|facilityType|groundFacilityDescriptor",
            "Game.PMMissionParameterDataSave, Assembly-CSharp|missionOrigin",
            "Game.SolarSystemLoader.SolarSystemData, Assembly-CSharp|planetCount",
            "InterstellarConstructionFacility, Assembly-CSharp|facilityType|groundFacilityDescriptor",
            "LabModule, Assembly-CSharp|facilityType|groundFacilityDescriptor",
            "NuclearBomb, Assembly-CSharp|facilityType|groundFacilityDescriptor",
            "OrbitalGasExtractorFacility, Assembly-CSharp|facilityType|groundFacilityDescriptor",
            "SpaceStationConstructionFacility, Assembly-CSharp|facilityType|groundFacilityDescriptor",
            "SpaceStationFacilityNextSegment, Assembly-CSharp|facilityType|groundFacilityDescriptor",
            "VehicleAssemblyModule, Assembly-CSharp|facilityType|groundFacilityDescriptor",
        };
        // END GENERATED

        static readonly Dictionary<string, byte[][]> ByType = Build();

        static Dictionary<string, byte[][]> Build()
        {
            var map = new Dictionary<string, byte[][]>(StringComparer.Ordinal);
            foreach (string entry in Entries)
            {
                string[] parts = entry.Split('|');
                var aliases = new byte[parts.Length - 1][];
                for (int k = 1; k < parts.Length; k++) aliases[k - 1] = Encoding.ASCII.GetBytes(parts[k]);
                map[parts[0]] = aliases;
            }
            return map;
        }

        // The aliases of a type ("Name, Assembly" as a node's $type gives it) as ASCII bytes, or null when it has none.
        public static byte[][] Of(string odinTypeName)
        {
            byte[][] aliases;
            return odinTypeName != null && ByType.TryGetValue(odinTypeName, out aliases) ? aliases : null;
        }
    }
}

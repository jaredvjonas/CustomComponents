using System;
using System.Collections.Generic;
using System.Linq;
using BattleTech;
using UnityEngine;

namespace CustomComponents;

[HarmonyPatch(typeof(Contract), "GenerateSalvage")]
public static class Contract_GenerateSalvage
{
    public static bool IsDestroyed(MechDef mech)
    {
        if (mech.IsDestroyed)
        {
            return true;
        }

        if (Control.Settings.CheckCriticalComponent && mech.Inventory.Any(i =>
                i.Def.CriticalComponent && i.DamageLevel == ComponentDamageLevel.Destroyed))
        {
            return true;
        }

        return mech.Inventory.Any(item => (item.DamageLevel == ComponentDamageLevel.Destroyed && item.Def.CCFlags().Vital) || item.GetComponents<IIsDestroyed>().Any(isDestroyed => isDestroyed.IsMechDestroyed(item, mech)));
    }

    // CustomUnits represents combat vehicles as Mech-type units, so destroyed vehicles arrive
    // in the enemyMechs list (not enemyVehicles) and would be salvaged like mechs. Detect them
    // via the base-game DataManager (a vehicle's chassis is a registered VehicleChassisDef),
    // with CustomUnits' "unit_vehicle" tag as a fallback. Kept dependency-free so CustomComponents
    // does not need a reference to CustomUnits.
    public static bool IsVehicleUnit(MechDef mech)
    {
        if (mech == null)
        {
            return false;
        }

        var dataManager = UnityGameInstance.BattleTechGame?.DataManager;
        if (dataManager != null
            && !string.IsNullOrEmpty(mech.ChassisID)
            && dataManager.VehicleChassisDefs.Exists(mech.ChassisID))
        {
            return true;
        }

        return mech.MechTags != null && mech.MechTags.Contains("unit_vehicle");
    }

    [HarmonyPrefix]
    [HarmonyWrapSafe]
    [HarmonyPriority(Priority.Low)]
    public static void Prefix(ref bool __runOriginal, List<UnitResult> enemyMechs, List<VehicleDef> enemyVehicles,
        List<UnitResult> lostUnits, bool logResults,
        Contract __instance, ref List<SalvageDef> ___finalPotentialSalvage)
    {
        // IrianTech (TKT-022): a vehicle must NEVER contribute a PART/chassis to salvage - only its
        // mounted weapons/component upgrades (handled in the vehicle-components pass below). CustomUnits
        // delivers combat vehicles as Mech-type units in enemyMechs, where AddMechToSalvage would create
        // a vehicle mech-part AND other patchers (SalvageOperations) independently add vehicle parts.
        // So ALWAYS capture then strip vehicle units from enemyMechs up front (before the early-returns,
        // so it applies to every consumer). Their components are re-added (components-only) in the body.
        List<UnitResult> vehicleUnits = new();
        if (enemyMechs != null)
        {
            vehicleUnits = enemyMechs.Where(u => IsVehicleUnit(u?.mech)).ToList();
            if (vehicleUnits.Count > 0)
            {
                enemyMechs.RemoveAll(u => IsVehicleUnit(u?.mech));
                Log.SalvageProcess.Trace?.Log($"Stripped {vehicleUnits.Count} vehicle unit(s) from enemyMechs (no vehicle parts in salvage)");
            }
        }

        if (!__runOriginal)
        {
            return;
        }

        if (!Control.Settings.OverrideSalvageGeneration)
        {
            return;
        }

        __runOriginal = false;

        Log.SalvageProcess.Trace?.Log($"Start GenerateSalvage for {__instance.Name}");

        ___finalPotentialSalvage = new();

        var contract = __instance;

        contract.SalvagedChassis = new();
        contract.LostMechs = new();
        contract.SalvageResults = new();

        var simgame = __instance.BattleTechGame.Simulation;
        if (simgame == null)
        {
            Log.Main.Error?.Log("No simgame - cancel salvage");
            return;
        }

        var Constants = simgame.Constants;

        Log.SalvageProcess.Trace?.Log($"- Lost Units {__instance.Name}");
        for (var i = 0; i < lostUnits.Count; i++)
        {
            var mech = lostUnits[i].mech;

            if (!IsDestroyed(mech))
            {
                Log.SalvageProcess.Trace?.Log($"-- {mech.Name} not destroyed, skiping");
                continue;

            }


            if (Control.Settings.OverrideRecoveryChance)
            {
                Log.SalvageProcess.Trace?.Log($"-- Recovery {mech.Name} CC method");

                var chance = Constants.Salvage.DestroyedMechRecoveryChance;

                chance -= mech.IsLocationDamaged(ChassisLocations.Head)
                    ? Control.Settings.HeadRecoveryPenaly
                    : 0;

                chance -= mech.IsLocationDestroyed(ChassisLocations.LeftTorso)
                    ? Control.Settings.TorsoRecoveryPenalty
                    : 0;
                chance -= mech.IsLocationDestroyed(ChassisLocations.CenterTorso)
                    ? Control.Settings.TorsoRecoveryPenalty
                    : 0;
                chance -= mech.IsLocationDestroyed(ChassisLocations.RightTorso)
                    ? Control.Settings.TorsoRecoveryPenalty
                    : 0;

                chance -= mech.IsLocationDestroyed(ChassisLocations.RightArm)
                    ? Control.Settings.LimbRecoveryPenalty
                    : 0;
                chance -= mech.IsLocationDestroyed(ChassisLocations.RightLeg)
                    ? Control.Settings.LimbRecoveryPenalty
                    : 0;
                chance -= mech.IsLocationDestroyed(ChassisLocations.LeftArm)
                    ? Control.Settings.LimbRecoveryPenalty
                    : 0;
                chance -= mech.IsLocationDestroyed(ChassisLocations.LeftLeg)
                    ? Control.Settings.LimbRecoveryPenalty
                    : 0;

                chance += lostUnits[i].pilot.HasEjected
                    ? Control.Settings.EjectRecoveryBonus
                    : 0;


                var num = simgame.NetworkRandom.Float();

                lostUnits[i].mechLost = chance < num;

                if (lostUnits[i].mechLost)
                {
                    Log.SalvageProcess.Trace?.Log($"--- {chance:0.00} < {num:0.00} - roll failed, no recovery");
                }
                else
                {
                    Log.SalvageProcess.Trace?.Log($"--- {chance:0.00} >= {num:0.00} - roll success, recovery");
                }
            }
            else
            {
                Log.SalvageProcess.Trace?.Log($"-- Recovery {mech.Name} vanila method");
                var num = simgame.NetworkRandom.Float();

                if (mech.IsLocationDestroyed(ChassisLocations.CenterTorso))
                {
                    Log.SalvageProcess.Trace?.Log("--- CenterTorso Destroyed - no recovery");
                    lostUnits[i].mechLost = true;
                }
                else
                {
                    lostUnits[i].mechLost = Constants.Salvage.DestroyedMechRecoveryChance < num;
                    if (lostUnits[i].mechLost)
                    {
                        Log.SalvageProcess.Trace?.Log($"--- {Constants.Salvage.DestroyedMechRecoveryChance:0.00} < {num:0.00} - roll failed, no recovery");
                    }
                    else
                    {
                        Log.SalvageProcess.Trace?.Log($"--- {Constants.Salvage.DestroyedMechRecoveryChance:0.00} >= {num:0.00} - roll success, recovery");
                    }
                }
            }

            if (lostUnits[i].mechLost)
            {
                if (Control.Settings.SalvageUnrecoveredMech)
                {
                    AddMechToSalvage(mech, contract, simgame, Constants, ___finalPotentialSalvage);
                }
                else
                {
                    var old_diff = __instance.Override.finalDifficulty;

                    var old_rare_u = Constants.Salvage.RareUpgradeChance;
                    var old_rare_w = Constants.Salvage.RareWeaponChance;
                    var old_vrare_i = Constants.Salvage.VeryRareUpgradeChance;
                    var old_vrare_w = Constants.Salvage.VeryRareWeaponChance;

                    Constants.Salvage.RareUpgradeChance = 0;
                    Constants.Salvage.RareWeaponChance = 0;
                    Constants.Salvage.VeryRareUpgradeChance = 0;
                    Constants.Salvage.VeryRareWeaponChance = 0;

                    __instance.Override.finalDifficulty = 0;

                    AddMechToSalvage(mech, contract, simgame, Constants, __instance.SalvageResults);


                    Constants.Salvage.RareUpgradeChance = old_rare_u;
                    Constants.Salvage.RareWeaponChance = old_rare_w;
                    Constants.Salvage.VeryRareUpgradeChance = old_vrare_i;
                    Constants.Salvage.VeryRareWeaponChance = old_vrare_w;

                    __instance.Override.finalDifficulty = old_diff;


                }
            }

        }


        Log.SalvageProcess.Trace?.Log($"- Enemy Mechs {__instance.Name}");
        foreach (var unit in enemyMechs)
        {
            // vehicles were already stripped above (they never yield mech parts)
            if (unit.pilot.IsIncapacitated || IsDestroyed(unit.mech) || unit.pilot.HasEjected)
            {
                AddMechToSalvage(unit.mech, contract, simgame, Constants, ___finalPotentialSalvage);
            }
            else
            {
                Log.SalvageProcess.Trace?.Log($"-- Salvaging {unit.mech.Name}");
                Log.SalvageProcess.Trace?.Log("--- not destroyed, skipping");
            }
        }

        // IrianTech (TKT-022): salvage vehicle MOUNTED COMPONENTS ONLY (weapons + upgrades) - NEVER a
        // vehicle part/chassis (vehicles were stripped from enemyMechs above so no part is ever created
        // by us or other patchers). Recover them even from destroyed locations (parity with mech
        // recover-all, TKT-014). Unresolved/stub defs (Heavy Metal DLC) are filtered centrally by
        // AddMechComponentToSalvage -> CheckDefaults so they cannot crash AAR_SalvageChosen.SortBy_Name.
        if (Control.Settings.SalvageVehicleComponents)
        {
            Log.SalvageProcess.Trace?.Log($"- Vehicle components (no parts) {__instance.Name}");
            // (a) vehicles delivered as Mech-type units (CustomUnits) - captured & stripped above
            foreach (var unit in vehicleUnits)
            {
                if (!(unit.pilot.IsIncapacitated || IsDestroyed(unit.mech) || unit.pilot.HasEjected))
                {
                    continue;
                }
                foreach (var component in unit.mech.Inventory)
                {
                    Log.SalvageProcess.Trace?.Log($"--- Adding vehicle component {component.ComponentDefID}");
                    contract.AddMechComponentToSalvage(___finalPotentialSalvage, component.Def, ComponentDamageLevel.Functional, false,
                        Constants, simgame.NetworkRandom);
                }
            }
            // (b) true VehicleDef units, if any are delivered that way
            foreach (var vechicle in enemyVehicles)
            {
                Log.SalvageProcess.Trace?.Log($"-- Vehicle {vechicle?.Chassis?.Description?.Name}");
                foreach (var component in vechicle.Inventory)
                {
                    Log.SalvageProcess.Trace?.Log($"--- Adding vehicle component {component.ComponentDefID}");
                    contract.AddMechComponentToSalvage(___finalPotentialSalvage, component.Def, ComponentDamageLevel.Functional, false,
                        Constants, simgame.NetworkRandom);
                }
            }
        }
        else
        {
            Log.SalvageProcess.Trace?.Log($"- Vehicle components skipped (SalvageVehicleComponents=false) {__instance.Name}");
        }

        contract.FilterPotentialSalvage(___finalPotentialSalvage);
        var num2 = __instance.SalvagePotential;
        var num3 = Constants.Salvage.VictorySalvageChance;
        var num4 = Constants.Salvage.VictorySalvageLostPerMechDestroyed;
        if (__instance.State == Contract.ContractState.Failed)
        {
            num3 = Constants.Salvage.DefeatSalvageChance;
            num4 = Constants.Salvage.DefeatSalvageLostPerMechDestroyed;
        }
        else if (__instance.State == Contract.ContractState.Retreated)
        {
            num3 = Constants.Salvage.RetreatSalvageChance;
            num4 = Constants.Salvage.RetreatSalvageLostPerMechDestroyed;
        }
        var num5 = num3;
        var num6 = num2 * __instance.PercentageContractSalvage;
        if (num2 > 0)
        {
            num6 += Constants.Finances.ContractFloorSalvageBonus;
        }
        num3 = Mathf.Max(0f, num5 - num4 * lostUnits.Count);
        var num7 = Mathf.FloorToInt(num6 * num3);
        if (num2 > 0)
        {
            num2 += Constants.Finances.ContractFloorSalvageBonus;
        }

        contract.FinalSalvageCount = num7;
        contract.FinalPrioritySalvageCount = Math.Min(7, Mathf.FloorToInt(num7 * Constants.Salvage.PrioritySalvageModifier));
    }

    private static void AddMechToSalvage(MechDef mech, Contract contract, SimGameState simgame, SimGameConstants constants, List<SalvageDef> salvage)
    {
        Log.SalvageProcess.Trace?.Log($"-- Salvaging {mech.Name}");

        var numparts = 0;

        if (Control.Settings.OverrideMechPartCalculation)
        {
            if (mech.IsLocationDestroyed(ChassisLocations.CenterTorso))
            {
                numparts = Control.Settings.CenterTorsoDestroyedParts;
            }
            else
            {
                var total = Control.Settings.SalvageArmWeight * 2 + Control.Settings.SalvageHeadWeight +
                            Control.Settings.SalvageLegWeight * 2 + Control.Settings.SalvageTorsoWeight * 2 + 1;

                var val = total;

                val -= mech.IsLocationDestroyed(ChassisLocations.Head) ? Control.Settings.SalvageHeadWeight : 0;

                val -= mech.IsLocationDestroyed(ChassisLocations.LeftTorso)
                    ? Control.Settings.SalvageTorsoWeight
                    : 0;
                val -= mech.IsLocationDestroyed(ChassisLocations.RightTorso)
                    ? Control.Settings.SalvageTorsoWeight
                    : 0;

                val -= mech.IsLocationDestroyed(ChassisLocations.LeftLeg) ? Control.Settings.SalvageLegWeight : 0;
                val -= mech.IsLocationDestroyed(ChassisLocations.RightLeg) ? Control.Settings.SalvageLegWeight : 0;

                val -= mech.IsLocationDestroyed(ChassisLocations.LeftArm) ? Control.Settings.SalvageArmWeight : 0;
                val -= mech.IsLocationDestroyed(ChassisLocations.LeftLeg) ? Control.Settings.SalvageArmWeight : 0;

                numparts = (int)(constants.Story.DefaultMechPartMax * val / total + 0.5f);
                if (numparts <= 0)
                {
                    numparts = 1;
                }

                if (numparts > constants.Story.DefaultMechPartMax)
                {
                    numparts = constants.Story.DefaultMechPartMax;
                }
            }
        }
        else
        {
            numparts = 3;
            if (mech.IsLocationDestroyed(ChassisLocations.CenterTorso))
            {
                numparts = 1;
            }
            else if (mech.IsLocationDestroyed(ChassisLocations.LeftLeg) &&
                     mech.IsLocationDestroyed(ChassisLocations.RightLeg))
            {
                numparts = 2;
            }
        }

        try
        {
            Log.SalvageProcess.Trace?.Log($"--- Adding {numparts} parts");
            contract.CreateAndAddMechPart(constants, mech, numparts, salvage);
        }
        catch (Exception e)
        {
            Log.Main.Error?.Log("Error in adding parts", e);
        }

        try
        {
            if (Control.Settings.NoLootCTDestroyed && mech.IsLocationDestroyed(ChassisLocations.CenterTorso))
            {
                Log.SalvageProcess.Trace?.Log("--- CT Destroyed - no component loot");
            }
            else
            {
                // IrianTech (TKT-014): recover ALL weapons/components, even from destroyed
                // locations and even if the component itself was destroyed. Salvage adds
                // component defs to inventory as functional parts (loose items have no damaged
                // state). NoSalvage/fixed items are still excluded downstream by
                // AddMechComponentToSalvage -> CheckDefaults.
                foreach (var component in mech.Inventory)
                {
                    Log.SalvageProcess.Trace?.Log($"--- Adding {component.ComponentDefID} (dmg={component.DamageLevel}, locDestroyed={mech.IsLocationDestroyed(component.MountedLocation)})");
                    contract.AddMechComponentToSalvage(salvage, component.Def, ComponentDamageLevel.Functional, false,
                        constants, simgame.NetworkRandom);
                }
            }
        }
        catch (Exception e)
        {
            Log.Main.Error?.Log("Error in adding component", e);
        }
    }
}
using System;
using System.Linq;
using VMS.TPS.Common.Model.API;
using VMS.TPS.Common.Model.Types;

namespace PlanChecker.Services
{
    /// <summary>
    /// Generates a two-arc VMAT plan via the Varian ESAPI.
    ///
    /// Workflow (based on official Varian-Code-Samples patterns):
    ///   1.  Locate target structure (GTV / CTV / PTV)
    ///   2.  Create / reuse an "AutoPlanCourse"
    ///   3.  Add ExternalPlanSetup + prescription via UniqueFractionation
    ///   4.  Optionally configure optimiser and dose-calc models
    ///   5.  Place isocenter at target centre-of-mass
    ///   6.  Add two full VMAT arcs (CCW col30° + CW col330°)
    ///   7.  Add optimisation objectives: PTV coverage, PTV hot-spot,
    ///       Normal Tissue Objective (NTO), OAR mean-dose sparing
    ///   8.  Run VMAT optimiser  →  calculate final dose
    ///   9.  Normalise so D98%(PTV) matches prescription
    ///
    /// Requirements
    ///   • patient.BeginModifications() must be called before this method.
    ///   • ESAPI automation licence ("Advanced" or "Research") required
    ///     for plan creation, beam placement, and optimisation.
    /// </summary>
    public static class AutoPlanService
    {
        // ── Course / plan identifiers ─────────────────────────────────────
        public const string CourseName = "AutoPlanCourse";
        public const string PlanName   = "AutoVMAT";

        // ── Fixed two-arc geometry ────────────────────────────────────────
        //   Arc 1: CCW, collimator 30°,  gantry 181°→179°
        //   Arc 2: CW,  collimator 330°, gantry 179°→181°
        private static readonly int[]             CollAngles   = { 30,  330 };
        private static readonly int[]             GantryStarts = { 181, 179 };
        private static readonly int[]             GantryStops  = { 179, 181 };
        private static readonly GantryDirection[] ArcDirs      =
        {
            GantryDirection.CounterClockwise,
            GantryDirection.Clockwise
        };

        /// <summary>Symmetric 10 × 10 cm² jaw opening (mm).</summary>
        private static readonly VRect<double> JawPositions =
            new VRect<double>(-50, -50, 50, 50);

        // ── Optimisation priorities ───────────────────────────────────────
        private const double PriorityPtvCoverage = 100;
        private const double PriorityPtvHotSpot  = 80;
        private const double PriorityNto         = 80;
        private const double PriorityOarMean     = 50;

        // ─────────────────────────────────────────────────────────────────
        // Public entry point
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Creates and optimises a two-arc VMAT plan.
        /// </summary>
        /// <param name="patient">Open Eclipse patient (BeginModifications already called).</param>
        /// <param name="structureSet">Structure set that contains the target and OARs.</param>
        /// <param name="targetId">Structure Id of the planning target (GTV / CTV / PTV).</param>
        /// <param name="prescriptionGy">Total prescription dose in Gy.</param>
        /// <param name="nFractions">Number of treatment fractions.</param>
        /// <param name="machineName">Eclipse machine Id (e.g. "TrueBeam1").</param>
        /// <param name="energyMode">Photon energy mode Id (e.g. "6X", "10X", "6XFFF").</param>
        /// <param name="optimizerModelId">
        ///   Eclipse optimiser model Id (e.g. "PO 16.1.06").
        ///   Pass empty string to keep the plan's current model.
        /// </param>
        /// <param name="doseCalcModelId">
        ///   Eclipse dose-calc algorithm Id (e.g. "AAA 16.1.06").
        ///   Pass empty string to keep the plan's current model.
        /// </param>
        /// <param name="log">Callback for timestamped progress messages.</param>
        /// <returns>The newly created and optimised ExternalPlanSetup.</returns>
        public static ExternalPlanSetup RunVmatAutoPlan(
            Patient        patient,
            StructureSet   structureSet,
            string         targetId,
            double         prescriptionGy,
            int            nFractions,
            string         machineName,
            string         energyMode,
            string         optimizerModelId,
            string         doseCalcModelId,
            Action<string> log)
        {
            if (structureSet == null)
                throw new InvalidOperationException("No structure set available.");

            // ── 1. Target structure ───────────────────────────────────────
            var target = structureSet.Structures.FirstOrDefault(s =>
                s.Id.Equals(targetId, StringComparison.OrdinalIgnoreCase));

            if (target == null)
                throw new InvalidOperationException(
                    $"Structure '{targetId}' not found in the structure set.");

            log($"Target : {target.Id} ({target.DicomType})  {target.Volume:F1} cc");

            // ── 2. Course ─────────────────────────────────────────────────
            var course = patient.Courses
                             .FirstOrDefault(c => c.Id == CourseName);
            if (course == null)
            {
                course    = patient.AddCourse();
                course.Id = CourseName;
                log($"Created course '{CourseName}'.");
            }
            else
            {
                log($"Using existing course '{CourseName}'.");
            }

            // ── 3. Plan + prescription ────────────────────────────────────
            log("Creating ExternalPlanSetup...");
            var plan = course.AddExternalPlanSetup(structureSet);
            plan.Id  = PlanName;

            // Correct ESAPI pattern: UniqueFractionation.SetPrescription
            // (not plan.SetPrescription which does not exist in v15+)
            double dosePerFxGy = prescriptionGy / nFractions;
            plan.UniqueFractionation.SetPrescription(
                nFractions,
                new DoseValue(dosePerFxGy, DoseValue.DoseUnit.Gy),
                1.0);  // 1.0 = 100 % at isocenter

            log($"Prescription : {prescriptionGy} Gy  in {nFractions} fx " +
                $"({dosePerFxGy:F3} Gy/fx)");

            // ── 4. Calculation models (optional) ──────────────────────────
            if (!string.IsNullOrEmpty(optimizerModelId))
            {
                plan.SetCalculationModel(
                    CalculationType.PhotonVMATOptimization, optimizerModelId);
                log($"Optimiser model : {optimizerModelId}");
            }
            if (!string.IsNullOrEmpty(doseCalcModelId))
            {
                plan.SetCalculationModel(
                    CalculationType.PhotonVolumeDose, doseCalcModelId);
                log($"Dose-calc model : {doseCalcModelId}");
            }

            // ── 5. Isocenter ──────────────────────────────────────────────
            var iso = target.CenterPoint;
            log($"Isocenter (mm) : X={iso.x:F1}  Y={iso.y:F1}  Z={iso.z:F1}");

            // ── 6. Two VMAT arcs ──────────────────────────────────────────
            var machineParams = new ExternalBeamMachineParameters(
                machineName, energyMode, 600, "ARC", null);

            for (int i = 0; i < 2; i++)
            {
                string dir = ArcDirs[i] == GantryDirection.CounterClockwise ? "CCW" : "CW";
                log($"Adding Arc {i + 1} : {dir}, col {CollAngles[i]}°, " +
                    $"gantry {GantryStarts[i]}°→{GantryStops[i]}°");

                plan.AddArcBeam(
                    machineParams,
                    JawPositions,
                    CollAngles[i],
                    GantryStarts[i],
                    GantryStops[i],
                    ArcDirs[i],
                    0,    // couch (patient support) angle
                    iso);
            }
            log("Arcs created.");

            // ── 7. Optimisation objectives ────────────────────────────────
            log("Configuring optimisation objectives...");
            var opt = plan.OptimizationSetup;

            // PTV: D95 ≥ 95 % Rx  (Lower / coverage)
            opt.AddPointObjective(
                target,
                OptimizationObjectiveOperator.Lower,
                new DoseValue(prescriptionGy * 0.95, DoseValue.DoseUnit.Gy),
                95,
                PriorityPtvCoverage);

            // PTV: Dmax ≤ 107 % Rx  (Upper / hot-spot)
            opt.AddPointObjective(
                target,
                OptimizationObjectiveOperator.Upper,
                new DoseValue(prescriptionGy * 1.07, DoseValue.DoseUnit.Gy),
                0,
                PriorityPtvHotSpot);

            log("PTV objectives : D95 ≥ 95 % Rx, Dmax ≤ 107 % Rx.");

            // Normal Tissue Objective — enforces steep dose fall-off outside target
            // Pattern from Varian-Code-Samples AutomatedPlanningDemo:
            opt.AddNormalTissueObjective(
                priority:                    PriorityNto,
                distanceFromTargetBorderInMM: 6.0,
                startDosePercentage:          1.0,   // 100 % Rx at target border
                endDosePercentage:            0.3,   // 30 % Rx at distance
                fallOff:                      0.05);
            log("NTO added (6 mm margin, 100 %→30 %, fallOff 0.05).");

            // OARs: generic mean-dose sparing ≤ 30 % Rx
            foreach (var organ in structureSet.Structures
                .Where(s => s.DicomType == "ORGAN" && !s.IsEmpty))
            {
                try
                {
                    opt.AddMeanDoseObjective(
                        organ,
                        new DoseValue(prescriptionGy * 0.30, DoseValue.DoseUnit.Gy),
                        PriorityOarMean);
                    log($"  OAR mean-dose : {organ.Id} ≤ {prescriptionGy * 0.30:F1} Gy");
                }
                catch
                {
                    // Skip structures that don't accept this objective type.
                }
            }

            // ── 8. Optimise ───────────────────────────────────────────────
            log("Running VMAT optimisation (may take several minutes)...");

            // Standard ESAPI: plan.Optimize(OptimizationOptionsVMAT)
            // Research API:   plan.OptimizeVMAT(OptimizationOptionsVMAT)
            // Note: after successful VMAT optimisation Eclipse resets
            // plan normalisation to "No normalisation" — step 9 re-applies it.
            var optResult = plan.Optimize(
                new OptimizationOptionsVMAT(
                    OptimizationIntermediateDoseOption.NoIntermediateDose,
                    string.Empty));

            if (optResult.Success)
                log("Optimisation complete.");
            else
                log($"Optimisation finished with warnings: {optResult.StatusMessage}");

            // ── 9. Calculate dose ─────────────────────────────────────────
            log("Calculating final dose distribution...");
            plan.CalculateDose();
            log("Dose calculation complete.");

            // ── 10. Normalise — D98%(target) = prescription ───────────────
            // Pattern from Varian-Code-Samples AutomatedPlanningDemo
            try
            {
                var doseAt98 = plan.GetDoseAtVolume(
                    target,
                    98.0,
                    VolumePresentation.Relative,
                    DoseValuePresentation.Absolute);

                if (doseAt98.Unit != DoseValue.DoseUnit.Unknown && doseAt98.Dose > 0)
                {
                    double doseAt98Gy = doseAt98.Unit == DoseValue.DoseUnit.cGy
                        ? doseAt98.Dose / 100.0
                        : doseAt98.Dose;

                    plan.PlanNormalizationValue =
                        prescriptionGy / doseAt98Gy * plan.PlanNormalizationValue;

                    log($"Normalised : D98% = {doseAt98Gy:F2} Gy → " +
                        $"NormFactor = {plan.PlanNormalizationValue:F1} %");
                }
            }
            catch (Exception ex)
            {
                log($"Normalisation skipped: {ex.Message}");
            }

            log("─────────────────────────────────────────────────────");
            log($"Plan '{plan.Id}' in course '{course.Id}' is ready.");
            log("Review and approve in Eclipse before clinical use.");

            return plan;
        }
    }
}

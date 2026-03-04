using System;
using System.Linq;
using VMS.TPS.Common.Model.API;
using VMS.TPS.Common.Model.Types;

namespace PlanChecker.Services
{
    /// <summary>
    /// Generates a two-arc VMAT plan programmatically via the Varian ESAPI.
    ///
    /// Workflow (mirrors common clinical patterns from Varian-Code-Samples):
    ///   1. Locate the target structure (GTV / CTV / PTV)
    ///   2. Create an "AutoPlanCourse" if it does not already exist
    ///   3. Add an ExternalPlanSetup and apply the prescription
    ///   4. Place isocenter at the PTV center-of-mass
    ///   5. Add two full VMAT arcs (CCW col30° + CW col330°)
    ///   6. Configure optimization objectives for PTV coverage and OAR sparing
    ///   7. Run VMAT optimizer then calculate final dose
    ///
    /// Requires: ESAPI automation / "Advanced" license on the Eclipse TPS.
    /// Call patient.BeginModifications() before invoking this method.
    /// </summary>
    public static class AutoPlanService
    {
        // ── Course / plan identifiers ─────────────────────────────────────
        public const string CourseName = "AutoPlanCourse";
        public const string PlanName   = "AutoVMAT";

        // ── Default beam geometry ─────────────────────────────────────────
        // Two full arcs avoid a near-coplanar "hot-spot":
        //   Arc 1 — CCW, collimator 30°,  gantry 181°→179°
        //   Arc 2 — CW,  collimator 330°, gantry 179°→181°
        private static readonly int[]   CollAngles    = { 30, 330 };
        private static readonly int[]   GantryStarts  = { 181, 179 };
        private static readonly int[]   GantryStops   = { 179, 181 };
        private static readonly GantryDirection[] Directions =
        {
            GantryDirection.CounterClockwise,
            GantryDirection.Clockwise
        };

        /// <summary>Symmetric 10 × 10 cm² jaw opening (mm).</summary>
        private static readonly VRect<double> JawPositions =
            new VRect<double>(-50, -50, 50, 50);

        // ── Optimization priorities ───────────────────────────────────────
        private const int PriorityPtvCoverage = 100;
        private const int PriorityPtvMax      = 80;
        private const int PriorityOarMean     = 50;

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
        /// <param name="nFractions">Number of fractions.</param>
        /// <param name="machineName">Eclipse machine Id (e.g. "TrueBeam1").</param>
        /// <param name="energyMode">Photon energy mode Id (e.g. "6X", "10X").</param>
        /// <param name="log">Callback for progress messages displayed in the UI.</param>
        /// <returns>The newly created ExternalPlanSetup.</returns>
        public static ExternalPlanSetup RunVmatAutoPlan(
            Patient        patient,
            StructureSet   structureSet,
            string         targetId,
            double         prescriptionGy,
            int            nFractions,
            string         machineName,
            string         energyMode,
            Action<string> log)
        {
            if (structureSet == null)
                throw new InvalidOperationException("No structure set is available.");

            // ── 1. Locate target ─────────────────────────────────────────
            var target = structureSet.Structures.FirstOrDefault(s =>
                s.Id.Equals(targetId, StringComparison.OrdinalIgnoreCase));

            if (target == null)
                throw new InvalidOperationException(
                    $"Structure '{targetId}' not found in the structure set.");

            log($"Target: {target.Id}  ({target.DicomType})  {target.Volume:F1} cc");

            // ── 2. Create / reuse course ──────────────────────────────────
            var course = patient.Courses
                .FirstOrDefault(c => c.Id == CourseName);

            if (course == null)
            {
                course = patient.AddCourse();
                course.Id = CourseName;
                log($"Created course '{CourseName}'.");
            }
            else
            {
                log($"Using existing course '{CourseName}'.");
            }

            // ── 3. Create plan and set prescription ───────────────────────
            log("Creating ExternalPlanSetup...");
            var plan = course.AddExternalPlanSetup(structureSet);
            plan.Id = PlanName;

            double dosePerFxCgy = prescriptionGy * 100.0 / nFractions;
            plan.SetPrescription(
                nFractions,
                new DoseValue(dosePerFxCgy, DoseValue.DoseUnit.cGy),
                100.0);   // 100 % at isocenter

            log($"Prescription: {prescriptionGy} Gy in {nFractions} fx " +
                $"({dosePerFxCgy:F1} cGy / fx)");

            // ── 4. Isocenter at PTV centre ────────────────────────────────
            var iso = target.CenterPoint;
            log($"Isocenter (mm): X={iso.x:F1}  Y={iso.y:F1}  Z={iso.z:F1}");

            // ── 5. Add two VMAT arcs ──────────────────────────────────────
            var machineParams = new ExternalBeamMachineParameters(
                machineName, energyMode, 600, "ARC", null);

            for (int i = 0; i < 2; i++)
            {
                string dir = Directions[i] == GantryDirection.CounterClockwise ? "CCW" : "CW";
                log($"Adding Arc {i + 1}: {dir}, col {CollAngles[i]}°, " +
                    $"gantry {GantryStarts[i]}°→{GantryStops[i]}°");

                plan.AddArcBeam(
                    machineParams,
                    JawPositions,
                    CollAngles[i],
                    GantryStarts[i],
                    GantryStops[i],
                    Directions[i],
                    0,    // couch angle
                    iso);
            }

            log("Arcs created.");

            // ── 6. Optimization objectives ────────────────────────────────
            log("Configuring optimization objectives...");
            var opt = plan.OptimizationSetup;

            // PTV: D95 ≥ 95 % Rx  (Lower / coverage objective)
            opt.AddPointObjective(
                target,
                OptimizationObjectiveOperator.Lower,
                new DoseValue(prescriptionGy * 0.95 * 100.0, DoseValue.DoseUnit.cGy),
                95,
                PriorityPtvCoverage);

            // PTV: Dmax ≤ 107 % Rx  (Upper / hot-spot objective)
            opt.AddPointObjective(
                target,
                OptimizationObjectiveOperator.Upper,
                new DoseValue(prescriptionGy * 1.07 * 100.0, DoseValue.DoseUnit.cGy),
                0,
                PriorityPtvMax);

            log("PTV objectives set: D95 ≥ 95 % Rx, Dmax ≤ 107 % Rx.");

            // OARs: generic mean dose ≤ 30 % Rx
            foreach (var organ in structureSet.Structures
                .Where(s => s.DicomType == "ORGAN" && !s.IsEmpty))
            {
                try
                {
                    opt.AddMeanDoseObjective(
                        organ,
                        new DoseValue(prescriptionGy * 0.30 * 100.0, DoseValue.DoseUnit.cGy),
                        PriorityOarMean);

                    log($"  OAR mean-dose objective: {organ.Id} ≤ {prescriptionGy * 0.30:F1} Gy");
                }
                catch
                {
                    // Some structures do not accept mean-dose objectives; skip silently.
                }
            }

            // ── 7. Optimise ───────────────────────────────────────────────
            log("Running VMAT optimisation (this may take several minutes)...");

            var optResult = plan.Optimize(
                new OptimizationOptionsVMAT(
                    OptimizationIntermediateDoseOption.NoIntermediateDose,
                    string.Empty));

            if (optResult.Success)
                log("Optimisation completed successfully.");
            else
                log($"Optimisation finished with warnings: {optResult.StatusMessage}");

            // ── 8. Calculate final dose ───────────────────────────────────
            log("Calculating final dose distribution...");
            plan.CalculateDose();
            log("Dose calculation complete.");

            log($"───────────────────────────────────────────────────");
            log($"Auto-plan '{plan.Id}' in course '{course.Id}' is ready.");
            log($"Review in Eclipse before clinical use.");

            return plan;
        }
    }
}

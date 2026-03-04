using ESAPIX.Common;
using ESAPIX.Interfaces;
using PlanChecker.Services;
using Prism.Commands;
using Prism.Mvvm;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using VMS.TPS.Common.Model.API;
using VMS.TPS.Common.Model.Types;

namespace PlanChecker.ViewModels
{
    public class MainViewModel : BindableBase
    {
        private readonly AppComThread VMS = AppComThread.Instance;

        // ── DVH table ────────────────────────────────────────────────────
        public ObservableCollection<DvhRowViewModel> DvhRows { get; }
            = new ObservableCollection<DvhRowViewModel>();

        // ── Auto-plan: input parameters ──────────────────────────────────
        public ObservableCollection<string> TargetStructures { get; }
            = new ObservableCollection<string>();

        private string selectedTargetId;
        public string SelectedTargetId
        {
            get => selectedTargetId;
            set => SetProperty(ref selectedTargetId, value);
        }

        private double prescriptionGy = 60.0;
        public double PrescriptionGy
        {
            get => prescriptionGy;
            set => SetProperty(ref prescriptionGy, value);
        }

        private int fractions = 30;
        public int Fractions
        {
            get => fractions;
            set => SetProperty(ref fractions, value);
        }

        private string machineName = "TrueBeam1";
        public string MachineName
        {
            get => machineName;
            set => SetProperty(ref machineName, value);
        }

        private string energyMode = "6X";
        public string EnergyMode
        {
            get => energyMode;
            set => SetProperty(ref energyMode, value);
        }

        // ── Auto-plan: state and log ─────────────────────────────────────
        private bool isAutoPlanning;
        public bool IsAutoPlanning
        {
            get => isAutoPlanning;
            set
            {
                SetProperty(ref isAutoPlanning, value);
                RunAutoPlanCommand?.RaiseCanExecuteChanged();
            }
        }

        private string autoPlanLog = string.Empty;
        public string AutoPlanLog
        {
            get => autoPlanLog;
            set => SetProperty(ref autoPlanLog, value);
        }

        public DelegateCommand RunAutoPlanCommand { get; }

        // ── Plan info (header panel) ─────────────────────────────────────
        private string id;
        public string Id
        {
            get => id;
            set => SetProperty(ref id, value);
        }

        private string uid;
        public string UID
        {
            get => uid;
            set => SetProperty(ref uid, value);
        }

        private int? nBeams;
        public int? NBeams
        {
            get => nBeams;
            set => SetProperty(ref nBeams, value);
        }

        private bool isDoseCalculated;
        public bool IsDoseCalculated
        {
            get => isDoseCalculated;
            set => SetProperty(ref isDoseCalculated, value);
        }

        // ────────────────────────────────────────────────────────────────
        public MainViewModel()
        {
            RunAutoPlanCommand = new DelegateCommand(ExecuteAutoPlan,
                () => !IsAutoPlanning && !string.IsNullOrEmpty(SelectedTargetId));

            OnPlanChanged(VMS.GetValue(sc => sc.PlanSetup));
            VMS.Execute(sc => sc.PlanSetupChanged += OnPlanChanged);
        }

        // ── Plan-changed handler ─────────────────────────────────────────
        public void OnPlanChanged(PlanSetup ps)
        {
            VMS.Execute(sc =>
            {
                Id               = ps?.Id;
                UID              = ps?.UID;
                IsDoseCalculated = ps?.Dose != null;
                NBeams           = ps?.Beams.Count();

                var dvhRows    = new List<DvhRowViewModel>();
                var targetIds  = new List<string>();

                if (ps?.StructureSet != null)
                {
                    foreach (var s in ps.StructureSet.Structures
                        .Where(s => !s.IsEmpty)
                        .OrderBy(s => GetStructureOrder(s.DicomType))
                        .ThenBy(s => s.Id))
                    {
                        // Populate target selector for auto-plan tab
                        if (s.DicomType == "GTV" || s.DicomType == "CTV" || s.DicomType == "PTV")
                            targetIds.Add(s.Id);

                        // DVH row (only when dose exists)
                        if (ps.Dose != null)
                        {
                            try
                            {
                                var dvh = ps.GetDVHCumulativeData(s,
                                    DoseValuePresentation.Absolute,
                                    VolumePresentation.AbsoluteCm3,
                                    0.001);

                                if (dvh == null) continue;

                                double d95Raw = GetDoseAtVolumeCc(dvh, s.Volume * 0.95);
                                var (bg, fg)  = GetRowColors(s.DicomType);

                                dvhRows.Add(new DvhRowViewModel
                                {
                                    StructureId = s.Id,
                                    DicomType   = s.DicomType,
                                    Volume      = $"{s.Volume:F2}",
                                    MinDose     = FormatDoseGy(dvh.MinDoseValue),
                                    MeanDose    = FormatDoseGy(dvh.MeanDose),
                                    MaxDose     = FormatDoseGy(dvh.MaxDoseValue),
                                    D95         = FormatRawDoseGy(d95Raw, dvh.MinDoseValue.Unit),
                                    RowBackground = bg,
                                    RowForeground = fg
                                });
                            }
                            catch { /* skip structures with unavailable DVH */ }
                        }
                    }
                }

                Application.Current.Dispatcher.Invoke(() =>
                {
                    DvhRows.Clear();
                    foreach (var row in dvhRows) DvhRows.Add(row);

                    TargetStructures.Clear();
                    foreach (var tid in targetIds) TargetStructures.Add(tid);
                    if (TargetStructures.Any())
                        SelectedTargetId = TargetStructures.First();

                    RunAutoPlanCommand.RaiseCanExecuteChanged();
                });
            });
        }

        // ── Auto-plan command ────────────────────────────────────────────
        private void ExecuteAutoPlan()
        {
            IsAutoPlanning = true;
            AutoPlanLog    = string.Empty;
            AppendLog("Initiating VMAT auto-plan workflow...");
            AppendLog($"Target: {SelectedTargetId}  |  " +
                      $"Rx: {PrescriptionGy} Gy / {Fractions} fx  |  " +
                      $"Machine: {MachineName} {EnergyMode}");

            // Run on a background thread so the UI stays responsive.
            Task.Run(() =>
            {
                try
                {
                    VMS.Execute(sc =>
                    {
                        sc.Patient.BeginModifications();

                        var structureSet = sc.StructureSet
                                          ?? sc.PlanSetup?.StructureSet;

                        AutoPlanService.RunVmatAutoPlan(
                            sc.Patient,
                            structureSet,
                            SelectedTargetId,
                            PrescriptionGy,
                            Fractions,
                            MachineName,
                            EnergyMode,
                            AppendLog);
                    });
                }
                catch (Exception ex)
                {
                    AppendLog($"ERROR: {ex.Message}");
                }
                finally
                {
                    Application.Current.Dispatcher.Invoke(
                        () => IsAutoPlanning = false);
                }
            });
        }

        private void AppendLog(string message)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            Application.Current.Dispatcher.Invoke(
                () => AutoPlanLog += line + Environment.NewLine);
        }

        // ── DVH helpers ──────────────────────────────────────────────────
        private static int GetStructureOrder(string dicomType)
        {
            switch (dicomType?.ToUpperInvariant())
            {
                case "GTV":       return 0;
                case "CTV":       return 1;
                case "PTV":       return 2;
                case "ORGAN":     return 3;
                case "AVOIDANCE": return 4;
                case "EXTERNAL":  return 5;
                default:          return 6;
            }
        }

        private static double GetDoseAtVolumeCc(DVHData dvh, double targetVolumeCc)
        {
            if (dvh.CurveData == null || dvh.CurveData.Length == 0) return 0;

            for (int i = 0; i < dvh.CurveData.Length - 1; i++)
            {
                if (dvh.CurveData[i].Volume >= targetVolumeCc &&
                    dvh.CurveData[i + 1].Volume < targetVolumeCc)
                {
                    double volRange  = dvh.CurveData[i].Volume - dvh.CurveData[i + 1].Volume;
                    if (volRange == 0) return dvh.CurveData[i].DoseValue.Dose;
                    double fraction  = (dvh.CurveData[i].Volume - targetVolumeCc) / volRange;
                    double doseRange = dvh.CurveData[i + 1].DoseValue.Dose - dvh.CurveData[i].DoseValue.Dose;
                    return dvh.CurveData[i].DoseValue.Dose + fraction * doseRange;
                }
            }
            return 0;
        }

        private static string FormatDoseGy(DoseValue d)
        {
            double gy = d.Unit == DoseValue.DoseUnit.cGy ? d.Dose / 100.0 : d.Dose;
            return $"{gy:F2}";
        }

        private static string FormatRawDoseGy(double raw, DoseValue.DoseUnit unit)
        {
            double gy = unit == DoseValue.DoseUnit.cGy ? raw / 100.0 : raw;
            return $"{gy:F2}";
        }

        private static (SolidColorBrush Background, SolidColorBrush Foreground) GetRowColors(
            string dicomType)
        {
            switch (dicomType?.ToUpperInvariant())
            {
                case "GTV":       return (Brush(198,  40,  40), White());
                case "CTV":       return (Brush(230,  81,   0), White());
                case "PTV":       return (Brush(249, 168,  37), Black());
                case "ORGAN":     return (Brush(173, 216, 230), Black());
                case "AVOIDANCE": return (Brush(197, 225, 165), Black());
                case "EXTERNAL":  return (Brush(224, 224, 224), Black());
                default:          return (White(), Black());
            }
        }

        private static SolidColorBrush Brush(byte r, byte g, byte b)
            => new SolidColorBrush(Color.FromRgb(r, g, b));

        private static SolidColorBrush White() => new SolidColorBrush(Colors.White);
        private static SolidColorBrush Black() => new SolidColorBrush(Colors.Black);
    }
}

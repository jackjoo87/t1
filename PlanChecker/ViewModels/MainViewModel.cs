using ESAPIX.Common;
using ESAPIX.Interfaces;
using Prism.Mvvm;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using VMS.TPS.Common.Model.API;
using VMS.TPS.Common.Model.Types;

namespace PlanChecker.ViewModels
{
    public class MainViewModel : BindableBase
    {
        AppComThread VMS = AppComThread.Instance;

        public ObservableCollection<DvhRowViewModel> DvhRows { get; }
            = new ObservableCollection<DvhRowViewModel>();

        public MainViewModel()
        {
            OnPlanChanged(VMS.GetValue(sc => sc.PlanSetup));
            VMS.Execute(sc =>
            {
                sc.PlanSetupChanged += OnPlanChanged;
            });
        }

        public void OnPlanChanged(PlanSetup ps)
        {
            VMS.Execute(sc =>
            {
                Id = ps?.Id;
                UID = ps?.UID;
                IsDoseCalculated = ps?.Dose != null;
                NBeams = ps?.Beams.Count();

                var rows = new List<DvhRowViewModel>();

                if (ps?.StructureSet != null && ps.Dose != null)
                {
                    foreach (var structure in ps.StructureSet.Structures
                        .Where(s => !s.IsEmpty)
                        .OrderBy(s => GetStructureOrder(s.DicomType))
                        .ThenBy(s => s.Id))
                    {
                        try
                        {
                            var dvh = ps.GetDVHCumulativeData(
                                structure,
                                DoseValuePresentation.Absolute,
                                VolumePresentation.AbsoluteCm3,
                                0.001);

                            if (dvh == null) continue;

                            double d95Raw = GetDoseAtVolumeCc(dvh, structure.Volume * 0.95);
                            var (bg, fg) = GetRowColors(structure.DicomType);

                            rows.Add(new DvhRowViewModel
                            {
                                StructureId = structure.Id,
                                DicomType = structure.DicomType,
                                Volume = $"{structure.Volume:F2}",
                                MinDose = FormatDoseGy(dvh.MinDoseValue),
                                MeanDose = FormatDoseGy(dvh.MeanDose),
                                MaxDose = FormatDoseGy(dvh.MaxDoseValue),
                                D95 = FormatRawDoseGy(d95Raw, dvh.MinDoseValue.Unit),
                                RowBackground = bg,
                                RowForeground = fg
                            });
                        }
                        catch { /* skip structures where DVH is unavailable */ }
                    }
                }

                Application.Current.Dispatcher.Invoke(() =>
                {
                    DvhRows.Clear();
                    foreach (var row in rows)
                        DvhRows.Add(row);
                });
            });
        }

        private int GetStructureOrder(string dicomType)
        {
            switch (dicomType?.ToUpperInvariant())
            {
                case "GTV": return 0;
                case "CTV": return 1;
                case "PTV": return 2;
                case "ORGAN": return 3;
                case "AVOIDANCE": return 4;
                case "EXTERNAL": return 5;
                default: return 6;
            }
        }

        private double GetDoseAtVolumeCc(DVHData dvh, double targetVolumeCc)
        {
            if (dvh.CurveData == null || dvh.CurveData.Length == 0) return 0;

            for (int i = 0; i < dvh.CurveData.Length - 1; i++)
            {
                if (dvh.CurveData[i].Volume >= targetVolumeCc &&
                    dvh.CurveData[i + 1].Volume < targetVolumeCc)
                {
                    double volRange = dvh.CurveData[i].Volume - dvh.CurveData[i + 1].Volume;
                    if (volRange == 0) return dvh.CurveData[i].DoseValue.Dose;
                    double fraction = (dvh.CurveData[i].Volume - targetVolumeCc) / volRange;
                    double doseRange = dvh.CurveData[i + 1].DoseValue.Dose - dvh.CurveData[i].DoseValue.Dose;
                    return dvh.CurveData[i].DoseValue.Dose + fraction * doseRange;
                }
            }
            return 0;
        }

        private string FormatDoseGy(DoseValue doseValue)
        {
            double gy = doseValue.Unit == DoseValue.DoseUnit.cGy
                ? doseValue.Dose / 100.0
                : doseValue.Dose;
            return $"{gy:F2}";
        }

        private string FormatRawDoseGy(double rawDose, DoseValue.DoseUnit unit)
        {
            double gy = unit == DoseValue.DoseUnit.cGy ? rawDose / 100.0 : rawDose;
            return $"{gy:F2}";
        }

        private (SolidColorBrush Background, SolidColorBrush Foreground) GetRowColors(string dicomType)
        {
            switch (dicomType?.ToUpperInvariant())
            {
                case "GTV":
                    return (new SolidColorBrush(Color.FromRgb(198, 40, 40)), new SolidColorBrush(Colors.White));
                case "CTV":
                    return (new SolidColorBrush(Color.FromRgb(230, 81, 0)), new SolidColorBrush(Colors.White));
                case "PTV":
                    return (new SolidColorBrush(Color.FromRgb(249, 168, 37)), new SolidColorBrush(Colors.Black));
                case "ORGAN":
                    return (new SolidColorBrush(Color.FromRgb(173, 216, 230)), new SolidColorBrush(Colors.Black));
                case "AVOIDANCE":
                    return (new SolidColorBrush(Color.FromRgb(197, 225, 165)), new SolidColorBrush(Colors.Black));
                case "EXTERNAL":
                    return (new SolidColorBrush(Color.FromRgb(224, 224, 224)), new SolidColorBrush(Colors.Black));
                default:
                    return (new SolidColorBrush(Colors.White), new SolidColorBrush(Colors.Black));
            }
        }

        private string id;
        public string Id
        {
            get { return id; }
            set { SetProperty(ref id, value); }
        }

        private string uid;
        public string UID
        {
            get { return uid; }
            set { SetProperty(ref uid, value); }
        }

        private int? nBeams;
        public int? NBeams
        {
            get { return nBeams; }
            set { SetProperty(ref nBeams, value); }
        }

        private bool isDoseCalculated;
        public bool IsDoseCalculated
        {
            get { return isDoseCalculated; }
            set { SetProperty(ref isDoseCalculated, value); }
        }
    }
}

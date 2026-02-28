using System.Windows.Media;

namespace PlanChecker.ViewModels
{
    public class DvhRowViewModel
    {
        public string StructureId { get; set; }
        public string DicomType { get; set; }
        public string Volume { get; set; }
        public string MinDose { get; set; }
        public string MeanDose { get; set; }
        public string MaxDose { get; set; }
        public string D95 { get; set; }
        public SolidColorBrush RowBackground { get; set; }
        public SolidColorBrush RowForeground { get; set; }
    }
}

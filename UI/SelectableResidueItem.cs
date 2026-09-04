using System.ComponentModel;

namespace UninstallTool.UI
{
    /// <summary>
    /// ResidueItemにチェックボックス選択状態を持たせるためのUI用ラッパー。
    /// </summary>
    public sealed class SelectableResidueItem : INotifyPropertyChanged
    {
        private bool _isSelected;

        public ResidueItem Item { get; }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public string CategoryText => Item.Category.ToString();
        public string LocationText => Item.Location;
        public string DetailText => Item.Detail;
        public string ConfidenceText => Item.Confidence == ResidueConfidence.Medium ? "中" : "低";
        public string DetectionReasonText => Item.DetectionReason;

        public SelectableResidueItem(ResidueItem item)
        {
            Item = item;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}

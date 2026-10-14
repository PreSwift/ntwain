using System.Windows;

namespace Sample.WPF
{
    public partial class ScanSizeSelectionWindow : Window
    {
        public ScanSizeSelectionWindow()
            : this(ScanSizePreference.Load())
        {
        }

        public ScanSizeSelectionWindow(ScanPaperSize initialSize)
        {
            InitializeComponent();
            ApplySelection(initialSize);
        }

        public ScanPaperSize SelectedSize { get; private set; }

        void Ok_Click(object sender, RoutedEventArgs e)
        {
            SelectedSize = GetSelectedSize();
            ScanSizePreference.Save(SelectedSize);
            DialogResult = true;
        }

        void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        void ApplySelection(ScanPaperSize size)
        {
            switch (size)
            {
                case ScanPaperSize.A3: SizeA3.IsChecked = true; break;
                case ScanPaperSize.A4: SizeA4.IsChecked = true; break;
                case ScanPaperSize.A4L: SizeA4L.IsChecked = true; break;
                case ScanPaperSize.K8: Size8K.IsChecked = true; break;
                case ScanPaperSize.K16: Size16K.IsChecked = true; break;
                case ScanPaperSize.K16L: Size16KL.IsChecked = true; break;
                default: SizeAuto.IsChecked = true; break;
            }
        }

        ScanPaperSize GetSelectedSize()
        {
            if (SizeAuto.IsChecked == true) return ScanPaperSize.Auto;
            if (SizeA3.IsChecked == true) return ScanPaperSize.A3;
            if (SizeA4L.IsChecked == true) return ScanPaperSize.A4L;
            if (Size8K.IsChecked == true) return ScanPaperSize.K8;
            if (Size16K.IsChecked == true) return ScanPaperSize.K16;
            if (Size16KL.IsChecked == true) return ScanPaperSize.K16L;
            return ScanPaperSize.A4;
        }
    }
}

using System.ComponentModel;
using System.Windows.Media;

namespace Sample.WPF
{
    /// <summary>
    /// 已保存的扫描页：先落盘，缩略图异步加载，避免阻塞 TWAIN 传输。
    /// </summary>
    class CapturedScanItem : INotifyPropertyChanged
    {
        ImageSource _thumbnail;
        string _ocrStatus = "自动转正已关闭";

        public string FilePath { get; set; }

        public string OcrStatus
        {
            get { return _ocrStatus; }
            set { _ocrStatus = value; OnPropertyChanged(nameof(OcrStatus)); }
        }

        public ImageSource Thumbnail
        {
            get { return _thumbnail; }
            set
            {
                if (_thumbnail == value)
                    return;
                _thumbnail = value;
                OnPropertyChanged(nameof(Thumbnail));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

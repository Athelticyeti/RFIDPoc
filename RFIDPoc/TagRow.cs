using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RFIDPoc
{
    /// <summary>
    /// One unique tag seen by the reader, aggregated across reads.
    /// </summary>
    public class TagRow : INotifyPropertyChanged
    {
        private int _antenna;
        private int _readCount;
        private double _rssi;
        private DateTime _lastSeen;

        public required string TagId { get; init; }
        public DateTime FirstSeen { get; init; }

        public int Antenna { get => _antenna; set => Set(ref _antenna, value); }
        public int ReadCount { get => _readCount; set => Set(ref _readCount, value); }
        public double Rssi { get => _rssi; set => Set(ref _rssi, value); }
        public DateTime LastSeen { get => _lastSeen; set => Set(ref _lastSeen, value); }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}

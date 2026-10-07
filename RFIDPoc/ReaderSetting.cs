using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using nsAlienRFID2;

namespace RFIDPoc
{
    /// <summary>
    /// One reader setting backed by a clsReader CLI property (each get/set is a live "Get X" / "Set X=v" on the reader).
    /// </summary>
    public sealed class ReaderSetting : INotifyPropertyChanged
    {
        private string _current = "";
        private string _newValue = "";
        private string _status = "";

        public ReaderSetting(string group, string name, string description, bool editable = true, params string[] options)
        {
            Group = group;
            Name = name;
            Description = description;
            IsEditable = editable;
            Options = options;
            Property = typeof(clsReader).GetProperty(name)
                       ?? throw new ArgumentException($"clsReader has no property '{name}'.");
        }

        public string Group { get; }
        public string Name { get; }
        public string Description { get; }
        public bool IsEditable { get; }
        public string[] Options { get; }
        private PropertyInfo Property { get; }

        public string Current
        {
            get => _current;
            private set { if (Set(ref _current, value)) OnPropertyChanged(nameof(IsDirty)); }
        }

        public string NewValue
        {
            get => _newValue;
            set { if (Set(ref _newValue, value)) OnPropertyChanged(nameof(IsDirty)); }
        }

        public string Status { get => _status; set => Set(ref _status, value); }

        public bool IsDirty => IsEditable && NewValue.Trim() != Current;

        /// <summary>Reads the value from the reader. Call on the reader's thread.</summary>
        public string ReadFrom(clsReader reader) => Convert.ToString(Property.GetValue(reader))?.Trim() ?? "";

        /// <summary>Writes NewValue to the reader. Call on the reader's thread.</summary>
        public void WriteTo(clsReader reader, string value)
        {
            object converted = Property.PropertyType == typeof(int) ? int.Parse(value) : value;
            Property.SetValue(reader, converted);
        }

        /// <summary>Applies a freshly read value (on the UI thread), resetting any pending edit.</summary>
        public void Loaded(string value)
        {
            Current = value;
            NewValue = value;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name!);
            return true;
        }

        /// <summary>
        /// The settings shown on the config page. Network settings are read-only because changing them
        /// can make the reader unreachable from this PC.
        /// </summary>
        public static List<ReaderSetting> CreateAll() =>
        [
            new("General", "ReaderName", "Friendly name shown in heartbeats and discovery."),
            new("General", "DateTime", "Reader clock (yyyy/MM/dd HH:mm:ss). Use 'Sync clock to PC' to set it."),
            new("General", "TimeZone", "Reader time zone as an hour offset from UTC."),
            new("General", "TimeServer", "NTP server the reader syncs its clock from (needs network access to it)."),

            new("Antennas & RF", "AntennaSequence", "Antenna ports to cycle through, space separated (e.g. \"0 1 2 3\").", true, "0", "0 1", "0 1 2", "0 1 2 3"),
            new("Antennas & RF", "RFAttenuation", "Transmit power reduction applied to all antennas. 0 = full power; higher = less power / shorter range."),
            new("Antennas & RF", "RFModulation", "Gen2 RF mode (link profile) used to talk to tags."),
            new("Antennas & RF", "TagListAntennaCombine", "ON = one tag list entry per tag across antennas; OFF = separate entry per antenna.", true, "ON", "OFF"),

            new("Acquisition", "AcquireMode", "Inventory = full anti-collision for many tags; Global Scroll = fast single-tag reads.", true, "Inventory", "Global Scroll"),
            new("Acquisition", "PersistTime", "Seconds a tag stays in the tag list after it was last seen. -1 = forever, 0 = current read only."),
            new("Acquisition", "TagType", "Tag protocol bitmask the reader looks for (16 = EPC Class 1 Gen 2)."),
            new("Acquisition", "AcqG2Cycles", "Number of Gen2 inventory cycles per acquire."),
            new("Acquisition", "AcqG2Count", "Number of reads per inventory cycle."),
            new("Acquisition", "AcqG2Q", "Initial Gen2 Q value (2^Q slots). Raise it for larger tag populations."),
            new("Acquisition", "AcqG2Session", "Gen2 session. 0 = tags respond every round; 1-3 = tags stay quiet longer after being read.", true, "0", "1", "2", "3"),
            new("Acquisition", "AcqG2Target", "Gen2 inventoried flag to target.", true, "A", "B", "AB"),
            new("Acquisition", "AcqG2Select", "Number of Gen2 Select commands sent before each inventory."),
            new("Acquisition", "AcqG2Mask", "Only read tags matching this mask (bank, bit pointer, bit length, hex data). Empty = all tags."),
            new("Acquisition", "RSSIFilter", "Ignore reads outside this RSSI range (\"min max\")."),

            new("Tag List", "TagListFormat", "Format of the tag list returned to this app. The app parses Text and XML fully.", true, "Text", "Terse", "XML", "Custom"),
            new("Tag List", "TagListCustomFormat", "Field template used when TagListFormat = Custom (e.g. %k = EPC without spaces)."),
            new("Tag List", "TagListMillis", "Include milliseconds in tag timestamps.", true, "ON", "OFF"),

            new("Autonomous Mode", "AutoMode", "ON = reader reads continuously by itself; OFF = reads only when asked (e.g. by Get TagList).", true, "ON", "OFF"),
            new("Autonomous Mode", "AutoAction", "Action performed in the work state (usually Acquire).", true, "Acquire", "Report"),
            new("Autonomous Mode", "AutoStartTrigger", "External input change that starts reading (\"rising falling\" input masks; 0 0 = no trigger)."),
            new("Autonomous Mode", "AutoStopTrigger", "External input change that stops reading."),
            new("Autonomous Mode", "AutoStopTimer", "Milliseconds to read before stopping (-1 = never stop)."),
            new("Autonomous Mode", "AutoTruePause", "Milliseconds to wait after a cycle that found tags."),
            new("Autonomous Mode", "AutoFalsePause", "Milliseconds to wait after a cycle that found no tags."),

            new("Notification", "NotifyMode", "ON = reader pushes tag lists to NotifyAddress.", true, "ON", "OFF"),
            new("Notification", "NotifyAddress", "Where notifications go: host:port (TCP), serial, or e-mail address."),
            new("Notification", "NotifyFormat", "Format of notification messages.", true, "Text", "Terse", "XML", "Custom"),
            new("Notification", "NotifyTrigger", "When to notify.", true, "Add", "Remove", "Change", "True", "False", "TrueFalse"),
            new("Notification", "NotifyTime", "Also notify every N seconds (0 = off)."),

            new("Tag Stream", "TagStreamMode", "ON = reader streams every read to TagStreamAddress (UDP/TCP).", true, "ON", "OFF"),
            new("Tag Stream", "TagStreamAddress", "host:port the tag stream is sent to."),
            new("Tag Stream", "TagStreamFormat", "Format of streamed reads.", true, "Text", "Terse", "XML", "Custom"),

            new("Network (read-only)", "IPAddress", "Reader IP address.", false),
            new("Network (read-only)", "DHCP", "Whether the reader gets its IP from DHCP.", false),
            new("Network (read-only)", "Netmask", "Subnet mask.", false),
            new("Network (read-only)", "Gateway", "Default gateway.", false),
            new("Network (read-only)", "DNS", "DNS server.", false),
            new("Network (read-only)", "HostName", "Network host name.", false),
            new("Network (read-only)", "MACAddress", "Hardware address.", false),
            new("Network (read-only)", "CommandPort", "Telnet command port this app connects to.", false),
            new("Network (read-only)", "NetworkTimeout", "Seconds of inactivity before the reader drops a command connection.", false),
            new("Network (read-only)", "HeartbeatTime", "Seconds between discovery heartbeats.", false),
            new("Network (read-only)", "HeartbeatAddress", "Where heartbeats are broadcast.", false),
        ];
    }
}

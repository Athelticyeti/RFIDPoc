using System.Globalization;
using System.Net;
using System.Net.Sockets;
using KQ.Rfid.Alien;
using nsAlienRFID2;

namespace RFIDPoc
{
    public enum LiveMode
    {
        /// <summary>Reader sends every tag read the moment it happens (Tag Stream over TCP).</summary>
        Stream,

        /// <summary>Reader sends its whole tag list at the end of a read cycle when it changes (Notify, via the SDK).</summary>
        Notify,
    }

    /// <summary>
    /// Event-driven reading: puts the reader in autonomous mode and has it push data to a listener on this PC,
    /// instead of the app polling "Get TagList".
    /// </summary>
    public sealed class LiveReader : IDisposable
    {
        public const int NotifyPort = 3600;
        public const int StreamPort = 4000;

        private static readonly string[] ReaderTimeFormats = ["yyyy/MM/dd HH:mm:ss.fff", "yyyy/MM/dd HH:mm:ss"];

        private readonly ReaderConnection _connection;
        private CAlienServer? _notifyServer;
        private TagStreamListener? _streamListener;
        private ReaderLock? _lock;

        // The reader can queue notifications it couldn't deliver and replay them when a listener comes back
        // (NotifyRetryCount/NotifyQueueLimit), so notifications whose reads predate this session are dropped.
        private DateTime _sessionStart;
        private TimeSpan _clockOffset; // PC time minus reader time
        private int _staleCount;

        /// <summary>
        /// Raised on a background thread. In Stream mode each TagInfo is one read; in Notify mode the array is the
        /// reader's whole tag list and ReadCount is the reader's running count.
        /// </summary>
        public event Action<TagInfo[]>? TagsReceived;

        /// <summary>Raised on a background thread when stale (replayed) notifications are discarded.</summary>
        public event Action<int>? StaleDropped;

        /// <summary>Raised on a background thread for listener/parse problems.</summary>
        public event Action<string>? Error;

        public LiveReader(ReaderConnection connection, LiveMode mode)
        {
            _connection = connection;
            Mode = mode;
        }

        public LiveMode Mode { get; }

        public string? ListenAddress { get; private set; }

        /// <summary>Starts the listener, then points the reader at it and switches on autonomous mode.</summary>
        public async Task StartAsync()
        {
            var (readerIp, readerTime) = await _connection.RunAsync(r => (r.IPAddress.Trim(), r.DateTime));
            _lock = ReaderLock.TryAcquire(readerIp)
                    ?? throw new InvalidOperationException("Another app (e.g. the KQ baggage POC) is streaming from this reader. Stop it there first.");
            var hostIp = NetworkUtil.LocalAddressFacing(IPAddress.Parse(readerIp)).ToString();
            _sessionStart = DateTime.Now;
            _clockOffset = TryParseReaderTime(readerTime, out var t) ? _sessionStart - t : TimeSpan.Zero;
            _staleCount = 0;

            if (Mode == LiveMode.Stream)
            {
                _streamListener = new TagStreamListener(IPAddress.Parse(hostIp), StreamPort, "RFIDPoc");
                _streamListener.ReadsReceived += reads => TagsReceived?.Invoke(Array.ConvertAll(reads, ToTagInfo));
                _streamListener.Error += msg => Error?.Invoke(msg);
                _streamListener.Start();
                ListenAddress = $"{hostIp}:{StreamPort}";

                await _connection.RunAsync(reader =>
                {
                    reader.NotifyMode = "OFF";
                    reader.TagStreamFormat = "Custom";
                    reader.TagStreamCustomFormat = AlienTagStream.CustomFormat;
                    reader.TagStreamAddress = ListenAddress;
                    reader.TagStreamMode = "ON";
                    reader.AutoMode = "ON";
                });
            }
            else
            {
                _notifyServer = new CAlienServer(NotifyPort, hostIp);
                _notifyServer.ServerMessageReceived += OnNotification;
                _notifyServer.ServerSocketError += msg => Error?.Invoke($"Listener error: {msg}");
                _notifyServer.StartListening();
                ListenAddress = $"{hostIp}:{NotifyPort}";

                // Trigger "Change" = notify when a tag enters or leaves the reader's tag list.
                await _connection.RunAsync(reader =>
                {
                    reader.TagStreamMode = "OFF";
                    reader.NotifyAddress = ListenAddress;
                    reader.NotifyFormat = "XML";
                    reader.NotifyTrigger = "Change";
                    reader.NotifyMode = "ON";
                    reader.AutoMode = "ON";
                });
            }
        }

        /// <summary>Switches autonomous mode off (so the reader stops transmitting) and stops the listener.</summary>
        public async Task StopAsync()
        {
            try
            {
                if (_connection.IsConnected)
                    await _connection.RunAsync(StopReader);
            }
            finally
            {
                StopListeners();
            }
        }

        private void StopReader(clsReader reader)
        {
            reader.AutoMode = "OFF";
            if (Mode == LiveMode.Stream)
                reader.TagStreamMode = "OFF";
        }

        private void OnNotification(string message)
        {
            try
            {
                AlienUtils.ParseNotification(message, out var info);
                if (info?.TagList is not { Length: > 0 } list)
                    return;

                var tags = list.OfType<TagInfo>().ToArray();
                if (IsStale(tags))
                {
                    StaleDropped?.Invoke(Interlocked.Increment(ref _staleCount));
                    return;
                }
                TagsReceived?.Invoke(tags);
            }
            catch (Exception ex)
            {
                Error?.Invoke($"Could not parse notification: {ex.Message}");
            }
        }

        /// <summary>
        /// True when even the most recent read in the notification predates this session (allowing for the
        /// reader clock's 1 s resolution). If times can't be parsed, the notification is kept.
        /// </summary>
        private bool IsStale(TagInfo[] tags)
        {
            DateTime? newest = null;
            foreach (var tag in tags)
            {
                if (TryParseReaderTime(tag.LastSeenTime, out var seen) && (newest == null || seen > newest))
                    newest = seen;
            }
            return newest is { } n && n + _clockOffset < _sessionStart - TimeSpan.FromSeconds(2);
        }

        private static bool TryParseReaderTime(string? text, out DateTime value) =>
            DateTime.TryParseExact(text?.Trim(), ReaderTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);

        /// <summary>Shared-library read → the SDK type the rest of this app uses.</summary>
        private static TagInfo ToTagInfo(TagRead r) => new()
        {
            TagID = r.Epc, Antenna = r.Antenna, ReadCount = 1, RSSI = r.Rssi ?? 0, Speed = r.Speed ?? 0, Frequency = (float)(r.FrequencyMHz ?? 0),
        };

        private void StopListeners()
        {
            _lock?.Dispose();
            _lock = null;
            _streamListener?.Dispose();
            _streamListener = null;

            if (_notifyServer == null) return;
            try
            {
                _notifyServer.ServerMessageReceived -= OnNotification;
                _notifyServer.StopListening();
                _notifyServer.Dispose();
            }
            catch { }
            _notifyServer = null;
        }

        public void Dispose()
        {
            try
            {
                if (_connection.IsConnected)
                    StopReader(_connection.Reader);
            }
            catch { }
            StopListeners();
        }
    }
}

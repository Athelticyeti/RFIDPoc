using nsAlienRFID2;

namespace RFIDPoc
{
    /// <summary>
    /// Owns the SDK reader object. The SDK is synchronous and not thread-safe, so every call is
    /// serialized through one gate and run on a background thread.
    /// </summary>
    public sealed class ReaderConnection
    {
        private readonly SemaphoreSlim _gate = new(1, 1);

        public clsReader Reader { get; }

        public bool IsConnected => Reader.IsConnected;

        private ReaderConnection(clsReader reader) => Reader = reader;

        /// <summary>
        /// Connects over Telnet and logs in. Throws with a readable message on failure.
        /// </summary>
        public static ReaderConnection Open(string ip, int port, string user, string password)
        {
            var reader = new clsReader();
            reader.InitOnNetwork(ip, port);
            var result = reader.Connect();
            if (!reader.IsConnected)
                throw new InvalidOperationException($"Connect failed: {result}");
            if (!reader.Login(user, password))
            {
                try { reader.Disconnect(); } catch { }
                throw new InvalidOperationException("Login failed (check username/password).");
            }
            return new ReaderConnection(reader);
        }

        public async Task<T> RunAsync<T>(Func<clsReader, T> action)
        {
            await _gate.WaitAsync();
            try
            {
                return await Task.Run(() => action(Reader));
            }
            finally
            {
                _gate.Release();
            }
        }

        public Task RunAsync(Action<clsReader> action) => RunAsync(r => { action(r); return true; });
    }
}

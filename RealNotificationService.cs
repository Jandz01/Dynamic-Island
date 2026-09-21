using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;

namespace DynamicIsland
{
    public class RealNotification
    {
        public long Id { get; set; }
        public string AppName { get; set; } = "Windows";
        public string AppIcon { get; set; } = "🔔";
        public string AppColor { get; set; } = "#38BDF8";
        public string Sender { get; set; } = "";
        public string Message { get; set; } = "";
        public string Time { get; set; } = "Vừa xong";
        public string? ImagePath { get; set; }
        public string? PrimaryId { get; set; }
    }

    public class RealNotificationService : IDisposable
    {
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        private readonly string _dbPath;
        private readonly string _dbDir;
        private long _lastNotificationId = 0;
        private FileSystemWatcher? _watcher;
        private System.Threading.Timer? _pollTimer;
        private readonly object _lock = new();
        private bool _isDisposed = false;

        public Func<long, bool>? IsDeletedPredicate { get; set; }
        public event Action<RealNotification>? NotificationReceived;

        public RealNotificationService()
        {
            _dbPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Microsoft\Windows\Notifications\wpndatabase.db"
            );
            _dbDir = Path.GetDirectoryName(_dbPath) ?? "";
        }

        public void Start()
        {
            if (!File.Exists(_dbPath)) return;

            // Initialize _lastNotificationId to current maximum
            try
            {
                using var conn = CreateConnection();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT MAX(Id) FROM Notification WHERE Type = 'toast';";
                var res = cmd.ExecuteScalar();
                if (res != null && res != DBNull.Value)
                {
                    _lastNotificationId = Convert.ToInt64(res);
                }
            }
            catch { }

            // Watch for changes on the notifications folder (including WAL writes)
            try
            {
                if (Directory.Exists(_dbDir))
                {
                    _watcher = new FileSystemWatcher(_dbDir)
                    {
                        Filter = "*wpndatabase*",
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                        EnableRaisingEvents = true
                    };
                    _watcher.Changed += OnDatabaseFileChanged;
                    _watcher.Created += OnDatabaseFileChanged;
                }
            }
            catch { }

            // Polling timer: check every 1200ms for guaranteed detection
            _pollTimer = new System.Threading.Timer(PollCallback, null, 1200, 1200);
        }

        private void OnDatabaseFileChanged(object sender, FileSystemEventArgs e)
        {
            // Run quick check on background thread
            Task.Run(CheckNewNotifications);
        }

        private void PollCallback(object? state)
        {
            CheckNewNotifications();
        }

        private void CheckNewNotifications()
        {
            if (_isDisposed) return;
            lock (_lock)
            {
                try
                {
                    using var conn = CreateConnection();
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        SELECT n.Id, h.PrimaryId, n.Payload, n.ArrivalTime
                        FROM Notification n
                        LEFT JOIN NotificationHandler h ON n.HandlerId = h.RecordId
                        WHERE n.Type = 'toast' AND n.Id > @lastId
                        ORDER BY n.Id ASC;";
                    cmd.Parameters.AddWithValue("@lastId", _lastNotificationId);

                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        long id = reader.GetInt64(0);
                        string? primaryId = reader.IsDBNull(1) ? null : reader.GetString(1);
                        byte[]? payload = reader.IsDBNull(2) ? null : (byte[])reader[2];
                        long arrivalTime = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);

                        if (id > _lastNotificationId)
                        {
                            _lastNotificationId = id;
                        }

                        if (IsDeletedPredicate != null && IsDeletedPredicate(id))
                        {
                            continue; // Skip permanently deleted notification!
                        }

                        var notif = ParseFromPayload(id, primaryId, payload, arrivalTime);
                        if (notif != null)
                        {
                            NotificationReceived?.Invoke(notif);
                        }
                    }
                }
                catch { }
            }
        }

        public List<RealNotification> GetRecentNotifications(int count = 10)
        {
            var list = new List<RealNotification>();
            try
            {
                using var conn = CreateConnection();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT n.Id, h.PrimaryId, n.Payload, n.ArrivalTime
                    FROM Notification n
                    LEFT JOIN NotificationHandler h ON n.HandlerId = h.RecordId
                    WHERE n.Type = 'toast'
                    ORDER BY n.ArrivalTime DESC
                    LIMIT @count;";
                cmd.Parameters.AddWithValue("@count", count * 2);

                using var reader = cmd.ExecuteReader();
                while (reader.Read() && list.Count < count)
                {
                    long id = reader.GetInt64(0);
                    if (IsDeletedPredicate != null && IsDeletedPredicate(id))
                    {
                        continue; // Skip deleted
                    }

                    string? primaryId = reader.IsDBNull(1) ? null : reader.GetString(1);
                    byte[]? payload = reader.IsDBNull(2) ? null : (byte[])reader[2];
                    long arrivalTime = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);

                    var notif = ParseFromPayload(id, primaryId, payload, arrivalTime);
                    if (notif != null)
                    {
                        list.Add(notif);
                    }
                }
            }
            catch { }
            return list;
        }

        private SqliteConnection CreateConnection()
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared
            };
            return new SqliteConnection(builder.ConnectionString);
        }

        public static RealNotification? ParseFromPayload(long id, string? primaryId, byte[]? payloadBytes, long arrivalTime)
        {
            if (payloadBytes == null || payloadBytes.Length == 0) return null;
            string xmlStr = Encoding.UTF8.GetString(payloadBytes);
            try
            {
                var doc = XDocument.Parse(xmlStr);
                var texts = doc.Descendants("text")
                               .Select(t => t.Value?.Trim())
                               .Where(v => !string.IsNullOrEmpty(v))
                               .Select(v => v!)
                               .ToList();
                if (texts.Count == 0) return null;

                string pid = (primaryId ?? "").ToLowerInvariant();
                string xmlLower = xmlStr.ToLowerInvariant();

                string sender = texts[0]!;
                string message = texts.Count > 1 ? string.Join("\n", texts.Skip(1)) : "";

                // Accurate check for Zalo
                bool isZalo = pid.Contains("zalo") ||
                              xmlLower.Contains("zalo.me") ||
                              xmlLower.Contains("chat.zalo.me") ||
                              xmlLower.Contains("zalocdn") ||
                              sender.Contains("Zalo", StringComparison.OrdinalIgnoreCase);

                // Comprehensive check for Facebook (Web push, Messenger, Facebook App, etc.)
                bool isFacebook = !isZalo && (
                                  pid.Contains("facebook") ||
                                  pid.Contains("messenger") ||
                                  pid.Contains("meta") ||
                                  xmlLower.Contains("facebook.com") ||
                                  xmlLower.Contains("messenger.com") ||
                                  xmlLower.Contains("m.me") ||
                                  xmlLower.Contains("fbcdn.net") ||
                                  xmlLower.Contains("fbcdn") ||
                                  xmlLower.Contains("facebook") ||
                                  xmlLower.Contains("messenger") ||
                                  sender.Contains("Facebook", StringComparison.OrdinalIgnoreCase) ||
                                  sender.Contains("Messenger", StringComparison.OrdinalIgnoreCase) ||
                                  message.Contains("Facebook", StringComparison.OrdinalIgnoreCase));

                string appName = "Windows";
                string appIcon = "🔔";
                string appColor = "#38BDF8";

                if (isZalo)
                {
                    appName = "Zalo";
                    appIcon = "💬";
                    appColor = "#0068FF";

                    // Clean generic sender names
                    if ((sender.Equals("Zalo", StringComparison.OrdinalIgnoreCase) || sender.Contains("chat.zalo.me", StringComparison.OrdinalIgnoreCase)) && texts.Count > 1)
                    {
                        sender = texts[1];
                        message = texts.Count > 2 ? string.Join("\n", texts.Skip(2)) : "";
                    }
                }
                else if (isFacebook)
                {
                    appName = "Facebook";
                    appIcon = "📘";
                    appColor = "#1877F2";

                    // Extract actual person's name if first text is generic
                    if ((sender.Equals("Facebook", StringComparison.OrdinalIgnoreCase) ||
                         sender.Equals("Messenger", StringComparison.OrdinalIgnoreCase) ||
                         sender.Contains("facebook.com", StringComparison.OrdinalIgnoreCase)) && texts.Count > 1)
                    {
                        sender = texts[1];
                        message = texts.Count > 2 ? string.Join("\n", texts.Skip(2)) : "";
                    }
                    else if (sender.StartsWith("Facebook • ", StringComparison.OrdinalIgnoreCase))
                    {
                        sender = sender.Substring("Facebook • ".Length).Trim();
                    }
                }
                else if (pid.Contains("telegram") || xmlLower.Contains("telegram"))
                {
                    appName = "Telegram";
                    appIcon = "✈️";
                    appColor = "#229ED9";
                }
                else if (pid.Contains("teams"))
                {
                    appName = "Microsoft Teams";
                    appIcon = "👥";
                    appColor = "#6264A7";
                }
                else if (pid.Contains("discord"))
                {
                    appName = "Discord";
                    appIcon = "🎮";
                    appColor = "#5865F2";
                }
                else if (pid.Contains("chrome"))
                {
                    appName = "Google Chrome";
                    appIcon = "🌐";
                    appColor = "#EA4335";
                }
                else if (pid.Contains("edge"))
                {
                    appName = "Microsoft Edge";
                    appIcon = "🌐";
                    appColor = "#0284C7";
                }
                else if (pid.Contains("openai") || pid.Contains("codex"))
                {
                    appName = "OpenAI Codex";
                    appIcon = "🤖";
                    appColor = "#10A37F";
                }
                else if (!string.IsNullOrEmpty(primaryId))
                {
                    string clean = primaryId.Split('!')[0];
                    int under = clean.IndexOf('_');
                    if (under > 0) clean = clean.Substring(0, under);
                    int dot = clean.IndexOf('.');
                    if (dot > 0 && dot < clean.Length - 1) clean = clean.Substring(dot + 1);
                    appName = clean;
                }

                var imgElem = doc.Descendants("image").FirstOrDefault(i => i.Attribute("src") != null);
                string? imagePath = imgElem?.Attribute("src")?.Value;

                string timeStr = DateTime.Now.ToString("HH:mm");
                if (arrivalTime > 0)
                {
                    try
                    {
                        var dt = DateTime.FromFileTimeUtc(arrivalTime).ToLocalTime();
                        if (dt.Year >= 2020 && dt <= DateTime.Now.AddMinutes(5))
                        {
                            var span = DateTime.Now - dt;
                            if (span.TotalSeconds < 90) timeStr = "Vừa xong";
                            else if (span.TotalMinutes < 60) timeStr = $"{(int)span.TotalMinutes}p trước ({dt:HH:mm})";
                            else if (span.TotalHours < 24) timeStr = dt.ToString("HH:mm Hôm nay");
                            else timeStr = dt.ToString("HH:mm dd/MM");
                        }
                    }
                    catch { }
                }

                return new RealNotification
                {
                    Id = id,
                    AppName = appName,
                    AppIcon = appIcon,
                    AppColor = appColor,
                    Sender = sender,
                    Message = message,
                    Time = timeStr,
                    ImagePath = imagePath,
                    PrimaryId = primaryId
                };
            }
            catch
            {
                return null;
            }
        }

        public static IntPtr FocusApp(string? primaryId, string appName = "")
        {
            string pid = (primaryId ?? "").ToLowerInvariant();
            string lowerApp = appName.ToLowerInvariant();
            IntPtr targetHwnd = IntPtr.Zero;

            if (lowerApp.Contains("zalo") || pid.Contains("zalo"))
            {
                var procs = Process.GetProcessesByName("Zalo");
                foreach (var p in procs)
                {
                    if (p.MainWindowHandle != IntPtr.Zero)
                    {
                        ShowWindow(p.MainWindowHandle, 9);
                        SetForegroundWindow(p.MainWindowHandle);
                        return p.MainWindowHandle;
                    }
                }
            }
            else if (lowerApp.Contains("facebook") || lowerApp.Contains("messenger") || pid.Contains("facebook") || pid.Contains("messenger"))
            {
                var mProcs = Process.GetProcessesByName("Messenger");
                foreach (var p in mProcs)
                {
                    if (p.MainWindowHandle != IntPtr.Zero)
                    {
                        ShowWindow(p.MainWindowHandle, 9);
                        SetForegroundWindow(p.MainWindowHandle);
                        return p.MainWindowHandle;
                    }
                }
                var fbProcs = Process.GetProcessesByName("Facebook");
                foreach (var p in fbProcs)
                {
                    if (p.MainWindowHandle != IntPtr.Zero)
                    {
                        ShowWindow(p.MainWindowHandle, 9);
                        SetForegroundWindow(p.MainWindowHandle);
                        return p.MainWindowHandle;
                    }
                }
            }

            // Find running browser window (Chrome, Edge, Brave, etc.) with relevant title
            string searchKeyword = lowerApp.Contains("zalo") ? "Zalo" : "Facebook";
            EnumWindows((hWnd, lParam) =>
            {
                if (!IsWindowVisible(hWnd)) return true;
                int len = GetWindowTextLength(hWnd);
                if (len == 0) return true;
                var sb = new StringBuilder(len + 1);
                GetWindowText(hWnd, sb, sb.Capacity);
                string title = sb.ToString();

                if (title.Contains(searchKeyword, StringComparison.OrdinalIgnoreCase) ||
                    (searchKeyword == "Facebook" && title.Contains("Messenger", StringComparison.OrdinalIgnoreCase)))
                {
                    targetHwnd = hWnd;
                    ShowWindow(hWnd, 9);
                    SetForegroundWindow(hWnd);
                    return false;
                }
                return true;
            }, IntPtr.Zero);

            if (targetHwnd != IntPtr.Zero) return targetHwnd;

            // Fallback launch if app/web isn't open yet
            if (lowerApp.Contains("facebook") || pid.Contains("facebook"))
            {
                try
                {
                    Process.Start(new ProcessStartInfo("https://www.facebook.com/messages/") { UseShellExecute = true });
                }
                catch { }
            }
            else if (lowerApp.Contains("zalo") || pid.Contains("zalo"))
            {
                try
                {
                    Process.Start(new ProcessStartInfo("https://chat.zalo.me/") { UseShellExecute = true });
                }
                catch { }
            }

            return targetHwnd;
        }

        public void Dispose()
        {
            _isDisposed = true;
            _watcher?.Dispose();
            _pollTimer?.Dispose();
        }
    }
}

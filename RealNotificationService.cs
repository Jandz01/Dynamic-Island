using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

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

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hWnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        private const uint EVENT_OBJECT_SHOW = 0x8002;
        private const uint EVENT_OBJECT_NAMECHANGE = 0x800C;
        private const uint WINEVENT_OUTOFCONTEXT = 0;

        private IntPtr _zaloWinEventHook = IntPtr.Zero;
        private WinEventDelegate? _winEventProc;

        private readonly string _dbPath;
        private readonly string _dbDir;
        private long _lastNotificationId = 0;
        private FileSystemWatcher? _watcher;
        private System.Threading.Timer? _pollTimer;
        private readonly object _lock = new();
        private bool _isDisposed = false;

        private UserNotificationListener? _winRtListener;
        private readonly HashSet<string> _recentlyDispatchedFingerprints = new();
        private readonly Queue<(string hash, DateTime time)> _fingerprintExpiry = new();

        private FileSystemWatcher? _zaloDataWatcher;
        private System.Threading.Timer? _zaloPollTimer;

        public Func<long, bool>? IsDeletedPredicate { get; set; }
        public Func<RealNotification, bool>? IsNotificationDeletedPredicate { get; set; }
        public HashSet<string>? DeletedNotificationHashes { get; set; }
        public event Action<RealNotification>? NotificationReceived;

        public static string ComputeFingerprint(string appName, string sender, string message)
        {
            string raw = $"{appName?.Trim().ToLowerInvariant()}|{sender?.Trim().ToLowerInvariant()}|{message?.Trim()}";
            using var sha = System.Security.Cryptography.SHA256.Create();
            byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
            return Convert.ToHexString(bytes)[..16];
        }

        private bool TryRecordFingerprint(string hash)
        {
            var now = DateTime.UtcNow;
            while (_fingerprintExpiry.Count > 0 && (now - _fingerprintExpiry.Peek().time).TotalSeconds > 15)
            {
                var old = _fingerprintExpiry.Dequeue();
                _recentlyDispatchedFingerprints.Remove(old.hash);
            }

            if (_recentlyDispatchedFingerprints.Contains(hash))
            {
                return false;
            }

            _recentlyDispatchedFingerprints.Add(hash);
            _fingerprintExpiry.Enqueue((hash, now));
            return true;
        }

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
            // 1. Initialize modern Windows Runtime Toast Listener (Real-time OS events)
            Task.Run(InitWinRtListenerAsync);

            // 2. Initialize Windows wpndatabase SQLite watcher (Fallback / Historical persistence)
            if (File.Exists(_dbPath))
            {
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

                _pollTimer = new System.Threading.Timer(PollCallback, null, 1200, 1200);
            }

            // 3. Initialize Zalo Desktop App Watcher (Window Title & Database updates)
            StartZaloAppWatcher();

            // 4. Initialize Browser Watcher for Facebook & Messenger (Chrome, Edge, Brave, Opera, Firefox)
            StartBrowserAppWatcher();
        }

        private void StartZaloAppWatcher()
        {
            try
            {
                string zaloDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZaloData");
                if (Directory.Exists(zaloDataDir))
                {
                    _zaloDataWatcher = new FileSystemWatcher(zaloDataDir)
                    {
                        IncludeSubdirectories = true,
                        Filter = "*",
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                        EnableRaisingEvents = true
                    };
                    _zaloDataWatcher.Changed += (s, e) => Task.Run(CheckZaloNewMessages);
                    _zaloDataWatcher.Created += (s, e) => Task.Run(CheckZaloNewMessages);
                }
            }
            catch { }

            // Win32 WinEventHook for real-time window show & title changes
            try
            {
                _winEventProc = new WinEventDelegate((hHook, eventType, hWnd, idObject, idChild, thread, time) =>
                {
                    try
                    {
                        if (idObject != 0 || hWnd == IntPtr.Zero) return;
                        GetWindowThreadProcessId(hWnd, out uint pid);
                        var proc = Process.GetProcessById((int)pid);
                        if (proc.ProcessName.Contains("zalo", StringComparison.OrdinalIgnoreCase))
                        {
                            Task.Run(CheckZaloNewMessages);
                        }
                    }
                    catch { }
                });
                _zaloWinEventHook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_NAMECHANGE, IntPtr.Zero, _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT);
            }
            catch { }

            _zaloPollTimer = new System.Threading.Timer(_ => CheckZaloNewMessages(), null, 800, 600);
        }

        private void CheckZaloNewMessages()
        {
            if (_isDisposed) return;
            try
            {
                var zaloProcs = Process.GetProcessesByName("Zalo");
                if (zaloProcs.Length == 0)
                {
                    zaloProcs = Process.GetProcesses().Where(p => p.ProcessName.Contains("zalo", StringComparison.OrdinalIgnoreCase)).ToArray();
                }
                if (zaloProcs.Length == 0) return;

                var zaloPids = new HashSet<uint>(zaloProcs.Select(p => (uint)p.Id));

                EnumWindows((hWnd, lParam) =>
                {
                    try
                    {
                        GetWindowThreadProcessId(hWnd, out uint pid);
                        if (!zaloPids.Contains(pid)) return true;

                        int len = GetWindowTextLength(hWnd);
                        var sbTitle = new StringBuilder(len + 1);
                        if (len > 0)
                        {
                            GetWindowText(hWnd, sbTitle, sbTitle.Capacity);
                        }
                        string title = sbTitle.ToString().Trim();

                        GetWindowRect(hWnd, out RECT rect);
                        int width = rect.Right - rect.Left;
                        int height = rect.Bottom - rect.Top;

                        // Case 1: Zalo Notification Popup Window (Floating notification at bottom right)
                        if (width > 80 && width < 520 && height > 35 && height < 320 && IsWindowVisible(hWnd))
                        {
                            if (TryExtractFromZaloUI(hWnd, out string popSender, out string popMsg) && !string.IsNullOrWhiteSpace(popSender))
                            {
                                DispatchZaloNotification(popSender, popMsg);
                                return true;
                            }
                            else if (!string.IsNullOrEmpty(title) && !title.Equals("Zalo", StringComparison.OrdinalIgnoreCase))
                            {
                                DispatchZaloNotification(title, "Có tin nhắn mới trên Zalo");
                                return true;
                            }
                            else
                            {
                                DispatchZaloNotification("Zalo", "Bạn có tin nhắn mới trên Zalo");
                                return true;
                            }
                        }

                        // Case 2: Zalo Main Window with unread count or chat name
                        if (!string.IsNullOrEmpty(title))
                        {
                            string foundSender = "";
                            string foundMsg = "";

                            var m = System.Text.RegularExpressions.Regex.Match(title, @"^\((\d+\+?)\)\s*(?:Zalo\s*-\s*)?(.*)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                            if (m.Success)
                            {
                                string count = m.Groups[1].Value;
                                string rest = m.Groups[2].Value.Trim();

                                if (!string.IsNullOrEmpty(rest) && !rest.Equals("Zalo", StringComparison.OrdinalIgnoreCase))
                                {
                                    foundSender = rest.TrimStart('-', ' ').Trim();
                                }
                                else
                                {
                                    if (TryExtractFromZaloUI(hWnd, out string uiSender, out string uiMsg) && !string.IsNullOrWhiteSpace(uiSender))
                                    {
                                        foundSender = uiSender;
                                        foundMsg = uiMsg;
                                    }
                                    else
                                    {
                                        foundSender = "Zalo";
                                        foundMsg = $"Bạn có {count} tin nhắn mới trên Zalo";
                                    }
                                }
                            }
                            else if (title.Contains(" - ") && title.Contains("Zalo", StringComparison.OrdinalIgnoreCase))
                            {
                                foundSender = title.Replace("Zalo", "", StringComparison.OrdinalIgnoreCase).Replace("-", "").Trim();
                            }
                            else if (!title.Equals("Zalo", StringComparison.OrdinalIgnoreCase) && 
                                     !title.Equals("Chrome Legacy Window", StringComparison.OrdinalIgnoreCase) && 
                                     !title.Equals("Shared Worker", StringComparison.OrdinalIgnoreCase) &&
                                     title.Length > 1 && title.Length < 60)
                            {
                                foundSender = title;
                            }

                            if (!string.IsNullOrEmpty(foundSender))
                            {
                                if (string.IsNullOrEmpty(foundMsg))
                                {
                                    if (TryExtractFromZaloUI(hWnd, out _, out string extractedMsg) && !string.IsNullOrWhiteSpace(extractedMsg))
                                    {
                                        foundMsg = extractedMsg;
                                    }
                                    else
                                    {
                                        foundMsg = $"Có tin nhắn mới từ {foundSender}";
                                    }
                                }

                                DispatchZaloNotification(foundSender, foundMsg);
                            }
                        }
                    }
                    catch { }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
        }

        private static bool TryExtractFromZaloUI(IntPtr hWnd, out string sender, out string message)
        {
            sender = "";
            message = "";
            try
            {
                var el = AutomationElement.FromHandle(hWnd);
                if (el == null) return false;

                var textCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text);
                var textCollection = el.FindAll(TreeScope.Descendants, textCond);

                var list = new List<string>();
                foreach (AutomationElement item in textCollection)
                {
                    try
                    {
                        string name = item.Current.Name?.Trim() ?? "";
                        if (!string.IsNullOrEmpty(name) && !list.Contains(name) && name.Length < 300)
                        {
                            list.Add(name);
                        }
                    }
                    catch { }
                }

                var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Zalo", "Tìm kiếm", "Tin nhắn", "Danh bạ", "Thông báo", "Cài đặt",
                    "Đóng", "Thu nhỏ", "Phóng to", "Xem", "Gửi", "Trả lời", "Ẩn danh sách",
                    "Thêm bạn", "Tạo nhóm", "Dấu hiệu", "Đang kết nối", "Đã kết nối"
                };

                var filtered = list.Where(t => !ignored.Contains(t) && !t.StartsWith("http")).ToList();
                if (filtered.Count >= 2)
                {
                    sender = filtered[0];
                    message = filtered[1];
                    return true;
                }
                else if (filtered.Count == 1)
                {
                    sender = "Zalo";
                    message = filtered[0];
                    return true;
                }
            }
            catch { }
            return false;
        }

        private void DispatchZaloNotification(string sender, string message)
        {
            if (string.IsNullOrWhiteSpace(sender)) sender = "Zalo";
            if (string.IsNullOrWhiteSpace(message)) message = "Bạn có tin nhắn mới trên Zalo";

            var notif = new RealNotification
            {
                Id = DateTime.Now.Ticks,
                AppName = "Zalo",
                AppIcon = "💬",
                AppColor = "#0068FF",
                Sender = sender.Trim(),
                Message = message.Trim(),
                Time = "Vừa xong",
                PrimaryId = "com.vng.zalo"
            };

            string hash = ComputeFingerprint(notif.AppName, notif.Sender, notif.Message);
            if (DeletedNotificationHashes != null && DeletedNotificationHashes.Contains(hash)) return;

            lock (_lock)
            {
                if (!TryRecordFingerprint(hash)) return;
            }

            NotificationReceived?.Invoke(notif);
        }

        #region Browser Watcher for Facebook & Messenger
        private System.Threading.Timer? _browserPollTimer;
        private readonly Dictionary<IntPtr, string> _lastBrowserTitles = new();

        private void StartBrowserAppWatcher()
        {
            _browserPollTimer = new System.Threading.Timer(_ => CheckBrowserNewMessages(), null, 800, 600);
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        private const uint WM_KEYDOWN = 0x0100;
        private const uint WM_KEYUP = 0x0101;
        private const int VK_RETURN = 0x0D;

        public static bool TrySendSilentReply(string appName, string recipient, string replyText)
        {
            if (string.IsNullOrWhiteSpace(replyText)) return false;
            try
            {
                string lowerApp = (appName ?? "").ToLowerInvariant();

                // 1. Silent Zalo background reply
                if (lowerApp.Contains("zalo"))
                {
                    var procs = Process.GetProcessesByName("Zalo");
                    foreach (var p in procs)
                    {
                        if (p.MainWindowHandle == IntPtr.Zero) continue;
                        IntPtr hWnd = p.MainWindowHandle;
                        try
                        {
                            var el = AutomationElement.FromHandle(hWnd);
                            if (el != null)
                            {
                                var editCond = new OrCondition(
                                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document)
                                );
                                var inputs = el.FindAll(TreeScope.Descendants, editCond);
                                foreach (AutomationElement input in inputs)
                                {
                                    if (input.TryGetCurrentPattern(ValuePattern.Pattern, out object vpObj) && vpObj is ValuePattern vp)
                                    {
                                        vp.SetValue(replyText);
                                        PostMessage(hWnd, WM_KEYDOWN, (IntPtr)VK_RETURN, IntPtr.Zero);
                                        PostMessage(hWnd, WM_KEYUP, (IntPtr)VK_RETURN, IntPtr.Zero);
                                        return true;
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }
                // 2. Silent Messenger App background reply
                else if (lowerApp.Contains("messenger") || lowerApp.Contains("facebook"))
                {
                    var mProcs = Process.GetProcessesByName("Messenger");
                    foreach (var p in mProcs)
                    {
                        if (p.MainWindowHandle == IntPtr.Zero) continue;
                        IntPtr hWnd = p.MainWindowHandle;
                        try
                        {
                            var el = AutomationElement.FromHandle(hWnd);
                            if (el != null)
                            {
                                var editCond = new OrCondition(
                                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document)
                                );
                                var inputs = el.FindAll(TreeScope.Descendants, editCond);
                                foreach (AutomationElement input in inputs)
                                {
                                    if (input.TryGetCurrentPattern(ValuePattern.Pattern, out object vpObj) && vpObj is ValuePattern vp)
                                    {
                                        vp.SetValue(replyText);
                                        PostMessage(hWnd, WM_KEYDOWN, (IntPtr)VK_RETURN, IntPtr.Zero);
                                        PostMessage(hWnd, WM_KEYUP, (IntPtr)VK_RETURN, IntPtr.Zero);
                                        return true;
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return false;
        }

        private void CheckBrowserNewMessages()
        {
            if (_isDisposed) return;
            try
            {
                var browserNames = new[] { "chrome", "msedge", "brave", "firefox", "opera", "coccoc", "browser", "Messenger", "Facebook" };
                var browserPids = new HashSet<uint>();
                foreach (var name in browserNames)
                {
                    foreach (var p in Process.GetProcessesByName(name))
                    {
                        browserPids.Add((uint)p.Id);
                    }
                }

                if (browserPids.Count == 0) return;

                EnumWindows((hWnd, lParam) =>
                {
                    try
                    {
                        GetWindowThreadProcessId(hWnd, out uint pid);
                        if (!browserPids.Contains(pid)) return true;

                        int len = GetWindowTextLength(hWnd);
                        if (len <= 0) return true;

                        var sb = new StringBuilder(len + 1);
                        GetWindowText(hWnd, sb, sb.Capacity);
                        string title = sb.ToString().Trim();
                        if (string.IsNullOrEmpty(title)) return true;

                        if (_lastBrowserTitles.TryGetValue(hWnd, out var prev) && prev == title)
                        {
                            return true;
                        }
                        _lastBrowserTitles[hWnd] = title;

                        if (TryExtractFacebookFromTitle(title, out string sender, out string msg))
                        {
                            DispatchFacebookNotification(sender, msg);
                        }
                    }
                    catch { }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
        }

        public static bool TryExtractFacebookFromTitle(string title, out string sender, out string message)
        {
            sender = "Facebook";
            message = "";

            if (string.IsNullOrWhiteSpace(title)) return false;

            string lower = title.ToLowerInvariant();
            bool isFbRelated = lower.Contains("facebook") || lower.Contains("messenger") || lower.Contains("meta");
            if (!isFbRelated) return false;

            // Filter out user's own sent indicator
            if (lower.StartsWith("bạn:") || lower.StartsWith("tôi:") || lower.StartsWith("you:")) return false;

            // 1. Sent message pattern: "Nguyễn Văn A đã gửi một tin nhắn..."
            var mSent = System.Text.RegularExpressions.Regex.Match(title, @"^(.+?)\s*(?:đã gửi một tin nhắn|đã gửi tin nhắn|sent you a message|đã gửi một ảnh|đã gửi một nhãn dán|đã gửi một video|nhắn cho bạn|đã nhắc đến bạn)(.*?)(?:\s*[-|•]\s*(?:Google Chrome|Microsoft Edge|Brave|Cốc Cốc|Firefox|Opera|Facebook|Messenger))?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (mSent.Success)
            {
                string person = mSent.Groups[1].Value.Trim().TrimStart('(', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '+', ')', ' ');
                string rest = mSent.Groups[2].Value.Trim();
                if (!string.IsNullOrEmpty(person) && !person.Equals("Facebook", StringComparison.OrdinalIgnoreCase) && !person.Equals("Messenger", StringComparison.OrdinalIgnoreCase))
                {
                    sender = person;
                    message = !string.IsNullOrEmpty(rest) ? rest : $"Có tin nhắn mới từ {sender}";
                    return true;
                }
            }

            // 2. Unread count pattern: "(1) ..." or "(2) ..."
            var mDigits = System.Text.RegularExpressions.Regex.Match(title, @"^\((\d+\+?)\)\s*(.*)$");
            if (mDigits.Success)
            {
                string count = mDigits.Groups[1].Value;
                string rest = mDigits.Groups[2].Value.Trim();

                // Split by standard title delimiters: " - ", " | ", " • "
                var parts = rest.Split(new[] { " - ", " | ", " • " }, StringSplitOptions.RemoveEmptyEntries)
                                .Select(p => p.Trim())
                                .Where(p => !string.IsNullOrEmpty(p))
                                .ToList();

                var ignoredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Google Chrome", "Microsoft Edge", "Brave", "Cốc Cốc", "Firefox", "Opera",
                    "Facebook", "Messenger", "Meta", "Chat"
                };

                string foundPerson = "";
                foreach (var p in parts)
                {
                    if (!ignoredNames.Contains(p) && p.Length > 1 && p.Length < 60)
                    {
                        foundPerson = p;
                        break;
                    }
                }

                if (!string.IsNullOrEmpty(foundPerson))
                {
                    sender = foundPerson;
                    message = $"Có {count} tin nhắn mới từ {sender}";
                    return true;
                }
                else
                {
                    sender = rest.Contains("Messenger", StringComparison.OrdinalIgnoreCase) ? "Messenger" : "Facebook";
                    message = $"Bạn có {count} tin nhắn mới trên {sender}";
                    return true;
                }
            }

            return false;
        }

        private void DispatchFacebookNotification(string sender, string message)
        {
            if (string.IsNullOrWhiteSpace(sender)) sender = "Facebook";
            if (string.IsNullOrWhiteSpace(message)) message = "Bạn có tin nhắn mới trên Facebook";

            // Drop outgoing message from user
            if (sender.Equals("Bạn", StringComparison.OrdinalIgnoreCase) || 
                sender.Equals("Tôi", StringComparison.OrdinalIgnoreCase) || 
                sender.Equals("You", StringComparison.OrdinalIgnoreCase) ||
                sender.StartsWith("Bạn:", StringComparison.OrdinalIgnoreCase) ||
                sender.StartsWith("Tôi:", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var notif = new RealNotification
            {
                Id = DateTime.Now.Ticks,
                AppName = "Facebook",
                AppIcon = "📘",
                AppColor = "#1877F2",
                Sender = sender.Trim(),
                Message = message.Trim(),
                Time = "Vừa xong",
                PrimaryId = "com.facebook.web"
            };

            string hash = ComputeFingerprint(notif.AppName, notif.Sender, notif.Message);
            if (DeletedNotificationHashes != null && DeletedNotificationHashes.Contains(hash)) return;

            lock (_lock)
            {
                if (!TryRecordFingerprint(hash)) return;
            }

            NotificationReceived?.Invoke(notif);
        }
        #endregion

        private async Task InitWinRtListenerAsync()
        {
            try
            {
                _winRtListener = UserNotificationListener.Current;
                if (_winRtListener != null)
                {
                    var accessStatus = await _winRtListener.RequestAccessAsync();
                    if (accessStatus == UserNotificationListenerAccessStatus.Allowed)
                    {
                        _winRtListener.NotificationChanged += OnWinRtNotificationChanged;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"InitWinRtListenerAsync error: {ex.Message}");
            }
        }

        private void OnWinRtNotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args)
        {
            if (args.ChangeKind != UserNotificationChangedKind.Added) return;
            try
            {
                var notif = sender.GetNotification(args.UserNotificationId);
                if (notif == null) return;
                ProcessWinRtNotification(notif);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"OnWinRtNotificationChanged error: {ex.Message}");
            }
        }

        private void ProcessWinRtNotification(UserNotification notif)
        {
            try
            {
                string rawApp = notif.AppInfo?.DisplayInfo?.DisplayName ?? "";
                string rawAppId = notif.AppInfo?.Id ?? "";

                var textElements = new List<string>();
                if (notif.Notification?.Visual?.Bindings != null)
                {
                    foreach (var binding in notif.Notification.Visual.Bindings)
                    {
                        var texts = binding.GetTextElements();
                        if (texts != null)
                        {
                            foreach (var t in texts)
                            {
                                if (!string.IsNullOrWhiteSpace(t.Text))
                                {
                                    textElements.Add(t.Text.Trim());
                                }
                            }
                        }
                    }
                }

                if (textElements.Count == 0) return;

                string sender = textElements[0];
                string message = textElements.Count > 1 ? string.Join("\n", textElements.Skip(1)) : "";

                // Collect all indicators from rawApp, rawAppId, textElements, and image URIs
                var allIndicators = new List<string> { rawApp, rawAppId, sender, message };
                allIndicators.AddRange(textElements);
                string combined = string.Join(" ", allIndicators).ToLowerInvariant();

                // Drop outgoing messages sent by the user themselves!
                if (sender.Equals("Bạn", StringComparison.OrdinalIgnoreCase) || 
                    sender.Equals("Tôi", StringComparison.OrdinalIgnoreCase) || 
                    sender.Equals("You", StringComparison.OrdinalIgnoreCase) ||
                    sender.StartsWith("Bạn:", StringComparison.OrdinalIgnoreCase) ||
                    sender.StartsWith("Tôi:", StringComparison.OrdinalIgnoreCase))
                {
                    return; // NEVER show user's own sent messages on Dynamic Island!
                }

                bool isZalo = combined.Contains("zalo") || rawAppId.Contains("zalo");
                bool isFacebook = !isZalo && (
                    combined.Contains("facebook") || 
                    combined.Contains("messenger") || 
                    combined.Contains("fbcdn") || 
                    combined.Contains("m.me") || 
                    combined.Contains("meta") ||
                    rawAppId.Contains("facebook") || 
                    rawAppId.Contains("messenger")
                );

                string appName = "Windows";
                string appIcon = "🔔";
                string appColor = "#38BDF8";

                if (isZalo)
                {
                    appName = "Zalo";
                    appIcon = "💬";
                    appColor = "#0068FF";
                    if ((sender.Equals("Zalo", StringComparison.OrdinalIgnoreCase) || sender.Contains("chat.zalo.me", StringComparison.OrdinalIgnoreCase)) && textElements.Count > 1)
                    {
                        sender = textElements[1];
                        message = textElements.Count > 2 ? string.Join("\n", textElements.Skip(2)) : "";
                    }
                    if (string.IsNullOrWhiteSpace(message))
                    {
                        int colonIdx = sender.IndexOf(':');
                        if (colonIdx > 0 && colonIdx < sender.Length - 1)
                        {
                            message = sender.Substring(colonIdx + 1).Trim();
                            sender = sender.Substring(0, colonIdx).Trim();
                        }
                        else
                        {
                            message = "Có tin nhắn mới trên Zalo";
                        }
                    }
                }
                else if (isFacebook)
                {
                    appName = "Facebook";
                    appIcon = "📘";
                    appColor = "#1877F2";
                    if ((sender.Equals("Facebook", StringComparison.OrdinalIgnoreCase) ||
                         sender.Equals("Messenger", StringComparison.OrdinalIgnoreCase) ||
                         sender.Contains("facebook.com", StringComparison.OrdinalIgnoreCase) ||
                         sender.Contains("messenger.com", StringComparison.OrdinalIgnoreCase)) && textElements.Count > 1)
                    {
                        sender = textElements[1];
                        message = textElements.Count > 2 ? string.Join("\n", textElements.Skip(2)) : "";
                    }
                    else if (sender.StartsWith("Facebook • ", StringComparison.OrdinalIgnoreCase))
                    {
                        sender = sender.Substring("Facebook • ".Length).Trim();
                    }
                    else if (sender.StartsWith("Messenger • ", StringComparison.OrdinalIgnoreCase))
                    {
                        sender = sender.Substring("Messenger • ".Length).Trim();
                    }

                    if (string.IsNullOrWhiteSpace(message))
                    {
                        int colonIdx = sender.IndexOf(':');
                        if (colonIdx > 0 && colonIdx < sender.Length - 1)
                        {
                            message = sender.Substring(colonIdx + 1).Trim();
                            sender = sender.Substring(0, colonIdx).Trim();
                        }
                        else
                        {
                            message = "Có tin nhắn mới trên Facebook";
                        }
                    }

                    // Clean web push domain footers from message body
                    var cleanLines = message.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                           .Where(l => !l.Trim().Equals("facebook.com", StringComparison.OrdinalIgnoreCase) &&
                                                       !l.Trim().Equals("www.facebook.com", StringComparison.OrdinalIgnoreCase) &&
                                                       !l.Trim().Equals("messenger.com", StringComparison.OrdinalIgnoreCase))
                                           .ToList();
                    message = cleanLines.Count > 0 ? string.Join("\n", cleanLines) : "Có tin nhắn mới trên Facebook";
                }
                else if (!string.IsNullOrWhiteSpace(rawApp))
                {
                    appName = rawApp;
                    if (appName.Contains("Chrome", StringComparison.OrdinalIgnoreCase))
                    {
                        appName = "Google Chrome";
                        appIcon = "🌐";
                        appColor = "#EA4335";
                    }
                    else if (appName.Contains("Edge", StringComparison.OrdinalIgnoreCase))
                    {
                        appName = "Microsoft Edge";
                        appIcon = "🌐";
                        appColor = "#0284C7";
                    }
                    else if (appName.Contains("Telegram", StringComparison.OrdinalIgnoreCase))
                    {
                        appName = "Telegram";
                        appIcon = "✈️";
                        appColor = "#229ED9";
                    }
                    else if (appName.Contains("Discord", StringComparison.OrdinalIgnoreCase))
                    {
                        appName = "Discord";
                        appIcon = "🎮";
                        appColor = "#5865F2";
                    }
                }

                long id = notif.Id;
                if (IsDeletedPredicate != null && IsDeletedPredicate(id)) return;

                string hash = ComputeFingerprint(appName, sender, message);
                if (DeletedNotificationHashes != null && DeletedNotificationHashes.Contains(hash)) return;

                lock (_lock)
                {
                    if (!TryRecordFingerprint(hash)) return;
                }

                var realNotif = new RealNotification
                {
                    Id = id,
                    AppName = appName,
                    AppIcon = appIcon,
                    AppColor = appColor,
                    Sender = sender,
                    Message = message,
                    Time = "Vừa xong",
                    PrimaryId = rawAppId
                };

                if (IsNotificationDeletedPredicate != null && IsNotificationDeletedPredicate(realNotif)) return;

                NotificationReceived?.Invoke(realNotif);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ProcessWinRtNotification inner error: {ex.Message}");
            }
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
                        WHERE (n.Type = 'toast' OR n.Type IS NULL OR n.Type = '') AND n.Id > @lastId AND n.Payload IS NOT NULL
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
                            if (IsNotificationDeletedPredicate != null && IsNotificationDeletedPredicate(notif))
                            {
                                continue;
                            }
                            string hash = ComputeFingerprint(notif.AppName, notif.Sender, notif.Message);
                            if (DeletedNotificationHashes != null && DeletedNotificationHashes.Contains(hash))
                            {
                                continue;
                            }
                            if (!TryRecordFingerprint(hash))
                            {
                                continue;
                            }
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
                    WHERE (n.Type = 'toast' OR n.Type IS NULL OR n.Type = '') AND n.Payload IS NOT NULL
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
                        if (IsNotificationDeletedPredicate != null && IsNotificationDeletedPredicate(notif))
                        {
                            continue;
                        }
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
                Cache = SqliteCacheMode.Shared,
                DefaultTimeout = 5
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

                // Drop outgoing messages sent by the user themselves!
                if (sender.Equals("Bạn", StringComparison.OrdinalIgnoreCase) || 
                    sender.Equals("Tôi", StringComparison.OrdinalIgnoreCase) || 
                    sender.Equals("You", StringComparison.OrdinalIgnoreCase) ||
                    sender.StartsWith("Bạn:", StringComparison.OrdinalIgnoreCase) ||
                    sender.StartsWith("Tôi:", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

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
                    if (string.IsNullOrWhiteSpace(message))
                    {
                        int colonIdx = sender.IndexOf(':');
                        if (colonIdx > 0 && colonIdx < sender.Length - 1)
                        {
                            message = sender.Substring(colonIdx + 1).Trim();
                            sender = sender.Substring(0, colonIdx).Trim();
                        }
                        else
                        {
                            message = "Có tin nhắn mới trên Zalo";
                        }
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

                    if (string.IsNullOrWhiteSpace(message))
                    {
                        int colonIdx = sender.IndexOf(':');
                        if (colonIdx > 0 && colonIdx < sender.Length - 1)
                        {
                            message = sender.Substring(colonIdx + 1).Trim();
                            sender = sender.Substring(0, colonIdx).Trim();
                        }
                        else
                        {
                            message = "Có tin nhắn mới trên Facebook";
                        }
                    }

                    // Remove Chrome domain footers if present
                    if (message.EndsWith("\nfacebook.com", StringComparison.OrdinalIgnoreCase))
                    {
                        message = message.Substring(0, message.Length - "\nfacebook.com".Length).Trim();
                    }
                    else if (message.EndsWith("\nwww.facebook.com", StringComparison.OrdinalIgnoreCase))
                    {
                        message = message.Substring(0, message.Length - "\nwww.facebook.com".Length).Trim();
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

            return targetHwnd;
        }

        public void Dispose()
        {
            _isDisposed = true;
            if (_winRtListener != null)
            {
                try { _winRtListener.NotificationChanged -= OnWinRtNotificationChanged; } catch { }
            }
            if (_zaloWinEventHook != IntPtr.Zero)
            {
                try { UnhookWinEvent(_zaloWinEventHook); } catch { }
                _zaloWinEventHook = IntPtr.Zero;
            }
            _zaloDataWatcher?.Dispose();
            _zaloPollTimer?.Dispose();
            _browserPollTimer?.Dispose();
            _watcher?.Dispose();
            _pollTimer?.Dispose();
        }
    }
}

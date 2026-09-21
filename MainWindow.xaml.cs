using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;

namespace DynamicIsland
{
    public enum IslandState
    {
        Mini,
        Compact,
        Orbital,
        Music,
        Pomodoro,
        Camera,
        Notification,
        Dropzone,
        LockScreen,
        Update
    }

    public partial class MainWindow : Window
    {
        #region Win32 Native Hardware APIs
        [DllImport("user32.dll")]
        private static extern bool LockWorkStation();

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public int BatteryLifeTime;
            public int BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll")]
        private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private const int SW_RESTORE = 9;
        private const byte VK_CONTROL = 0x11;
        private const byte VK_V = 0x56;
        private const byte VK_RETURN = 0x0D;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        #endregion

        // State & Timers
        private IslandState _currentState = IslandState.Compact;
        private readonly DispatcherTimer _clockTimer = new();
        private readonly DispatcherTimer _pomoTimer = new();
        private readonly DispatcherTimer _mediaTrackTimer = new();

        // Orbital Physics & Hover Freeze (VSync Hardware Accelerated via CompositionTarget.Rendering)
        private double _orbitAngle = 0;
        private double _bloomProgress = 1.0;
        private bool _isBlooming = false;
        private bool _isOrbitPaused = false;
        private Border? _hoveredOrb = null;
        private readonly Stopwatch _orbitStopwatch = Stopwatch.StartNew();
        private double _lastOrbitTime = 0;
        private const double AngularSpeed = 0.55; // Radians per second (~11.4s per full orbit)
        private DateTime _bloomStartTime;
        private const double BloomDurationMs = 450.0;

        // Real Windows Notification Service (wpndatabase.db)
        private readonly RealNotificationService _realNotificationService = new();
        private List<DynamicNotification> _realNotificationsList = new();
        private DynamicNotification? _currentNotification = null;

        // Viscous Liquid Rubber-Band Dragging
        private bool _isDraggingLiquid = false;
        private Point _dragStartPoint;
        private DateTime _preventPullDownUntil = DateTime.MinValue;

        // Windows Media Controls
        private GlobalSystemMediaTransportControlsSessionManager? _mediaManager;
        private GlobalSystemMediaTransportControlsSession? _currentSession;
        private bool _isPlaying = false;
        private bool _isUserSeeking = false;
        private TimeSpan _currentTrackPosition = TimeSpan.Zero;
        private TimeSpan _currentTrackDuration = TimeSpan.Zero;
        private DateTime _lastPositionUpdateTime = DateTime.UtcNow;
        private DoubleAnimation? _vinylRotateAnim;

        // Pomodoro Timer
        private int _pomoRemaining = 25 * 60;
        private int _pomoTotal = 25 * 60;
        private bool _pomoIsRunning = false;

        // Dropzone & Sources
        private enum SourceType { None, File, Link }
        private SourceType _currentSourceType = SourceType.None;
        private string? _currentFilePath = null;
        private string? _currentSourceUrl = null;
        private string _notebookName = "";
        private string _notebookUrl = "";
        private bool _isNotebookVerified = false;

        // Persistent Deleted Notifications (both ID and Content Fingerprint)
        private readonly string _deletedNotifsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DynamicIsland", "deleted_notifs.txt"
        );
        private readonly string _deletedNotifHashesPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DynamicIsland", "deleted_notif_hashes.txt"
        );
        private readonly HashSet<long> _deletedNotificationIds = new();
        private readonly HashSet<string> _deletedNotificationHashes = new(StringComparer.OrdinalIgnoreCase);

        public MainWindow()
        {
            InitializeComponent();
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Position at top center of primary screen
            double screenWidth = SystemParameters.PrimaryScreenWidth;
            Left = (screenWidth - Width) / 2;
            Top = 0;

            // Setup vinyl rotation animation
            _vinylRotateAnim = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(3.5))
            {
                RepeatBehavior = RepeatBehavior.Forever
            };

            // Setup recording dot pulsing animation
            var pulseAnim = new DoubleAnimation(0.2, 0.9, TimeSpan.FromMilliseconds(700))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            DotRecordPulse.BeginAnimation(OpacityProperty, pulseAnim);

            // Hardware & Clock Timer (1s)
            _clockTimer.Interval = TimeSpan.FromSeconds(1);
            _clockTimer.Tick += ClockTimer_Tick;
            _clockTimer.Start();
            ClockTimer_Tick(null, EventArgs.Empty);

            // Keep notch geometry 100% in sync with IslandCard width and height on every animation frame
            IslandCard.SizeChanged += (s, e) =>
            {
                if (!_isDraggingLiquid && e.NewSize.Width > 20 && e.NewSize.Height > 20)
                {
                    UpdateNotchGeometry(e.NewSize.Width, e.NewSize.Height, 0, 0);
                }
            };

            // Orbit Rotation (VSync hardware accelerated via CompositionTarget.Rendering)
            CompositionTarget.Rendering += CompositionTarget_Rendering;

            // Pomodoro Timer (1s)
            _pomoTimer.Interval = TimeSpan.FromSeconds(1);
            _pomoTimer.Tick += PomoTimer_Tick;

            // Media Position Tracker (250ms for smooth real-time progress)
            _mediaTrackTimer.Interval = TimeSpan.FromMilliseconds(250);
            _mediaTrackTimer.Tick += MediaTrackTimer_Tick;
            _mediaTrackTimer.Start();

            // Initialize Windows Media
            await InitMediaManagerAsync();

            // Load permanently deleted notification IDs & fingerprints
            LoadDeletedNotifications();

            // Initialize Real Windows Notification Service
            _realNotificationService.IsDeletedPredicate = id => _deletedNotificationIds.Contains(id);
            _realNotificationService.IsNotificationDeletedPredicate = r =>
                _deletedNotificationHashes.Contains(RealNotificationService.ComputeFingerprint(r.AppName, r.Sender, r.Message));
            _realNotificationService.DeletedNotificationHashes = _deletedNotificationHashes;
            _realNotificationService.NotificationReceived += OnRealNotificationReceived;
            _realNotificationService.Start();

            // Start Local HTTP Webhook Server for Zalo & Facebook messages (Port 5005)
            StartLocalMessageApiServer();

            // Pre-load recent real notifications from user's system (excluding any deleted ones)
            var recent = _realNotificationService.GetRecentNotifications(10);
            if (recent.Count > 0)
            {
                _realNotificationsList = recent.Select(r => new DynamicNotification
                {
                    Id = r.Id,
                    AppName = r.AppName,
                    AppIcon = r.AppIcon,
                    AppColor = r.AppColor,
                    Sender = r.Sender,
                    Message = r.Message,
                    Time = r.Time,
                    PrimaryId = r.PrimaryId
                }).ToList();
            }

            // Initialize NotebookLM configuration
            LoadNotebookLmConfig();

            // Initial view: Compact Notch (580px for spacious layout without overlap)
            ApplyState(IslandState.Compact, animate: false);
            UpdateNotchGeometry(580, 38, 0, 0);

            // Update Notification UI with loaded notifications
            UpdateNotificationUI();

            // Check if freshly updated
            string[] launchArgs = Environment.GetCommandLineArgs();
            for (int i = 0; i < launchArgs.Length; i++)
            {
                if (launchArgs[i] == "--updated")
                {
                    string ver = (i + 1 < launchArgs.Length) ? launchArgs[i + 1] : UpdateService.CurrentVersionString;
                    _ = Dispatcher.InvokeAsync(async () =>
                    {
                        await Task.Delay(1200);
                        ShowModernToast($"Đã cập nhật thành công lên {ver}! 🎉", "🚀", "#10B981");
                    });
                    break;
                }
            }

            // Check for updates in background after startup
            _ = CheckForUpdateInBackgroundAsync();
        }

        // Horizontal offset from screen center
        private double _horizontalOffset = 0;

        public void SetHorizontalOffset(double offset)
        {
            double screenWidth = SystemParameters.PrimaryScreenWidth;
            double maxOffset = (screenWidth - IslandCard.Width) / 2 - 20;
            _horizontalOffset = Math.Clamp(offset, -maxOffset, maxOffset);

            double desiredLeft = (screenWidth - Width) / 2 + _horizontalOffset;

            var moveAnim = new DoubleAnimation(Left, desiredLeft, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            BeginAnimation(LeftProperty, moveAnim);
        }

        // Lock window position (respecting user horizontal offset)
        protected override void OnLocationChanged(EventArgs e)
        {
            base.OnLocationChanged(e);
            double screenWidth = SystemParameters.PrimaryScreenWidth;
            double desiredLeft = (screenWidth - Width) / 2 + _horizontalOffset;
            if (Math.Abs(Left - desiredLeft) > 0.5 || Math.Abs(Top) > 0.5)
            {
                Left = desiredLeft;
                Top = 0;
            }
        }

        // Keyboard arrow navigation (Left, Right to move, Down / Home to center)
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                if (PopupNotebookConfig != null && PopupNotebookConfig.Visibility == Visibility.Visible)
                {
                    CloseNotebookConfigModal();
                    e.Handled = true;
                    return;
                }
            }

            if (e.OriginalSource is TextBox) return;

            if (e.Key == Key.Left)
            {
                SetHorizontalOffset(_horizontalOffset - 40);
                e.Handled = true;
            }
            else if (e.Key == Key.Right)
            {
                SetHorizontalOffset(_horizontalOffset + 40);
                e.Handled = true;
            }
            else if (e.Key == Key.Down || e.Key == Key.Home)
            {
                SetHorizontalOffset(0); // Center position
                e.Handled = true;
            }
        }

        #region Authentic Notch Geometry with Flared Ear Brackets
        private void UpdateNotchGeometry(double width, double height, double sagX, double sagY)
        {
            // E = ear width flare
            double E = 22;
            double R_ear = 14;
            double R_bot = 18;

            var figure = new PathFigure { StartPoint = new Point(0, 0), IsClosed = true };

            if (Math.Abs(sagY) < 1)
            {
                // Normal resting notch shape with flared ears
                figure.Segments.Add(new BezierSegment(new Point(E * 0.5, 0), new Point(E, R_ear * 0.3), new Point(E, R_ear), true));
                figure.Segments.Add(new LineSegment(new Point(E, height - R_bot), true));
                figure.Segments.Add(new BezierSegment(new Point(E, height), new Point(E + R_bot * 0.4, height), new Point(E + R_bot, height), true));
                figure.Segments.Add(new LineSegment(new Point(E + width - R_bot, height), true));
                figure.Segments.Add(new BezierSegment(new Point(E + width - R_bot * 0.4, height), new Point(E + width, height), new Point(E + width, height - R_bot), true));
                figure.Segments.Add(new LineSegment(new Point(E + width, R_ear), true));
                figure.Segments.Add(new BezierSegment(new Point(E + width, R_ear * 0.3), new Point(E + width + E * 0.5, 0), new Point(E + width + E, 0), true));
            }
            else
            {
                // Deformed liquid notch: The notch bar and corners stay 100% horizontal and level!
                // Only a localized liquid droop forms under sagX, mimicking viscous mercury/honey.
                double dipWidth = 65.0; // localized pull span
                double leftCornerEnd = E + R_bot;
                double rightCornerStart = E + width - R_bot;

                double dipLeft = Math.Clamp(sagX - dipWidth, leftCornerEnd + 8, rightCornerStart - 20);
                double dipRight = Math.Clamp(sagX + dipWidth, dipLeft + 20, rightCornerStart - 8);
                double actualTipX = (dipLeft + dipRight) / 2.0;
                double dipY = height + sagY;

                // Left ear flare & left vertical side
                figure.Segments.Add(new BezierSegment(new Point(E * 0.5, 0), new Point(E, R_ear * 0.3), new Point(E, R_ear), true));
                figure.Segments.Add(new LineSegment(new Point(E, height - R_bot), true));
                
                // Left bottom corner (completely flat, standard radius - NEVER slants!)
                figure.Segments.Add(new BezierSegment(new Point(E, height), new Point(leftCornerEnd - R_bot * 0.6, height), new Point(leftCornerEnd, height), true));
                
                // Horizontal flat bottom line leading up to the localized fluid dip
                if (dipLeft > leftCornerEnd)
                {
                    figure.Segments.Add(new LineSegment(new Point(dipLeft, height), true));
                }

                // Fluid meniscus catenary dip entering droplet tip
                double c1X = dipLeft + (actualTipX - dipLeft) * 0.45;
                double c2X = actualTipX - 16;
                figure.Segments.Add(new BezierSegment(new Point(c1X, height), new Point(c2X, dipY), new Point(actualTipX - 10, dipY + 2), true));

                // Organic rounded bulbous droplet tip at actualTipX
                figure.Segments.Add(new BezierSegment(new Point(actualTipX, dipY + 5), new Point(actualTipX, dipY + 5), new Point(actualTipX + 10, dipY + 2), true));

                // Fluid meniscus catenary dip returning to horizontal bottom edge
                double c3X = actualTipX + 16;
                double c4X = dipRight - (dipRight - actualTipX) * 0.45;
                figure.Segments.Add(new BezierSegment(new Point(c3X, dipY), new Point(c4X, height), new Point(dipRight, height), true));

                // Horizontal flat bottom line continuing to the right corner
                if (rightCornerStart > dipRight)
                {
                    figure.Segments.Add(new LineSegment(new Point(rightCornerStart, height), true));
                }

                // Right bottom corner (completely flat, standard radius - NEVER slants!)
                figure.Segments.Add(new BezierSegment(new Point(rightCornerStart + R_bot * 0.4, height), new Point(E + width, height), new Point(E + width, height - R_bot), true));
                
                // Right vertical side & right ear flare
                figure.Segments.Add(new LineSegment(new Point(E + width, R_ear), true));
                figure.Segments.Add(new BezierSegment(new Point(E + width, R_ear * 0.3), new Point(E + width + E * 0.5, 0), new Point(E + width + E, 0), true));
            }

            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            NotchPath.Data = geometry;
        }
        #endregion

        #region Viscous Liquid Rubber-Band Dragging
        private double _initialNotchHeight = 38;
        private double _currentSagX = 292;
        private double _currentSagY = 0;
        private double _currentExtraHeight = 0;

        private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
        {
            while (child != null)
            {
                if (child is T parent) return parent;
                child = VisualTreeHelper.GetParent(child);
            }
            return null;
        }

        #region Drag Grip Handle (Dấu :: Kéo Di Chuyển Dynamic Island)
        private bool _isDraggingHandle = false;
        private Point _dragStartScreenPos;
        private double _dragStartOffset;

        private void DragHandle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                SetHorizontalOffset(0);
                ShowModernToast("Đã căn giữa Dynamic Island!", "⦿", "#38BDF8");
                e.Handled = true;
                return;
            }

            _isDraggingHandle = true;
            _dragStartScreenPos = PointToScreen(e.GetPosition(this));
            _dragStartOffset = _horizontalOffset;
            ((UIElement)sender).CaptureMouse();
            e.Handled = true;
        }

        private void DragHandle_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_isDraggingHandle)
            {
                Point cur = PointToScreen(e.GetPosition(this));
                double deltaX = cur.X - _dragStartScreenPos.X;
                SetHorizontalOffset(_dragStartOffset + deltaX);
                e.Handled = true;
            }
        }

        private void DragHandle_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDraggingHandle)
            {
                _isDraggingHandle = false;
                ((UIElement)sender).ReleaseMouseCapture();
                e.Handled = true;
            }
        }
        #endregion

        #region Screen Capture, Recording & Custom Save Folder
        private static readonly string CaptureConfigFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DynamicIsland",
            "capture_folder.txt"
        );

        private string GetCaptureFolder()
        {
            try
            {
                if (File.Exists(CaptureConfigFile))
                {
                    string path = File.ReadAllText(CaptureConfigFile).Trim();
                    if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                    {
                        return path;
                    }
                }
            }
            catch { }

            string def = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                "DynamicIsland_Captures"
            );
            try { Directory.CreateDirectory(def); } catch { }
            return def;
        }

        private void SetCaptureFolder(string folderPath)
        {
            try
            {
                Directory.CreateDirectory(folderPath);
                string dir = Path.GetDirectoryName(CaptureConfigFile)!;
                Directory.CreateDirectory(dir);
                File.WriteAllText(CaptureConfigFile, folderPath);
                UpdateCaptureFolderUi(folderPath);
                ShowModernToast($"Đã đổi thư mục lưu:\n{folderPath}", "📁", "#10B981");
            }
            catch (Exception ex)
            {
                ShowModernToast("Không thể lưu cấu hình thư mục: " + ex.Message, "⚠️", "#EF4444");
            }
        }

        private void UpdateCaptureFolderUi(string? path = null)
        {
            Dispatcher.Invoke(() =>
            {
                string p = path ?? GetCaptureFolder();
                if (TxtCaptureFolder != null)
                {
                    TxtCaptureFolder.Text = p;
                    TxtCaptureFolder.ToolTip = $"Thư mục lưu ảnh và video:\n{p}";
                }
            });
        }

        private void BtnChangeCaptureFolder_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            try
            {
                var dialog = new Microsoft.Win32.OpenFolderDialog
                {
                    Title = "Chọn thư mục lưu ảnh chụp màn hình và video quay màn hình",
                    InitialDirectory = GetCaptureFolder(),
                    Multiselect = false
                };

                if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
                {
                    SetCaptureFolder(dialog.FolderName);
                }
            }
            catch (Exception ex)
            {
                ShowModernToast("Lỗi mở chọn thư mục: " + ex.Message, "⚠️", "#EF4444");
            }
        }

        private void BtnOpenCaptureFolder_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            try
            {
                string folder = GetCaptureFolder();
                Directory.CreateDirectory(folder);
                Process.Start(new ProcessStartInfo("explorer.exe", folder) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                ShowModernToast("Không thể mở thư mục: " + ex.Message, "⚠️", "#EF4444");
            }
        }

        private void BtnRecordVideo_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            try
            {
                // Kích hoạt Snipping Tool quay video hoặc Game Bar Screen Recording
                try
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", "ms-gamebar:") { UseShellExecute = true });
                }
                catch
                {
                    Process.Start(new ProcessStartInfo("ms-screenclip:") { UseShellExecute = true });
                }
                ShowModernToast("Đang mở công cụ quay màn hình Windows...", "🎥", "#8B5CF6");
            }
            catch (Exception ex)
            {
                ShowModernToast("Không thể mở công cụ quay: " + ex.Message, "⚠️", "#EF4444");
            }
        }

        private async void BtnRecordScreen_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            try
            {
                double prevOpacity = this.Opacity;

                // 1. Tự động ẩn Dynamic Island để không bị vướng / lọt vào ảnh chụp
                this.Opacity = 0;
                this.Visibility = Visibility.Hidden;

                // Nghỉ 200ms để DWM kịp vẽ lại màn hình sạch sẽ
                await Task.Delay(200);

                // 2. Kích hoạt công cụ chụp / quay màn hình Windows
                try
                {
                    Process.Start(new ProcessStartInfo("ms-screenclip:") { UseShellExecute = true });
                }
                catch
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo("explorer.exe", "ms-gamebar:") { UseShellExecute = true });
                    }
                    catch { }
                }

                // 3. Đợi người dùng khoanh vùng chụp xong (kiểm tra clipboard có ảnh mới hoặc chờ tối đa 6.5s)
                bool captured = false;
                await Task.Delay(1200);

                for (int i = 0; i < 22; i++)
                {
                    await Task.Delay(250);
                    try
                    {
                        if (Clipboard.ContainsImage())
                        {
                            captured = true;
                            break;
                        }
                    }
                    catch { }
                }

                // 4. Hiện lại thanh Dynamic Island mượt mà như cũ
                this.Visibility = Visibility.Visible;
                var fadeIn = new DoubleAnimation(0, prevOpacity > 0 ? prevOpacity : 1.0, TimeSpan.FromMilliseconds(250))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                this.BeginAnimation(OpacityProperty, fadeIn);

                if (captured)
                {
                    try
                    {
                        var img = Clipboard.GetImage();
                        if (img != null)
                        {
                            string folder = GetCaptureFolder();
                            Directory.CreateDirectory(folder);
                            string fileName = $"Capture_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                            string filePath = Path.Combine(folder, fileName);
                            using (var fs = new FileStream(filePath, FileMode.Create))
                            {
                                var encoder = new PngBitmapEncoder();
                                encoder.Frames.Add(BitmapFrame.Create(img));
                                encoder.Save(fs);
                            }
                            ShowModernToast($"Đã chụp và lưu ảnh vào:\n{fileName}", "📸", "#10B981");
                        }
                        else
                        {
                            ShowModernToast("Đã chụp và lưu ảnh vào Clipboard!", "📸", "#38BDF8");
                        }
                    }
                    catch
                    {
                        ShowModernToast("Đã chụp và lưu ảnh vào Clipboard!", "📸", "#38BDF8");
                    }
                }
            }
            catch
            {
                this.Visibility = Visibility.Visible;
                this.Opacity = 1.0;
            }
        }
        #endregion

        private void NotchRoot_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_currentState == IslandState.Orbital) return;
            if (DateTime.UtcNow < _preventPullDownUntil) return;

            // Do not intercept clicks on buttons, textboxes, sliders, or drag grip handle
            if (e.OriginalSource is DependencyObject dep)
            {
                if (FindVisualParent<Button>(dep) != null || 
                    FindVisualParent<TextBox>(dep) != null || 
                    FindVisualParent<Slider>(dep) != null ||
                    FindVisualParent<Border>(dep)?.Tag?.ToString() == "DragGrip" ||
                    FindVisualParent<Border>(dep)?.Name?.StartsWith("DragGrip") == true)
                {
                    return;
                }
            }

            _isDraggingLiquid = true;
            _dragStartPoint = e.GetPosition(NotchRoot);
            _initialNotchHeight = IslandCard.Height;
            NotchRoot.CaptureMouse();
        }

        private void Window_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDraggingLiquid) return;

            Point cur = e.GetPosition(NotchRoot);
            double deltaY = cur.Y - _dragStartPoint.Y;

            if (deltaY > 2)
            {
                double clampedDeltaY = Math.Max(0, deltaY);
                // Physical elastic resistance curve: capped smoothly at max 46px
                double factor = 1.0 - Math.Exp(-clampedDeltaY / 55.0);
                double sagY = 46.0 * factor;
                double extraHeight = 16.0 * factor;

                // Localized sag follows cursor position organically within the notch width
                double sagX = Math.Clamp(cur.X, 60, IslandCard.Width + 44 - 60);

                _currentSagX = sagX;
                _currentSagY = sagY;
                _currentExtraHeight = extraHeight;

                UpdateNotchGeometry(IslandCard.Width, _initialNotchHeight, sagX, sagY);
                IslandCard.Height = _initialNotchHeight + extraHeight;
            }
            else
            {
                _currentSagY = 0;
                _currentExtraHeight = 0;
                UpdateNotchGeometry(IslandCard.Width, _initialNotchHeight, 0, 0);
                IslandCard.Height = _initialNotchHeight;
            }
        }

        private void AnimateNotchSnapBack(double startSagX, double startSagY, double startExtraHeight)
        {
            if (startSagY <= 0.5)
            {
                UpdateNotchGeometry(IslandCard.Width, _initialNotchHeight, 0, 0);
                IslandCard.Height = _initialNotchHeight;
                return;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            const double totalMs = 280.0;
            double w = IslandCard.Width;
            double h0 = _initialNotchHeight;

            EventHandler? onRendering = null;
            onRendering = (s, e) =>
            {
                double elapsed = sw.ElapsedMilliseconds;
                if (elapsed >= totalMs)
                {
                    CompositionTarget.Rendering -= onRendering;
                    UpdateNotchGeometry(w, h0, 0, 0);
                    IslandCard.Height = h0;
                    _currentSagY = 0;
                    _currentExtraHeight = 0;
                    return;
                }

                double t = elapsed / totalMs;
                // Smooth viscous cubic snap-back
                double progress = 1.0 - Math.Pow(1.0 - t, 3.0);
                double currentSag = startSagY * (1.0 - progress);
                double currentExtra = startExtraHeight * (1.0 - progress);

                UpdateNotchGeometry(w, h0, startSagX, currentSag);
                IslandCard.Height = h0 + currentExtra;
            };

            CompositionTarget.Rendering += onRendering;
        }

        private void Window_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDraggingLiquid)
            {
                Point cur = e.GetPosition(NotchRoot);
                double deltaY = cur.Y - _dragStartPoint.Y;

                _isDraggingLiquid = false;
                NotchRoot.ReleaseMouseCapture();

                _currentSagX = 0;
                _currentSagY = 0;
                _currentExtraHeight = 0;

                // Snap notch back to rest immediately without frame drops
                UpdateNotchGeometry(IslandCard.Width, _initialNotchHeight, 0, 0);
                IslandCard.Height = _initialNotchHeight;

                if (deltaY > 16)
                {
                    // User released mouse after dragging -> drop droplet straight down in center!
                    TriggerCenterDropletDrop();
                }
                else if (Math.Abs(deltaY) < 5)
                {
                    if (_currentState == IslandState.Mini)
                    {
                        // Click mini disc -> expand back to Compact!
                        SwitchState(IslandState.Compact);
                    }
                    else if (_currentState == IslandState.Compact)
                    {
                        // Simple click on compact notch also drops the liquid!
                        TriggerCenterDropletDrop();
                    }
                }
            }
        }

        private void TriggerCenterDropletDrop()
        {
            if (_currentState == IslandState.Camera)
            {
                CloseCameraAppIfRunning();
            }

            // If we are currently in an expanded planet/task (Music, Camera, Notification, Pomodoro, Dropzone, etc.)
            // The user explicitly requires: "yêu cầu khi đóng hành tinh thì thu nhỏ lại cái dynamic island xong bắt đầu có giọt nước xuống"
            if (_currentState != IslandState.Compact && _currentState != IslandState.Mini)
            {
                // Step 1: First shrink the expanded dynamic island smoothly back to Compact!
                var shrinkDuration = TimeSpan.FromMilliseconds(240);
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

                double currentW = IslandCard.ActualWidth > 0 ? IslandCard.ActualWidth : IslandCard.Width;
                double currentH = IslandCard.ActualHeight > 0 ? IslandCard.ActualHeight : IslandCard.Height;

                // Hide all subviews and show Compact
                ViewMini.Visibility = Visibility.Collapsed;
                ViewMusic.Visibility = Visibility.Collapsed;
                ViewPomodoro.Visibility = Visibility.Collapsed;
                ViewCamera.Visibility = Visibility.Collapsed;
                ViewNotification.Visibility = Visibility.Collapsed;
                ViewDropzone.Visibility = Visibility.Collapsed;
                ViewLockScreen.Visibility = Visibility.Collapsed;
                ViewUpdate.Visibility = Visibility.Collapsed;
                ViewCompact.Visibility = Visibility.Visible;
                CardGlow.Color = (Color)ColorConverter.ConvertFromString("#38BDF8");

                _currentState = IslandState.Compact;
                _initialNotchHeight = 38;

                var animW = new DoubleAnimation(currentW, 580, shrinkDuration) { EasingFunction = ease };
                var animH = new DoubleAnimation(currentH, 38, shrinkDuration) { EasingFunction = ease };

                animH.Completed += (s, e) =>
                {
                    IslandCard.BeginAnimation(WidthProperty, null);
                    IslandCard.BeginAnimation(HeightProperty, null);
                    IslandCard.Width = 580;
                    IslandCard.Height = 38;
                    UpdateNotchGeometry(580, 38, 0, 0);

                    // Step 2: "Xong bắt đầu có giọt nước xuống" - NOW drop the droplet from compact notch!
                    StartDropletDescent();
                };

                IslandCard.BeginAnimation(WidthProperty, animW);
                IslandCard.BeginAnimation(HeightProperty, animH);
                return;
            }

            // If already in Compact: start droplet descent immediately!
            StartDropletDescent();
        }

        private void StartDropletDescent()
        {
            // Drop straight down along the center vertical axis: X = 474 (Window center: 490 - 16)
            double centerX = 474;
            // Always starts cleanly at the bottom edge of the compact notch (38 - 8 = 30)
            double startY = 30;
            // Target is Center of Black Hole: Y=150 minus droplet bulb center 36 = 114
            double targetY = 114;

            // Explicitly clear all previous animation clocks on FallingDroplet to prevent WPF holding clocks
            FallingDroplet.BeginAnimation(Canvas.TopProperty, null);
            FallingDroplet.BeginAnimation(OpacityProperty, null);
            FallingDropletScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            FallingDropletScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

            Canvas.SetLeft(FallingDroplet, centerX);
            Canvas.SetTop(FallingDroplet, startY);
            FallingDroplet.Opacity = 1.0;

            FallingDropletScale.ScaleX = 0.85;
            FallingDropletScale.ScaleY = 1.0;

            // Ensure orbital view is Collapsed during drop for 0 GPU/CPU overhead (silky-smooth 120 FPS!)
            ViewOrbital.Visibility = Visibility.Collapsed;
            NotchRoot.BeginAnimation(OpacityProperty, null);
            NotchRoot.Opacity = 1.0;
            NotchRoot.Visibility = Visibility.Visible;

            // Straight vertical drop (duration: 340ms - fluid, natural speed, NO frame drop!)
            var dropDuration = TimeSpan.FromMilliseconds(340);
            var dropYAnim = new DoubleAnimation(startY, targetY, dropDuration)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };

            // Viscous elongation during fall
            var stretchYAnim = new DoubleAnimation(1.0, 1.35, dropDuration)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            var stretchXAnim = new DoubleAnimation(0.85, 0.72, dropDuration)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };

            dropYAnim.Completed += (s, e) =>
            {
                // Impact & Splash: Droplet squashes and dissipates into the black hole!
                var splashDuration = TimeSpan.FromMilliseconds(120);
                var squashXAnim = new DoubleAnimation(0.72, 1.8, splashDuration)
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                var squashYAnim = new DoubleAnimation(1.35, 0.22, splashDuration)
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                var fadeOutAnim = new DoubleAnimation(1.0, 0.0, splashDuration);

                fadeOutAnim.Completed += (s2, e2) =>
                {
                    FallingDroplet.BeginAnimation(Canvas.TopProperty, null);
                    FallingDroplet.BeginAnimation(OpacityProperty, null);
                    FallingDropletScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    FallingDropletScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                    FallingDroplet.Opacity = 0.0;
                };

                FallingDropletScale.BeginAnimation(ScaleTransform.ScaleXProperty, squashXAnim);
                FallingDropletScale.BeginAnimation(ScaleTransform.ScaleYProperty, squashYAnim);
                FallingDroplet.BeginAnimation(OpacityProperty, fadeOutAnim);

                // Switch to Orbital mode & Black Hole pop-in animation
                SwitchState(IslandState.Orbital);

                // Pop-in Black Hole accretion disk (from 0.0 to 1.0 at arrival)
                var bhPopAnim = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(300))
                {
                    EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 }
                };
                BlackHoleScale.BeginAnimation(ScaleTransform.ScaleXProperty, bhPopAnim);
                BlackHoleScale.BeginAnimation(ScaleTransform.ScaleYProperty, bhPopAnim);

                // Smooth bloom out planets
                TriggerBloomPlanets();
            };

            FallingDroplet.BeginAnimation(Canvas.TopProperty, dropYAnim);
            FallingDropletScale.BeginAnimation(ScaleTransform.ScaleYProperty, stretchYAnim);
            FallingDropletScale.BeginAnimation(ScaleTransform.ScaleXProperty, stretchXAnim);
        }
        #endregion

        #region Real Hardware Metrics (Clock, RAM, ROM, Battery, CPU)
        private void ClockTimer_Tick(object? sender, EventArgs e)
        {
            var viCulture = new CultureInfo("vi-VN");
            string dayAbbr = DateTime.Now.ToString("ddd", viCulture);

            if (PillCompactNotification != null && PillCompactNotification.Visibility == Visibility.Visible)
            {
                TxtClock.Text = DateTime.Now.ToString("HH:mm:ss");
            }
            else
            {
                TxtClock.Text = DateTime.Now.ToString($"HH:mm:ss  '{dayAbbr}', dd/MM");
            }
            PillClock.ToolTip = $"🕒 THỜI GIAN HỆ THỐNG\n• Giờ: {DateTime.Now:HH:mm:ss}\n• Ngày: {DateTime.Now.ToString("dddd, ngày dd/MM/yyyy", viCulture)}";

            // Real RAM with exact GB calculation & rich tooltip
            var memStatus = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
            if (GlobalMemoryStatusEx(ref memStatus))
            {
                TxtRam.Text = $"{memStatus.dwMemoryLoad}%";
                double totalRamGb = memStatus.ullTotalPhys / (1024.0 * 1024.0 * 1024.0);
                double freeRamGb = memStatus.ullAvailPhys / (1024.0 * 1024.0 * 1024.0);
                double usedRamGb = totalRamGb - freeRamGb;
                PillRam.ToolTip = $"🧠 BỘ NHỚ RAM\n• Đang dùng: {usedRamGb:F1} GB / {totalRamGb:F1} GB ({memStatus.dwMemoryLoad}%)\n• Còn trống: {freeRamGb:F1} GB";
            }

            // Real ROM (Drive C) with exact GB calculation & rich tooltip
            try
            {
                var drive = new DriveInfo("C");
                double totalRom = drive.TotalSize;
                double freeRom = drive.AvailableFreeSpace;
                double usedRom = totalRom - freeRom;
                int romPercent = (int)((usedRom / totalRom) * 100);
                TxtRom.Text = $"{romPercent}%";
                double totalRomGb = totalRom / (1024.0 * 1024.0 * 1024.0);
                double freeRomGb = freeRom / (1024.0 * 1024.0 * 1024.0);
                double usedRomGb = usedRom / (1024.0 * 1024.0 * 1024.0);
                PillRom.ToolTip = $"💾 Ổ ĐĨA HỆ THỐNG (C:)\n• Đã dùng: {usedRomGb:F1} GB / {totalRomGb:F1} GB ({romPercent}%)\n• Còn trống: {freeRomGb:F1} GB";
            }
            catch
            {
                TxtRom.Text = "45%";
                PillRom.ToolTip = "💾 Ổ đĩa hệ thống (C:)";
            }

            // Real Battery with charging status & rich tooltip
            if (GetSystemPowerStatus(out SYSTEM_POWER_STATUS powerStatus))
            {
                if (powerStatus.BatteryLifePercent != 255)
                {
                    TxtBattery.Text = $"{powerStatus.BatteryLifePercent}%";
                    bool isCharging = powerStatus.ACLineStatus == 1;
                    TxtBatteryIcon.Text = isCharging ? "⚡" : "🔋";
                    string statusStr = isCharging ? "Đang cắm sạc (AC)" : "Đang dùng Pin";
                    PillBattery.ToolTip = $"🔋 PIN THIẾT BỊ\n• Mức pin: {powerStatus.BatteryLifePercent}%\n• Trạng thái nguồn: {statusStr}";
                }
                else
                {
                    TxtBattery.Text = "AC";
                    TxtBatteryIcon.Text = "⚡";
                    PillBattery.ToolTip = "⚡ Nguồn điện trực tiếp AC (Máy bàn hoặc cắm nguồn liên tục)";
                }
            }
        }
        #endregion

        #region Windows Media Integration & Smooth Live Progress
        private async Task InitMediaManagerAsync()
        {
            try
            {
                _mediaManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                if (_mediaManager != null)
                {
                    _mediaManager.CurrentSessionChanged += async (s, args) =>
                    {
                        await Dispatcher.InvokeAsync(UpdateMediaInfoAsync);
                    };
                    await UpdateMediaInfoAsync();
                }
            }
            catch { }
        }

        private async Task UpdateMediaInfoAsync()
        {
            try
            {
                _currentSession = _mediaManager?.GetCurrentSession();
                if (_currentSession != null)
                {
                    _currentSession.MediaPropertiesChanged -= CurrentSession_MediaPropertiesChanged;
                    _currentSession.MediaPropertiesChanged += CurrentSession_MediaPropertiesChanged;
                    _currentSession.PlaybackInfoChanged -= CurrentSession_PlaybackInfoChanged;
                    _currentSession.PlaybackInfoChanged += CurrentSession_PlaybackInfoChanged;

                    var props = await _currentSession.TryGetMediaPropertiesAsync();
                    var playback = _currentSession.GetPlaybackInfo();
                    var timeline = _currentSession.GetTimelineProperties();

                    if (props != null)
                    {
                        string title = string.IsNullOrWhiteSpace(props.Title) ? "Đang phát nhạc" : props.Title;
                        string artist = string.IsNullOrWhiteSpace(props.Artist) ? "Windows Media" : props.Artist;

                        TxtMusicTitle.Text = title;
                        TxtMusicArtist.Text = artist;
                        TxtMediaTitleCompact.Text = title;

                        // Large Album Art Cover
                        if (props.Thumbnail != null)
                        {
                            try
                            {
                                using var stream = await props.Thumbnail.OpenReadAsync();
                                var bmp = new BitmapImage();
                                bmp.BeginInit();
                                bmp.StreamSource = stream.AsStreamForRead();
                                bmp.CacheOption = BitmapCacheOption.OnLoad;
                                bmp.EndInit();
                                AlbumCoverBrush.ImageSource = bmp;
                                TxtVinylCenterIcon.Visibility = Visibility.Collapsed;
                            }
                            catch
                            {
                                AlbumCoverBrush.ImageSource = null;
                                TxtVinylCenterIcon.Visibility = Visibility.Visible;
                            }
                        }
                        else
                        {
                            AlbumCoverBrush.ImageSource = null;
                            TxtVinylCenterIcon.Visibility = Visibility.Visible;
                        }
                    }

                    if (timeline != null && timeline.EndTime.TotalSeconds > 0)
                    {
                        _currentTrackDuration = timeline.EndTime;
                        _currentTrackPosition = timeline.Position;
                        _lastPositionUpdateTime = DateTime.UtcNow;
                        SliderMusicTime.Maximum = _currentTrackDuration.TotalSeconds;
                        SliderMusicTime.Value = _currentTrackPosition.TotalSeconds;
                        TxtCurrentTime.Text = _currentTrackPosition.ToString(@"mm\:ss");
                        TxtTotalTime.Text = _currentTrackDuration.ToString(@"mm\:ss");
                    }

                    if (playback != null)
                    {
                        _isPlaying = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                        TxtPlayPauseIcon.Text = _isPlaying ? "⏸️" : "▶️";
                        TxtMediaWave.Text = _isPlaying ? "🎶" : "🎵";

                        // Animate rotating vinyl
                        if (_isPlaying && _vinylRotateAnim != null)
                        {
                            VinylRotate.BeginAnimation(RotateTransform.AngleProperty, _vinylRotateAnim);
                            MiniVinylRotate.BeginAnimation(RotateTransform.AngleProperty, _vinylRotateAnim);
                        }
                        else
                        {
                            VinylRotate.BeginAnimation(RotateTransform.AngleProperty, null);
                            MiniVinylRotate.BeginAnimation(RotateTransform.AngleProperty, null);
                        }
                    }
                }
                else
                {
                    TxtMediaTitleCompact.Text = "Music";
                    TxtMusicTitle.Text = "Chưa phát bài hát nào";
                    TxtMusicArtist.Text = "Mở Spotify, YouTube hoặc Apple Music để nghe";
                    TxtPlayPauseIcon.Text = "▶️";
                    VinylRotate.BeginAnimation(RotateTransform.AngleProperty, null);
                    MiniVinylRotate.BeginAnimation(RotateTransform.AngleProperty, null);
                    AlbumCoverBrush.ImageSource = null;
                    TxtVinylCenterIcon.Visibility = Visibility.Visible;
                }
            }
            catch { }
        }

        private async void CurrentSession_MediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
        {
            await Dispatcher.InvokeAsync(UpdateMediaInfoAsync);
        }

        private async void CurrentSession_PlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        {
            await Dispatcher.InvokeAsync(UpdateMediaInfoAsync);
        }

        private void MediaTrackTimer_Tick(object? sender, EventArgs e)
        {
            if (_currentState == IslandState.Music && !_isUserSeeking)
            {
                try
                {
                    if (_isPlaying && _currentTrackDuration.TotalSeconds > 0)
                    {
                        double elapsed = (DateTime.UtcNow - _lastPositionUpdateTime).TotalSeconds;
                        double currentSeconds = Math.Min(_currentTrackPosition.TotalSeconds + elapsed, _currentTrackDuration.TotalSeconds);

                        SliderMusicTime.Maximum = _currentTrackDuration.TotalSeconds;
                        SliderMusicTime.Value = currentSeconds;
                        TxtCurrentTime.Text = TimeSpan.FromSeconds(currentSeconds).ToString(@"mm\:ss");
                        TxtTotalTime.Text = _currentTrackDuration.ToString(@"mm\:ss");
                    }
                }
                catch { }
            }
        }

        private void SliderMusicTime_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _isUserSeeking = true;
        }

        private async void SliderMusicTime_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            _isUserSeeking = false;
            try
            {
                _currentTrackPosition = TimeSpan.FromSeconds(SliderMusicTime.Value);
                _lastPositionUpdateTime = DateTime.UtcNow;

                if (_currentSession != null)
                {
                    await _currentSession.TryChangePlaybackPositionAsync(TimeSpan.FromSeconds(SliderMusicTime.Value).Ticks);
                }
            }
            catch { }
        }

        private async void BtnMediaPlayPause_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_currentSession != null)
                {
                    if (_isPlaying)
                        await _currentSession.TryPauseAsync();
                    else
                        await _currentSession.TryPlayAsync();
                }
            }
            catch { }
        }

        private async void BtnMediaNext_Click(object sender, RoutedEventArgs e)
        {
            try { if (_currentSession != null) await _currentSession.TrySkipNextAsync(); } catch { }
        }

        private async void BtnMediaPrev_Click(object sender, RoutedEventArgs e)
        {
            try { if (_currentSession != null) await _currentSession.TrySkipPreviousAsync(); } catch { }
        }
        #endregion

        #region Orbital Physics & Bloom Animation
        private void TriggerBloomPlanets()
        {
            _bloomProgress = 0.0;
            _isBlooming = true;
            _bloomStartTime = DateTime.UtcNow;
            _lastOrbitTime = _orbitStopwatch.Elapsed.TotalSeconds;
        }

        private void CompositionTarget_Rendering(object? sender, EventArgs e)
        {
            if (_currentState != IslandState.Orbital) return;

            double now = _orbitStopwatch.Elapsed.TotalSeconds;
            double dt = now - _lastOrbitTime;
            _lastOrbitTime = now;
            if (dt <= 0 || dt > 0.1) dt = 0.016;

            if (_isBlooming)
            {
                double elapsed = (DateTime.UtcNow - _bloomStartTime).TotalMilliseconds;
                double t = Math.Clamp(elapsed / BloomDurationMs, 0.0, 1.0);
                // Silky smooth EaseOutCubic: 1 - (1 - t)^3
                _bloomProgress = 1.0 - Math.Pow(1.0 - t, 3.0);
                if (t >= 1.0)
                {
                    _bloomProgress = 1.0;
                    _isBlooming = false;
                }
            }

            // FREEZE ORBITAL ROTATION WHEN A PLANET IS HOVERED!
            if (!_isOrbitPaused)
            {
                _orbitAngle += AngularSpeed * dt;
                if (_orbitAngle > Math.PI * 2) _orbitAngle -= Math.PI * 2;
            }

            // Center of Canvas (340, 150)
            double radiusX = 200 * _bloomProgress;
            double radiusY = 100 * _bloomProgress;

            Border[] orbs = [OrbMusic, OrbNotify, OrbLockScreen, OrbDrop, OrbCamera, OrbPomodoro];
            TranslateTransform[] translates = [TransMusic, TransNotify, TransLockScreen, TransDrop, TransCamera, TransPomodoro];

            for (int i = 0; i < orbs.Length; i++)
            {
                double angle = _orbitAngle + i * (Math.PI * 2 / orbs.Length);
                double widthOffset = (orbs[i].ActualWidth - 52) / 2;
                translates[i].X = radiusX * Math.Cos(angle) - widthOffset;
                translates[i].Y = radiusY * Math.Sin(angle);
            }

            // Laser line connects Black Hole to Hovered Planet
            if (_hoveredOrb != null)
            {
                int idx = Array.IndexOf(orbs, _hoveredOrb);
                if (idx >= 0)
                {
                    double angle = _orbitAngle + idx * (Math.PI * 2 / orbs.Length);
                    ConnectingLine.X1 = 340;
                    ConnectingLine.Y1 = 150;
                    ConnectingLine.X2 = 340 + radiusX * Math.Cos(angle);
                    ConnectingLine.Y2 = 150 + radiusY * Math.Sin(angle);
                }
            }
        }

        private void Planet_MouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is Border orb)
            {
                _hoveredOrb = orb;
                _isOrbitPaused = true; // FREEZE ALL PLANETS WHEN HOVERED!

                // Morph orb width to pill (52 -> 120)
                var widthAnim = new DoubleAnimation(orb.ActualWidth, 120, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                orb.BeginAnimation(WidthProperty, widthAnim);

                // Reveal text label
                TextBlock? lbl = orb.Name switch
                {
                    "OrbMusic" => LblMusic,
                    "OrbNotify" => LblNotify,
                    "OrbLockScreen" => LblLockScreen,
                    "OrbDrop" => LblDrop,
                    "OrbCamera" => LblCamera,
                    "OrbPomodoro" => LblPomodoro,
                    _ => null
                };
                if (lbl != null) lbl.Visibility = Visibility.Visible;

                // Connecting laser line
                ConnectingLine.Stroke = orb.BorderBrush;
                if (orb.Effect is DropShadowEffect dropShadow)
                {
                    LineGlow.Color = dropShadow.Color;
                }
                var lineFade = new DoubleAnimation(ConnectingLine.Opacity, 1.0, TimeSpan.FromMilliseconds(150));
                ConnectingLine.BeginAnimation(OpacityProperty, lineFade);
            }
        }

        private void Planet_MouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is Border orb)
            {
                if (_hoveredOrb == orb) _hoveredOrb = null;
                _isOrbitPaused = false; // RESUME ORBITING WHEN UNHOVERED!
                _lastOrbitTime = _orbitStopwatch.Elapsed.TotalSeconds;

                // Morph orb back to circle (120 -> 52)
                var widthAnim = new DoubleAnimation(orb.ActualWidth, 52, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                orb.BeginAnimation(WidthProperty, widthAnim);

                // Hide text label
                TextBlock? lbl = orb.Name switch
                {
                    "OrbMusic" => LblMusic,
                    "OrbNotify" => LblNotify,
                    "OrbLockScreen" => LblLockScreen,
                    "OrbDrop" => LblDrop,
                    "OrbCamera" => LblCamera,
                    "OrbPomodoro" => LblPomodoro,
                    _ => null
                };
                if (lbl != null) lbl.Visibility = Visibility.Collapsed;

                // Fade out laser line
                var lineFade = new DoubleAnimation(ConnectingLine.Opacity, 0.0, TimeSpan.FromMilliseconds(150));
                ConnectingLine.BeginAnimation(OpacityProperty, lineFade);
            }
        }
        #endregion

        #region Reverse Teardrop Droplet Flow to Task
        private void TriggerReverseDropletToTask(Border? orb, IslandState targetState)
        {
            // Halt orbital calculations immediately so CPU/GPU focus completely on droplet rendering
            _isOrbitPaused = true;
            _preventPullDownUntil = DateTime.UtcNow.AddMilliseconds(800);

            // Core Center coordinates: Center of window 474 (490 - 16), Core Center Y = 114
            double coreX = 474;
            double coreY = 114;
            // Flows up near the top screen edge without piercing through (stops gracefully inside notch at Y=14)
            double targetY = 14;

            if (orb != null)
            {
                // Inherit the clicked planet's color aura
                ReverseDroplet.Stroke = orb.BorderBrush;
                ReverseDroplet.Fill = orb.Background;
                if (orb.Effect is DropShadowEffect ds)
                {
                    ReverseDropletGlow.Color = ds.Color;
                }
            }
            else
            {
                // Core Black Hole amber aura
                ReverseDroplet.Stroke = (Brush)new BrushConverter().ConvertFromString("#F97316")!;
                ReverseDroplet.Fill = (Brush)new BrushConverter().ConvertFromString("#020202")!;
                ReverseDropletGlow.Color = (Color)ColorConverter.ConvertFromString("#EA580C");
            }

            // CRITICAL: Explicitly clear all previous animation clocks on ReverseDroplet to prevent WPF holding clocks
            ReverseDroplet.BeginAnimation(Canvas.TopProperty, null);
            ReverseDroplet.BeginAnimation(OpacityProperty, null);
            ReverseDropletScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ReverseDropletScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

            // Spawn strictly at the Core center
            Canvas.SetLeft(ReverseDroplet, coreX);
            Canvas.SetTop(ReverseDroplet, coreY);
            ReverseDroplet.Opacity = 1.0;

            ReverseDropletScale.ScaleX = 1.0;
            ReverseDropletScale.ScaleY = 0.85;

            // Fluid vertical flow from Core UP towards top edge (duration: 340ms - snappy and responsive)
            var flowDuration = TimeSpan.FromMilliseconds(340);
            var upAnim = new DoubleAnimation(coreY, targetY, flowDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            // Organic vertical elongation pointing towards the notch (capped at 1.25 so it never pierces screen edge)
            var stretchYAnim = new DoubleAnimation(0.85, 1.25, flowDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            var stretchXAnim = new DoubleAnimation(1.0, 0.78, flowDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            // Guaranteed visibility: 1.0 from 0ms to 240ms, then smoothly dissolves in the final 100ms as it merges into the notch!
            var opacityAnim = new DoubleAnimationUsingKeyFrames();
            opacityAnim.KeyFrames.Add(new DiscreteDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            opacityAnim.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(240))));
            opacityAnim.KeyFrames.Add(new SplineDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(340))));

            upAnim.Completed += (s, e) =>
            {
                _isOrbitPaused = false;
                // Clear all animations completely
                ReverseDroplet.BeginAnimation(Canvas.TopProperty, null);
                ReverseDroplet.BeginAnimation(OpacityProperty, null);
                ReverseDropletScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                ReverseDropletScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                ReverseDroplet.Opacity = 0.0;

                ViewOrbital.Visibility = Visibility.Collapsed;
                ViewOrbital.BeginAnimation(OpacityProperty, null);
                ViewOrbital.Opacity = 1.0;

                NotchRoot.BeginAnimation(OpacityProperty, null);
                NotchRoot.Opacity = 1.0;
                NotchRoot.Visibility = Visibility.Visible;

                // Morph into target dynamic island
                ApplyState(targetState, animate: true);
            };

            ReverseDroplet.BeginAnimation(Canvas.TopProperty, upAnim);
            ReverseDropletScale.BeginAnimation(ScaleTransform.ScaleYProperty, stretchYAnim);
            ReverseDropletScale.BeginAnimation(ScaleTransform.ScaleXProperty, stretchXAnim);
            ReverseDroplet.BeginAnimation(OpacityProperty, opacityAnim);

            // Smoothly fade out orbital planets as droplet shoots upwards
            var orbitFade = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(260));
            ViewOrbital.BeginAnimation(OpacityProperty, orbitFade);
        }
        #endregion

        #region Pomodoro Logic
        private void PomoTimer_Tick(object? sender, EventArgs e)
        {
            if (_pomoRemaining > 0)
            {
                _pomoRemaining--;
                TxtPomodoroDigits.Text = $"{_pomoRemaining / 60:D2}:{_pomoRemaining % 60:D2}";
                PomodoroBar.Value = ((double)_pomoRemaining / _pomoTotal) * 100.0;
            }
            else
            {
                _pomoTimer.Stop();
                _pomoIsRunning = false;
                BtnPomoStartPause.Content = "▶ Start";
                ShowModernToast("Hết phiên làm việc! Hãy nghỉ ngơi thư giãn đôi mắt.", "🔔", "#EAB308");
            }
        }

        private void BtnPomoStartPause_Click(object sender, RoutedEventArgs e)
        {
            if (_pomoIsRunning)
            {
                _pomoTimer.Stop();
                _pomoIsRunning = false;
                BtnPomoStartPause.Content = "▶ Start";
                TxtPomodoroStatus.Text = "PAUSED";
            }
            else
            {
                _pomoTimer.Start();
                _pomoIsRunning = true;
                BtnPomoStartPause.Content = "⏸ Pause";
                TxtPomodoroStatus.Text = "FOCUS";
            }
        }

        private void BtnPomoReset_Click(object sender, RoutedEventArgs e)
        {
            _pomoTimer.Stop();
            _pomoIsRunning = false;
            BtnPomoStartPause.Content = "▶ Start";
            _pomoRemaining = _pomoTotal;
            TxtPomodoroDigits.Text = $"{_pomoRemaining / 60:D2}:{_pomoRemaining % 60:D2}";
            PomodoroBar.Value = 100;
            TxtPomodoroStatus.Text = "READY";
        }

        private void BtnPomoPreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn)
            {
                int mins = btn.Name switch
                {
                    "BtnPomo25" => 25,
                    "BtnPomo5" => 5,
                    "BtnPomo15" => 15,
                    _ => 25
                };
                _pomoTotal = mins * 60;
                _pomoRemaining = _pomoTotal;
                TxtPomodoroDigits.Text = $"{mins:D2}:00";
                PomodoroBar.Value = 100;
                TxtPomodoroStatus.Text = mins == 25 ? "FOCUS" : "BREAK";

                _pomoTimer.Stop();
                _pomoIsRunning = false;
                BtnPomoStartPause.Content = "▶ Start";
            }
        }
        #endregion

        #region Dropzone & NotebookLM Integration
        private readonly string _notebookLmConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DynamicIsland", "notebooklm_url.txt"
        );

        private bool IsAuthenticNotebookLink(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            url = url.Trim();
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                url = "https://" + url;
            }
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uriResult)) return false;

            string host = uriResult.Host.ToLowerInvariant();
            bool isGoogleNotebookHost = host.Contains("notebooklm") || 
                                       (host.Contains("notebook") && host.Contains("google")) ||
                                       host.EndsWith("google.com", StringComparison.OrdinalIgnoreCase);
            if (!isGoogleNotebookHost) return false;

            // An authentic notebook link MUST contain /notebook/ followed by a notebook ID
            string path = uriResult.AbsolutePath.ToLowerInvariant();
            if (!path.Contains("/notebook/")) return false;

            string[] segments = uriResult.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < segments.Length; i++)
            {
                if (segments[i].Equals("notebook", StringComparison.OrdinalIgnoreCase) && i + 1 < segments.Length)
                {
                    return !string.IsNullOrWhiteSpace(segments[i + 1]);
                }
            }
            return false;
        }

        private void UpdateNotebookLmStatus(bool isAuthentic, string url, string notebookName = "", bool isWindowLive = false)
        {
            _isNotebookVerified = isAuthentic;
            Dispatcher.Invoke(() =>
            {
                if (isAuthentic)
                {
                    BorderNotebookStatus.Background = (Brush)new BrushConverter().ConvertFromString("#064E3B")!;
                    BorderNotebookStatus.BorderBrush = (Brush)new BrushConverter().ConvertFromString("#10B981")!;
                    TxtNotebookStatus.Foreground = (Brush)new BrushConverter().ConvertFromString("#34D399")!;
                    if (!string.IsNullOrWhiteSpace(notebookName))
                    {
                        TxtNotebookStatus.Text = isWindowLive 
                            ? $"🟢 Đang mở: {notebookName}" 
                            : $"🟢 Sổ tay: {notebookName}";
                    }
                    else
                    {
                        TxtNotebookStatus.Text = "🟢 Đã xác thực Sổ tay thật";
                    }
                    BtnSendToNotebookLM.IsEnabled = true;
                    BtnSendToNotebookLM.Opacity = 1.0;
                }
                else
                {
                    BorderNotebookStatus.Background = (Brush)new BrushConverter().ConvertFromString("#291219")!;
                    BorderNotebookStatus.BorderBrush = (Brush)new BrushConverter().ConvertFromString("#E11D48")!;
                    TxtNotebookStatus.Foreground = (Brush)new BrushConverter().ConvertFromString("#FDA4AF")!;
                    TxtNotebookStatus.Text = "🔴 Chưa xác thực Sổ tay (Bấm để cài)";
                    BtnSendToNotebookLM.IsEnabled = false;
                    BtnSendToNotebookLM.Opacity = 0.55;
                }
            });
        }

        private IntPtr FindNotebookLmWindow(out string detectedTitle)
        {
            IntPtr bestHwnd = IntPtr.Zero;
            string bestTitle = "";
            IntPtr fallbackBrowserHwnd = IntPtr.Zero;
            string fallbackBrowserTitle = "";
            IntPtr myHwnd = IntPtr.Zero;
            try
            {
                myHwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            }
            catch { }

            string[] knownBrowsers = { "chrome", "msedge", "brave", "firefox", "opera", "vivaldi" };

            EnumWindows((hWnd, lParam) =>
            {
                if (hWnd == myHwnd) return true;
                if (!IsWindowVisible(hWnd)) return true;

                int length = GetWindowTextLength(hWnd);
                if (length == 0) return true;

                var builder = new System.Text.StringBuilder(length + 1);
                GetWindowText(hWnd, builder, builder.Capacity);
                string title = builder.ToString();
                if (string.IsNullOrWhiteSpace(title)) return true;

                uint pid = 0;
                GetWindowThreadProcessId(hWnd, out pid);
                string procName = "";
                if (pid != 0)
                {
                    try
                    {
                        using var proc = Process.GetProcessById((int)pid);
                        procName = proc.ProcessName.ToLowerInvariant();
                    }
                    catch { }
                }

                bool isBrowser = knownBrowsers.Any(b => procName.Contains(b));

                bool isHighPriorityNotebook =
                    title.Contains("NotebookLM", StringComparison.OrdinalIgnoreCase) ||
                    title.Contains("Notebook LM", StringComparison.OrdinalIgnoreCase) ||
                    (title.Contains("Notebook", StringComparison.OrdinalIgnoreCase) && title.Contains("Google", StringComparison.OrdinalIgnoreCase)) ||
                    title.Contains("Sổ ghi chép", StringComparison.OrdinalIgnoreCase) ||
                    title.Contains("Sổ tay", StringComparison.OrdinalIgnoreCase);

                if (!isHighPriorityNotebook && !string.IsNullOrWhiteSpace(_notebookName) && _notebookName.Length > 2)
                {
                    if (title.Contains(_notebookName, StringComparison.OrdinalIgnoreCase))
                    {
                        isHighPriorityNotebook = true;
                    }
                }

                if (isHighPriorityNotebook)
                {
                    bestHwnd = hWnd;
                    bestTitle = title;
                    return false; // Found NotebookLM window directly!
                }

                if (isBrowser && fallbackBrowserHwnd == IntPtr.Zero)
                {
                    fallbackBrowserHwnd = hWnd;
                    fallbackBrowserTitle = title;
                }

                return true;
            }, IntPtr.Zero);

            if (bestHwnd != IntPtr.Zero)
            {
                detectedTitle = bestTitle;
                return bestHwnd;
            }

            detectedTitle = fallbackBrowserTitle;
            return fallbackBrowserHwnd;
        }

        private string ExtractNotebookName(string windowTitle, string url)
        {
            if (!string.IsNullOrWhiteSpace(windowTitle))
            {
                string clean = windowTitle;
                string[] browserSuffixes = {
                    " - Google Chrome",
                    " - Microsoft​ Edge",
                    " - Microsoft Edge",
                    " - Brave",
                    " - Mozilla Firefox",
                    " - Firefox",
                    " - Opera",
                    " - Vivaldi"
                };
                foreach (var suffix in browserSuffixes)
                {
                    int sIdx = clean.IndexOf(suffix, StringComparison.OrdinalIgnoreCase);
                    if (sIdx >= 0)
                    {
                        clean = clean.Substring(0, sIdx).Trim();
                    }
                }

                int nIdx = clean.IndexOf("- NotebookLM", StringComparison.OrdinalIgnoreCase);
                if (nIdx > 0)
                {
                    string name = clean.Substring(0, nIdx).Trim();
                    if (!string.IsNullOrWhiteSpace(name)) return name;
                }

                int gIdx = clean.IndexOf("- Google NotebookLM", StringComparison.OrdinalIgnoreCase);
                if (gIdx > 0)
                {
                    string name = clean.Substring(0, gIdx).Trim();
                    if (!string.IsNullOrWhiteSpace(name)) return name;
                }

                if (!clean.Equals("NotebookLM", StringComparison.OrdinalIgnoreCase) &&
                    !clean.Equals("Google NotebookLM", StringComparison.OrdinalIgnoreCase) &&
                    !clean.Equals("Google", StringComparison.OrdinalIgnoreCase) &&
                    !clean.Equals("New Tab", StringComparison.OrdinalIgnoreCase) &&
                    !clean.Equals("Tab mới", StringComparison.OrdinalIgnoreCase))
                {
                    return clean;
                }
            }

            if (!string.IsNullOrWhiteSpace(url) && url.Contains("/notebook/"))
            {
                try
                {
                    string fullUrl = url.StartsWith("http") ? url : "https://" + url;
                    if (Uri.TryCreate(fullUrl, UriKind.Absolute, out Uri? uri))
                    {
                        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                        for (int i = 0; i < segments.Length; i++)
                        {
                            if (segments[i].Equals("notebook", StringComparison.OrdinalIgnoreCase) && i + 1 < segments.Length)
                            {
                                string id = segments[i + 1];
                                string shortId = id.Length > 8 ? id.Substring(0, 8) : id;
                                return $"Sổ tay #{shortId}";
                            }
                        }
                    }
                }
                catch { }
            }

            return "";
        }

        private async Task DetectAndRefreshNotebookAsync(bool showToast = false)
        {
            try
            {
                var spinAnim = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(450))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                RefreshSpinRotate.BeginAnimation(RotateTransform.AngleProperty, spinAnim);
            }
            catch { }

            string url = !string.IsNullOrWhiteSpace(_notebookUrl) ? _notebookUrl : (InputNotebookUrl?.Text ?? "").Trim();
            IntPtr hWnd = FindNotebookLmWindow(out string windowTitle);

            string detected = ExtractNotebookName(windowTitle, url);
            if (!string.IsNullOrWhiteSpace(detected))
            {
                _notebookName = detected;
                TxtNotebookHeaderTitle.Text = $"NotebookLM • {_notebookName}";
            }
            else if (!string.IsNullOrWhiteSpace(_notebookName))
            {
                TxtNotebookHeaderTitle.Text = $"NotebookLM • {_notebookName}";
            }
            else
            {
                TxtNotebookHeaderTitle.Text = "NotebookLM";
            }

            bool hasWindow = hWnd != IntPtr.Zero;
            bool authentic = IsAuthenticNotebookLink(url);

            UpdateNotebookLmStatus(authentic, url, _notebookName, hasWindow);
            SaveNotebookLmConfig(url, _notebookName);

            if (authentic)
            {
                _ = Task.Run(async () =>
                {
                    string? realTitle = await ResolveNotebookViaApiAsync(url);
                    if (!string.IsNullOrWhiteSpace(realTitle))
                    {
                        Dispatcher.Invoke(() =>
                        {
                            _notebookName = realTitle;
                            TxtNotebookHeaderTitle.Text = $"NotebookLM • {_notebookName}";
                            SaveNotebookLmConfig(url, _notebookName);
                        });
                    }
                });
            }

            if (showToast)
            {
                if (hasWindow && !string.IsNullOrWhiteSpace(_notebookName))
                {
                    ShowModernToast($"Đã nhận diện Sổ tay: '{_notebookName}' (Tab đang mở)", "🎯", "#10B981");
                }
                else if (authentic)
                {
                    ShowModernToast("Đã kết nối Sổ tay NotebookLM qua API!", "🟢", "#10B981");
                }
                else
                {
                    ShowModernToast("Chưa xác thực Sổ tay! Hãy dán link Sổ tay thật (.../notebook/ID)", "ℹ️", "#F59E0B");
                }
            }
            await Task.CompletedTask;
        }

        private async void BtnRefreshNotebook_Click(object sender, RoutedEventArgs e)
        {
            await DetectAndRefreshNotebookAsync(showToast: true);
        }

        private void LoadNotebookLmConfig()
        {
            try
            {
                if (File.Exists(_notebookLmConfigPath))
                {
                    string content = File.ReadAllText(_notebookLmConfigPath).Trim();
                    if (!string.IsNullOrEmpty(content))
                    {
                        string savedUrl = content;
                        string savedName = "";
                        if (content.Contains('|'))
                        {
                            var parts = content.Split('|', 2);
                            savedUrl = parts[0].Trim();
                            savedName = parts[1].Trim();
                        }

                        _notebookUrl = savedUrl;
                        if (InputNotebookUrl != null) InputNotebookUrl.Text = savedUrl;
                        if (InputPopupNotebookUrl != null) InputPopupNotebookUrl.Text = savedUrl;
                        _notebookName = savedName;
                        if (!string.IsNullOrWhiteSpace(_notebookName))
                        {
                            TxtNotebookHeaderTitle.Text = $"NotebookLM • {_notebookName}";
                        }
                        bool authentic = IsAuthenticNotebookLink(savedUrl);
                        UpdateNotebookLmStatus(authentic, savedUrl, _notebookName, false);
                        return;
                    }
                }
            }
            catch { }

            _notebookUrl = "";
            if (InputNotebookUrl != null) InputNotebookUrl.Text = "";
            if (InputPopupNotebookUrl != null) InputPopupNotebookUrl.Text = "";
            UpdateNotebookLmStatus(false, "", "", false);
        }

        private void SaveNotebookLmConfig(string url, string name)
        {
            try
            {
                string dir = Path.GetDirectoryName(_notebookLmConfigPath)!;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_notebookLmConfigPath, $"{url}|{name}");
            }
            catch { }
        }

        private void ApplyNotebookUrl(string url, bool showToast = false)
        {
            url = url.Trim();
            _notebookUrl = url;
            if (InputNotebookUrl != null) InputNotebookUrl.Text = url;
            if (InputPopupNotebookUrl != null) InputPopupNotebookUrl.Text = url;

            bool authentic = IsAuthenticNotebookLink(url);
            string detected = ExtractNotebookName("", url);
            if (!string.IsNullOrWhiteSpace(detected))
            {
                _notebookName = detected;
                TxtNotebookHeaderTitle.Text = $"NotebookLM • {_notebookName}";
            }
            else
            {
                TxtNotebookHeaderTitle.Text = "NotebookLM";
            }

            SaveNotebookLmConfig(url, _notebookName);
            UpdateNotebookLmStatus(authentic, url, _notebookName, false);

            if (authentic)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var realTitle = await ResolveNotebookViaApiAsync(url);
                        if (!string.IsNullOrWhiteSpace(realTitle))
                        {
                            await Dispatcher.InvokeAsync(() =>
                            {
                                _notebookName = realTitle;
                                TxtNotebookHeaderTitle.Text = $"NotebookLM • {_notebookName}";
                                SaveNotebookLmConfig(url, _notebookName);
                                UpdateNotebookLmStatus(true, url, _notebookName, false);
                                ShowModernToast($"🟢 Đã liên kết: '{_notebookName}'!", "⚡", "#10B981");
                            });
                        }
                    }
                    catch { }
                });
            }

            if (showToast)
            {
                if (authentic)
                {
                    ShowModernToast(string.IsNullOrEmpty(_notebookName) ? "✓ Đã liên kết Sổ tay NotebookLM thành công!" : $"✓ Đã liên kết: '{_notebookName}'!", "🟢", "#10B981");
                }
                else
                {
                    ShowModernToast("Link Sổ tay chưa đúng chuẩn notebooklm.google.com/notebook/[id]", "⚠️", "#EF4444");
                }
            }
        }

        #region Modern NotebookLM Config Popup Modal Handlers
        private void OpenNotebookConfigModal()
        {
            if (PopupNotebookConfig == null) return;
            string current = !string.IsNullOrWhiteSpace(_notebookUrl) ? _notebookUrl : (InputNotebookUrl?.Text ?? "").Trim();
            InputPopupNotebookUrl.Text = current;
            TxtPopupUrlPlaceholder.Visibility = string.IsNullOrEmpty(current) ? Visibility.Visible : Visibility.Collapsed;

            // If empty, auto-inspect clipboard
            if (string.IsNullOrEmpty(current))
            {
                try
                {
                    if (Clipboard.ContainsText())
                    {
                        string clip = Clipboard.GetText().Trim();
                        if (IsAuthenticNotebookLink(clip))
                        {
                            InputPopupNotebookUrl.Text = clip;
                            TxtPopupUrlPlaceholder.Visibility = Visibility.Collapsed;
                        }
                    }
                }
                catch { }
            }

            InputPopupNotebookUrl_TextChanged(InputPopupNotebookUrl, null!);

            PopupNotebookConfig.Visibility = Visibility.Visible;
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            var slideDown = new DoubleAnimation(-20, 0, TimeSpan.FromMilliseconds(250))
            {
                EasingFunction = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut }
            };

            PopupNotebookConfig.BeginAnimation(OpacityProperty, fadeIn);
            PopupNotebookTranslate.BeginAnimation(TranslateTransform.YProperty, slideDown);
            InputPopupNotebookUrl.Focus();
            InputPopupNotebookUrl.SelectAll();
        }

        private void CloseNotebookConfigModal()
        {
            if (PopupNotebookConfig == null || PopupNotebookConfig.Visibility != Visibility.Visible) return;
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            var slideUp = new DoubleAnimation(0, -20, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            fadeOut.Completed += (s, e) =>
            {
                PopupNotebookConfig.Visibility = Visibility.Collapsed;
            };
            PopupNotebookConfig.BeginAnimation(OpacityProperty, fadeOut);
            PopupNotebookTranslate.BeginAnimation(TranslateTransform.YProperty, slideUp);
        }

        private void BorderNotebookStatus_Click(object sender, MouseButtonEventArgs e)
        {
            OpenNotebookConfigModal();
        }

        private void BtnOpenNotebookConfig_Click(object sender, RoutedEventArgs e)
        {
            OpenNotebookConfigModal();
        }

        private void BtnQuickPasteNotebook_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    string clip = Clipboard.GetText().Trim();
                    if (IsAuthenticNotebookLink(clip))
                    {
                        ApplyNotebookUrl(clip, showToast: true);
                        return;
                    }
                }
            }
            catch { }

            OpenNotebookConfigModal();
        }

        private void BtnCloseNotebookPopup_Click(object sender, RoutedEventArgs e)
        {
            CloseNotebookConfigModal();
        }

        private void BtnPasteFromClipboard_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    string clip = Clipboard.GetText().Trim();
                    InputPopupNotebookUrl.Text = clip;
                    ActivateWindow();
                    InputPopupNotebookUrl.Focus();
                    InputPopupNotebookUrl.Select(clip.Length, 0);
                }
                else
                {
                    ShowModernToast("Clipboard hiện không có văn bản!", "⚠️", "#EF4444");
                }
            }
            catch { }
        }

        private void InputPopupNotebookUrl_TextChanged(object sender, TextChangedEventArgs? e)
        {
            string text = InputPopupNotebookUrl.Text.Trim();
            TxtPopupUrlPlaceholder.Visibility = string.IsNullOrEmpty(text) ? Visibility.Visible : Visibility.Collapsed;

            bool authentic = IsAuthenticNotebookLink(text);
            if (authentic)
            {
                string name = ExtractNotebookName("", text);
                TxtPopupValidationStatus.Text = string.IsNullOrEmpty(name) ? "🟢 Sổ tay hợp lệ! Sẵn sàng kết nối" : $"🟢 Hợp lệ: Sổ tay '{name}'";
                TxtPopupValidationStatus.Foreground = (Brush)new BrushConverter().ConvertFromString("#10B981")!;
            }
            else if (string.IsNullOrEmpty(text))
            {
                TxtPopupValidationStatus.Text = "💡 Hãy dán liên kết Sổ tay NotebookLM của bạn vào ô trên";
                TxtPopupValidationStatus.Foreground = (Brush)new BrushConverter().ConvertFromString("#94A3B8")!;
            }
            else
            {
                TxtPopupValidationStatus.Text = "🔴 Link chưa đúng dạng https://notebooklm.google.com/notebook/[id]";
                TxtPopupValidationStatus.Foreground = (Brush)new BrushConverter().ConvertFromString("#EF4444")!;
            }
        }

        private void InputPopupNotebookUrl_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                BtnSavePopupNotebook_Click(null!, null!);
            }
        }

        private void BtnSavePopupNotebook_Click(object sender, RoutedEventArgs e)
        {
            string text = InputPopupNotebookUrl.Text.Trim();
            if (IsAuthenticNotebookLink(text))
            {
                ApplyNotebookUrl(text, showToast: true);
                CloseNotebookConfigModal();
            }
            else
            {
                ShowModernToast("Vui lòng nhập link Sổ tay thật dạng https://notebooklm.google.com/notebook/<id>!", "⚠️", "#EF4444");
            }
        }

        private void BtnSaveNotebookUrl_Click(object sender, RoutedEventArgs e)
        {
            string text = InputNotebookUrl.Text.Trim();
            if (IsAuthenticNotebookLink(text))
            {
                ApplyNotebookUrl(text, showToast: true);
            }
            else
            {
                ShowModernToast("Vui lòng nhập link Sổ tay thật dạng https://notebooklm.google.com/notebook/<id>!", "⚠️", "#EF4444");
            }
        }

        private void InputNotebookUrl_TextChanged(object sender, TextChangedEventArgs e)
        {
            string text = InputNotebookUrl.Text.Trim();
            TxtNotebookUrlPlaceholder.Visibility = string.IsNullOrEmpty(text) ? Visibility.Visible : Visibility.Collapsed;
            _notebookUrl = text;

            bool authentic = IsAuthenticNotebookLink(text);
            string detected = ExtractNotebookName("", text);
            if (!string.IsNullOrWhiteSpace(detected))
            {
                _notebookName = detected;
                TxtNotebookHeaderTitle.Text = $"NotebookLM • {_notebookName}";
            }
            UpdateNotebookLmStatus(authentic, text, _notebookName, false);

            if (authentic)
            {
                SaveNotebookLmConfig(text, _notebookName);
            }
        }

        private void InputNotebookUrl_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                BtnSaveNotebookUrl_Click(sender, e);
                e.Handled = true;
            }
        }

        public void ActivateWindow()
        {
            try
            {
                var helper = new System.Windows.Interop.WindowInteropHelper(this);
                IntPtr hWnd = helper.Handle;
                if (hWnd != IntPtr.Zero)
                {
                    SetForegroundWindow(hWnd);
                }
                Activate();
            }
            catch { }
        }

        private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            ActivateWindow();
        }

        private void TextBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            ActivateWindow();
            if (sender is TextBox tb)
            {
                tb.Focus();
            }
        }

        private void TextBox_GotFocus(object sender, RoutedEventArgs e)
        {
            ActivateWindow();
        }
        #endregion

        private Point _stagedDragStart;

        private void BorderStagedSource_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject dep && FindVisualParent<Button>(dep) != null) return;
            _stagedDragStart = e.GetPosition(null);
        }

        private void BorderStagedSource_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                Point currentPos = e.GetPosition(null);
                Vector diff = _stagedDragStart - currentPos;
                if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    if (_currentSourceType == SourceType.File && !string.IsNullOrEmpty(_currentFilePath) && File.Exists(_currentFilePath))
                    {
                        var data = new DataObject();
                        var fileDrop = new System.Collections.Specialized.StringCollection { _currentFilePath };
                        data.SetFileDropList(fileDrop);
                        try
                        {
                            var fi = new FileInfo(_currentFilePath);
                            string ext = fi.Extension.ToLowerInvariant();
                            if (ext == ".txt" || ext == ".md" || ext == ".csv" || ext == ".json" || ext == ".cs" || ext == ".py" || ext == ".js" || ext == ".html")
                            {
                                if (fi.Length <= 2 * 1024 * 1024)
                                {
                                    data.SetText(File.ReadAllText(_currentFilePath));
                                }
                            }
                            else
                            {
                                data.SetText(_currentFilePath);
                            }
                        }
                        catch { }

                        DragDrop.DoDragDrop(BorderStagedSource, data, DragDropEffects.Copy | DragDropEffects.Move);
                    }
                    else if (_currentSourceType == SourceType.Link && !string.IsNullOrEmpty(_currentSourceUrl))
                    {
                        var data = new DataObject(DataFormats.Text, _currentSourceUrl);
                        data.SetData(DataFormats.UnicodeText, _currentSourceUrl);
                        DragDrop.DoDragDrop(BorderStagedSource, data, DragDropEffects.Copy);
                    }
                }
            }
        }

        private void Dropzone_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effects = DragDropEffects.Copy;
            else
                e.Effects = DragDropEffects.None;
            e.Handled = true;
        }

        private void Dropzone_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files != null && files.Length > 0)
                {
                    SetSelectedFile(files[0]);
                }
            }
        }

        private void BtnBrowseFile_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Chọn file đưa vào Sổ tay NotebookLM",
                Filter = "Tất cả file hỗ trợ (*.*)|*.*|Tài liệu PDF & Office (*.pdf;*.docx;*.txt;*.md)|*.pdf;*.docx;*.txt;*.md|Âm thanh (*.mp3;*.wav)|*.mp3;*.wav"
            };
            if (ofd.ShowDialog() == true)
            {
                SetSelectedFile(ofd.FileName);
            }
        }

        private void SetSelectedFile(string path)
        {
            _currentSourceType = SourceType.File;
            _currentFilePath = path;
            _currentSourceUrl = null;

            var fi = new FileInfo(path);
            TxtFileName.Text = fi.Name;
            TxtFileMeta.Text = $"{fi.Length / 1024.0:F1} KB • {fi.Extension.ToUpper()} • 💡 Giữ chuột kéo khung này thả vào NotebookLM";
            BtnClearFiles.Visibility = Visibility.Visible;
            if (BorderStagedSource != null)
            {
                BorderStagedSource.BorderBrush = (Brush)new BrushConverter().ConvertFromString("#0284C7")!;
                BorderStagedSource.Background = (Brush)new BrushConverter().ConvertFromString("#0F2238")!;
            }

            string ext = fi.Extension.ToLowerInvariant();
            TxtFileIcon.Text = ext switch
            {
                ".cs" or ".js" or ".py" or ".cpp" or ".html" or ".css" or ".json" => "💻",
                ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" => "🖼️",
                ".zip" or ".rar" or ".7z" or ".tar" => "📦",
                ".pdf" => "📕",
                ".doc" or ".docx" => "📘",
                ".txt" or ".md" => "📝",
                ".mp3" or ".wav" or ".flac" or ".m4a" => "🎵",
                ".mp4" or ".mkv" => "🎬",
                _ => "📁"
            };

            ShowModernToast($"Đã nhận: {fi.Name} • Kéo thả vào NotebookLM!", "📎", "#10B981");
        }

        private void SetSelectedLink(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            url = url.Trim();
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                url = "https://" + url;
            }

            _currentSourceType = SourceType.Link;
            _currentSourceUrl = url;
            _currentFilePath = null;

            TxtFileName.Text = url;
            TxtFileIcon.Text = (url.Contains("youtube.com") || url.Contains("youtu.be")) ? "🎬" : "🌐";
            TxtFileMeta.Text = "Nguồn liên kết trực tuyến • Bấm 'Thêm thẳng' hoặc kéo thả vào NotebookLM";
            BtnClearFiles.Visibility = Visibility.Visible;
            if (BorderStagedSource != null)
            {
                BorderStagedSource.BorderBrush = (Brush)new BrushConverter().ConvertFromString("#0284C7")!;
                BorderStagedSource.Background = (Brush)new BrushConverter().ConvertFromString("#0F2238")!;
            }

            ShowModernToast("Đã nhận Link nguồn để add vào Sổ tay!", "🔗", "#10B981");
        }

        private void BtnPasteSourceLink_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    string clip = Clipboard.GetText().Trim();
                    if (clip.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || clip.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        SetSelectedLink(clip);
                        return;
                    }
                }
            }
            catch { }

            ShowModernToast("Hãy copy link (YouTube, Web, Doc...) vào Clipboard rồi bấm lại nút này!", "📋", "#38BDF8");
        }

        private void BtnOpenDownloads_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string folder = _currentFilePath != null && File.Exists(_currentFilePath)
                    ? Path.GetDirectoryName(_currentFilePath)!
                    : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\Downloads";

                if (Directory.Exists(folder))
                {
                    Process.Start("explorer.exe", folder);
                    ShowModernToast("Đã mở thư mục tệp!", "📂", "#10B981");
                }
            }
            catch (Exception ex)
            {
                ShowModernToast("Lỗi mở thư mục: " + ex.Message, "⚠️", "#EF4444");
            }
        }

        #region NotebookLM Direct API Bridge Methods
        private static string GetBridgeScriptPath()
        {
            string p1 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts", "notebooklm_bridge.py");
            if (File.Exists(p1)) return p1;
            string p2 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "scripts", "notebooklm_bridge.py");
            if (File.Exists(p2)) return Path.GetFullPath(p2);
            return p1;
        }

        private static (string fileName, string argsPrefix) GetBridgeCommand(string actionArgs)
        {
            // Prefer standalone compiled executable (No Python installation required on user's machine!)
            string[] exeCandidates = [
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts", "notebooklm_bridge.exe"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "scripts", "notebooklm_bridge.exe")
            ];
            foreach (var cand in exeCandidates)
            {
                if (File.Exists(cand))
                {
                    return (Path.GetFullPath(cand), actionArgs);
                }
            }

            // Fallback to python script if standalone executable is not found
            string scriptPath = GetBridgeScriptPath();
            return ("python", $"\"{scriptPath}\" {actionArgs}");
        }

        private async Task<string?> ResolveNotebookViaApiAsync(string notebookUrl)
        {
            try
            {
                var (cmdExe, cmdArgs) = GetBridgeCommand($"--action resolve --notebook \"{notebookUrl}\"");

                var psi = new ProcessStartInfo
                {
                    FileName = cmdExe,
                    Arguments = cmdArgs,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8
                };

                using var proc = Process.Start(psi);
                if (proc == null) return null;

                string output = await proc.StandardOutput.ReadToEndAsync();
                await proc.WaitForExitAsync();

                if (string.IsNullOrWhiteSpace(output)) return null;

                using var doc = System.Text.Json.JsonDocument.Parse(output);
                var root = doc.RootElement;
                if (root.TryGetProperty("success", out var succProp) && succProp.GetBoolean())
                {
                    return root.TryGetProperty("title", out var tProp) ? tProp.GetString() : null;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Resolve API Error: {ex.Message}");
            }
            return null;
        }

        private async Task<bool> SendSourceToNotebookViaApiAsync(string notebookUrl, SourceType type, string targetPathOrUrl)
        {
            try
            {
                string argType = type == SourceType.File ? "file" : "url";
                var (cmdExe, cmdArgs) = GetBridgeCommand($"--action add_source --notebook \"{notebookUrl}\" --type {argType} --target \"{targetPathOrUrl}\"");

                var psi = new ProcessStartInfo
                {
                    FileName = cmdExe,
                    Arguments = cmdArgs,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8
                };

                using var proc = Process.Start(psi);
                if (proc == null) return false;

                string output = await proc.StandardOutput.ReadToEndAsync();
                await proc.WaitForExitAsync();

                if (string.IsNullOrWhiteSpace(output)) return false;

                using var doc = System.Text.Json.JsonDocument.Parse(output);
                var root = doc.RootElement;
                if (root.TryGetProperty("success", out var succProp) && succProp.GetBoolean())
                {
                    string sourceTitle = root.TryGetProperty("title", out var tProp) ? tProp.GetString() ?? "" : "";
                    string nbTitle = root.TryGetProperty("notebook_title", out var nbProp) ? nbProp.GetString() ?? "" : "";
                    if (!string.IsNullOrWhiteSpace(nbTitle))
                    {
                        _notebookName = nbTitle;
                        TxtNotebookHeaderTitle.Text = $"NotebookLM • {_notebookName}";
                    }
                    TxtFileMeta.Text = $"✅ Đã thêm thẳng vào Nguồn NotebookLM: {sourceTitle}";
                    ShowModernToast($"✅ Đã nạp thành công vào Nguồn: {sourceTitle}!", "⚡", "#10B981");
                    return true;
                }
                else if (root.TryGetProperty("error", out var errProp))
                {
                    string errMsg = errProp.GetString() ?? "";
                    ShowModernToast($"⚠️ {errMsg}", "⚠️", "#EF4444");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"API Bridge Error: {ex.Message}");
            }
            return false;
        }
        #endregion

        private async void BtnSendToNotebookLM_Click(object sender, RoutedEventArgs e)
        {
            string url = !string.IsNullOrWhiteSpace(_notebookUrl) ? _notebookUrl : (InputNotebookUrl?.Text ?? "").Trim();
            if (!IsAuthenticNotebookLink(url))
            {
                ShowModernToast("Vui lòng dán link Sổ tay thật (.../notebook/ID) trước khi thêm nguồn!", "⚠️", "#F59E0B");
                OpenNotebookConfigModal();
                return;
            }

            if (_currentSourceType == SourceType.None ||
                (_currentSourceType == SourceType.File && string.IsNullOrEmpty(_currentFilePath)) ||
                (_currentSourceType == SourceType.Link && string.IsNullOrEmpty(_currentSourceUrl)))
            {
                ShowModernToast("Vui lòng kéo thả tệp hoặc dán Link nguồn trước khi thêm!", "⚠️", "#F59E0B");
                return;
            }

            string targetPayload = _currentSourceType == SourceType.File ? _currentFilePath! : _currentSourceUrl!;

            // DIRECT API INJECTION: Background addition directly to NotebookLM Sources
            // Pure background process: zero browser interference, zero window restoration, zero zoom!
            TxtFileMeta.Text = "⚡ Đang nạp trực tiếp vào Nguồn Sổ tay qua API...";
            ShowModernToast("⚡ Đang nạp thẳng vào Nguồn NotebookLM...", "⚡", "#38BDF8");

            bool apiSuccess = await SendSourceToNotebookViaApiAsync(url, _currentSourceType, targetPayload);
            if (apiSuccess)
            {
                // Added seamlessly in background! Do NOT touch browser at all.
                return;
            }

            // Fallback error toast if API bridge fails
            ShowModernToast("Không thể kết nối API nguồn NotebookLM. Vui lòng kiểm tra lại tài khoản hoặc link!", "⚠️", "#EF4444");
        }

        private void BtnClearFiles_Click(object sender, RoutedEventArgs e)
        {
            _currentSourceType = SourceType.None;
            _currentFilePath = null;
            _currentSourceUrl = null;
            TxtFileName.Text = "Kéo thả tệp hoặc bấm '🌐 Dán Link Web / YouTube' để nạp nguồn";
            TxtFileMeta.Text = "Chưa có nguồn tài liệu • Thả tệp hoặc dán link bài viết/video để thêm vào Sổ tay";
            TxtFileIcon.Text = "📄";
            BtnClearFiles.Visibility = Visibility.Collapsed;
            if (BorderStagedSource != null)
            {
                BorderStagedSource.BorderBrush = (Brush)new BrushConverter().ConvertFromString("#1E293B")!;
                BorderStagedSource.Background = (Brush)new BrushConverter().ConvertFromString("#0F172A")!;
            }
        }
        #endregion

        #region Modern HUD Floating Toast System
        private DispatcherTimer? _toastTimer;

        public void ShowModernToast(string message, string icon = "✨", string accentColor = "#10B981")
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    TxtToastMessage.Text = message;
                    TxtToastIcon.Text = icon;

                    var brush = (Brush)new BrushConverter().ConvertFromString(accentColor)!;
                    ModernToastHost.BorderBrush = brush;
                    ToastGlow.Color = (Color)ColorConverter.ConvertFromString(accentColor);

                    ModernToastHost.Visibility = Visibility.Visible;

                    // Slide down & Fade in
                    var fadeIn = new DoubleAnimation(0, 1.0, TimeSpan.FromMilliseconds(200));
                    var slideDown = new DoubleAnimation(-15, 0, TimeSpan.FromMilliseconds(200))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    };

                    ModernToastHost.BeginAnimation(OpacityProperty, fadeIn);
                    ToastTranslate.BeginAnimation(TranslateTransform.YProperty, slideDown);

                    _toastTimer?.Stop();
                    _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.6) };
                    _toastTimer.Tick += (s, ev) =>
                    {
                        _toastTimer.Stop();
                        var fadeOut = new DoubleAnimation(1.0, 0, TimeSpan.FromMilliseconds(250));
                        var slideUp = new DoubleAnimation(0, -15, TimeSpan.FromMilliseconds(250))
                        {
                            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
                        };
                        fadeOut.Completed += (s2, ev2) =>
                        {
                            ModernToastHost.Visibility = Visibility.Collapsed;
                        };
                        ModernToastHost.BeginAnimation(OpacityProperty, fadeOut);
                        ToastTranslate.BeginAnimation(TranslateTransform.YProperty, slideUp);
                    };
                    _toastTimer.Start();
                }
                catch { }
            });
        }
        #endregion

        #region State Transitions & Task Proportions
        private void SwitchState(IslandState newState)
        {
            if (_currentState == IslandState.Camera && newState != IslandState.Camera)
            {
                CloseCameraAppIfRunning();
            }

            if (_currentState == newState) return;
            ApplyState(newState, animate: true);
        }

        private void ApplyState(IslandState newState, bool animate)
        {
            _currentState = newState;

            double targetWidth = 560;
            double targetHeight = 38;

            // Hide subviews
            ViewMini.Visibility = Visibility.Collapsed;
            ViewCompact.Visibility = Visibility.Collapsed;
            ViewMusic.Visibility = Visibility.Collapsed;
            ViewPomodoro.Visibility = Visibility.Collapsed;
            ViewCamera.Visibility = Visibility.Collapsed;
            ViewNotification.Visibility = Visibility.Collapsed;
            ViewDropzone.Visibility = Visibility.Collapsed;
            ViewLockScreen.Visibility = Visibility.Collapsed;
            ViewUpdate.Visibility = Visibility.Collapsed;

            FrameworkElement targetView = ViewCompact;

            if (newState == IslandState.Orbital)
            {
                NotchRoot.Visibility = Visibility.Collapsed;
                ViewOrbital.Visibility = Visibility.Visible;
                targetView = ViewOrbital;
            }
            else
            {
                ViewOrbital.Visibility = Visibility.Collapsed;
                NotchRoot.BeginAnimation(OpacityProperty, null);
                NotchRoot.Opacity = 1.0;
                NotchRoot.Visibility = Visibility.Visible;

                switch (newState)
                {
                    case IslandState.Mini:
                        targetWidth = 74;
                        targetHeight = 32;
                        targetView = ViewMini;
                        CardGlow.Color = (Color)ColorConverter.ConvertFromString("#A855F7");
                        break;

                    case IslandState.Compact:
                        targetWidth = 580;
                        targetHeight = 38;
                        targetView = ViewCompact;
                        CardGlow.Color = (Color)ColorConverter.ConvertFromString("#38BDF8");
                        break;

                    case IslandState.Music:
                        targetWidth = 640;
                        targetHeight = 135;
                        targetView = ViewMusic;
                        CardGlow.Color = (Color)ColorConverter.ConvertFromString("#C084FC");
                        break;

                    case IslandState.Pomodoro:
                        targetWidth = 480;
                        targetHeight = 100;
                        targetView = ViewPomodoro;
                        CardGlow.Color = (Color)ColorConverter.ConvertFromString("#EAB308");
                        break;

                    case IslandState.Camera:
                        targetWidth = 640;
                        targetHeight = 136;
                        targetView = ViewCamera;
                        CardGlow.Color = (Color)ColorConverter.ConvertFromString("#10B981");
                        UpdateCameraUi(Process.GetProcessesByName("WindowsCamera").Length > 0);
                        UpdateCaptureFolderUi();
                        break;

                    case IslandState.Notification:
                        targetWidth = 680;
                        targetHeight = 190;
                        targetView = ViewNotification;
                        CardGlow.Color = (Color)ColorConverter.ConvertFromString("#0284C7");
                        break;

                    case IslandState.Dropzone:
                        targetWidth = 680;
                        targetHeight = 175;
                        targetView = ViewDropzone;
                        CardGlow.Color = (Color)ColorConverter.ConvertFromString("#10B981");
                        _ = DetectAndRefreshNotebookAsync(showToast: false);
                        break;

                    case IslandState.LockScreen:
                        targetWidth = 520;
                        targetHeight = 95;
                        targetView = ViewLockScreen;
                        CardGlow.Color = (Color)ColorConverter.ConvertFromString("#F43F5E");
                        break;

                    case IslandState.Update:
                        targetWidth = 520;
                        targetHeight = 135;
                        targetView = ViewUpdate;
                        CardGlow.Color = (Color)ColorConverter.ConvertFromString("#6366F1");
                        break;
                }

                if (!animate)
                {
                    UpdateNotchGeometry(targetWidth, targetHeight, 0, 0);
                }
            }

            targetView.Visibility = Visibility.Visible;

            if (animate)
            {
                if (newState == IslandState.Orbital)
                {
                    var fadeAnim = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(200));
                    ViewOrbital.BeginAnimation(OpacityProperty, fadeAnim);
                }
                else
                {
                    var duration = TimeSpan.FromMilliseconds(260);
                    var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

                    double currentW = IslandCard.ActualWidth > 0 ? IslandCard.ActualWidth : IslandCard.Width;
                    if (currentW < 350 || double.IsNaN(currentW))
                    {
                        currentW = targetWidth;
                        IslandCard.Width = targetWidth;
                    }

                    double currentH = IslandCard.ActualHeight > 0 ? IslandCard.ActualHeight : IslandCard.Height;
                    if (currentH < 25 || double.IsNaN(currentH))
                    {
                        currentH = targetHeight;
                        IslandCard.Height = targetHeight;
                    }

                    var animWidth = new DoubleAnimation(currentW, targetWidth, duration) { EasingFunction = ease };
                    var animHeight = new DoubleAnimation(currentH, targetHeight, duration) { EasingFunction = ease };

                    animHeight.Completed += (s, e) =>
                    {
                        UpdateNotchGeometry(targetWidth, targetHeight, 0, 0);
                    };

                    IslandCard.BeginAnimation(WidthProperty, animWidth);
                    IslandCard.BeginAnimation(HeightProperty, animHeight);

                    var fadeAnim = new DoubleAnimation(0.2, 1.0, TimeSpan.FromMilliseconds(220));
                    targetView.BeginAnimation(OpacityProperty, fadeAnim);
                }
            }
            else
            {
                IslandCard.BeginAnimation(WidthProperty, null);
                IslandCard.BeginAnimation(HeightProperty, null);
                IslandCard.Width = targetWidth;
                IslandCard.Height = targetHeight;
            }
        }
        #endregion

        #region User Interaction Handlers
        private void BtnResetCenter_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            SetHorizontalOffset(0);
            ShowModernToast("Đã căn giữa Dynamic Island!", "⦿", "#38BDF8");
        }

        // Close to compact mode
        private void BtnCloseToCompact_Click(object sender, RoutedEventArgs e) => SwitchState(IslandState.Compact);

        // Minisize button click: collapses to tiny floating disc
        private void BtnMiniSize_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            _preventPullDownUntil = DateTime.UtcNow.AddMilliseconds(500);
            SwitchState(IslandState.Mini);
        }

        // Exit / Shutdown App Completely
        private void BtnExitApp_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            Application.Current.Shutdown();
        }

        // Core Hub Click: returns to Compact via reverse droplet flow straight from Core
        private void CoreHub_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            _preventPullDownUntil = DateTime.UtcNow.AddMilliseconds(700);

            // If an update is available AND not muted today: show update confirmation modal!
            if (_availableUpdate != null && !IsUpdateMutedToday())
            {
                ShowUpdateModal();
                return;
            }

            // Normal behavior: return to compact notch via reverse droplet flow
            TriggerReverseDropletToTask(null, IslandState.Compact);
        }

        // Planet Click Handlers (Trigger Reverse Droplet Flow!)
        private void OrbMusic_Click(object sender, MouseButtonEventArgs e) => TriggerReverseDropletToTask(OrbMusic, IslandState.Music);
        private void OrbNotify_Click(object sender, MouseButtonEventArgs e) => TriggerReverseDropletToTask(OrbNotify, IslandState.Notification);
        private void OrbPomodoro_Click(object sender, MouseButtonEventArgs e) => TriggerReverseDropletToTask(OrbPomodoro, IslandState.Pomodoro);
        private void OrbDrop_Click(object sender, MouseButtonEventArgs e) => TriggerReverseDropletToTask(OrbDrop, IslandState.Dropzone);

        private void OrbCamera_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (Process.GetProcessesByName("WindowsCamera").Length == 0)
                {
                    Process.Start(new ProcessStartInfo("microsoft.windows.camera:") { UseShellExecute = true });
                }
            }
            catch { }
            TriggerReverseDropletToTask(OrbCamera, IslandState.Camera);
        }

        private void OrbLockScreen_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            LockWorkStation();
        }

        #region Camera Laptop & Webcam Handlers
        private void CloseCameraAppIfRunning()
        {
            try
            {
                var procs = Process.GetProcessesByName("WindowsCamera");
                foreach (var p in procs)
                {
                    try { p.Kill(); } catch { }
                }
                UpdateCameraUi(isRunning: false);
            }
            catch { }
        }

        private void UpdateCameraUi(bool isRunning)
        {
            Dispatcher.Invoke(() =>
            {
                if (isRunning)
                {
                    BtnToggleCamera.Content = "🛑 Tắt Camera";
                    BtnToggleCamera.Background = (Brush)new BrushConverter().ConvertFromString("#991B1B")!;
                    BtnToggleCamera.BorderBrush = (Brush)new BrushConverter().ConvertFromString("#EF4444")!;
                    TxtCameraStatus.Text = "🟢 Đang mở";
                    TxtCameraStatus.Foreground = (Brush)new BrushConverter().ConvertFromString("#34D399")!;
                    BorderCameraStatus.Background = (Brush)new BrushConverter().ConvertFromString("#064E3B")!;
                    BorderCameraStatus.BorderBrush = (Brush)new BrushConverter().ConvertFromString("#10B981")!;
                }
                else
                {
                    BtnToggleCamera.Content = "📷 Bật Camera";
                    BtnToggleCamera.Background = (Brush)new BrushConverter().ConvertFromString("#059669")!;
                    BtnToggleCamera.BorderBrush = (Brush)new BrushConverter().ConvertFromString("#34D399")!;
                    TxtCameraStatus.Text = "⚪ Đang tắt";
                    TxtCameraStatus.Foreground = (Brush)new BrushConverter().ConvertFromString("#94A3B8")!;
                    BorderCameraStatus.Background = (Brush)new BrushConverter().ConvertFromString("#1E293B")!;
                    BorderCameraStatus.BorderBrush = (Brush)new BrushConverter().ConvertFromString("#475569")!;
                }
            });
        }

        private void BtnToggleCamera_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var procs = Process.GetProcessesByName("WindowsCamera");
                if (procs.Length > 0)
                {
                    foreach (var p in procs)
                    {
                        try { p.Kill(); } catch { }
                    }
                    UpdateCameraUi(isRunning: false);
                    ShowModernToast("Đã tắt Camera laptop!", "🛑", "#EF4444");
                }
                else
                {
                    Process.Start(new ProcessStartInfo("microsoft.windows.camera:") { UseShellExecute = true });
                    UpdateCameraUi(isRunning: true);
                    ShowModernToast("Đang bật Camera laptop...", "📷", "#10B981");
                }
            }
            catch (Exception ex)
            {
                ShowModernToast("Không thể điều khiển Camera: " + ex.Message, "⚠️", "#F59E0B");
            }
        }

        private void BtnCameraSettings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("ms-settings:privacy-webcam") { UseShellExecute = true });
            }
            catch { }
        }
        #endregion

        #region Lock Screen Handlers
        private void BtnLockWorkstation_Click(object sender, RoutedEventArgs e)
        {
            LockWorkStation();
        }
        #endregion

        #region Notification & Messaging System (Facebook, Zalo, Realtime Filtering & Dismiss)
        public enum NotificationFilter { All, Zalo, Facebook }
        private NotificationFilter _currentFilter = NotificationFilter.All;
        private int _currentFilteredIndex = 0;

        public class DynamicNotification
        {
            public long Id { get; set; }
            public string AppName { get; set; } = "Zalo";
            public string AppIcon { get; set; } = "💬";
            public string AppColor { get; set; } = "#0068FF";
            public string Sender { get; set; } = "Zalo";
            public string Message { get; set; } = "";
            public string Time { get; set; } = "Vừa xong";
            public string? PrimaryId { get; set; }
        }

        public class OutboxMessage
        {
            public long Id { get; set; }
            public string App { get; set; } = "";
            public string Recipient { get; set; } = "";
            public string Message { get; set; } = "";
            public string Time { get; set; } = "";
        }

        private readonly List<OutboxMessage> _pendingOutboxMessages = new();
        private readonly object _outboxLock = new();

        private void PillCompactNotification_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            SwitchState(IslandState.Notification);
        }

        private List<DynamicNotification> GetFilteredNotifications()
        {
            return _currentFilter switch
            {
                NotificationFilter.Zalo => _realNotificationsList.Where(n => n.AppName.Equals("Zalo", StringComparison.OrdinalIgnoreCase) || (n.PrimaryId ?? "").Contains("zalo", StringComparison.OrdinalIgnoreCase)).ToList(),
                NotificationFilter.Facebook => _realNotificationsList.Where(n => n.AppName.Equals("Facebook", StringComparison.OrdinalIgnoreCase) || (n.PrimaryId ?? "").Contains("facebook", StringComparison.OrdinalIgnoreCase)).ToList(),
                _ => _realNotificationsList
            };
        }

        public void UpdateNotificationUI()
        {
            // Update filter buttons counter text
            int allCount = _realNotificationsList.Count;
            int zaloCount = _realNotificationsList.Count(n => n.AppName.Equals("Zalo", StringComparison.OrdinalIgnoreCase) || (n.PrimaryId ?? "").Contains("zalo", StringComparison.OrdinalIgnoreCase));
            int fbCount = _realNotificationsList.Count(n => n.AppName.Equals("Facebook", StringComparison.OrdinalIgnoreCase) || (n.PrimaryId ?? "").Contains("facebook", StringComparison.OrdinalIgnoreCase));

            BtnFilterAll.Content = $"Tất cả ({allCount})";
            BtnFilterZalo.Content = $"💬 Zalo ({zaloCount})";
            BtnFilterFacebook.Content = $"📘 Facebook ({fbCount})";

            ApplyFilterButtonStyle(BtnFilterAll, _currentFilter == NotificationFilter.All);
            ApplyFilterButtonStyle(BtnFilterZalo, _currentFilter == NotificationFilter.Zalo);
            ApplyFilterButtonStyle(BtnFilterFacebook, _currentFilter == NotificationFilter.Facebook);

            var filtered = GetFilteredNotifications();
            if (filtered.Count == 0)
            {
                _currentNotification = null;
                BorderMsgBubble.Visibility = Visibility.Collapsed;
                BorderEmptyState.Visibility = Visibility.Visible;
                GridReplyBar.Visibility = Visibility.Collapsed;
                TxtNotifySender.Text = "Đã xem hết";
                TxtAppTag.Text = _currentFilter == NotificationFilter.Zalo ? "Zalo" : _currentFilter == NotificationFilter.Facebook ? "Facebook" : "Hệ thống";
                TxtNotifyTime.Text = "Không còn thông báo chờ";
                TxtNotifCounter.Text = "0/0";
                PillCompactNotification.Visibility = Visibility.Collapsed;
                return;
            }

            BorderMsgBubble.Visibility = Visibility.Visible;
            BorderEmptyState.Visibility = Visibility.Collapsed;
            GridReplyBar.Visibility = Visibility.Visible;

            _currentFilteredIndex = Math.Clamp(_currentFilteredIndex, 0, filtered.Count - 1);
            var notif = filtered[_currentFilteredIndex];
            _currentNotification = notif;

            try
            {
                BadgeAppIcon.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(notif.AppColor));
                BadgeAppGlow.Color = (Color)ColorConverter.ConvertFromString(notif.AppColor);
                GlowCompactNotif.Color = (Color)ColorConverter.ConvertFromString(notif.AppColor);
            }
            catch
            {
                BadgeAppIcon.Background = new SolidColorBrush(Color.FromRgb(2, 132, 199));
            }

            TxtAppIcon.Text = notif.AppIcon;
            TxtAppTag.Text = notif.AppName;
            TxtNotifySender.Text = notif.Sender;
            TxtNotifyTime.Text = $"{notif.Time} • Thời gian thực";
            TxtNotifyContent.Text = notif.Message;
            TxtNotifCounter.Text = $"{_currentFilteredIndex + 1}/{filtered.Count}";
            InputReply.Text = "";

            // Update compact notification ticker on the notch bar
            PillCompactNotification.Visibility = Visibility.Visible;
            TxtCompactAppIcon.Text = notif.AppIcon;
            TxtCompactSender.Text = notif.Sender;
            TxtCompactMessage.Text = notif.Message;
            try
            {
                GlowCompactNotif.Color = (Color)ColorConverter.ConvertFromString(notif.AppColor);
            }
            catch { }
            ClockTimer_Tick(null, EventArgs.Empty);
        }

        private void ApplyFilterButtonStyle(Button btn, bool isActive)
        {
            if (isActive)
            {
                btn.Background = new SolidColorBrush(Color.FromRgb(2, 132, 199));
                btn.BorderBrush = new SolidColorBrush(Color.FromRgb(56, 189, 248));
                btn.Foreground = Brushes.White;
            }
            else
            {
                btn.Background = new SolidColorBrush(Color.FromRgb(26, 31, 44));
                btn.BorderBrush = new SolidColorBrush(Color.FromRgb(51, 65, 85));
                btn.Foreground = new SolidColorBrush(Color.FromRgb(241, 245, 249));
            }
        }

        private void BtnFilterAll_Click(object sender, RoutedEventArgs e)
        {
            _currentFilter = NotificationFilter.All;
            _currentFilteredIndex = 0;
            UpdateNotificationUI();
        }

        private void BtnFilterZalo_Click(object sender, RoutedEventArgs e)
        {
            _currentFilter = NotificationFilter.Zalo;
            _currentFilteredIndex = 0;
            UpdateNotificationUI();
        }

        private void BtnFilterFacebook_Click(object sender, RoutedEventArgs e)
        {
            _currentFilter = NotificationFilter.Facebook;
            _currentFilteredIndex = 0;
            UpdateNotificationUI();
        }

        private void LoadDeletedNotifications()
        {
            try
            {
                if (File.Exists(_deletedNotifsPath))
                {
                    var lines = File.ReadAllLines(_deletedNotifsPath);
                    foreach (var line in lines)
                    {
                        if (long.TryParse(line.Trim(), out long id))
                        {
                            _deletedNotificationIds.Add(id);
                        }
                    }
                }
                if (File.Exists(_deletedNotifHashesPath))
                {
                    var lines = File.ReadAllLines(_deletedNotifHashesPath);
                    foreach (var line in lines)
                    {
                        string trimmed = line.Trim();
                        if (!string.IsNullOrEmpty(trimmed))
                        {
                            _deletedNotificationHashes.Add(trimmed);
                        }
                    }
                }
            }
            catch { }
        }

        private void SaveDeletedNotifications()
        {
            try
            {
                string dir = Path.GetDirectoryName(_deletedNotifsPath)!;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllLines(_deletedNotifsPath, _deletedNotificationIds.Select(id => id.ToString()));
                File.WriteAllLines(_deletedNotifHashesPath, _deletedNotificationHashes);
            }
            catch { }
        }

        private void BtnDeleteNotification_Click(object sender, RoutedEventArgs e)
        {
            if (_currentNotification != null)
            {
                long id = _currentNotification.Id;
                string hash = RealNotificationService.ComputeFingerprint(_currentNotification.AppName, _currentNotification.Sender, _currentNotification.Message);

                _deletedNotificationIds.Add(id);
                _deletedNotificationHashes.Add(hash);
                SaveDeletedNotifications();

                _realNotificationsList.RemoveAll(n => n.Id == id || RealNotificationService.ComputeFingerprint(n.AppName, n.Sender, n.Message) == hash);
                var filtered = GetFilteredNotifications();
                if (_currentFilteredIndex >= filtered.Count)
                {
                    _currentFilteredIndex = Math.Max(0, filtered.Count - 1);
                }
                UpdateNotificationUI();
                ShowModernToast("Đã xóa vĩnh viễn thông báo này!", "🗑️", "#EF4444");
            }
        }

        private void BtnPrevNotif_Click(object sender, RoutedEventArgs e)
        {
            var filtered = GetFilteredNotifications();
            if (filtered.Count > 0)
            {
                _currentFilteredIndex = (_currentFilteredIndex - 1 + filtered.Count) % filtered.Count;
                UpdateNotificationUI();
            }
        }

        private void BtnNextNotif_Click(object sender, RoutedEventArgs e)
        {
            var filtered = GetFilteredNotifications();
            if (filtered.Count > 0)
            {
                _currentFilteredIndex = (_currentFilteredIndex + 1) % filtered.Count;
                UpdateNotificationUI();
            }
        }

        private void OnRealNotificationReceived(RealNotification r)
        {
            Dispatcher.InvokeAsync(() =>
            {
                string hash = RealNotificationService.ComputeFingerprint(r.AppName, r.Sender, r.Message);
                if (_deletedNotificationIds.Contains(r.Id) || _deletedNotificationHashes.Contains(hash)) return; // Ignore if deleted!

                var notif = new DynamicNotification
                {
                    Id = r.Id,
                    AppName = r.AppName,
                    AppIcon = r.AppIcon,
                    AppColor = r.AppColor,
                    Sender = r.Sender,
                    Message = r.Message,
                    Time = r.Time,
                    PrimaryId = r.PrimaryId
                };

                // Avoid duplicate
                if (!_realNotificationsList.Any(n => n.Id == notif.Id || RealNotificationService.ComputeFingerprint(n.AppName, n.Sender, n.Message) == hash))
                {
                    _realNotificationsList.Insert(0, notif);
                }

                // If currently filtered by specific app, match filter
                if (_currentFilter == NotificationFilter.Zalo && !notif.AppName.Equals("Zalo", StringComparison.OrdinalIgnoreCase))
                {
                    _currentFilter = NotificationFilter.All;
                }
                else if (_currentFilter == NotificationFilter.Facebook && !notif.AppName.Equals("Facebook", StringComparison.OrdinalIgnoreCase))
                {
                    _currentFilter = NotificationFilter.All;
                }

                _currentFilteredIndex = 0;
                UpdateNotificationUI();

                if (_currentState == IslandState.Compact || _currentState == IslandState.Mini)
                {
                    SwitchState(IslandState.Notification);
                }
            });
        }

        public void ShowNotification(DynamicNotification notif)
        {
            string hash = RealNotificationService.ComputeFingerprint(notif.AppName, notif.Sender, notif.Message);
            if (_deletedNotificationIds.Contains(notif.Id) || _deletedNotificationHashes.Contains(hash)) return;

            if (!_realNotificationsList.Any(n => n.Id == notif.Id || RealNotificationService.ComputeFingerprint(n.AppName, n.Sender, n.Message) == hash))
            {
                _realNotificationsList.Insert(0, notif);
            }
            _currentFilteredIndex = 0;
            UpdateNotificationUI();
            SwitchState(IslandState.Notification);
        }

        #region Local HTTP Webhook Server for Facebook & Zalo Messages
        private HttpListener? _httpServer;
        private CancellationTokenSource? _serverCts;

        private void StartLocalMessageApiServer()
        {
            try
            {
                _serverCts = new CancellationTokenSource();
                _httpServer = new HttpListener();
                _httpServer.Prefixes.Add("http://127.0.0.1:5005/api/");
                _httpServer.Start();

                Task.Run(() => ListenForApiRequestsAsync(_serverCts.Token));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Local API Server start error: {ex.Message}");
            }
        }

        private async Task ListenForApiRequestsAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _httpServer != null && _httpServer.IsListening)
            {
                try
                {
                    var ctx = await _httpServer.GetContextAsync();
                    _ = ProcessApiRequestAsync(ctx);
                }
                catch when (token.IsCancellationRequested)
                {
                    break;
                }
                catch { }
            }
        }

        private async Task ProcessApiRequestAsync(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var res = ctx.Response;

            // Enable Full Cross-Origin Resource Sharing (CORS) & Chrome Private Network Access (PNA)
            res.Headers.Add("Access-Control-Allow-Origin", "*");
            res.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            res.Headers.Add("Access-Control-Allow-Headers", "Content-Type, Access-Control-Allow-Private-Network, *");
            res.Headers.Add("Access-Control-Allow-Private-Network", "true");
            res.Headers.Add("Access-Control-Max-Age", "86400");

            if (req.HttpMethod == "OPTIONS")
            {
                res.StatusCode = 204;
                res.Close();
                return;
            }

            try
            {
                string path = req.Url?.AbsolutePath.ToLowerInvariant() ?? "";

                // Interactive Test Endpoint: visiting in browser triggers test alert and displays web dashboard
                if (path.EndsWith("/test") || path.Contains("/test/"))
                {
                    string testApp = path.Contains("zalo") ? "Zalo" : (path.Contains("facebook") ? "Facebook" : "Facebook & Zalo");
                    string testSender = path.Contains("zalo") ? "Zalo Test" : "Facebook Test";
                    string testMsg = path.Contains("zalo")
                        ? "💬 Tin nhắn Zalo test kết nối thành công vào Dynamic Island!"
                        : "📘 Tin nhắn Facebook test kết nối thành công vào Dynamic Island!";

                    bool isZalo = testApp.Contains("Zalo");
                    var testNotif = new DynamicNotification
                    {
                        Id = DateTime.Now.Ticks,
                        AppName = isZalo ? "Zalo" : "Facebook",
                        AppIcon = isZalo ? "💬" : "📘",
                        AppColor = isZalo ? "#0068FF" : "#1877F2",
                        Sender = testSender,
                        Message = testMsg,
                        Time = "Vừa xong",
                        PrimaryId = isZalo ? "com.vng.zalo" : "facebook"
                    };

                    await Dispatcher.InvokeAsync(() =>
                    {
                        ShowNotification(testNotif);
                    });

                    string html = @"<!DOCTYPE html>
<html lang=""vi"">
<head>
  <meta charset=""utf-8""/>
  <meta name=""viewport"" content=""width=device-width, initial-scale=1""/>
  <title>Dynamic Island Message Bridge</title>
  <style>
    body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; background: #0b0f19; color: #f1f5f9; padding: 40px 20px; text-align: center; }
    .card { max-width: 600px; margin: 0 auto; background: #1e293b; border-radius: 20px; padding: 36px; box-shadow: 0 20px 40px rgba(0,0,0,0.6); border: 1px solid #334155; }
    h1 { color: #38bdf8; margin: 12px 0; font-size: 24px; }
    .badge { display: inline-block; padding: 6px 16px; background: #064e3b; color: #34d399; border-radius: 999px; font-size: 13px; font-weight: 700; letter-spacing: 0.5px; }
    p { color: #94a3b8; font-size: 14px; line-height: 1.6; }
    .btn-group { margin: 28px 0; display: flex; justify-content: center; gap: 14px; flex-wrap: wrap; }
    .btn { padding: 12px 24px; border-radius: 12px; font-weight: 600; text-decoration: none; color: white; display: inline-block; font-size: 14px; transition: transform 0.15s, opacity 0.15s; }
    .btn:hover { transform: translateY(-2px); opacity: 0.95; }
    .btn-fb { background: #1877f2; }
    .btn-zalo { background: #0068ff; }
    .status-box { background: #0f172a; border-radius: 12px; padding: 16px; margin-top: 24px; text-align: left; font-family: monospace; font-size: 13px; color: #38bdf8; }
  </style>
</head>
<body>
  <div class=""card"">
    <div class=""badge"">🟢 HTTP API Port 5005 Online</div>
    <h1>🏝️ Dynamic Island Message Bridge</h1>
    <p>Hệ thống nhận thông báo Facebook &amp; Zalo đang hoạt động chuẩn xác trên Windows! Click nút bên dưới để thử nghiệm hiển thị lên Dynamic Island ngay lập tức.</p>
    <div class=""btn-group"">
      <a href=""/api/test/facebook"" class=""btn btn-fb"">📘 Test Thông Báo Facebook</a>
      <a href=""/api/test/zalo"" class=""btn btn-zalo"">💬 Test Thông Báo Zalo</a>
    </div>
    <div class=""status-box"">
      &bull; Endpoint: http://127.0.0.1:5005/api/message<br/>
      &bull; PNA (Private Network Access): Enabled<br/>
      &bull; Trạng thái: Sẵn sàng nhận tin nhắn từ Facebook và Zalo
    </div>
  </div>
</body>
</html>";
                    byte[] htmlBytes = Encoding.UTF8.GetBytes(html);
                    res.ContentType = "text/html; charset=utf-8";
                    res.StatusCode = 200;
                    await res.OutputStream.WriteAsync(htmlBytes);
                    return;
                }
                else if (path.EndsWith("/message") || path.EndsWith("/notification"))
                {
                    string app = "Facebook";
                    string sender = "";
                    string message = "";
                    string time = "Vừa xong";

                    if (req.HttpMethod == "POST")
                    {
                        using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
                        string body = await reader.ReadToEndAsync();
                        if (!string.IsNullOrWhiteSpace(body))
                        {
                            using var doc = System.Text.Json.JsonDocument.Parse(body);
                            var root = doc.RootElement;
                            if (root.TryGetProperty("app", out var appProp)) app = appProp.GetString() ?? "Facebook";
                            if (root.TryGetProperty("sender", out var sProp)) sender = sProp.GetString() ?? "Người dùng";
                            if (root.TryGetProperty("message", out var mProp)) message = mProp.GetString() ?? "";
                            if (root.TryGetProperty("time", out var tProp)) time = tProp.GetString() ?? "Vừa xong";
                        }
                    }
                    else if (req.HttpMethod == "GET")
                    {
                        var qs = req.QueryString;
                        if (!string.IsNullOrEmpty(qs["app"])) app = qs["app"]!;
                        if (!string.IsNullOrEmpty(qs["sender"])) sender = qs["sender"]!;
                        if (!string.IsNullOrEmpty(qs["message"])) message = qs["message"]!;
                        if (!string.IsNullOrEmpty(qs["time"])) time = qs["time"]!;
                    }

                    if (!string.IsNullOrWhiteSpace(sender) || !string.IsNullOrWhiteSpace(message))
                    {
                        bool isZalo = app.Contains("zalo", StringComparison.OrdinalIgnoreCase);
                        sender = string.IsNullOrWhiteSpace(sender) ? (isZalo ? "Zalo" : "Facebook") : sender.Trim();
                        message = (message ?? "").Trim();

                        var notif = new DynamicNotification
                        {
                            Id = DateTime.Now.Ticks,
                            AppName = isZalo ? "Zalo" : "Facebook",
                            AppIcon = isZalo ? "💬" : "📘",
                            AppColor = isZalo ? "#0068FF" : "#1877F2",
                            Sender = sender,
                            Message = message,
                            Time = time,
                            PrimaryId = isZalo ? "com.vng.zalo" : "facebook"
                        };

                        await Dispatcher.InvokeAsync(() =>
                        {
                            ShowNotification(notif);
                        });
                    }

                    // If request came from an Image ping (e.g. new Image().src = ...), return 1x1 GIF
                    if (req.AcceptTypes != null && req.AcceptTypes.Any(t => t.Contains("image", StringComparison.OrdinalIgnoreCase)))
                    {
                        byte[] gif1x1 = new byte[] {
                            0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01, 0x00, 0x01, 0x00, 0x80, 0x00, 0x00,
                            0xff, 0xff, 0xff, 0x00, 0x00, 0x00, 0x21, 0xf9, 0x04, 0x01, 0x00, 0x00, 0x00,
                            0x00, 0x2c, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x02, 0x02,
                            0x44, 0x01, 0x00, 0x3b
                        };
                        res.ContentType = "image/gif";
                        res.StatusCode = 200;
                        await res.OutputStream.WriteAsync(gif1x1);
                    }
                    else
                    {
                        byte[] respBytes = Encoding.UTF8.GetBytes("{\"success\": true, \"received\": true}");
                        res.ContentType = "application/json";
                        res.StatusCode = 200;
                        await res.OutputStream.WriteAsync(respBytes);
                    }
                }
                else if (path.EndsWith("/outbox"))
                {
                    string json;
                    lock (_outboxLock)
                    {
                        json = System.Text.Json.JsonSerializer.Serialize(_pendingOutboxMessages);
                        _pendingOutboxMessages.Clear();
                    }
                    byte[] bytes = Encoding.UTF8.GetBytes(json);
                    res.ContentType = "application/json";
                    res.StatusCode = 200;
                    await res.OutputStream.WriteAsync(bytes);
                }
                else if (path.EndsWith("/status"))
                {
                    string json = $"{{\"status\": \"running\", \"pna\": true, \"count\": {_realNotificationsList.Count}, \"state\": \"{_currentState}\"}}";
                    byte[] bytes = Encoding.UTF8.GetBytes(json);
                    res.ContentType = "application/json";
                    res.StatusCode = 200;
                    await res.OutputStream.WriteAsync(bytes);
                }
                else
                {
                    res.StatusCode = 404;
                }
            }
            catch (Exception ex)
            {
                res.StatusCode = 500;
                byte[] errBytes = Encoding.UTF8.GetBytes($"{{\"error\": \"{ex.Message}\"}}");
                await res.OutputStream.WriteAsync(errBytes);
            }
            finally
            {
                try { res.Close(); } catch { }
            }
        }
        #endregion

        private void InputReply_TextChanged(object sender, TextChangedEventArgs e)
        {
            TxtReplyPlaceholder.Visibility = string.IsNullOrEmpty(InputReply.Text) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void InputReply_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                BtnSendReply_Click(sender, e);
            }
        }

        private void BtnSendReply_Click(object sender, RoutedEventArgs e)
        {
            string reply = InputReply.Text.Trim();
            if (string.IsNullOrWhiteSpace(reply)) return;

            string senderName = _currentNotification?.Sender ?? TxtNotifySender.Text;
            string appName = _currentNotification?.AppName ?? TxtAppTag.Text;

            // 1. Silent Background Send (Zero browser tab opening or window switching!)
            bool sentSilently = RealNotificationService.TrySendSilentReply(appName, senderName, reply);

            // 2. Safely copy to clipboard as seamless backup with retry
            try
            {
                Clipboard.SetDataObject(reply, true);
            }
            catch { }

            // 3. Enqueue to silent background outbox queue
            lock (_outboxLock)
            {
                _pendingOutboxMessages.Add(new OutboxMessage
                {
                    Id = DateTime.Now.Ticks,
                    App = appName,
                    Recipient = senderName,
                    Message = reply,
                    Time = DateTime.Now.ToString("HH:mm:ss")
                });
            }

            // 4. Mark current notification as permanently handled and dismissed
            if (_currentNotification != null)
            {
                _deletedNotificationIds.Add(_currentNotification.Id);
                string hash = RealNotificationService.ComputeFingerprint(_currentNotification.AppName, _currentNotification.Sender, _currentNotification.Message);
                _deletedNotificationHashes.Add(hash);
                SaveDeletedNotifications();
                _realNotificationsList.RemoveAll(n => n.Id == _currentNotification.Id);
                _currentNotification = null;
            }

            // 5. Clear reply input
            InputReply.Text = "";

            // 6. Modern feedback toast (Zero outgoing message on Core Island!)
            ShowModernToast($"✓ Đã gửi phản hồi tới {senderName}!", "✓", "#10B981");

            // CRITICAL: NEVER display outgoing messages from user ("Bạn") on the compact island at Core!
            PillCompactNotification.Visibility = Visibility.Collapsed;

            // 7. Smoothly collapse back to Compact state
            SwitchState(IslandState.Compact);
        }

        private void ChipReply_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Content != null)
            {
                InputReply.Text = btn.Content.ToString();
                BtnSendReply_Click(sender, e);
            }
        }

        private void BtnShareFile_Click(object sender, RoutedEventArgs e)
        {
            string fileName = _currentFilePath != null ? Path.GetFileName(_currentFilePath) : "file đính kèm";
            var shareNotif = new DynamicNotification
            {
                Id = DateTime.Now.Ticks,
                AppName = "Zalo",
                AppIcon = "💬",
                AppColor = "#0068FF",
                Sender = "Chia sẻ tệp tin",
                Message = $"Đang gửi tệp: {fileName} qua tin nhắn Zalo...",
                PrimaryId = "com.vng.zalo"
            };
            ShowNotification(shareNotif);
            InputReply.Text = $"Gửi bạn tệp {fileName} nhé!";
            InputReply.Focus();
        }
        #endregion

        #region Auto-Update System
        private UpdateInfo? _availableUpdate;

        private static string GetMuteSettingsFilePath()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DynamicIsland");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "update_muted.txt");
        }

        private bool IsUpdateMutedToday()
        {
            try
            {
                string file = GetMuteSettingsFilePath();
                if (File.Exists(file))
                {
                    string dateStr = File.ReadAllText(file).Trim();
                    if (dateStr == DateTime.Today.ToString("yyyy-MM-dd"))
                    {
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private void SetUpdateMutedForToday()
        {
            try
            {
                string file = GetMuteSettingsFilePath();
                File.WriteAllText(file, DateTime.Today.ToString("yyyy-MM-dd"));
            }
            catch { }
        }

        private async Task CheckForUpdateInBackgroundAsync()
        {
            await Task.Delay(3000);
            try
            {
                var update = await UpdateService.CheckForUpdatesAsync();
                if (update != null)
                {
                    _availableUpdate = update;
                    await Dispatcher.InvokeAsync(() =>
                    {
                        UpdateCorePlanetAppearance();
                    });
                }
                else
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        ResetCorePlanetAppearance();
                    });
                }
            }
            catch { }
        }

        private void UpdateCorePlanetAppearance()
        {
            // If already on latest version OR user checked "Không hỏi lại trong hôm nay":
            // Core planet remains 100% original, exactly as default!
            if (_availableUpdate == null || IsUpdateMutedToday())
            {
                ResetCorePlanetAppearance();
                return;
            }

            // Highlight Core Planet with glowing Update rocket badge!
            BadgeCoreUpdate.Visibility = Visibility.Visible;
            TxtCoreUpdateVersion.Text = _availableUpdate.VersionTag;
            BlackHoleGlow.Color = (Color)ColorConverter.ConvertFromString("#818CF8");
            BlackHoleCore.ToolTip = $"🚀 Có bản cập nhật mới {_availableUpdate.VersionTag}! Nhấp vào hố đen trung tâm để nâng cấp.";
        }

        private void ResetCorePlanetAppearance()
        {
            BadgeCoreUpdate.Visibility = Visibility.Collapsed;
            BlackHoleGlow.Color = (Color)ColorConverter.ConvertFromString("#EA580C");
            BlackHoleCore.ToolTip = "Hố đen trung tâm - Nhấp để thu gọn về thanh đảo";
        }

        private void ShowUpdateModal()
        {
            if (_availableUpdate == null) return;
            TxtModalUpdateVersion.Text = $"Dynamic Island {_availableUpdate.VersionTag}";
            TxtModalUpdateChangelog.Text = string.IsNullOrWhiteSpace(_availableUpdate.Changelog)
                ? "Bản cập nhật mới với nhiều cải tiến hiệu năng và sửa lỗi!"
                : _availableUpdate.Changelog;
            ChkDoNotAskToday.IsChecked = false;
            GridModalProgress.Visibility = Visibility.Collapsed;
            BtnModalConfirm.IsEnabled = true;
            BtnModalDismiss.IsEnabled = true;
            ModalUpdateHost.Visibility = Visibility.Visible;
        }

        private void BtnCancelModalUpdate_Click(object sender, RoutedEventArgs e)
        {
            ModalUpdateHost.Visibility = Visibility.Collapsed;

            // If user checked "Không hỏi lại trong hôm nay":
            if (ChkDoNotAskToday.IsChecked == true)
            {
                SetUpdateMutedForToday();
                // Immediately revert Core Planet to its original clean state with no badge or indicator!
                ResetCorePlanetAppearance();
                ShowModernToast("Đã tắt thông báo cập nhật trong hôm nay.", "ℹ️", "#94A3B8");
            }
        }

        private async void BtnConfirmModalUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (_availableUpdate == null) return;
            BtnModalConfirm.IsEnabled = false;
            BtnModalDismiss.IsEnabled = false;
            GridModalProgress.Visibility = Visibility.Visible;
            PbModalProgress.Value = 0;
            TxtModalPercent.Text = "0%";
            TxtModalUpdateChangelog.Text = "Đang tải bản cập nhật mới...";

            var progress = new Progress<int>(percent =>
            {
                PbModalProgress.Value = percent;
                TxtModalPercent.Text = $"{percent}%";
                TxtModalUpdateChangelog.Text = $"Đang tải bản cập nhật: {percent}%...";
            });

            try
            {
                await UpdateService.DownloadAndInstallUpdateAsync(_availableUpdate, progress);
            }
            catch (Exception ex)
            {
                BtnModalConfirm.IsEnabled = true;
                BtnModalDismiss.IsEnabled = true;
                GridModalProgress.Visibility = Visibility.Collapsed;
                TxtModalUpdateChangelog.Text = $"Lỗi cập nhật: {ex.Message}";
                ShowModernToast("Tải bản cập nhật thất bại!", "❌", "#EF4444");
            }
        }

        private void PillMediaMini_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            SwitchState(IslandState.Music);
        }

        private async void BtnApplyUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (_availableUpdate == null) return;
            BtnApplyUpdate.IsEnabled = false;
            BtnCancelUpdate.IsEnabled = false;
            GridUpdateProgress.Visibility = Visibility.Visible;
            PbUpdateProgress.Value = 0;
            TxtUpdatePercent.Text = "0%";
            TxtUpdateChangelog.Text = "Đang tải gói cập nhật...";

            var progress = new Progress<int>(percent =>
            {
                PbUpdateProgress.Value = percent;
                TxtUpdatePercent.Text = $"{percent}%";
                TxtUpdateChangelog.Text = $"Đang tải bản cập nhật: {percent}%...";
            });

            try
            {
                await UpdateService.DownloadAndInstallUpdateAsync(_availableUpdate, progress);
            }
            catch (Exception ex)
            {
                BtnApplyUpdate.IsEnabled = true;
                BtnCancelUpdate.IsEnabled = true;
                GridUpdateProgress.Visibility = Visibility.Collapsed;
                TxtUpdateChangelog.Text = $"Lỗi cập nhật: {ex.Message}";
                ShowModernToast("Tải bản cập nhật thất bại!", "❌", "#EF4444");
            }
        }
        #endregion
        #endregion
    }
}
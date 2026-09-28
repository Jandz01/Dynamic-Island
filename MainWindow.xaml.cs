using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.Json;
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

        // Calendar & Schedule Reminder System
        private readonly List<ScheduleReminder> _remindersList = new();
        private ScheduleReminder? _activeReminder = null;
        private ReminderFilter _currentReminderFilter = ReminderFilter.All;
        private int _currentReminderIndex = 0;
        private DispatcherTimer? _reminderAutoHideTimer;
        private bool _isAlertAutoExpanding = false;
        private int _selectedDayOffset = 0;
        private int _selectedCategoryIndex = 0;
        private int _calCategoryIndex = 0;
        private DateTime _calendarViewingMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
        private DateTime _calendarSelectedDate = DateTime.Today;
        private int _romCheckCounter = 0;
        private string _cachedRomPercent = "45%";
        private string _cachedRomTooltip = "💾 Ổ đĩa hệ thống (C:)";
        private Point _taskDragStartPoint;
        private ScheduleReminder? _draggedReminder = null;
        private Border? _draggedCard = null;
        private static readonly string[] Categories = { "💼 Công việc", "📚 Học tập", "⭐ Quan trọng", "🏠 Cá nhân" };
        private static readonly string[] DayOptions = { "📅 Hôm nay", "📅 Ngày mai", "📅 Ngày kia" };
        private readonly string _remindersFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DynamicIsland", "reminders.json"
        );

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

            // Pomodoro Timer (1s)
            _pomoTimer.Interval = TimeSpan.FromSeconds(1);
            _pomoTimer.Tick += PomoTimer_Tick;

            // Media Position Tracker (250ms for smooth real-time progress, active only in Music mode)
            _mediaTrackTimer.Interval = TimeSpan.FromMilliseconds(250);
            _mediaTrackTimer.Tick += MediaTrackTimer_Tick;

            // Initialize Windows Media
            await InitMediaManagerAsync();

            // Initialize Calendar & Schedule Reminder System
            LoadReminders();

            // Initialize NotebookLM configuration
            LoadNotebookLmConfig();

            // Initial view: Compact Notch (580px for spacious layout without overlap)
            ApplyState(IslandState.Compact, animate: false);
            UpdateNotchGeometry(580, 38, 0, 0);

            // Update Reminder UI with loaded tasks
            UpdateRemindersUI();

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
            try
            {
                double screenWidth = SystemParameters.PrimaryScreenWidth;
                double maxOffset = Math.Max(0, (screenWidth - IslandCard.Width) / 2 - 20);
                _horizontalOffset = Math.Clamp(offset, -maxOffset, maxOffset);

                double desiredLeft = (screenWidth - Width) / 2 + _horizontalOffset;

                var moveAnim = new DoubleAnimation(Left, desiredLeft, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                BeginAnimation(LeftProperty, moveAnim);
            }
            catch { }
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

                double minDipLeft = leftCornerEnd + 8;
                double maxDipLeft = Math.Max(minDipLeft, rightCornerStart - 20);
                double dipLeft = Math.Clamp(sagX - dipWidth, minDipLeft, maxDipLeft);

                double minDipRight = dipLeft + 20;
                double maxDipRight = Math.Max(minDipRight, rightCornerStart - 8);
                double dipRight = Math.Clamp(sagX + dipWidth, minDipRight, maxDipRight);
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

            try
            {
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
                    double minSag = Math.Min(25.0, (IslandCard.Width + 44) / 4.0);
                    double maxSag = Math.Max(minSag, IslandCard.Width + 44 - minSag);
                    double sagX = Math.Clamp(cur.X, minSag, maxSag);

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
            catch { }
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

            // Realtime check for due schedule reminders
            CheckDueReminders();

            if (ViewNotification != null && ViewNotification.Visibility == Visibility.Visible && TxtCurrentDateSub != null)
            {
                TxtCurrentDateSub.Text = DateTime.Now.ToString("dddd, dd/MM/yyyy", viCulture);
            }

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

            // Real ROM (Drive C) with exact GB calculation & rich tooltip (queried every 30s to prevent disk I/O lag)
            if (_romCheckCounter++ % 30 == 0)
            {
                try
                {
                    var drive = new DriveInfo("C");
                    double totalRom = drive.TotalSize;
                    double freeRom = drive.AvailableFreeSpace;
                    double usedRom = totalRom - freeRom;
                    int romPercent = (int)((usedRom / totalRom) * 100);
                    _cachedRomPercent = $"{romPercent}%";
                    double totalRomGb = totalRom / (1024.0 * 1024.0 * 1024.0);
                    double freeRomGb = freeRom / (1024.0 * 1024.0 * 1024.0);
                    double usedRomGb = usedRom / (1024.0 * 1024.0 * 1024.0);
                    _cachedRomTooltip = $"💾 Ổ ĐĨA HỆ THỐNG (C:)\n• Đã dùng: {usedRomGb:F1} GB / {totalRomGb:F1} GB ({romPercent}%)\n• Còn trống: {freeRomGb:F1} GB";
                }
                catch
                {
                    _cachedRomPercent = "45%";
                    _cachedRomTooltip = "💾 Ổ đĩa hệ thống (C:)";
                }
            }
            TxtRom.Text = _cachedRomPercent;
            PillRom.ToolTip = _cachedRomTooltip;

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

            // Media position tracker is only needed when Music view is visible
            if (newState == IslandState.Music)
            {
                _mediaTrackTimer.Start();
            }
            else
            {
                _mediaTrackTimer.Stop();
            }

            if (newState == IslandState.Orbital)
            {
                NotchRoot.Visibility = Visibility.Collapsed;
                ViewOrbital.Visibility = Visibility.Visible;
                targetView = ViewOrbital;
                CompositionTarget.Rendering -= CompositionTarget_Rendering;
                CompositionTarget.Rendering += CompositionTarget_Rendering;
            }
            else
            {
                CompositionTarget.Rendering -= CompositionTarget_Rendering;
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
                        targetWidth = 710;
                        targetHeight = 245;
                        targetView = ViewNotification;
                        CardGlow.Color = (Color)ColorConverter.ConvertFromString("#0284C7");
                        UpdateRemindersUI();
                        RenderCalendarDays();
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

        #region Calendar & Schedule Reminder System
        public enum ReminderFilter { All, Today, Upcoming, Done }

        public class ScheduleReminder
        {
            public string Id { get; set; } = Guid.NewGuid().ToString("N");
            public string Title { get; set; } = "";
            public DateTime DueDate { get; set; }
            public string Category { get; set; } = "💼 Công việc";
            public bool IsCompleted { get; set; } = false;
            public bool IsNotified { get; set; } = false;
            public bool IsChecked { get; set; } = false;
            public int PriorityOrder { get; set; } = 0;
            public DateTime CreatedAt { get; set; } = DateTime.Now;
        }

        private void LoadReminders()
        {
            try
            {
                if (File.Exists(_remindersFilePath))
                {
                    string json = File.ReadAllText(_remindersFilePath);
                    var items = JsonSerializer.Deserialize<List<ScheduleReminder>>(json);
                    if (items != null)
                    {
                        _remindersList.Clear();
                        bool allZero = items.Count > 1 && items.All(x => x.PriorityOrder == 0);
                        if (allZero)
                        {
                            for (int i = 0; i < items.Count; i++) items[i].PriorityOrder = i;
                        }
                        _remindersList.AddRange(items.OrderBy(x => x.PriorityOrder));
                    }
                }
                else
                {
                    // Seed initial sample reminders for today so user immediately sees how it works
                    _remindersList.Clear();
                    _remindersList.Add(new ScheduleReminder
                    {
                        Title = "Kiểm tra tiến độ công việc trong ngày",
                        DueDate = DateTime.Today.AddHours(Math.Min(23, DateTime.Now.Hour + 1)),
                        Category = "💼 Công việc",
                        IsCompleted = false,
                        IsNotified = false,
                        IsChecked = false,
                        PriorityOrder = 0
                    });
                    _remindersList.Add(new ScheduleReminder
                    {
                        Title = "Đọc tài liệu & tổng kết task",
                        DueDate = DateTime.Today.AddHours(20),
                        Category = "📚 Học tập",
                        IsCompleted = false,
                        IsNotified = false,
                        IsChecked = false,
                        PriorityOrder = 1
                    });
                    SaveReminders();
                }
            }
            catch { }
        }

        private void SaveReminders()
        {
            try
            {
                string? dir = Path.GetDirectoryName(_remindersFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                string json = JsonSerializer.Serialize(_remindersList, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_remindersFilePath, json);
            }
            catch { }
        }

        private List<ScheduleReminder> GetFilteredReminders()
        {
            var now = DateTime.Now;
            var today = DateTime.Today;

            return _currentReminderFilter switch
            {
                ReminderFilter.Today => _remindersList
                    .Where(r => r.DueDate.Date == today)
                    .OrderBy(r => r.IsCompleted)
                    .ThenBy(r => r.PriorityOrder)
                    .ThenBy(r => r.DueDate)
                    .ToList(),

                ReminderFilter.Upcoming => _remindersList
                    .Where(r => r.DueDate > now && !r.IsCompleted)
                    .OrderBy(r => r.PriorityOrder)
                    .ThenBy(r => r.DueDate)
                    .ToList(),

                ReminderFilter.Done => _remindersList
                    .Where(r => r.IsCompleted)
                    .OrderBy(r => r.PriorityOrder)
                    .ThenByDescending(r => r.DueDate)
                    .ToList(),

                _ => _remindersList
                    .OrderBy(r => r.IsCompleted)
                    .ThenBy(r => r.PriorityOrder)
                    .ThenBy(r => r.DueDate)
                    .ToList()
            };
        }

        private ScheduleReminder? GetCurrentSelectedReminder()
        {
            var filtered = GetFilteredReminders();
            if (filtered.Count == 0) return null;
            _currentReminderIndex = Math.Clamp(_currentReminderIndex, 0, filtered.Count - 1);
            return filtered[_currentReminderIndex];
        }

        public void UpdateRemindersUI()
        {
            // Update filter counters
            int allCount = _remindersList.Count;
            int todayCount = _remindersList.Count(r => r.DueDate.Date == DateTime.Today);
            int upcomingCount = _remindersList.Count(r => r.DueDate > DateTime.Now && !r.IsCompleted);
            int doneCount = _remindersList.Count(r => r.IsCompleted);
            int pendingDueCount = _remindersList.Count(r => !r.IsCompleted && DateTime.Now >= r.DueDate);

            BtnFilterAll.Content = $"Tất cả ({allCount})";
            BtnFilterToday.Content = $"Hôm nay ({todayCount})";
            BtnFilterUpcoming.Content = $"Sắp tới ({upcomingCount})";
            BtnFilterDone.Content = $"Đã xong ({doneCount})";

            ApplyFilterButtonStyle(BtnFilterAll, _currentReminderFilter == ReminderFilter.All);
            ApplyFilterButtonStyle(BtnFilterToday, _currentReminderFilter == ReminderFilter.Today);
            ApplyFilterButtonStyle(BtnFilterUpcoming, _currentReminderFilter == ReminderFilter.Upcoming);
            ApplyFilterButtonStyle(BtnFilterDone, _currentReminderFilter == ReminderFilter.Done);

            // Subtitle & badge
            var viCulture = new CultureInfo("vi-VN");
            TxtCurrentDateSub.Text = DateTime.Now.ToString("dddd, dd/MM/yyyy", viCulture);
            TxtTaskBadge.Text = pendingDueCount > 0 ? $"⚡ {pendingDueCount} đến hạn" : $"{_remindersList.Count(r => !r.IsCompleted)} chờ";

            // Default time placeholder in Row 2
            TxtTimePlaceholder.Text = DateTime.Now.AddMinutes(30).ToString("HH:mm");

            var filtered = GetFilteredReminders();
            PanelTaskList.Children.Clear();

            if (filtered.Count == 0)
            {
                ScrollTaskList.Visibility = Visibility.Collapsed;
                BorderEmptyReminders.Visibility = Visibility.Visible;
                TxtReminderCounter.Text = "0 task";
            }
            else
            {
                ScrollTaskList.Visibility = Visibility.Visible;
                BorderEmptyReminders.Visibility = Visibility.Collapsed;
                TxtReminderCounter.Text = $"{filtered.Count} task";

                for (int i = 0; i < filtered.Count; i++)
                {
                    var card = CreateTaskCard(filtered[i], i, filtered.Count);
                    PanelTaskList.Children.Add(card);
                }
            }

            // Sync Compact Pill on the notch bar:
            // Check if there is any due reminder that user hasn't checked yet
            var unreadDue = _remindersList.FirstOrDefault(r => !r.IsCompleted && r.IsNotified && !r.IsChecked);
            if (unreadDue != null)
            {
                PillCompactNotification.Visibility = Visibility.Visible;
                TxtCompactAppIcon.Text = "⏰";
                TxtCompactSender.Text = unreadDue.Category;
                TxtCompactMessage.Text = $"{unreadDue.Title} ({unreadDue.DueDate:HH:mm})";
                GlowCompactNotif.Color = Color.FromRgb(245, 158, 11);
            }
            else
            {
                // Only hide pill if no other reminder alert is currently expanding
                if (!_isAlertAutoExpanding)
                {
                    PillCompactNotification.Visibility = Visibility.Collapsed;
                }
            }

            ClockTimer_Tick(null, EventArgs.Empty);
        }

        private void CheckDueReminders()
        {
            var now = DateTime.Now;
            ScheduleReminder? dueItem = null;

            foreach (var r in _remindersList)
            {
                if (!r.IsCompleted && !r.IsNotified && now >= r.DueDate)
                {
                    r.IsNotified = true;
                    r.IsChecked = false;
                    dueItem = r;
                    break;
                }
            }

            if (dueItem != null)
            {
                SaveReminders();
                TriggerDynamicReminderAlert(dueItem);
            }
        }

        private void TriggerDynamicReminderAlert(ScheduleReminder reminder)
        {
            _activeReminder = reminder;

            // 1. Play subtle chime
            try { System.Media.SystemSounds.Asterisk.Play(); } catch { }

            // 2. Set compact reminder pill on the notch bar
            PillCompactNotification.Visibility = Visibility.Visible;
            TxtCompactAppIcon.Text = "⏰";
            TxtCompactSender.Text = reminder.Category;
            TxtCompactMessage.Text = $"{reminder.Title} ({reminder.DueDate:HH:mm})";
            GlowCompactNotif.Color = Color.FromRgb(245, 158, 11);
            ClockTimer_Tick(null, EventArgs.Empty);

            // 3. Dynamically expand like Apple Dynamic Island if currently Compact or Mini
            if (_currentState == IslandState.Compact || _currentState == IslandState.Mini)
            {
                _isAlertAutoExpanding = true;
                _currentReminderFilter = ReminderFilter.All;
                var filtered = GetFilteredReminders();
                int idx = filtered.FindIndex(r => r.Id == reminder.Id);
                if (idx >= 0) _currentReminderIndex = idx;

                SwitchState(IslandState.Notification);
                UpdateRemindersUI();

                // 4. Auto-collapse after 7 seconds ("xong tự ẩn đi thông báo cho tới khi bấm vào check thông báo")
                _reminderAutoHideTimer?.Stop();
                _reminderAutoHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
                _reminderAutoHideTimer.Tick += (s, e) =>
                {
                    _reminderAutoHideTimer.Stop();
                    // If user is still in notification view and hasn't explicitly checked it yet, smoothly auto-hide back to Compact!
                    if (_isAlertAutoExpanding && _currentState == IslandState.Notification)
                    {
                        _isAlertAutoExpanding = false;
                        SwitchState(IslandState.Compact);
                        // PillCompactNotification remains VISIBLE on the compact bar until user clicks to check!
                    }
                };
                _reminderAutoHideTimer.Start();
            }
            else
            {
                // In other states (e.g. Pomodoro, Dropzone, Camera), show sleek toast HUD so user is notified without interrupting their current task
                ShowModernToast($"⏰ Đến giờ: {reminder.Title} ({reminder.DueDate:HH:mm})", "📅", "#F59E0B");
                UpdateRemindersUI();
            }
        }

        private void PillCompactNotification_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            // User explicitly clicked the notification pill to check! Stop auto-hide
            _isAlertAutoExpanding = false;
            _reminderAutoHideTimer?.Stop();

            // Find the active or first due reminder
            var due = _remindersList.FirstOrDefault(r => !r.IsCompleted && r.IsNotified && !r.IsChecked)
                   ?? _remindersList.FirstOrDefault(r => !r.IsCompleted && DateTime.Now >= r.DueDate);

            if (due != null)
            {
                _currentReminderFilter = ReminderFilter.All;
                var filtered = GetFilteredReminders();
                int idx = filtered.FindIndex(r => r.Id == due.Id);
                if (idx >= 0) _currentReminderIndex = idx;
            }

            SwitchState(IslandState.Notification);
            UpdateRemindersUI();
        }

        private void BtnCheckNotification_Click(object sender, RoutedEventArgs e)
        {
            var current = GetCurrentSelectedReminder();
            if (current != null) CheckReminderNotification(current);
        }

        private void BtnToggleComplete_Click(object sender, RoutedEventArgs e)
        {
            var current = GetCurrentSelectedReminder();
            if (current != null) ToggleReminderComplete(current);
        }

        private void BtnSnooze5_Click(object sender, RoutedEventArgs e)
        {
            var current = GetCurrentSelectedReminder();
            if (current != null) SnoozeReminder(current);
        }

        private void BtnDeleteReminder_Click(object sender, RoutedEventArgs e)
        {
            var current = GetCurrentSelectedReminder();
            if (current != null) DeleteReminder(current);
        }

        private void BtnPrevReminder_Click(object sender, RoutedEventArgs e)
        {
            var filtered = GetFilteredReminders();
            if (filtered.Count > 0)
            {
                _currentReminderIndex = (_currentReminderIndex - 1 + filtered.Count) % filtered.Count;
                UpdateRemindersUI();
            }
        }

        private void BtnNextReminder_Click(object sender, RoutedEventArgs e)
        {
            var filtered = GetFilteredReminders();
            if (filtered.Count > 0)
            {
                _currentReminderIndex = (_currentReminderIndex + 1) % filtered.Count;
                UpdateRemindersUI();
            }
        }

        private void CheckReminderNotification(ScheduleReminder item)
        {
            _reminderAutoHideTimer?.Stop();
            _isAlertAutoExpanding = false;

            item.IsChecked = true;
            SaveReminders();
            UpdateRemindersUI();
            ShowModernToast($"✓ Đã kiểm tra lịch nhắc: {item.Title}!", "📅", "#10B981");
        }

        private void ToggleReminderComplete(ScheduleReminder item)
        {
            _reminderAutoHideTimer?.Stop();
            _isAlertAutoExpanding = false;

            item.IsCompleted = !item.IsCompleted;
            item.IsChecked = true;
            SaveReminders();
            UpdateRemindersUI();
            ShowModernToast(item.IsCompleted ? "✓ Đã hoàn thành công việc!" : "Đã chuyển về chưa hoàn thành", "✓", "#10B981");
        }

        private void SnoozeReminder(ScheduleReminder item)
        {
            _reminderAutoHideTimer?.Stop();
            _isAlertAutoExpanding = false;

            item.DueDate = DateTime.Now.AddMinutes(5);
            item.IsNotified = false;
            item.IsChecked = false;
            SaveReminders();
            UpdateRemindersUI();
            ShowModernToast($"⏰ Đã hoãn báo lại sau 5 phút ({item.DueDate:HH:mm})", "⏰", "#38BDF8");
        }

        private void DeleteReminder(ScheduleReminder item)
        {
            _reminderAutoHideTimer?.Stop();
            _isAlertAutoExpanding = false;

            _remindersList.Remove(item);
            ReindexPriorities();
            UpdateRemindersUI();
            ShowModernToast("🗑️ Đã xóa lịch nhắc!", "🗑️", "#EF4444");
        }

        private void MoveReminderPriority(ScheduleReminder source, ScheduleReminder target)
        {
            int srcIdx = _remindersList.FindIndex(r => r.Id == source.Id);
            int tgtIdx = _remindersList.FindIndex(r => r.Id == target.Id);
            if (srcIdx >= 0 && tgtIdx >= 0 && srcIdx != tgtIdx)
            {
                _remindersList.RemoveAt(srcIdx);
                _remindersList.Insert(tgtIdx, source);
                ReindexPriorities();
                UpdateRemindersUI();
                ShowModernToast($"⭐ Đã đổi thứ tự ưu tiên: \"{source.Title}\"", "↕", "#38BDF8");
            }
        }

        private void MoveReminderUp(ScheduleReminder item)
        {
            var filtered = GetFilteredReminders();
            int idx = filtered.FindIndex(r => r.Id == item.Id);
            if (idx > 0)
            {
                MoveReminderPriority(item, filtered[idx - 1]);
            }
        }

        private void MoveReminderDown(ScheduleReminder item)
        {
            var filtered = GetFilteredReminders();
            int idx = filtered.FindIndex(r => r.Id == item.Id);
            if (idx >= 0 && idx < filtered.Count - 1)
            {
                MoveReminderPriority(item, filtered[idx + 1]);
            }
        }

        private void ReindexPriorities()
        {
            for (int i = 0; i < _remindersList.Count; i++)
            {
                _remindersList[i].PriorityOrder = i;
            }
            SaveReminders();
        }

        private Border CreateTaskCard(ScheduleReminder item, int displayIndex, int totalCount)
        {
            var card = new Border
            {
                Background = new SolidColorBrush(item.IsCompleted 
                    ? Color.FromArgb(180, 15, 23, 42)
                    : (DateTime.Now >= item.DueDate ? Color.FromArgb(210, 30, 20, 30) : Color.FromArgb(220, 18, 24, 38))),
                BorderBrush = new SolidColorBrush(item.IsCompleted 
                    ? Color.FromRgb(30, 41, 59) 
                    : (DateTime.Now >= item.DueDate ? Color.FromRgb(239, 68, 68) : Color.FromRgb(30, 41, 59))),
                BorderThickness = new Thickness(1.2),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(7, 5, 7, 5),
                Margin = new Thickness(0, 0, 0, 4),
                Tag = item,
                AllowDrop = true
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // --- Column 0: Left side controls (Drag grip, ▲/▼, Priority badge, Category) ---
            var leftPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            var grip = new TextBlock
            {
                Text = "⠿",
                Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0),
                Cursor = Cursors.SizeAll,
                ToolTip = "Kéo lên/xuống để đổi độ ưu tiên"
            };
            leftPanel.Children.Add(grip);

            var btnUp = new Button
            {
                Content = "▲",
                FontSize = 8,
                Padding = new Thickness(2, 0, 2, 0),
                Margin = new Thickness(0, 0, 2, 0),
                Background = new SolidColorBrush(Color.FromRgb(30, 41, 59)),
                Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                ToolTip = "Tăng ưu tiên (chuyển lên trên)",
                Visibility = displayIndex > 0 ? Visibility.Visible : Visibility.Hidden
            };
            btnUp.Click += (s, e) => MoveReminderUp(item);
            leftPanel.Children.Add(btnUp);

            var btnDown = new Button
            {
                Content = "▼",
                FontSize = 8,
                Padding = new Thickness(2, 0, 2, 0),
                Margin = new Thickness(0, 0, 4, 0),
                Background = new SolidColorBrush(Color.FromRgb(30, 41, 59)),
                Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                ToolTip = "Giảm ưu tiên (chuyển xuống dưới)",
                Visibility = displayIndex < totalCount - 1 ? Visibility.Visible : Visibility.Hidden
            };
            btnDown.Click += (s, e) => MoveReminderDown(item);
            leftPanel.Children.Add(btnDown);

            bool isTopPriority = displayIndex == 0 && !item.IsCompleted;
            var badgePriority = new Border
            {
                Background = new SolidColorBrush(isTopPriority ? Color.FromRgb(120, 53, 15) : Color.FromRgb(30, 41, 59)),
                BorderBrush = new SolidColorBrush(isTopPriority ? Color.FromRgb(245, 158, 11) : Color.FromRgb(51, 65, 85)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(5, 1, 5, 1),
                Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = $"Mức ưu tiên #{displayIndex + 1}"
            };
            var txtPriority = new TextBlock
            {
                Text = isTopPriority ? "🔥 #1" : $"#{displayIndex + 1}",
                FontSize = 9.5,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(isTopPriority ? Color.FromRgb(254, 240, 138) : Color.FromRgb(148, 163, 184))
            };
            badgePriority.Child = txtPriority;
            leftPanel.Children.Add(badgePriority);

            Color catBorder = Color.FromRgb(56, 189, 248);
            Color catFg = Color.FromRgb(56, 189, 248);
            if (item.Category.Contains("Quan trọng"))
            {
                catBorder = Color.FromRgb(244, 63, 94);
                catFg = Color.FromRgb(251, 113, 133);
            }
            else if (item.Category.Contains("Học tập"))
            {
                catBorder = Color.FromRgb(168, 85, 247);
                catFg = Color.FromRgb(192, 132, 252);
            }
            else if (item.Category.Contains("Cá nhân"))
            {
                catBorder = Color.FromRgb(16, 185, 129);
                catFg = Color.FromRgb(52, 211, 153);
            }

            var badgeCat = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(30, 41, 59)),
                BorderBrush = new SolidColorBrush(catBorder),
                BorderThickness = new Thickness(0.8),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(4, 1, 4, 1),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            badgeCat.Child = new TextBlock
            {
                Text = item.Category,
                FontSize = 9.5,
                Foreground = new SolidColorBrush(catFg)
            };
            leftPanel.Children.Add(badgeCat);

            Grid.SetColumn(leftPanel, 0);
            grid.Children.Add(leftPanel);

            // --- Column 1: Task Title & Subtext ---
            var centerPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };

            var txtTitle = new TextBlock
            {
                Text = item.Title,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 310
            };
            if (item.IsCompleted)
            {
                txtTitle.Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139));
                txtTitle.TextDecorations = TextDecorations.Strikethrough;
            }
            else if (DateTime.Now >= item.DueDate)
            {
                txtTitle.Foreground = new SolidColorBrush(Color.FromRgb(252, 165, 165));
            }
            else
            {
                txtTitle.Foreground = new SolidColorBrush(Color.FromRgb(248, 250, 252));
            }
            centerPanel.Children.Add(txtTitle);

            string dateStr = item.DueDate.Date == DateTime.Today ? "Hôm nay"
                : item.DueDate.Date == DateTime.Today.AddDays(1) ? "Ngày mai"
                : item.DueDate.Date == DateTime.Today.AddDays(2) ? "Ngày kia"
                : item.DueDate.ToString("dd/MM");

            string statusStr;
            Color statusColor;

            if (item.IsCompleted)
            {
                statusStr = $"✓ Đã xong ({item.DueDate:HH:mm})";
                statusColor = Color.FromRgb(16, 185, 129);
            }
            else if (DateTime.Now >= item.DueDate)
            {
                var diff = DateTime.Now - item.DueDate;
                string diffStr = diff.TotalHours >= 1 ? $"{(int)diff.TotalHours}h {diff.Minutes}p" : $"{Math.Max(1, (int)diff.TotalMinutes)}p";
                statusStr = $"⏰ Quá hạn {diffStr} ({item.DueDate:HH:mm})";
                statusColor = Color.FromRgb(248, 113, 113);
            }
            else
            {
                var diff = item.DueDate - DateTime.Now;
                string diffStr = diff.TotalDays >= 1 ? $"{(int)diff.TotalDays}d {(int)diff.Hours}h"
                    : diff.TotalHours >= 1 ? $"{(int)diff.TotalHours}h {diff.Minutes}p"
                    : $"{Math.Max(1, (int)diff.TotalMinutes)}p";
                statusStr = $"📅 {dateStr} • ⏰ {item.DueDate:HH:mm} (còn {diffStr})";
                statusColor = Color.FromRgb(100, 116, 139);
            }

            centerPanel.Children.Add(new TextBlock
            {
                Text = statusStr,
                FontSize = 9.5,
                Foreground = new SolidColorBrush(statusColor),
                Margin = new Thickness(0, 1, 0, 0)
            });

            Grid.SetColumn(centerPanel, 1);
            grid.Children.Add(centerPanel);

            // --- Column 2: Right Actions ---
            var rightPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            if (!item.IsCompleted && DateTime.Now >= item.DueDate && !item.IsChecked)
            {
                var btnCheck = new Button
                {
                    Content = "✓ Đã xem",
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    Background = new SolidColorBrush(Color.FromRgb(245, 158, 11)),
                    Foreground = Brushes.White,
                    Padding = new Thickness(6, 2, 6, 2),
                    Margin = new Thickness(0, 0, 4, 0),
                    Cursor = Cursors.Hand,
                    ToolTip = "Đã xem thông báo nhắc việc này"
                };
                btnCheck.Click += (s, e) => CheckReminderNotification(item);
                rightPanel.Children.Add(btnCheck);
            }

            var btnToggle = new Button
            {
                Content = item.IsCompleted ? "↩" : "✓",
                FontSize = 10.5,
                FontWeight = FontWeights.Bold,
                Background = new SolidColorBrush(item.IsCompleted ? Color.FromRgb(30, 41, 59) : Color.FromRgb(16, 185, 129)),
                Foreground = Brushes.White,
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(0, 0, 3, 0),
                Cursor = Cursors.Hand,
                ToolTip = item.IsCompleted ? "Đánh dấu chưa hoàn thành" : "Hoàn thành công việc này"
            };
            btnToggle.Click += (s, e) => ToggleReminderComplete(item);
            rightPanel.Children.Add(btnToggle);

            if (!item.IsCompleted)
            {
                var btnSnooze = new Button
                {
                    Content = "+5p",
                    FontSize = 9.5,
                    Background = new SolidColorBrush(Color.FromRgb(30, 41, 59)),
                    Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248)),
                    Padding = new Thickness(4, 2, 4, 2),
                    Margin = new Thickness(0, 0, 3, 0),
                    Cursor = Cursors.Hand,
                    ToolTip = "Hoãn báo lại 5 phút"
                };
                btnSnooze.Click += (s, e) => SnoozeReminder(item);
                rightPanel.Children.Add(btnSnooze);
            }

            var btnDel = new Button
            {
                Content = "🗑️",
                FontSize = 9.5,
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                Padding = new Thickness(3, 2, 3, 2),
                Cursor = Cursors.Hand,
                ToolTip = "Xóa công việc này"
            };
            btnDel.Click += (s, e) => DeleteReminder(item);
            rightPanel.Children.Add(btnDel);

            Grid.SetColumn(rightPanel, 2);
            grid.Children.Add(rightPanel);

            card.Child = grid;

            // Wire drag-and-drop
            card.PreviewMouseLeftButtonDown += (s, e) =>
            {
                if (e.OriginalSource is DependencyObject dep && FindVisualParent<Button>(dep) != null) return;
                _taskDragStartPoint = e.GetPosition(null);
                _draggedReminder = item;
                _draggedCard = card;
            };

            card.PreviewMouseMove += (s, e) =>
            {
                if (e.LeftButton == MouseButtonState.Pressed && _draggedReminder == item && _draggedCard == card)
                {
                    Point curPos = e.GetPosition(null);
                    Vector diff = _taskDragStartPoint - curPos;
                    if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                        Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
                    {
                        card.Opacity = 0.45;
                        var data = new DataObject("ScheduleReminder", item);
                        DragDrop.DoDragDrop(card, data, DragDropEffects.Move);
                        card.Opacity = 1.0;
                        _draggedReminder = null;
                        _draggedCard = null;
                    }
                }
            };

            card.DragOver += (s, e) =>
            {
                if (e.Data.GetDataPresent("ScheduleReminder"))
                {
                    e.Effects = DragDropEffects.Move;
                    card.BorderBrush = new SolidColorBrush(Color.FromRgb(56, 189, 248));
                    e.Handled = true;
                }
            };

            card.DragLeave += (s, e) =>
            {
                card.BorderBrush = new SolidColorBrush(item.IsCompleted 
                    ? Color.FromRgb(30, 41, 59) 
                    : (DateTime.Now >= item.DueDate ? Color.FromRgb(239, 68, 68) : Color.FromRgb(30, 41, 59)));
            };

            card.Drop += (s, e) =>
            {
                if (e.Data.GetDataPresent("ScheduleReminder"))
                {
                    var source = e.Data.GetData("ScheduleReminder") as ScheduleReminder;
                    var target = card.Tag as ScheduleReminder;
                    if (source != null && target != null && source.Id != target.Id)
                    {
                        MoveReminderPriority(source, target);
                    }
                }
                card.BorderBrush = new SolidColorBrush(item.IsCompleted 
                    ? Color.FromRgb(30, 41, 59) 
                    : (DateTime.Now >= item.DueDate ? Color.FromRgb(239, 68, 68) : Color.FromRgb(30, 41, 59)));
                e.Handled = true;
            };

            return card;
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
            _currentReminderFilter = ReminderFilter.All;
            _currentReminderIndex = 0;
            UpdateRemindersUI();
        }

        private void BtnFilterToday_Click(object sender, RoutedEventArgs e)
        {
            _currentReminderFilter = ReminderFilter.Today;
            _currentReminderIndex = 0;
            UpdateRemindersUI();
        }

        private void BtnFilterUpcoming_Click(object sender, RoutedEventArgs e)
        {
            _currentReminderFilter = ReminderFilter.Upcoming;
            _currentReminderIndex = 0;
            UpdateRemindersUI();
        }

        private void BtnFilterDone_Click(object sender, RoutedEventArgs e)
        {
            _currentReminderFilter = ReminderFilter.Done;
            _currentReminderIndex = 0;
            UpdateRemindersUI();
        }

        private void BtnTaskDay_Click(object sender, RoutedEventArgs e)
        {
            _selectedDayOffset = (_selectedDayOffset + 1) % DayOptions.Length;
            BtnTaskDay.Content = DayOptions[_selectedDayOffset];
        }

        private void BtnTaskCategory_Click(object sender, RoutedEventArgs e)
        {
            _selectedCategoryIndex = (_selectedCategoryIndex + 1) % Categories.Length;
            BtnTaskCategory.Content = Categories[_selectedCategoryIndex];
        }

        private void QuickTimePreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Content != null)
            {
                string text = btn.Content.ToString() ?? "";
                DateTime target = DateTime.Now;
                if (text.Contains("15")) target = target.AddMinutes(15);
                else if (text.Contains("30")) target = target.AddMinutes(30);
                else if (text.Contains("1h")) target = target.AddHours(1);

                InputTaskTime.Text = target.ToString("HH:mm");
                _selectedDayOffset = 0;
                BtnTaskDay.Content = DayOptions[0];
            }
        }

        private void InputTaskTitle_TextChanged(object sender, TextChangedEventArgs e)
        {
            TxtTaskTitlePlaceholder.Visibility = string.IsNullOrEmpty(InputTaskTitle.Text) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void InputTaskTime_TextChanged(object sender, TextChangedEventArgs e)
        {
            TxtTimePlaceholder.Visibility = string.IsNullOrEmpty(InputTaskTime.Text) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void InputTaskTitle_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                BtnAddReminder_Click(sender, e);
            }
        }

        private void BtnAddReminder_Click(object sender, RoutedEventArgs e)
        {
            string title = InputTaskTitle.Text.Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                ShowModernToast("Vui lòng nhập nội dung việc cần làm!", "⚠️", "#F59E0B");
                InputTaskTitle.Focus();
                return;
            }

            string timeInput = InputTaskTime.Text.Trim();
            DateTime targetDate = DateTime.Today.AddDays(_selectedDayOffset);

            if (TryParseScheduleDateTime(timeInput, _selectedDayOffset, out DateTime parsedDate))
            {
                targetDate = parsedDate;
            }
            else
            {
                // Fallback to current time + 30 mins
                targetDate = targetDate.Add(DateTime.Now.TimeOfDay).AddMinutes(30);
            }

            var item = new ScheduleReminder
            {
                Title = title,
                DueDate = targetDate,
                Category = Categories[_selectedCategoryIndex],
                IsCompleted = false,
                IsNotified = targetDate <= DateTime.Now,
                IsChecked = false,
                PriorityOrder = _remindersList.Count > 0 ? _remindersList.Max(r => r.PriorityOrder) + 1 : 0
            };

            _remindersList.Add(item);
            SaveReminders();

            InputTaskTitle.Text = "";
            InputTaskTime.Text = "";

            // Focus on newly added item
            _currentReminderFilter = ReminderFilter.All;
            var filtered = GetFilteredReminders();
            int idx = filtered.FindIndex(r => r.Id == item.Id);
            if (idx >= 0) _currentReminderIndex = idx;

            UpdateRemindersUI();
            ShowModernToast($"✓ Đã thêm lịch nhắc lúc {targetDate:HH:mm} ({targetDate:dd/MM})!", "📅", "#10B981");
        }

        private bool TryParseScheduleDateTime(string timeText, int dayOffset, out DateTime result)
        {
            result = DateTime.Today.AddDays(dayOffset);
            if (string.IsNullOrWhiteSpace(timeText))
            {
                result = result.Add(DateTime.Now.TimeOfDay).AddMinutes(30);
                return true;
            }

            timeText = timeText.Trim().Replace("h", ":").Replace(".", ":");

            // 1. Try full DateTime
            if (DateTime.TryParse(timeText, new CultureInfo("vi-VN"), DateTimeStyles.None, out DateTime fullDt))
            {
                result = fullDt;
                return true;
            }

            // 2. Try standard TimeSpan
            if (TimeSpan.TryParse(timeText, out TimeSpan ts))
            {
                result = DateTime.Today.AddDays(dayOffset).Add(ts);
                return true;
            }

            // 3. Try integer hour (e.g. "16" -> 16:00)
            if (int.TryParse(timeText, out int hour) && hour >= 0 && hour <= 23)
            {
                result = DateTime.Today.AddDays(dayOffset).AddHours(hour);
                return true;
            }

            // 4. Try split H:M
            var parts = timeText.Split(':');
            if (parts.Length == 2 && int.TryParse(parts[0], out int h) && int.TryParse(parts[1], out int m))
            {
                if (h >= 0 && h <= 23 && m >= 0 && m <= 59)
                {
                    result = DateTime.Today.AddDays(dayOffset).AddHours(h).AddMinutes(m);
                    return true;
                }
            }

            return false;
        }

        #region Interactive Calendar Picker
        private void RenderCalendarDays()
        {
            if (UniformGridDays == null) return;
            UniformGridDays.Children.Clear();

            var viCulture = new CultureInfo("vi-VN");
            TxtCalMonthYear.Text = $"Tháng {_calendarViewingMonth.Month}, {_calendarViewingMonth.Year}";
            TxtSelectedDateDisplay.Text = $"📅 {_calendarSelectedDate.ToString("dddd, dd/MM/yyyy", viCulture)}";

            // Vietnamese / ISO calendar: Week starts on Monday (T2)
            int firstDayOffset = ((int)_calendarViewingMonth.DayOfWeek + 6) % 7;
            DateTime startDate = _calendarViewingMonth.AddDays(-firstDayOffset);

            for (int i = 0; i < 42; i++)
            {
                DateTime cellDate = startDate.AddDays(i);
                bool isCurrentMonth = cellDate.Month == _calendarViewingMonth.Month;
                bool isToday = cellDate.Date == DateTime.Today;
                bool isSelected = cellDate.Date == _calendarSelectedDate.Date;
                bool hasTasks = _remindersList.Any(r => !r.IsCompleted && r.DueDate.Date == cellDate.Date);

                var btn = new Button
                {
                    Margin = new Thickness(1.5),
                    Height = 22,
                    Cursor = Cursors.Hand,
                    BorderThickness = new Thickness(isSelected || isToday ? 1 : 0),
                    Tag = cellDate
                };

                var border = new FrameworkElementFactory(typeof(Border));
                border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));

                if (isSelected)
                {
                    border.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(2, 132, 199)));
                    border.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(56, 189, 248)));
                }
                else if (isToday)
                {
                    border.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(12, 37, 64)));
                    border.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(56, 189, 248)));
                }
                else if (isCurrentMonth)
                {
                    border.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)));
                    border.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
                }
                else
                {
                    border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
                    border.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
                }

                var grid = new FrameworkElementFactory(typeof(Grid));
                grid.SetValue(Grid.HorizontalAlignmentProperty, HorizontalAlignment.Center);
                grid.SetValue(Grid.VerticalAlignmentProperty, VerticalAlignment.Center);

                var txt = new FrameworkElementFactory(typeof(TextBlock));
                txt.SetValue(TextBlock.TextProperty, cellDate.Day.ToString());
                txt.SetValue(TextBlock.FontSizeProperty, 10.0);
                txt.SetValue(TextBlock.FontWeightProperty, isSelected || isToday ? FontWeights.Bold : FontWeights.Normal);
                txt.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
                txt.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);

                if (isSelected)
                {
                    txt.SetValue(TextBlock.ForegroundProperty, Brushes.White);
                }
                else if (isToday)
                {
                    txt.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(56, 189, 248)));
                }
                else if (isCurrentMonth)
                {
                    if (cellDate.DayOfWeek == DayOfWeek.Sunday)
                        txt.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(244, 63, 94)));
                    else if (cellDate.DayOfWeek == DayOfWeek.Saturday)
                        txt.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(56, 189, 248)));
                    else
                        txt.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(241, 245, 249)));
                }
                else
                {
                    txt.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(71, 85, 105)));
                }

                grid.AppendChild(txt);

                if (hasTasks)
                {
                    var dot = new FrameworkElementFactory(typeof(Border));
                    dot.SetValue(FrameworkElement.WidthProperty, 3.5);
                    dot.SetValue(FrameworkElement.HeightProperty, 3.5);
                    dot.SetValue(Border.CornerRadiusProperty, new CornerRadius(1.75));
                    dot.SetValue(Border.BackgroundProperty, isSelected ? Brushes.White : new SolidColorBrush(Color.FromRgb(245, 158, 11)));
                    dot.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
                    dot.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Bottom);
                    dot.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 14, 0, 0));
                    grid.AppendChild(dot);
                }

                border.AppendChild(grid);

                var template = new ControlTemplate(typeof(Button));
                template.VisualTree = border;
                btn.Template = template;

                btn.Click += (s, e) =>
                {
                    if (s is Button b && b.Tag is DateTime dt)
                    {
                        _calendarSelectedDate = dt;
                        if (dt.Month != _calendarViewingMonth.Month || dt.Year != _calendarViewingMonth.Year)
                        {
                            _calendarViewingMonth = new DateTime(dt.Year, dt.Month, 1);
                        }
                        RenderCalendarDays();
                    }
                };

                UniformGridDays.Children.Add(btn);
            }
        }

        private void BtnToggleCalendar_Click(object sender, RoutedEventArgs e)
        {
            if (GridCalendarPicker.Visibility == Visibility.Visible)
            {
                GridCalendarPicker.Visibility = Visibility.Collapsed;
                GridTaskOverview.Visibility = Visibility.Visible;
                BtnToggleCalendar.Content = "📅 Chọn theo lịch";
            }
            else
            {
                GridTaskOverview.Visibility = Visibility.Collapsed;
                GridCalendarPicker.Visibility = Visibility.Visible;
                BtnToggleCalendar.Content = "📋 Xem danh sách";

                if (!string.IsNullOrWhiteSpace(InputTaskTitle.Text))
                {
                    InputCalTitle.Text = InputTaskTitle.Text;
                    TxtCalTitlePlaceholder.Visibility = Visibility.Collapsed;
                }
                else
                {
                    TxtCalTitlePlaceholder.Visibility = Visibility.Visible;
                }

                if (string.IsNullOrWhiteSpace(InputCalTime.Text))
                {
                    InputCalTime.Text = DateTime.Now.AddMinutes(30).ToString("HH:mm");
                }

                _calendarViewingMonth = new DateTime(_calendarSelectedDate.Year, _calendarSelectedDate.Month, 1);
                RenderCalendarDays();
                InputCalTitle.Focus();
            }
        }

        private void BtnCloseCalendar_Click(object sender, RoutedEventArgs e)
        {
            GridCalendarPicker.Visibility = Visibility.Collapsed;
            GridTaskOverview.Visibility = Visibility.Visible;
            BtnToggleCalendar.Content = "📅 Chọn theo lịch";
        }

        private void BtnPrevMonth_Click(object sender, RoutedEventArgs e)
        {
            _calendarViewingMonth = _calendarViewingMonth.AddMonths(-1);
            RenderCalendarDays();
        }

        private void BtnNextMonth_Click(object sender, RoutedEventArgs e)
        {
            _calendarViewingMonth = _calendarViewingMonth.AddMonths(1);
            RenderCalendarDays();
        }

        private void BtnTodayCal_Click(object sender, RoutedEventArgs e)
        {
            _calendarSelectedDate = DateTime.Today;
            _calendarViewingMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            RenderCalendarDays();
        }

        private void ChipCalTime_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Content != null)
            {
                InputCalTime.Text = btn.Content.ToString() ?? "08:00";
            }
        }

        private void BtnCalCategory_Click(object sender, RoutedEventArgs e)
        {
            _calCategoryIndex = (_calCategoryIndex + 1) % Categories.Length;
            BtnCalCategory.Content = Categories[_calCategoryIndex];
        }

        private void InputCalTitle_TextChanged(object sender, TextChangedEventArgs e)
        {
            TxtCalTitlePlaceholder.Visibility = string.IsNullOrEmpty(InputCalTitle.Text) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void InputCalTitle_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                BtnSaveCalendarTask_Click(sender, e);
            }
        }

        private void BtnSaveCalendarTask_Click(object sender, RoutedEventArgs e)
        {
            string title = InputCalTitle.Text.Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                ShowModernToast("Vui lòng nhập nội dung công việc!", "⚠️", "#F59E0B");
                InputCalTitle.Focus();
                return;
            }

            string timeInput = InputCalTime.Text.Trim();
            DateTime targetDueDate = _calendarSelectedDate.Date;

            if (TryParseScheduleDateTime(timeInput, 0, out DateTime parsedTime))
            {
                targetDueDate = _calendarSelectedDate.Date.Add(parsedTime.TimeOfDay);
            }
            else
            {
                targetDueDate = _calendarSelectedDate.Date.Add(DateTime.Now.TimeOfDay).AddMinutes(30);
            }

            var item = new ScheduleReminder
            {
                Title = title,
                DueDate = targetDueDate,
                Category = Categories[_calCategoryIndex],
                IsCompleted = false,
                IsNotified = targetDueDate <= DateTime.Now,
                IsChecked = false,
                PriorityOrder = _remindersList.Count > 0 ? _remindersList.Max(r => r.PriorityOrder) + 1 : 0
            };

            _remindersList.Add(item);
            SaveReminders();

            InputCalTitle.Text = "";
            InputTaskTitle.Text = "";

            // Return to task overview and select newly created reminder
            GridCalendarPicker.Visibility = Visibility.Collapsed;
            GridTaskOverview.Visibility = Visibility.Visible;
            BtnToggleCalendar.Content = "📅 Chọn theo lịch";

            _currentReminderFilter = ReminderFilter.All;
            var filtered = GetFilteredReminders();
            int idx = filtered.FindIndex(r => r.Id == item.Id);
            if (idx >= 0) _currentReminderIndex = idx;

            UpdateRemindersUI();
            RenderCalendarDays();
            ShowModernToast($"✓ Đã lưu lịch nhắc: {item.Title} lúc {targetDueDate:HH:mm} ({targetDueDate:dd/MM})!", "📅", "#10B981");
        }
        #endregion
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
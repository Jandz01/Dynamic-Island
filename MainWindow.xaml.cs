using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
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
        LockScreen
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

        // Dropzone
        private string? _currentFilePath = null;

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

            // Initialize Real Windows Notification Service
            _realNotificationService.NotificationReceived += OnRealNotificationReceived;
            _realNotificationService.Start();

            // Pre-load recent real notifications from user's system
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

            // Initial view: Compact Notch
            ApplyState(IslandState.Compact, animate: false);
            UpdateNotchGeometry(540, 38, 0, 0);

            // Update Notification UI with loaded notifications
            UpdateNotificationUI();

            // Setup demo incoming notification after 8s using REAL recent notification if available
            var demoNotifyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            demoNotifyTimer.Tick += (s, args) =>
            {
                demoNotifyTimer.Stop();
                if (_currentState == IslandState.Compact || _currentState == IslandState.Mini)
                {
                    if (_realNotificationsList.Count > 0)
                    {
                        ShowNotification(_realNotificationsList[0]);
                    }
                }
            };
            demoNotifyTimer.Start();
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
                // Deformed liquid notch: bottom edge sags down at sagX like organic liquid
                double dipY = height + sagY;
                figure.Segments.Add(new BezierSegment(new Point(E * 0.5, 0), new Point(E, R_ear * 0.3), new Point(E, R_ear), true));
                figure.Segments.Add(new LineSegment(new Point(E, height - R_bot), true));
                
                // Left curve into sagging liquid teardrop
                figure.Segments.Add(new BezierSegment(new Point(E, height), new Point(sagX - 45, dipY), new Point(sagX - 15, dipY + 2), true));
                
                // Rounded liquid teardrop hanging tip at sagX
                figure.Segments.Add(new BezierSegment(new Point(sagX, dipY + 5), new Point(sagX, dipY + 5), new Point(sagX + 15, dipY + 2), true));

                // Right curve out of sagging liquid teardrop
                figure.Segments.Add(new BezierSegment(new Point(sagX + 45, dipY), new Point(E + width, height), new Point(E + width, height - R_bot), true));
                
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
                    ShowModernToast("Đã chụp và lưu ảnh vào Clipboard!", "📸", "#38BDF8");
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
                // Physical elastic resistance curve: strictly capped at max 46px!
                double factor = 1.0 - Math.Exp(-clampedDeltaY / 55.0);
                double sagY = 46.0 * factor;
                double extraHeight = 16.0 * factor;

                double E = 22;
                double minX = Math.Min(E + 10, E + IslandCard.Width / 2);
                double maxX = Math.Max(minX, E + IslandCard.Width - 10);
                double sagX = Math.Clamp(cur.X, minX, maxX);

                UpdateNotchGeometry(IslandCard.Width, _initialNotchHeight, sagX, sagY);
                IslandCard.Height = _initialNotchHeight + extraHeight;
            }
            else
            {
                UpdateNotchGeometry(IslandCard.Width, _initialNotchHeight, 0, 0);
                IslandCard.Height = _initialNotchHeight;
            }
        }

        private void Window_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDraggingLiquid)
            {
                Point cur = e.GetPosition(NotchRoot);
                double deltaY = cur.Y - _dragStartPoint.Y;

                _isDraggingLiquid = false;
                NotchRoot.ReleaseMouseCapture();

                // Snap notch back to rest immediately
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

            // Drop straight down along the center vertical axis: X = 474 (Window center: 490 - 16)
            double centerX = 474;
            double startY = Math.Max(25, _initialNotchHeight - 8);
            double targetY = 114; // Center of Black Hole (Y=150 minus droplet bulb center 36)

            Canvas.SetLeft(FallingDroplet, centerX);
            Canvas.SetTop(FallingDroplet, startY);
            FallingDroplet.Opacity = 1.0;

            FallingDropletScale.ScaleX = 0.85;
            FallingDropletScale.ScaleY = 1.0;

            // Straight vertical drop (NO X drift, perfectly centered!)
            var dropDuration = TimeSpan.FromMilliseconds(260);
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
                var splashDuration = TimeSpan.FromMilliseconds(110);
                var squashXAnim = new DoubleAnimation(0.72, 1.6, splashDuration)
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                var squashYAnim = new DoubleAnimation(1.35, 0.25, splashDuration)
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                var fadeOutAnim = new DoubleAnimation(1.0, 0.0, splashDuration);

                FallingDropletScale.BeginAnimation(ScaleTransform.ScaleXProperty, squashXAnim);
                FallingDropletScale.BeginAnimation(ScaleTransform.ScaleYProperty, squashYAnim);
                FallingDroplet.BeginAnimation(OpacityProperty, fadeOutAnim);

                // Switch to Orbital mode & Black Hole pop-in animation
                SwitchState(IslandState.Orbital);

                // Pop-in Black Hole accretion disk
                var bhPopAnim = new DoubleAnimation(0.2, 1.0, TimeSpan.FromMilliseconds(320))
                {
                    EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 }
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
            // Real Clock with Vietnamese Day & Date
            var viCulture = new CultureInfo("vi-VN");
            TxtClock.Text = DateTime.Now.ToString("HH:mm:ss dddd, dd/MM", viCulture);

            // Real RAM
            var memStatus = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
            if (GlobalMemoryStatusEx(ref memStatus))
            {
                TxtRam.Text = $"{memStatus.dwMemoryLoad}%";
            }

            // Real ROM (Drive C)
            try
            {
                var drive = new DriveInfo("C");
                double totalRom = drive.TotalSize;
                double freeRom = drive.AvailableFreeSpace;
                double usedRom = totalRom - freeRom;
                int romPercent = (int)((usedRom / totalRom) * 100);
                TxtRom.Text = $"{romPercent}%";
            }
            catch
            {
                TxtRom.Text = "45%";
            }

            // Real Battery
            if (GetSystemPowerStatus(out SYSTEM_POWER_STATUS powerStatus))
            {
                if (powerStatus.BatteryLifePercent != 255)
                {
                    TxtBattery.Text = $"{powerStatus.BatteryLifePercent}%";
                    TxtBatteryIcon.Text = powerStatus.ACLineStatus == 1 ? "⚡" : "🔋";
                }
                else
                {
                    TxtBattery.Text = "AC";
                    TxtBatteryIcon.Text = "⚡";
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
        private void TriggerReverseDropletToTask(Border orb, IslandState targetState)
        {
            Border[] orbs = [OrbMusic, OrbNotify, OrbLockScreen, OrbDrop, OrbCamera, OrbPomodoro];
            int idx = Array.IndexOf(orbs, orb);
            double angle = idx >= 0 ? _orbitAngle + idx * (Math.PI * 2 / orbs.Length) : 0;
            double radiusX = 200 * _bloomProgress;
            double radiusY = 100 * _bloomProgress;
            double orbCenterX = 340 + radiusX * Math.Cos(angle);
            double orbCenterY = 150 + radiusY * Math.Sin(angle);

            double startX = orbCenterX - 16;
            double startY = orbCenterY - 28;

            // Inherit planet's color
            ReverseDroplet.Stroke = orb.BorderBrush;
            ReverseDroplet.Fill = orb.Background;
            if (orb.Effect is DropShadowEffect ds)
            {
                ReverseDropletGlow.Color = ds.Color;
            }

            Canvas.SetLeft(ReverseDroplet, startX);
            Canvas.SetTop(ReverseDroplet, startY);
            ReverseDroplet.Opacity = 1.0;

            // Animate droplet flowing upwards into the top notch
            var upAnim = new DoubleAnimation(startY, 0, TimeSpan.FromMilliseconds(320))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            var xAnim = new DoubleAnimation(startX, 474, TimeSpan.FromMilliseconds(320))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };

            upAnim.Completed += (s, e) =>
            {
                ReverseDroplet.Opacity = 0;
                // Morph into target dynamic island
                ApplyState(targetState, animate: true);
            };

            ReverseDroplet.BeginAnimation(Canvas.TopProperty, upAnim);
            ReverseDroplet.BeginAnimation(Canvas.LeftProperty, xAnim);

            ViewOrbital.Visibility = Visibility.Collapsed;
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

        private bool IsValidNotebookLmUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            url = url.Trim();
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                url = "https://" + url;
            }
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uriResult)) return false;

            string host = uriResult.Host.ToLowerInvariant();
            return host.Contains("notebooklm") || (host.Contains("google.com") && uriResult.AbsolutePath.Contains("notebook"));
        }

        private void UpdateNotebookLmStatus(bool isValid, string url)
        {
            Dispatcher.Invoke(() =>
            {
                if (isValid)
                {
                    BorderNotebookStatus.Background = (Brush)new BrushConverter().ConvertFromString("#064E3B")!;
                    BorderNotebookStatus.BorderBrush = (Brush)new BrushConverter().ConvertFromString("#10B981")!;
                    TxtNotebookStatus.Foreground = (Brush)new BrushConverter().ConvertFromString("#34D399")!;
                    TxtNotebookStatus.Text = url.Contains("/notebook/") ? "🟢 Đã kết nối Sổ tay NotebookLM" : "🟢 Đã kết nối NotebookLM";
                }
                else
                {
                    BorderNotebookStatus.Background = (Brush)new BrushConverter().ConvertFromString("#291F03")!;
                    BorderNotebookStatus.BorderBrush = (Brush)new BrushConverter().ConvertFromString("#EAB308")!;
                    TxtNotebookStatus.Foreground = (Brush)new BrushConverter().ConvertFromString("#FDE047")!;
                    TxtNotebookStatus.Text = "🟡 Chưa liên kết (Nhập URL NotebookLM)";
                }
            });
        }

        private void LoadNotebookLmConfig()
        {
            try
            {
                if (File.Exists(_notebookLmConfigPath))
                {
                    string savedUrl = File.ReadAllText(_notebookLmConfigPath).Trim();
                    if (!string.IsNullOrEmpty(savedUrl))
                    {
                        InputNotebookUrl.Text = savedUrl;
                        TxtNotebookUrlPlaceholder.Visibility = Visibility.Collapsed;
                        bool valid = IsValidNotebookLmUrl(savedUrl);
                        UpdateNotebookLmStatus(valid, savedUrl);
                        return;
                    }
                }
            }
            catch { }

            InputNotebookUrl.Text = "https://notebooklm.google.com/";
            TxtNotebookUrlPlaceholder.Visibility = Visibility.Collapsed;
            UpdateNotebookLmStatus(true, InputNotebookUrl.Text);
        }

        private void BtnSaveNotebookUrl_Click(object sender, RoutedEventArgs e)
        {
            string text = InputNotebookUrl.Text.Trim();
            if (IsValidNotebookLmUrl(text))
            {
                try
                {
                    string dir = Path.GetDirectoryName(_notebookLmConfigPath)!;
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(_notebookLmConfigPath, text);
                    UpdateNotebookLmStatus(true, text);
                    ShowModernToast("Đã lưu và kết nối NotebookLM thành công!", "🟢", "#10B981");
                }
                catch (Exception ex)
                {
                    ShowModernToast("Lỗi khi lưu link: " + ex.Message, "⚠️", "#EF4444");
                }
            }
            else
            {
                UpdateNotebookLmStatus(false, text);
                ShowModernToast("Vui lòng nhập đúng định dạng link NotebookLM (notebooklm.google.com/...)!", "⚠️", "#F59E0B");
            }
        }

        private void InputNotebookUrl_TextChanged(object sender, TextChangedEventArgs e)
        {
            string text = InputNotebookUrl.Text.Trim();
            TxtNotebookUrlPlaceholder.Visibility = string.IsNullOrEmpty(text) ? Visibility.Visible : Visibility.Collapsed;

            bool isValid = IsValidNotebookLmUrl(text);
            UpdateNotebookLmStatus(isValid, text);

            if (isValid)
            {
                try
                {
                    string dir = Path.GetDirectoryName(_notebookLmConfigPath)!;
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(_notebookLmConfigPath, text);
                }
                catch { }
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
                Title = "Chọn file đưa vào NotebookLM",
                Filter = "Tất cả file hỗ trợ (*.*)|*.*|Tài liệu PDF & Office (*.pdf;*.docx;*.txt;*.md)|*.pdf;*.docx;*.txt;*.md|Âm thanh (*.mp3;*.wav)|*.mp3;*.wav"
            };
            if (ofd.ShowDialog() == true)
            {
                SetSelectedFile(ofd.FileName);
            }
        }

        private void SetSelectedFile(string path)
        {
            _currentFilePath = path;
            var fi = new FileInfo(path);
            TxtFileName.Text = fi.Name;
            TxtFileMeta.Text = $"{fi.Length / 1024.0:F1} KB • {fi.Extension.ToUpper()} • Cập nhật: {fi.LastWriteTime:dd/MM/yyyy HH:mm}";
            BtnClearFiles.Visibility = Visibility.Visible;

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

            ShowModernToast($"Đã nhận tệp: {fi.Name}", "📎", "#10B981");
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

        private void BtnCopyPath_Click(object sender, RoutedEventArgs e)
        {
            string path = _currentFilePath ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\Downloads";
            try
            {
                Clipboard.SetText(path);
                string fileName = Path.GetFileName(path);
                ShowModernToast($"Đã copy đường dẫn: {(string.IsNullOrEmpty(fileName) ? path : fileName)}", "📋", "#38BDF8");
            }
            catch { }
        }

        private void BtnSendToNotebookLM_Click(object sender, RoutedEventArgs e)
        {
            string url = !string.IsNullOrWhiteSpace(InputNotebookUrl.Text)
                ? InputNotebookUrl.Text.Trim()
                : "https://notebooklm.google.com/";

            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                url = "https://" + url;
            }

            if (!string.IsNullOrEmpty(_currentFilePath) && File.Exists(_currentFilePath))
            {
                try
                {
                    var fileDrop = new System.Collections.Specialized.StringCollection { _currentFilePath };
                    Clipboard.SetFileDropList(fileDrop);
                }
                catch
                {
                    try { Clipboard.SetText(_currentFilePath); } catch { }
                }
            }

            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                TxtFileMeta.Text = "✅ Đã mở NotebookLM! Đường dẫn file đã copy sẵn trong Clipboard.";
                ShowModernToast("Đang mở Google NotebookLM...", "🚀", "#38BDF8");
            }
            catch (Exception ex)
            {
                ShowModernToast("Không thể mở trình duyệt: " + ex.Message, "⚠️", "#EF4444");
            }
        }

        private void BtnClearFiles_Click(object sender, RoutedEventArgs e)
        {
            _currentFilePath = null;
            TxtFileName.Text = "Kéo thả file vào đây (PDF, Word, TXT, CSV, Code, Audio...)";
            TxtFileMeta.Text = "Chưa có file nào • Thả file để chuẩn bị đưa vào NotebookLM";
            TxtFileIcon.Text = "📄";
            BtnClearFiles.Visibility = Visibility.Collapsed;
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

            double targetWidth = 540;
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
                        targetWidth = 540;
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
                        targetWidth = 560;
                        targetHeight = 110;
                        targetView = ViewCamera;
                        CardGlow.Color = (Color)ColorConverter.ConvertFromString("#10B981");
                        UpdateCameraUi(Process.GetProcessesByName("WindowsCamera").Length > 0);
                        break;

                    case IslandState.Notification:
                        targetWidth = 620;
                        targetHeight = 155;
                        targetView = ViewNotification;
                        CardGlow.Color = (Color)ColorConverter.ConvertFromString("#0284C7");
                        break;

                    case IslandState.Dropzone:
                        targetWidth = 640;
                        targetHeight = 145;
                        targetView = ViewDropzone;
                        CardGlow.Color = (Color)ColorConverter.ConvertFromString("#10B981");
                        break;

                    case IslandState.LockScreen:
                        targetWidth = 520;
                        targetHeight = 95;
                        targetView = ViewLockScreen;
                        CardGlow.Color = (Color)ColorConverter.ConvertFromString("#F43F5E");
                        break;
                }

                UpdateNotchGeometry(targetWidth, targetHeight, 0, 0);
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

                    var animWidth = new DoubleAnimation(IslandCard.ActualWidth > 0 ? IslandCard.ActualWidth : IslandCard.Width, targetWidth, duration) { EasingFunction = ease };
                    var animHeight = new DoubleAnimation(IslandCard.ActualHeight > 0 ? IslandCard.ActualHeight : IslandCard.Height, targetHeight, duration) { EasingFunction = ease };

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

        // Core Hub Click: returns to Compact and prevents re-triggering pull-down
        private void CoreHub_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            _preventPullDownUntil = DateTime.UtcNow.AddMilliseconds(700);
            SwitchState(IslandState.Compact);
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

        private int _sampleNotifyIndex = 0;

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

        private void BtnDeleteNotification_Click(object sender, RoutedEventArgs e)
        {
            if (_currentNotification != null)
            {
                _realNotificationsList.Remove(_currentNotification);
                var filtered = GetFilteredNotifications();
                if (_currentFilteredIndex >= filtered.Count)
                {
                    _currentFilteredIndex = Math.Max(0, filtered.Count - 1);
                }
                UpdateNotificationUI();
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

                _realNotificationsList.Insert(0, notif);

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
            if (!_realNotificationsList.Contains(notif))
            {
                _realNotificationsList.Insert(0, notif);
            }
            _currentFilteredIndex = 0;
            UpdateNotificationUI();
            SwitchState(IslandState.Notification);
        }

        private void BtnSimulateMsg_Click(object sender, RoutedEventArgs e)
        {
            _sampleNotifyIndex++;
            DynamicNotification sampleNotif;

            if (_sampleNotifyIndex % 2 == 1)
            {
                // Sample Facebook notification
                sampleNotif = new DynamicNotification
                {
                    AppName = "Facebook",
                    AppIcon = "📘",
                    AppColor = "#1877F2",
                    Sender = "Facebook • Nguyễn Hoàng",
                    Message = "Đã nhắc đến bạn trong một bình luận: 'Alo bạn xem bài viết mới này hay quá nè!'",
                    Time = DateTime.Now.ToString("HH:mm"),
                    PrimaryId = "facebook"
                };
            }
            else
            {
                // Sample Zalo notification
                sampleNotif = new DynamicNotification
                {
                    AppName = "Zalo",
                    AppIcon = "💬",
                    AppColor = "#0068FF",
                    Sender = "Zalo • Trần Hải Đăng",
                    Message = "Alo bạn ơi! Tài liệu thiết kế dự án đã hoàn thiện rồi nhé, bạn xem qua rồi phản hồi mình nha!",
                    Time = DateTime.Now.ToString("HH:mm"),
                    PrimaryId = "com.vng.zalo"
                };
            }

            _realNotificationsList.Insert(0, sampleNotif);
            _currentFilteredIndex = 0;
            UpdateNotificationUI();
            SwitchState(IslandState.Notification);
        }

        private void InputReply_TextChanged(object sender, TextChangedEventArgs e)
        {
            TxtReplyPlaceholder.Visibility = string.IsNullOrEmpty(InputReply.Text) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void InputReply_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                BtnSendReply_Click(sender, e);
            }
        }

        private void BtnSendReply_Click(object sender, RoutedEventArgs e)
        {
            string reply = InputReply.Text.Trim();
            if (string.IsNullOrWhiteSpace(reply)) return;

            string senderName = TxtNotifySender.Text;
            string appName = TxtAppTag.Text;

            // Copy to Windows Clipboard
            try
            {
                Clipboard.SetText(reply);
            }
            catch { }

            // Bring application window (e.g. Zalo, Telegram, Facebook) to front!
            RealNotificationService.FocusApp(_currentNotification?.PrimaryId);

            // Visual feedback
            TxtNotifySender.Text = $"✓ Đã sao chép phản hồi!";
            TxtNotifyContent.Text = $"Đã sao chép: \"{reply}\" và mở {appName}. Bạn có thể dán ngay bằng Ctrl+V!";
            InputReply.Text = "";

            // Auto-collapse back to Compact after 2.2 seconds
            var returnTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2200) };
            returnTimer.Tick += (s, args) =>
            {
                returnTimer.Stop();
                if (_currentState == IslandState.Notification)
                {
                    SwitchState(IslandState.Compact);
                }
            };
            returnTimer.Start();
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
        #endregion
    }
}
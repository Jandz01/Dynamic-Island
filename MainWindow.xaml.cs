using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace DynamicIsland
{
    public enum IslandState
    {
        Compact,
        Ai,
        Privacy,
        Dropzone,
        Orbital
    }

    public partial class MainWindow : Window
    {
        private IslandState _currentState = IslandState.Compact;
        private readonly DispatcherTimer _clockTimer = new();
        private readonly DispatcherTimer _orbitTimer = new();
        private double _orbitAngle = 0;
        private string? _currentFilePath = null;

        public MainWindow()
        {
            InitializeComponent();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Position at top center of primary screen
            double screenWidth = SystemParameters.PrimaryScreenWidth;
            Left = (screenWidth - Width) / 2;
            Top = 0;

            // Start clock timer
            _clockTimer.Interval = TimeSpan.FromSeconds(1);
            _clockTimer.Tick += ClockTimer_Tick;
            _clockTimer.Start();
            ClockTimer_Tick(null, EventArgs.Empty);

            // Start orbital rotation timer
            _orbitTimer.Interval = TimeSpan.FromMilliseconds(30);
            _orbitTimer.Tick += OrbitTimer_Tick;
            _orbitTimer.Start();

            // Setup initial state
            ApplyState(IslandState.Compact, animate: false);
        }

        private void ClockTimer_Tick(object? sender, EventArgs e)
        {
            TxtClock.Text = DateTime.Now.ToString("HH:mm:ss");

            // RAM usage estimate
            long memoryUsed = GC.GetTotalMemory(false) / (1024 * 1024);
            TxtRam.Text = $"RAM {Math.Max(38, memoryUsed % 50 + 35)}%";
        }

        private void OrbitTimer_Tick(object? sender, EventArgs e)
        {
            if (_currentState != IslandState.Orbital) return;

            // Increment rotation angle
            _orbitAngle += 0.015;
            if (_orbitAngle > Math.PI * 2) _orbitAngle -= Math.PI * 2;

            // Central ellipse parameters
            double centerX = 260 - 24; // canvas width / 2 minus half bubble width
            double centerY = 110 - 24; // canvas height / 2 minus half bubble height
            double radiusX = 140;
            double radiusY = 70;

            Border[] orbs = [OrbMusic, OrbAi, OrbPrivacy, OrbDrop, OrbSys, OrbPomodoro];
            for (int i = 0; i < orbs.Length; i++)
            {
                double angle = _orbitAngle + i * (Math.PI * 2 / orbs.Length);
                double x = centerX + radiusX * Math.Cos(angle);
                double y = centerY + radiusY * Math.Sin(angle);

                Canvas.SetLeft(orbs[i], x);
                Canvas.SetTop(orbs[i], y);
            }
        }

        private void SwitchState(IslandState newState)
        {
            if (_currentState == newState) return;
            ApplyState(newState, animate: true);
        }

        private void ApplyState(IslandState newState, bool animate)
        {
            _currentState = newState;

            double targetWidth = 420;
            double targetHeight = 42;
            double targetRadius = 21;

            // Hide all sub-views first
            ViewCompact.Visibility = Visibility.Collapsed;
            ViewAi.Visibility = Visibility.Collapsed;
            ViewPrivacy.Visibility = Visibility.Collapsed;
            ViewDropzone.Visibility = Visibility.Collapsed;
            ViewOrbital.Visibility = Visibility.Collapsed;

            FrameworkElement targetView = ViewCompact;

            switch (newState)
            {
                case IslandState.Compact:
                    targetWidth = 430;
                    targetHeight = 44;
                    targetRadius = 22;
                    targetView = ViewCompact;
                    CardGlow.Color = (Color)ColorConverter.ConvertFromString("#38BDF8");
                    break;

                case IslandState.Ai:
                    targetWidth = 590;
                    targetHeight = 115;
                    targetRadius = 24;
                    targetView = ViewAi;
                    CardGlow.Color = (Color)ColorConverter.ConvertFromString("#A855F7");
                    break;

                case IslandState.Privacy:
                    targetWidth = 530;
                    targetHeight = 76;
                    targetRadius = 24;
                    targetView = ViewPrivacy;
                    CardGlow.Color = (Color)ColorConverter.ConvertFromString("#F43F5E");
                    break;

                case IslandState.Dropzone:
                    targetWidth = 520;
                    targetHeight = 98;
                    targetRadius = 24;
                    targetView = ViewDropzone;
                    CardGlow.Color = (Color)ColorConverter.ConvertFromString("#10B981");
                    break;

                case IslandState.Orbital:
                    targetWidth = 640;
                    targetHeight = 290;
                    targetRadius = 26;
                    targetView = ViewOrbital;
                    CardGlow.Color = (Color)ColorConverter.ConvertFromString("#60A5FA");
                    break;
            }

            targetView.Visibility = Visibility.Visible;

            if (animate)
            {
                var duration = TimeSpan.FromMilliseconds(320);
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

                var animWidth = new DoubleAnimation(IslandCard.Width, targetWidth, duration) { EasingFunction = ease };
                var animHeight = new DoubleAnimation(IslandCard.Height, targetHeight, duration) { EasingFunction = ease };

                IslandCard.BeginAnimation(WidthProperty, animWidth);
                IslandCard.BeginAnimation(HeightProperty, animHeight);
                IslandCard.CornerRadius = new CornerRadius(targetRadius);

                // Fade-in animation for content
                var fadeAnim = new DoubleAnimation(0.2, 1.0, TimeSpan.FromMilliseconds(250));
                targetView.BeginAnimation(OpacityProperty, fadeAnim);
            }
            else
            {
                IslandCard.Width = targetWidth;
                IslandCard.Height = targetHeight;
                IslandCard.CornerRadius = new CornerRadius(targetRadius);
            }
        }

        // Mouse Drag to adjust position
        private void IslandCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed && _currentState == IslandState.Compact)
            {
                DragMove();
            }
        }

        private void IslandCard_MouseEnter(object sender, MouseEventArgs e)
        {
            var anim = new DoubleAnimation(0.45, 0.75, TimeSpan.FromMilliseconds(200));
            CardGlow.BeginAnimation(DropShadowEffect.OpacityProperty, anim);
        }

        private void IslandCard_MouseLeave(object sender, MouseEventArgs e)
        {
            var anim = new DoubleAnimation(0.75, 0.45, TimeSpan.FromMilliseconds(250));
            CardGlow.BeginAnimation(DropShadowEffect.OpacityProperty, anim);
        }

        // Navigation Click Handlers
        private void BtnSwitchAi_Click(object sender, RoutedEventArgs e) => SwitchState(IslandState.Ai);
        private void BtnSwitchOrbital_Click(object sender, RoutedEventArgs e) => SwitchState(IslandState.Orbital);
        private void BtnSwitchPrivacy_Click(object sender, RoutedEventArgs e) => SwitchState(IslandState.Privacy);
        private void BtnSwitchDropzone_Click(object sender, RoutedEventArgs e) => SwitchState(IslandState.Dropzone);
        private void BtnCloseToCompact_Click(object sender, RoutedEventArgs e) => SwitchState(IslandState.Compact);

        // Orbital Core & Satellites Click Handlers
        private void CoreHub_Click(object sender, MouseButtonEventArgs e) => SwitchState(IslandState.Compact);
        private void OrbAi_Click(object sender, MouseButtonEventArgs e) => SwitchState(IslandState.Ai);
        private void OrbPrivacy_Click(object sender, MouseButtonEventArgs e) => SwitchState(IslandState.Privacy);
        private void OrbDrop_Click(object sender, MouseButtonEventArgs e) => SwitchState(IslandState.Dropzone);

        private void OrbMusic_Click(object sender, MouseButtonEventArgs e)
        {
            MessageBox.Show("🎵 Music Player Active\nPlaying: Giang Lê - Original Sound #dynamicisland", "Dynamic Island Music", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void OrbSys_Click(object sender, MouseButtonEventArgs e)
        {
            MessageBox.Show("💊 System Diagnostics:\nCPU: Normal\nRAM: 48% used\nThermal: 45°C Optimal", "Dynamic Island Sysmon", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void OrbPomodoro_Click(object sender, MouseButtonEventArgs e)
        {
            MessageBox.Show("⏱️ Pomodoro Focus Session\n25:00 Remaining - Keep Coding!", "Dynamic Island Focus", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // AI Input Handling
        private void InputAi_TextChanged(object sender, TextChangedEventArgs e)
        {
            TxtAiPlaceholder.Visibility = string.IsNullOrEmpty(InputAi.Text) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void InputAi_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(InputAi.Text))
            {
                string query = InputAi.Text.Trim();
                InputAi.Text = "";
                MessageBox.Show($"🤖 AI Assistant processing:\n\n\"{query}\"\n\nExecuting context-aware coding command...", "Dynamic Island AI", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void ChipAction_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Content != null)
            {
                InputAi.Text = btn.Content.ToString();
                InputAi.Focus();
                InputAi.CaretIndex = InputAi.Text.Length;
            }
        }

        // Privacy Shield Actions
        private void BtnBlockMic_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("🔒 Privacy Protected:\nMicrophone access has been instantly blocked!", "Dynamic Shield", MessageBoxButton.OK, MessageBoxImage.Information);
            SwitchState(IslandState.Compact);
        }

        // Dropzone Drag and Drop
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
                    _currentFilePath = files[0];
                    var fi = new FileInfo(_currentFilePath);
                    TxtFileName.Text = fi.Name;
                    TxtFileMeta.Text = $"{fi.Length / 1024.0:F1} KB • {fi.Extension.ToUpper()} • {fi.DirectoryName}";
                }
            }
        }

        private void BtnOpenDownloads_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string folder = _currentFilePath != null && File.Exists(_currentFilePath)
                    ? Path.GetDirectoryName(_currentFilePath)!
                    : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\Downloads";

                if (Directory.Exists(folder))
                    Process.Start("explorer.exe", folder);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        private void BtnCopyPath_Click(object sender, RoutedEventArgs e)
        {
            string path = _currentFilePath ?? @"~/Downloads/project-spec.zip";
            Clipboard.SetText(path);
            MessageBox.Show($"Copied path to clipboard:\n{path}", "Dynamic Island Dropzone");
        }

        private void BtnAskAiFile_Click(object sender, RoutedEventArgs e)
        {
            string fileName = TxtFileName.Text;
            SwitchState(IslandState.Ai);
            InputAi.Text = $"Explain and review code for file: {fileName}";
            InputAi.Focus();
        }
    }
}
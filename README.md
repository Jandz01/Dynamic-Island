<div align="center">

# 🏝️ Dynamic Island for Windows

**An organic, physics-driven Dynamic Island experience for Windows 11 & 10.**  
Featuring authentic liquid morphing, orbital multitasking satellites, realtime social notifications, laptop camera controls, Google NotebookLM dropzone integration, and 100% standalone zero-dependency execution.

[![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011%20(x64)-0078D4?style=for-the-badge&logo=windows)](https://github.com/Jandz01/Dynamic-Island)
[![.NET](https://img.shields.io/badge/.NET-9.0%20WPF-512BD4?style=for-the-badge&logo=dotnet)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/License-MIT-success?style=for-the-badge)](LICENSE)
[![Release](https://img.shields.io/github/v/release/Jandz01/Dynamic-Island?style=for-the-badge&color=blue)](https://github.com/Jandz01/Dynamic-Island/releases)
[![Zero Runtime](https://img.shields.io/badge/Install-Zero%20Dependencies%20(Portable)-10B981?style=for-the-badge)](https://github.com/Jandz01/Dynamic-Island/releases)

<br/>

[**Download Latest Release (v1.2.0)**](https://github.com/Jandz01/Dynamic-Island/releases/latest) • [**Key Features**](#-key-features) • [**Gestures & Navigation**](#-controls--gestures) • [**Architecture**](#-architecture--tech-stack)

</div>

---

## ✨ Overview

**Dynamic Island for Windows** brings the fluid, context-aware notch experience to desktop PCs and laptops. Engineered from scratch in **.NET 9 WPF** with native Win32 interop, it combines mathematical cubic Bézier curve geometry with physical rubber-band drag elasticity, transforming the static top bezel into an interactive command hub.

---

## 🚀 Key Features

### 1. 💧 Physics-Based Liquid Morphing & Orbital Satellites
- **Elastic Droplet Physics**: Pulling the notch downward deforms the bottom boundary like real viscous fluid, adhering to organic tensile curves before releasing a liquid droplet straight into the planetary Black Hole.
- **Orbital Multitasking Hub**: Expands into 6 orbiting planetary satellites representing distinct tasks:
  - 🎵 **Music & Media**
  - 💬 **Live Notification Hub**
  - ⏳ **Pomodoro Focus Timer**
  - 📷 **Laptop Camera & Webcam Hub**
  - 📁 **NotebookLM File Dropzone**
  - 🔒 **Instant Workstation Lock**

---

### 2. 🎯 Universal Drag Handle (`::`) & Absolute Positioning
- **Persistent `::` Grip Handle**: Available across **every single state and view** (Mini, Compact, Music, Pomodoro, Camera, Notifications, Dropzone, and Lock Screen).
- **Smooth Repositioning**: Hold and drag the `::` handle to position Dynamic Island anywhere along your top bezel.
- **Instant Snap-to-Center**: Double-click any `::` handle (or press <kbd>Home</kbd> / <kbd>↓</kbd>) to instantly center the Island on your active monitor.
- **Minisize Mode (Floating Disc)**: Collapse into an ultra-compact 32px pill disc when you need unobstructed view of browser tabs, while still retaining the `::` drag handle.

---

### 3. 📷 Camera Hub & Stealth Screenshot Mode
- **One-Tap Webcam Toggle**: Easily toggle your laptop's integrated camera or external webcam on/off with real-time status indicators (🟢 Active / ⚪ Off).
- **Stealth Screenshot (Vanishing Act)**: When you trigger a screen capture (<kbd>Win</kbd> + <kbd>Shift</kbd> + <kbd>S</kbd> or via the Island's record button), Dynamic Island **automatically fades out to 100% transparency** before the capture starts—preventing the Island itself from obstructing your screenshots—then smoothly fades back in once captured!
- **Auto-Turnoff on Exit**: Transitioning away from the Camera tab (e.g. pulling back down to orbital planets or closing to compact) automatically shuts down the camera process to protect your privacy and preserve battery life.

---

### 4. 📁 Google NotebookLM Dropzone Hub
- **Universal File Staging**: Drag and drop any document (PDF, Word, TXT, Markdown, CSV, source code, audio files) directly into the notch.
- **Smart URL Management**: Paste your specific Google NotebookLM notebook URL; Dynamic Island validates the syntax in real-time, displays a glowing connection badge (🟢 **Đã kết nối NotebookLM**), and remembers it as your persistent default.
- **One-Click Push**: Click **"🚀 Đưa vào NotebookLM"** to launch your browser directly to your notebook while automatically copying the staged file paths to your clipboard for instant source upload.

---

### 5. 🔔 Realtime Notification Hub (Facebook, Messenger & Zalo)
- **Direct Windows Action Center SQLite Integration**: Reads incoming notifications directly from `wpndatabase.db` in real-time.
- **Intelligent Source Separation**: Distinguishes between Facebook Web notifications, desktop Facebook, Messenger, and Zalo.
- **Category Tabs**: Filter by `All`, `💬 Zalo`, or `📘 Facebook`.
- **Dismiss & Reveal (`🗑️ Xóa`)**: Clear individual notifications with one click so pending notifications immediately bubble up into view.
- **Quick Reply Bar**: Preset response chips (`👍 Ok`, `⏳ Đợi 5p`) and inline reply box.

---

### 6. 🔒 Instant Lock Screen
- **Native Hardware Lock**: Uses Win32 `LockWorkStation()` to put Windows into the secure lock screen instantly, mimicking physical lid close or fresh boot.

---

### 7. 🎵 Media Player & ⏳ Pomodoro Focus
- **Live Media Ticker**: Connects with Spotify, YouTube, and Windows Media sessions with live spinning vinyl disc animations, album artwork, and seekable progress bar.
- **Pomodoro Timer**: Configurable 25m Focus, 5m Break, and 15m Rest intervals with modern audio-visual completion alerts.

---

### 8. 🛡️ 100% Standalone Portable (Zero .NET Install Required)
- Built as a **Self-Contained Single-File Executable** (`win-x64`).
- **No .NET Runtime required**: Friends and colleagues can download the `.zip` or `.exe` and run it out of the box on any Windows 10/11 system without encountering runtime errors.

---

## 🕹️ Controls & Gestures

| Action / Gesture | Target | Description |
| :--- | :--- | :--- |
| **Drag `::`** | `::` grip handle | Freely move Dynamic Island horizontally across your screen |
| **Double-Click `::`** | `::` grip handle | Instantly snaps Dynamic Island back to screen center |
| **Pull Down Notch** | Notch body | Stretches organic liquid downward to reveal Orbital Planets |
| **Click Planet** | Orbital satellite | Releases liquid droplet that morphs into the chosen task view |
| **Click ➖ (Minisize)** | Compact bar | Collapses into an unobtrusive mini vinyl disc |
| **Click Disc in Mini** | Mini disc | Expands back to full Compact mode |
| **Click 🔴 (Record)** | Compact bar | Hides Island and launches Windows Snipping Tool stealthily |
| **<kbd>←</kbd> / <kbd>→</kbd>** | Any state | Fine-tune horizontal offset pixel by pixel |
| **<kbd>Home</kbd> / <kbd>↓</kbd>** | Any state | Reset to exact screen center |

---

## 📥 Installation

### Option 1: Direct Executable (Recommended)
1. Head over to [**Releases**](https://github.com/Jandz01/Dynamic-Island/releases/latest).
2. Download `DynamicIsland-win-x64.zip` or `DynamicIsland.exe`.
3. Extract (if zipped) and double-click `DynamicIsland.exe`.
4. *No installation, no registry changes, no .NET download prompt.*

### Option 2: Build from Source
Ensure you have the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) installed:
```powershell
# Clone the repository
git clone https://github.com/Jandz01/Dynamic-Island.git
cd Dynamic-Island

# Build and run
dotnet run

# Or publish standalone self-contained win-x64
dotnet publish DynamicIsland.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

---

## 🏗️ Architecture & Tech Stack

```
DynamicIsland/
├── MainWindow.xaml          # Fluent dark-glass UI, SVG Bézier notch geometries, HUD toasts
├── MainWindow.xaml.cs       # Liquid physics, Win32 interop, drag kinematics, UI state machine
├── RealNotificationService.cs # SQLite watcher on Windows wpndatabase.db (Zalo, Facebook, etc.)
├── App.xaml / App.xaml.cs   # Application lifecycle, single-instance mutex, crash handler
└── .github/workflows/       # GitHub Actions CI/CD automated release pipeline
```

- **Framework**: .NET 9.0 (WPF - Windows Presentation Foundation)
- **Target OS**: Windows 10 (19041+) / Windows 11 (x64)
- **Graphics**: Hardware-accelerated Direct3D / WPF composition engine, custom SVG cubic Bézier paths
- **Packaging**: Self-Contained Single-File (`win-x64`) with compressed native assemblies

---

## 📄 License

This project is licensed under the [MIT License](LICENSE).
Feel free to use, modify, and distribute with attribution.

---

<div align="center">
  <sub>Crafted with passion for the Windows Community. If you like this project, please give it a ⭐ star on GitHub!</sub>
</div>

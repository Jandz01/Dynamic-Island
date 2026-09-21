using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace DynamicIsland
{
    public class UpdateInfo
    {
        public Version Version { get; set; } = new Version(1, 0, 0);
        public string VersionTag { get; set; } = "v1.0.0";
        public string Title { get; set; } = "";
        public string Changelog { get; set; } = "";
        public string DownloadUrl { get; set; } = "";
        public bool IsZip { get; set; } = false;
    }

    public static class UpdateService
    {
        public static readonly Version CurrentVersion = new Version(1, 5, 0);
        public static readonly string CurrentVersionString = "v1.5.0";

        private const string RepoOwner = "Jandz01";
        private const string RepoName = "Dynamic-Island";
        private const string GithubReleasesApi = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
        private const string GithubRawManifest = $"https://raw.githubusercontent.com/{RepoOwner}/{RepoName}/main/version.json";

        private static readonly HttpClient _httpClient = new HttpClient();

        static UpdateService()
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DynamicIsland", "1.0"));
            _httpClient.Timeout = TimeSpan.FromSeconds(15);
        }

        /// <summary>
        /// Check whether a newer version is available on GitHub Releases or raw manifest
        /// </summary>
        public static async Task<UpdateInfo?> CheckForUpdatesAsync()
        {
            try
            {
                // 1. Try GitHub Releases API first
                var req = new HttpRequestMessage(HttpMethod.Get, GithubReleasesApi);
                var res = await _httpClient.SendAsync(req);
                if (res.IsSuccessStatusCode)
                {
                    string json = await res.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    string tagName = root.TryGetProperty("tag_name", out var tProp) ? tProp.GetString() ?? "" : "";
                    string title = root.TryGetProperty("name", out var nProp) ? nProp.GetString() ?? "" : "";
                    string body = root.TryGetProperty("body", out var bProp) ? bProp.GetString() ?? "" : "";

                    string cleanVersion = tagName.TrimStart('v', 'V');
                    if (Version.TryParse(cleanVersion, out Version? remoteVer) && remoteVer > CurrentVersion)
                    {
                        string downloadUrl = "";
                        bool isZip = false;

                        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var asset in assets.EnumerateArray())
                            {
                                string aName = asset.TryGetProperty("name", out var an) ? an.GetString() ?? "" : "";
                                string aUrl = asset.TryGetProperty("browser_download_url", out var au) ? au.GetString() ?? "" : "";

                                if (aName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                                {
                                    downloadUrl = aUrl;
                                    isZip = false;
                                    break;
                                }
                                if (aName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                                {
                                    downloadUrl = aUrl;
                                    isZip = true;
                                }
                            }
                        }

                        if (string.IsNullOrEmpty(downloadUrl))
                        {
                            downloadUrl = $"https://github.com/{RepoOwner}/{RepoName}/releases/download/{tagName}/DynamicIsland.exe";
                        }

                        return new UpdateInfo
                        {
                            Version = remoteVer,
                            VersionTag = tagName,
                            Title = string.IsNullOrWhiteSpace(title) ? $"Dynamic Island {tagName}" : title,
                            Changelog = body,
                            DownloadUrl = downloadUrl,
                            IsZip = isZip
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GitHub API check failed: {ex.Message}");
            }

            // 2. Fallback to raw version.json on repository
            try
            {
                var rawRes = await _httpClient.GetAsync(GithubRawManifest);
                if (rawRes.IsSuccessStatusCode)
                {
                    string rawJson = await rawRes.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(rawJson);
                    var root = doc.RootElement;

                    string vStr = root.TryGetProperty("version", out var vp) ? vp.GetString() ?? "" : "";
                    string cleanVer = vStr.TrimStart('v', 'V');
                    if (Version.TryParse(cleanVer, out Version? rawVer) && rawVer > CurrentVersion)
                    {
                        string title = root.TryGetProperty("title", out var tp) ? tp.GetString() ?? "" : $"Bản cập nhật v{cleanVer}";
                        string changelog = root.TryGetProperty("changelog", out var cp) ? cp.GetString() ?? "" : "";
                        string dUrl = root.TryGetProperty("downloadUrl", out var dp) ? dp.GetString() ?? "" : "";

                        return new UpdateInfo
                        {
                            Version = rawVer,
                            VersionTag = $"v{cleanVer}",
                            Title = title,
                            Changelog = changelog,
                            DownloadUrl = dUrl,
                            IsZip = dUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Raw manifest check failed: {ex.Message}");
            }

            return null; // Up-to-date
        }

        /// <summary>
        /// Download the update with progress tracking and launch seamless installer
        /// </summary>
        public static async Task DownloadAndInstallUpdateAsync(UpdateInfo info, IProgress<int> progress)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "DynamicIsland_Update");
            Directory.CreateDirectory(tempDir);

            string fileExt = info.IsZip ? ".zip" : ".exe";
            string tempFile = Path.Combine(tempDir, $"update_package{fileExt}");

            // 1. Download file with progress reporting
            using (var response = await _httpClient.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                long totalBytes = response.Content.Headers.ContentLength ?? -1L;

                using var stream = await response.Content.ReadAsStreamAsync();
                using var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

                byte[] buffer = new byte[8192];
                long totalRead = 0;
                int bytesRead;

                while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, bytesRead);
                    totalRead += bytesRead;

                    if (totalBytes > 0)
                    {
                        int percent = (int)((totalRead * 100) / totalBytes);
                        progress.Report(percent);
                    }
                }
            }

            progress.Report(100);

            // 2. Prepare updater script
            string currentExePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            string appDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');

            string batchPath = Path.Combine(tempDir, "apply_update.cmd");
            string scriptContent;

            if (info.IsZip)
            {
                scriptContent = $@"@echo off
chcp 65001 > nul
timeout /t 1 /nobreak > nul
taskkill /f /im DynamicIsland.exe > nul 2>&1
timeout /t 1 /nobreak > nul
powershell -Command ""Expand-Archive -Path '{tempFile}' -DestinationPath '{appDir}' -Force""
start """" ""{currentExePath}"" --updated {info.VersionTag}
del ""%~f0""
";
            }
            else
            {
                scriptContent = $@"@echo off
chcp 65001 > nul
timeout /t 1 /nobreak > nul
taskkill /f /im DynamicIsland.exe > nul 2>&1
timeout /t 1 /nobreak > nul
copy /y ""{tempFile}"" ""{currentExePath}""
start """" ""{currentExePath}"" --updated {info.VersionTag}
del ""%~f0""
";
            }

            File.WriteAllText(batchPath, scriptContent, System.Text.Encoding.UTF8);

            // 3. Launch updater batch script and terminate current app
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{batchPath}\"",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            Process.Start(psi);
            Application.Current.Dispatcher.Invoke(() =>
            {
                Application.Current.Shutdown();
            });
        }
    }
}

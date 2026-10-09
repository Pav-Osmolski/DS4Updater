/*
DS4Updater
Copyright (C) 2023  Travis Nickles

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <https://www.gnu.org/licenses/>.
*/

using DS4Updater.Dtos;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Shell;

namespace DS4Updater
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private const string CUSTOM_EXE_CONFIG_FILENAME = "custom_exe_name.txt";
        private const string DS4WINDOWS_RELEASES_API_URI = "https://api.github.com/repos/Pav-Osmolski/DS4Windows/releases";
        private const string DS4WINDOWS_RELEASE_DOWNLOAD_BASE_URI = "https://github.com/Pav-Osmolski/DS4Windows/releases/download";
        //WebClient wc = new WebClient(), subwc = new WebClient();
        private HttpClient wc = new HttpClient();
        protected string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DS4Windows");
        string exepath = AppContext.BaseDirectory;
        string version = "0", newversion = "0";
        string currentProductVersion = "0";
        string newversionTag = "";
        Uri newversionDownloadUri = null;
        GitHubRelease selectedRelease = null;
        PortableReleaseIdentity verifiedRelease = null;
        readonly string requestedReleaseTag;
        bool downloading = false;
        private int round = 1;
        public bool downloadLang = false;
        private bool backup;
        private string outputUpdatePath = "";
        private string updatesFolder = "";
        public bool autoLaunchDS4W = false;
        public bool forceLaunchDS4WUser = false;
        internal string arch = Environment.Is64BitProcess ? "x64" : "x86";
        private string custom_exe_name_path;
        public string CustomExeNamePath { get => custom_exe_name_path; }

        private static bool TryParseReleaseVersion(string versionText, out Version parsedVersion)
        {
            return ReleaseChannelPolicy.TryParseReleaseVersion(versionText, out parsedVersion);
        }

        private static string FormatReleaseVersion(Version parsedVersion)
        {
            if (parsedVersion.Revision >= 0) return parsedVersion.ToString(4);
            if (parsedVersion.Build >= 0) return parsedVersion.ToString(3);
            return parsedVersion.ToString(2);
        }

        private bool ShouldUpdate()
        {
            if (selectedRelease is not null)
            {
                string installedReleaseTag = ReadInstalledReleaseTag();
                return ReleaseChannelPolicy.ShouldUpdate(
                    selectedRelease,
                    version,
                    ReleaseChannelPolicy.IsPrereleaseInstall(
                        currentProductVersion,
                        installedReleaseTag),
                    installedReleaseTag);
            }

            string currentVersion = version.Replace(',', '.');
            if (TryParseReleaseVersion(currentVersion, out var parsedCurrent) &&
                TryParseReleaseVersion(newversion, out var parsedNew))
            {
                return parsedCurrent < parsedNew;
            }

            return currentVersion.CompareTo(newversion) != 0;
        }

        private string ReadInstalledReleaseTag()
        {
            string markerPath = Path.Combine(
                exepath,
                ReleaseChannelPolicy.InstalledReleaseFileName);
            try
            {
                return File.Exists(markerPath) ? File.ReadAllText(markerPath).Trim() : string.Empty;
            }
            catch (IOException)
            {
                return string.Empty;
            }
            catch (UnauthorizedAccessException)
            {
                return string.Empty;
            }
        }

        private void SetNewVersion(string versionText)
        {
            string trimmedVersion = versionText.Trim();
            newversionTag = trimmedVersion.StartsWith("v", StringComparison.OrdinalIgnoreCase) ?
                trimmedVersion : $"v{trimmedVersion}";
            newversion = TryParseReleaseVersion(trimmedVersion, out var parsedVersion) ?
                FormatReleaseVersion(parsedVersion) : trimmedVersion.TrimStart('v', 'V');
            newversionDownloadUri = BuildDS4WindowsDownloadUri();
        }

        private Uri BuildDS4WindowsDownloadUri()
        {
            string releaseTag = string.IsNullOrWhiteSpace(newversionTag) ? $"v{newversion}" : newversionTag;
            return new Uri($"{DS4WINDOWS_RELEASE_DOWNLOAD_BASE_URI}/{Uri.EscapeDataString(releaseTag)}/DS4Windows_{newversion}_{arch}.zip");
        }

        private string GetArchiveFileName()
        {
            string releaseName = string.IsNullOrWhiteSpace(newversionTag) ?
                newversion : newversionTag;
            foreach (char invalidCharacter in Path.GetInvalidFileNameChars())
            {
                releaseName = releaseName.Replace(invalidCharacter, '_');
            }

            return $"DS4Windows_{releaseName}_{arch}.zip";
        }

        private GitHubReleaseAsset FindReleaseAsset(GitHubRelease release, string versionText)
        {
            if (release.assets == null) return null;

            string expectedName = $"DS4Windows_{versionText}_{arch}.zip";
            return release.assets.FirstOrDefault(asset =>
                       asset.name.Equals(expectedName, StringComparison.OrdinalIgnoreCase)) ??
                   release.assets.FirstOrDefault(asset =>
                       asset.name.StartsWith("DS4Windows_", StringComparison.OrdinalIgnoreCase) &&
                       asset.name.EndsWith($"_{arch}.zip", StringComparison.OrdinalIgnoreCase));
        }

        private bool TrySelectLatestRelease(GitHubRelease[] releases)
        {
            GitHubRelease[] availableReleases = releases ?? Array.Empty<GitHubRelease>();
            string installedReleaseTag = ReadInstalledReleaseTag();
            bool currentBuildIsPrerelease = ReleaseChannelPolicy.IsPrereleaseInstall(
                currentProductVersion,
                installedReleaseTag);
            GitHubRelease release = string.IsNullOrWhiteSpace(requestedReleaseTag) ?
                ReleaseChannelPolicy.SelectPreferredRelease(
                    availableReleases,
                    currentBuildIsPrerelease) :
                availableReleases.FirstOrDefault(candidate =>
                    string.Equals(candidate.tag_name, requestedReleaseTag,
                        StringComparison.OrdinalIgnoreCase));

            if (release is null) return false;

            string versionText = TryParseReleaseVersion(release.tag_name, out Version parsedVersion) &&
                !ReleaseChannelPolicy.IsPrerelease(release) ?
                    FormatReleaseVersion(parsedVersion) :
                    release.tag_name.TrimStart('v', 'V');
            GitHubReleaseAsset asset = FindReleaseAsset(release, versionText);
            if (asset is null && ReleaseChannelPolicy.IsPrerelease(release))
            {
                return false;
            }

            selectedRelease = release;
            newversion = versionText;
            newversionTag = release.tag_name;
            newversionDownloadUri = !string.IsNullOrWhiteSpace(asset?.browser_download_url) ?
                new Uri(asset.browser_download_url) : BuildDS4WindowsDownloadUri();
            return true;
        }

        [DllImport("Shell32.dll")]
        private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken,
        out IntPtr ppszPath);

        public bool AdminNeeded()
        {
            try
            {
                File.WriteAllText(exepath + "\\test.txt", "test");
                // Add a small sleep period as a pre-caution
                Thread.Sleep(20);
                File.Delete(exepath + "\\test.txt");
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }

        public MainWindow(string requestedReleaseTag = null)
        {
            this.requestedReleaseTag = requestedReleaseTag;
            InitializeComponent();

            wc.DefaultRequestHeaders.Add("User-Agent", "DS4Windows Updater");

            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            if (File.Exists(exepath + "\\DS4Windows.exe"))
            {
                FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(
                    exepath + "\\DS4Windows.exe");
                version = versionInfo.FileVersion;
                currentProductVersion = string.IsNullOrWhiteSpace(versionInfo.ProductVersion) ?
                    version : versionInfo.ProductVersion;
            }

            if (AdminNeeded())
                label1.Content = "Please re-run with admin rights";
            else
            {
                custom_exe_name_path = Path.Combine(exepath, CUSTOM_EXE_CONFIG_FILENAME);

                try
                {
                    string[] files = Directory.GetFiles(exepath);

                    for (int i = 0, arlen = files.Length; i < arlen; i++)
                    {
                        string tempFile = Path.GetFileName(files[i]);
                        if (new Regex(@"DS4Windows_[\w.]+_\w+.zip").IsMatch(tempFile))
                        {
                            File.Delete(files[i]);
                        }
                    }

                    if (Directory.Exists(exepath + "\\Update Files"))
                        Directory.Delete(exepath + "\\Update Files", true);

                    if (!Directory.Exists(Path.Combine(exepath, "Updates")))
                        Directory.CreateDirectory(Path.Combine(exepath, "Updates"));

                    updatesFolder = Path.Combine(exepath, "Updates");
                }
                catch (IOException) { label1.Content = "Cannot save download at this time"; return; }

                if (File.Exists(exepath + "\\Profiles.xml"))
                    path = exepath;

                if (File.Exists(path + "\\version.txt"))
                {
                    SetNewVersion(File.ReadAllText(path + "\\version.txt"));
                }
                else if (File.Exists(exepath + "\\version.txt"))
                {
                    SetNewVersion(File.ReadAllText(exepath + "\\version.txt"));
                }
                else
                {
                    StartVersionFileDownload();
                }

                if (!downloading && ShouldUpdate())
                {
                    sw.Start();
                    outputUpdatePath = Path.Combine(updatesFolder, GetArchiveFileName());
                    StartAppArchiveDownload(newversionDownloadUri, outputUpdatePath);
                }
                else if (!downloading)
                {
                    label1.Content = "DS4Windows is up to date";
                    try
                    {
                        File.Delete(path + "\\version.txt");
                        File.Delete(exepath + "\\version.txt");
                    }
                    catch { }
                    btnOpenDS4.IsEnabled = true;
                }
            }
        }

        private void StartAppArchiveDownload(Uri url, string outputUpdatePath)
        {
            Task.Run(async () =>
            {
                try
                {
                    await PrepareVerifiedReleaseAsync().ConfigureAwait(false);
                    url = newversionDownloadUri;
                    bool success = false;
                    using (var downloadStream = new FileStream(outputUpdatePath, FileMode.Create))
                    {
                        using HttpResponseMessage response = await wc.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                        long contentLen = response.Content.Headers.ContentLength ?? 0;
                        using (var contentStream = await response.Content.ReadAsStreamAsync())
                        {
                            byte[] buffer = new byte[16384];
                            int bytesRead = 0;
                            long totalBytesRead = 0;
                            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) != 0)
                            {
                                await downloadStream.WriteAsync(buffer, 0, bytesRead).ConfigureAwait(false);
                                totalBytesRead += bytesRead;
                                Application.Current.Dispatcher.BeginInvoke(() =>
                                {
                                    wc_DownloadProgressChanged(new CopyProgress(totalBytesRead, contentLen));
                                });
                            }

                            if (downloadStream.CanSeek) downloadStream.Position = 0;
                        }

                        success = response.IsSuccessStatusCode;
                        response.EnsureSuccessStatusCode();
                    }

                    if (success)
                    {
                        Application.Current.Dispatcher.BeginInvoke(() =>
                        {
                            wc_DownloadFileCompleted();
                        });
                    }
                    else
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            label1.Content = "Could not download update";
                        });
                    }

                    //wc.DownloadFileAsync(url, outputUpdatePath);
                }
                catch (HttpRequestException)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        label1.Content = "Could not download update";
                    });
                }
                catch (Exception e)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        label1.Content = e.Message;
                    });
                }
                //wc.DownloadFileCompleted += wc_DownloadFileCompleted;
                //wc.DownloadProgressChanged += wc_DownloadProgressChanged;
            });
        }

        private async Task PrepareVerifiedReleaseAsync()
        {
            verifiedRelease = null;
            string tag = selectedRelease?.tag_name ?? newversionTag;
            using var operations = new PortableUpdateOperations(updatesFolder, null);
            GitHubRelease exact = await operations.FetchReleaseAsync(tag, CancellationToken.None).ConfigureAwait(false);
            string marker = ReadInstalledReleaseTag();
            if (exact == null || exact.tag_name != tag ||
                !ReleaseChannelPolicy.ShouldUpdate(exact, version,
                    ReleaseChannelPolicy.IsPrereleaseInstall(currentProductVersion, marker), marker))
                throw new InvalidDataException("The requested release is not a verified forward update.");
            PortableReleaseIdentity resolved = await PortableUpdateCoordinator.ResolveReleaseAsync(
                exact, arch, operations, CancellationToken.None).ConfigureAwait(false);
            if (!resolved.IsNonDowngradingBinary(version))
                throw new InvalidDataException("The requested release would downgrade the installed Windows binaries.");
            selectedRelease = exact;
            newversionTag = exact.tag_name;
            newversionDownloadUri = new Uri(resolved.Asset.browser_download_url);
            verifiedRelease = resolved;
        }

        private void ValidateLegacyInstalledVersion()
        {
            FileVersionInfo current = FileVersionInfo.GetVersionInfo(Path.Combine(exepath, "DS4Windows.exe"));
            string marker = ReadInstalledReleaseTag();
            if (verifiedRelease == null || !verifiedRelease.IsNonDowngradingBinary(current.FileVersion) ||
                !ReleaseChannelPolicy.ShouldUpdate(selectedRelease, current.FileVersion,
                    ReleaseChannelPolicy.IsPrereleaseInstall(current.ProductVersion, marker), marker))
                throw new InvalidDataException("The installed release changed or would be downgraded. Retry the update.");
        }

        private void StartVersionFileDownload()
        {
            Uri urlv = new Uri(DS4WINDOWS_RELEASES_API_URI);
            downloading = true;

            label1.Content = "Getting Update info";
            Task.Run(async () =>
            {
                try
                {
                    bool success = false;
                    using HttpResponseMessage response = await wc.GetAsync(urlv);
                    response.EnsureSuccessStatusCode();
                    success = response.IsSuccessStatusCode;

                    if (success)
                    {
                        var gitHubReleases = await response.Content.ReadFromJsonAsync<GitHubRelease[]>();
                        if (!TrySelectLatestRelease(gitHubReleases))
                        {
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                label1.Content = "Could not download update";
                            });
                            return;
                        }

                        string verPath = Path.Combine(exepath, "version.txt");
                        using (StreamWriter sw = new(verPath, false))
                        {
                            sw.Write(newversion);
                        }

                        subwc_DownloadFileCompleted();
                    }
                    else
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            label1.Content = "Could not download update";
                        });
                    }
                    //subwc.DownloadFileAsync(urlv, exepath + "\\version.txt");
                    //subwc.DownloadFileCompleted += subwc_DownloadFileCompleted;
                }
                catch (HttpRequestException e)
                {
                    Console.WriteLine(e);
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        label1.Content = "Could not download update";
                    });
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        label1.Content = "Could not download update";
                    });
                }
            });
        }

        private void subwc_DownloadFileCompleted()
        {
            string versionFileValue = File.ReadAllText(Path.Combine(exepath, "version.txt"));
            if (newversionDownloadUri == null)
            {
                SetNewVersion(versionFileValue);
            }
            else
            {
                newversion = versionFileValue.Trim();
            }

            File.Delete(Path.Combine(exepath, "version.txt"));
            if (ShouldUpdate())
            {
                sw.Start();
                outputUpdatePath = Path.Combine(updatesFolder, GetArchiveFileName());

                StartAppArchiveDownload(newversionDownloadUri, outputUpdatePath);
            }
            else
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    label1.Content = "DS4Windows is up to date";
                });

                try
                {
                    File.Delete(Path.Combine(path, "version.txt"));
                    File.Delete(Path.Combine(exepath + "version.txt"));
                }
                catch { }

                if (autoLaunchDS4W)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        label1.Content = "Launching DS4Windows soon";
                        btnOpenDS4.IsEnabled = false;
                    });

                    Task.Delay(5000).ContinueWith((t) =>
                    {
                        PrepareAutoOpenDS4();
                    });
                }
                else
                {
                    btnOpenDS4.IsEnabled = true;
                }
            }
        }

        private bool IsAdministrator()
        {
            var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        Stopwatch sw = new Stopwatch();

        private void wc_DownloadProgressChanged(CopyProgress e)
        {
            label2.Opacity = 1;
            double speed = e.BytesTransferred / sw.Elapsed.TotalSeconds;
            double timeleft = (e.ExpectedBytes - e.BytesTransferred) / speed;
            if (timeleft > 3660)
                label2.Content = (int)timeleft / 3600 + "h left";
            else if (timeleft > 90)
                label2.Content = (int)timeleft / 60 + "m left";
            else
                label2.Content = (int)timeleft + "s left";

            UpdaterBar.Value = e.PercentComplete * 100.0;
            TaskbarItemInfo.ProgressValue = UpdaterBar.Value / 106d;
            string convertedrev, convertedtotal;
            if (e.BytesTransferred > 1024 * 1024 * 5) convertedrev = (int)(e.BytesTransferred / 1024d / 1024d) + "MB";
            else convertedrev = (int)(e.BytesTransferred / 1024d) + "kB";

            if (e.ExpectedBytes > 1024 * 1024 * 5) convertedtotal = (int)(e.ExpectedBytes / 1024d / 1024d) + "MB";
            else convertedtotal = (int)(e.ExpectedBytes / 1024d) + "kB";

            if (round == 1) label1.Content = "Downloading update: " + convertedrev + " / " + convertedtotal;
            else label1.Content = "Downloading Language Pack: " + convertedrev + " / " + convertedtotal;
        }

        //private void wc_DownloadFileCompleted(object sender, AsyncCompletedEventArgs e)
        private async void wc_DownloadFileCompleted()
        {
            sw.Reset();
            string lang = CultureInfo.CurrentCulture.ToString();

            if (new FileInfo(outputUpdatePath).Length > 0)
            {
                // Verify before process shutdown or any destructive legacy copy.
                // Keep the verified ZIP immutable while the legacy path uses it.
                FileStream archiveGuard;
                try
                {
                    if (verifiedRelease == null)
                        throw new InvalidDataException("The release identity has not been verified.");
                    PortablePackageTransaction.ValidateNoReparse(outputUpdatePath);
                    archiveGuard = new FileStream(outputUpdatePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                }
                catch (Exception error)
                {
                    label1.Content = "Update verification failed: " + error.Message;
                    btnOpenDS4.IsEnabled = false;
                    return;
                }
                using var lockedArchive = archiveGuard;
                try
                {
                    verifiedRelease.VerifyArchive(outputUpdatePath);
                    ValidateLegacyInstalledVersion();
                }
                catch (Exception error)
                {
                    label1.Content = "Update verification failed: " + error.Message;
                    btnOpenDS4.IsEnabled = false;
                    return;
                }
                Process[] processes = Process.GetProcessesByName("DS4Windows");
                label1.Content = "Download Complete";
                if (processes.Length > 0)
                {
                    if (MessageBox.Show("It will be closed to continue this update.", "DS4Windows is still running", MessageBoxButton.OKCancel, MessageBoxImage.Exclamation) == MessageBoxResult.OK)
                    {
                        label1.Content = "Terminating DS4Windows";
                        foreach (Process p in processes)
                        {
                            if (!p.HasExited)
                            {
                                try
                                {
                                    p.Kill();
                                }
                                catch
                                {
                                    MessageBox.Show("Failed to close DS4Windows. Cannot continue update. Please terminate DS4Windows and run DS4Updater again.");
                                    this.Close();
                                    return;
                                }
                            }
                        }

                        System.Threading.Thread.Sleep(5000);
                    }
                    else
                    {
                        this.Close();
                        return;
                    }
                }

                while (processes.Length > 0)
                {
                    label1.Content = "Waiting for DS4Windows to close";
                    processes = Process.GetProcessesByName("DS4Windows");
                    System.Threading.Thread.Sleep(200);
                }

                // Need to check for presense of HidGuardHelper
                processes = Process.GetProcessesByName("HidGuardHelper");
                if (processes.Length > 0)
                {
                    label1.Content = "Waiting for HidGuardHelper to close";
                    System.Threading.Thread.Sleep(5000);

                    processes = Process.GetProcessesByName("HidGuardHelper");
                    if (processes.Length > 0)
                    {
                        MessageBox.Show("HidGuardHelper will not close. Cannot continue update. Please terminate HidGuardHelper and run DS4Updater again.");
                        this.Close();
                        return;
                    }
                }

                try { ValidateLegacyInstalledVersion(); }
                catch (Exception error)
                {
                    label1.Content = "Update verification failed: " + error.Message;
                    btnOpenDS4.IsEnabled = false;
                    return;
                }
                label2.Opacity = 0;
                label1.Content = "Deleting old files";
                UpdaterBar.Value = 102;
                TaskbarItemInfo.ProgressValue = UpdaterBar.Value / 106d;

                string libsPath = Path.Combine(exepath, "libs");
                string oldLibsPath = Path.Combine(exepath, "oldlibs");

                // Grab relative file paths to DLL files in the current install
                string[] oldDLLFiles = Directory.GetDirectories(exepath, "*.dll", SearchOption.AllDirectories);
                for (int i = oldDLLFiles.Length - 1; i >= 0; i--)
                {
                    oldDLLFiles[i] = oldDLLFiles[i].Replace($"{exepath}", "");
                }

                try
                {
                    // Temporarily move existing libs folder
                    if (Directory.Exists(libsPath))
                    {
                        Directory.Move(libsPath, oldLibsPath);
                    }

                    string[] checkFiles = new string[]
                    {
                        exepath + "\\DS4Windows.exe",
                        exepath + "\\DS4Tool.exe",
                        exepath + "\\DS4Control.dll",
                        exepath + "\\DS4Library.dll",
                        exepath + "\\HidLibrary.dll",
                    };

                    foreach (string checkFile in checkFiles)
                    {
                        if (File.Exists(checkFile))
                        {
                            File.Delete(checkFile);
                        }
                    }

                    string updateFilesDir = exepath + "\\Update Files";
                    if (Directory.Exists(updateFilesDir))
                    {
                        Directory.Delete(updateFilesDir);
                    }

                    string[] updatefiles = Directory.GetFiles(exepath);
                    for (int i = 0, arlen = updatefiles.Length; i < arlen; i++)
                    {
                        if (Path.GetExtension(updatefiles[i]) == ".ds4w" && File.Exists(updatefiles[i]))
                            File.Delete(updatefiles[i]);
                    }
                }
                catch { }

                label1.Content = "Installing new files";
                UpdaterBar.Value = 104;
                TaskbarItemInfo.ProgressValue = UpdaterBar.Value / 106d;

                try
                {
                    Directory.CreateDirectory(exepath + "\\Update Files");
                    ZipFile.ExtractToDirectory(outputUpdatePath, exepath + "\\Update Files");
                }
                catch (IOException) { }

                try
                {
                    File.Delete(exepath + "\\version.txt");
                    File.Delete(path + "\\version.txt");
                }
                catch { }

                // Add small sleep timer here as a pre-caution
                Thread.Sleep(20);

                string[] directories = Directory.GetDirectories(exepath + "\\Update Files\\DS4Windows", "*", SearchOption.AllDirectories);
                for (int i = directories.Length - 1; i >= 0; i--)
                {
                    string relativePath = directories[i].Replace($"{exepath}\\Update Files\\DS4Windows\\", "");
                    string tempDestPath = Path.Combine(exepath, relativePath);
                    if (!Directory.Exists(tempDestPath))
                    {
                        Directory.CreateDirectory(tempDestPath);
                    }
                }

                // Grab relative file paths to DLL files in the newer install
                string[] newDLLFiles = Directory.GetFiles(exepath + "\\Update Files\\DS4Windows", "*.dll", SearchOption.AllDirectories);
                for (int i = newDLLFiles.Length - 1; i >= 0; i--)
                {
                    newDLLFiles[i] = newDLLFiles[i].Replace($"{exepath}\\Update Files\\DS4Windows\\", "");
                }

                string[] files = Directory.GetFiles(exepath + "\\Update Files\\DS4Windows", "*", SearchOption.AllDirectories);
                for (int i = files.Length - 1; i >= 0; i--)
                {
                    if (Path.GetFileNameWithoutExtension(files[i]) != "DS4Updater")
                    {
                        string relativePath = files[i].Replace($"{exepath}\\Update Files\\DS4Windows\\", "");
                        string tempDestPath = Path.Combine(exepath, relativePath);
                        //string tempDestPath = $"{exepath}\\{Path.GetFileName(files[i])}";
                        if (File.Exists(tempDestPath))
                        {
                            File.Delete(tempDestPath);
                        }

                        File.Move(files[i], tempDestPath);
                    }
                }

                // Delete old libs folder
                if (Directory.Exists(oldLibsPath))
                {
                    Directory.Delete(oldLibsPath, true);
                }

                // Remove unused DLLs (in main app folder) from previous install
                string[] excludedDLLs = oldDLLFiles.Except(newDLLFiles).ToArray();
                foreach (string dllFile in excludedDLLs)
                {
                    if (File.Exists(dllFile))
                    {
                        File.Delete(dllFile);
                    }
                }

                bool appExists = File.Exists(exepath + "\\DS4Windows.exe") ||
                    File.Exists(exepath + "\\DS4Tool.exe");
                string ds4winversion = File.Exists(exepath + "\\DS4Windows.exe") ?
                    FileVersionInfo.GetVersionInfo(exepath + "\\DS4Windows.exe").FileVersion :
                    string.Empty;
                bool versionMatches = verifiedRelease.VerifyInstalled(new PortableInstalledIdentity(ds4winversion,
                    File.Exists(exepath + "\\DS4Windows.exe") ?
                        FileVersionInfo.GetVersionInfo(exepath + "\\DS4Windows.exe").ProductVersion : null,
                    ReadInstalledReleaseTag()));
                if (appExists && versionMatches)
                {
                    if (selectedRelease is not null)
                    {
                        File.WriteAllText(
                            Path.Combine(exepath, ReleaseChannelPolicy.InstalledReleaseFileName),
                            verifiedRelease.PackageTag);
                    }

                    //File.Delete(exepath + $"\\DS4Windows_{newversion}_{arch}.zip");
                    //File.Delete(exepath + "\\" + lang + ".zip");
                    label1.Content = $"DS4Windows has been updated to {newversionTag}";
                }
                else if (appExists)
                {
                    label1.Content = "Could not replace DS4Windows, please manually unzip";
                }
                else
                    label1.Content = "Could not unpack zip, please manually unzip";

                if (!appExists || !versionMatches)
                {
                    btnOpenDS4.IsEnabled = false;
                    return;
                }

                // Check for custom exe name setting
                string custom_exe_name_path = Path.Combine(exepath, CUSTOM_EXE_CONFIG_FILENAME);
                bool fakeExeFileExists = File.Exists(custom_exe_name_path);
                if (fakeExeFileExists)
                {
                    string fake_exe_name = File.ReadAllText(custom_exe_name_path).Trim();
                    bool valid = !string.IsNullOrEmpty(fake_exe_name) && !(fake_exe_name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0);
                    // Attempt to copy executable and assembly config file
                    if (valid)
                    {
                        string current_exe_location = Path.Combine(exepath, "DS4Windows.exe");
                        string current_conf_file_path = Path.Combine(exepath, "DS4Windows.runtimeconfig.json");
                        string current_deps_file_path = Path.Combine(exepath, "DS4Windows.deps.json");

                        string fake_exe_file = Path.Combine(exepath, $"{fake_exe_name}.exe");
                        string fake_conf_file = Path.Combine(exepath, $"{fake_exe_name}.runtimeconfig.json");
                        string fake_deps_file = Path.Combine(exepath, $"{fake_exe_name}.deps.json");

                        File.Copy(current_exe_location, fake_exe_file, true); // Copy exe file

                        // Copy needed app config and deps files
                        File.Copy(current_conf_file_path, fake_conf_file, true);
                        File.Copy(current_deps_file_path, fake_deps_file, true);
                    }
                }

                UpdaterBar.Value = 106;
                TaskbarItemInfo.ProgressState = TaskbarItemProgressState.None;

                if (autoLaunchDS4W)
                {
                    label1.Content = "Launching DS4Windows soon";
                    btnOpenDS4.IsEnabled = false;
                    Task.Delay(5000).ContinueWith((t) =>
                    {
                        PrepareAutoOpenDS4();
                    });
                }
                else
                {
                    btnOpenDS4.IsEnabled = true;
                }
            }
            else if (!backup)
            {
                sw.Start();
                outputUpdatePath = Path.Combine(updatesFolder, GetArchiveFileName());
                try
                {
                    bool success = false;
                    using (var downloadStream = new FileStream(outputUpdatePath, FileMode.Create))
                    {
                        using HttpResponseMessage response = await wc.GetAsync(newversionDownloadUri);
                        response.EnsureSuccessStatusCode();
                        success = response.IsSuccessStatusCode;
                        if (success)
                        {
                            await response.Content.CopyToAsync(downloadStream);
                        }
                    }
                    //wc.DownloadFileAsync(url, outputUpdatePath);
                }
                catch (Exception ex) { label1.Content = ex.Message; }
                backup = true;
            }
            else
            {
                label1.Content = "Could not download update";
                try
                {
                    File.Delete(exepath + "\\version.txt");
                    File.Delete(path + "\\version.txt");
                }
                catch { }
                btnOpenDS4.IsEnabled = true;
            }
        }

        private void BtnChangelog_Click(object sender, RoutedEventArgs e)
        {
            // TODO change
            ProcessStartInfo startInfo = new ProcessStartInfo("https://docs.google.com/document/d/1CovpH08fbPSXrC6TmEprzgPwCe0tTjQ_HTFfDotpmxk/edit?usp=sharing");
            startInfo.UseShellExecute = true;
            try
            {
                using (Process tempProc = Process.Start(startInfo))
                {
                }
            }
            catch { }
        }

        private void BtnOpenDS4_Click(object sender, RoutedEventArgs e)
        {
            if (File.Exists(exepath + "\\DS4Windows.exe"))
                Process.Start(exepath + "\\DS4Windows.exe");
            else
                Process.Start(exepath);

            App.openingDS4W = true;
            this.Close();
        }

        private void PrepareAutoOpenDS4()
        {
            App.openingDS4W = true;
            Dispatcher.BeginInvoke((Action)(() =>
            {
                this.Close();
            }));
        }
    }
}


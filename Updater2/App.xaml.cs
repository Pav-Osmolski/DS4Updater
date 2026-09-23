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

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DS4Updater
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private string exedirpath = AppContext.BaseDirectory;
        public static bool openingDS4W;
        private string launchExeName;
        private string launchExePath;
        private MainWindow mwd;
        private bool portableSession;

        private void Application_Startup(object sender, StartupEventArgs e)
        {
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

            // Suppress legacy exit handlers until deployment is known. In
            // particular, a stale ZIP marker in an MSI installation must not
            // send its normal -autolaunch handoff into the portable parser.
            portableSession = true;
            UpdateDeployment deployment;
            try { deployment = UpdateDeploymentPolicy.Resolve(e.Args, Environment.ProcessPath); }
            catch (Exception error)
            {
                MessageBox.Show("The update could not start safely. No apps were stopped.\n\n" + error.Message,
                    "DS4Windows update", MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown(1);
                return;
            }
            if (deployment == UpdateDeployment.Managed)
            {
                try
                {
                    MainWindow = new ManagedUpdateWindow(ManagedUpdateRequest.Parse(e.Args, Environment.ProcessPath));
                    ShutdownMode = ShutdownMode.OnMainWindowClose;
                    MainWindow.Show();
                }
                catch (Exception error)
                {
                    MessageBox.Show("The update could not start safely. No apps were stopped.\n\n" + error.Message,
                        "DS4Windows update", MessageBoxButton.OK, MessageBoxImage.Warning);
                    Shutdown(1);
                }
                return;
            }
            // Even malformed portable requests must never fall through to the
            // legacy cleanup or process-termination behavior.
            if (deployment == UpdateDeployment.Portable)
            {
                portableSession = true;
                try
                {
                    string executable = Environment.ProcessPath ?? throw new IOException("The updater path is unavailable.");
                    PortableUpdateRequest request = PortableUpdateRequest.Parse(e.Args, executable);
                    if (!request.IsWorker)
                    {
                        PortableWorkerSession.Start(request);
                        Shutdown();
                        return;
                    }
                    PortableWorkerRecord record = PortableWorkerSession.ValidateWorker(request, executable);
                    MainWindow = new PortableUpdateWindow(request, record);
                    MainWindow.Show();
                }
                catch (Exception error)
                {
                    MessageBox.Show("The portable update could not start safely. No apps were stopped.\n\n" + error.Message,
                        "Portable update", MessageBoxButton.OK, MessageBoxImage.Warning);
                    Shutdown(1);
                }
                return;
            }

            portableSession = false;
            launchExePath = Path.Combine(exedirpath, "DS4Windows.exe");
            bool autoLaunch = false;
            bool forceUserLaunch = false;
            string requestedReleaseTag = null;
            for (int i=0, arlen = e.Args.Length; i < arlen; i++)
            {
                string temp = e.Args[i];
                if (temp.Equals("-autolaunch"))
                {
                    autoLaunch = true;
                }
                else if (temp.Equals("-user"))
                {
                    forceUserLaunch = true;
                }
                else if (temp.Equals("--releaseTag", StringComparison.OrdinalIgnoreCase))
                {
                    if ((i + 1) < arlen)
                    {
                        requestedReleaseTag = e.Args[++i];
                    }
                }
                else if (temp.Equals("--launchExe"))
                {
                    if ((i+1) < arlen)
                    {
                        i++;
                        temp = e.Args[i];
                        string tempPath = Path.Combine(exedirpath, temp);
                        if (File.Exists(tempPath))
                        {
                            launchExeName = temp;
                            launchExePath = tempPath;
                        }
                    }
                }
            }

            mwd = new MainWindow(requestedReleaseTag)
            {
                autoLaunchDS4W = autoLaunch,
                forceLaunchDS4WUser = forceUserLaunch,
            };
            mwd.Show();
        }

        public App()
        {
            //Debug.WriteLine(CultureInfo.CurrentCulture);
            this.Exit += (s, e) =>
            {
                if (portableSession) return;
                string currentUpdaterPath = Path.Combine(exedirpath, "Update Files", "DS4Windows", "DS4Updater.exe");
                string tempNewUpdaterPath = Path.Combine(exedirpath, "DS4Updater NEW.exe");

                string fileName = $"{Assembly.GetExecutingAssembly().GetName().Name}.exe";
                string filePath = Path.Combine(AppContext.BaseDirectory, fileName);
                FileVersionInfo fileVersion = FileVersionInfo.GetVersionInfo(filePath);
                string version = fileVersion.ProductVersion;
                if (File.Exists(exedirpath + "\\Update Files\\DS4Windows\\DS4Updater.exe")
                    && FileVersionInfo.GetVersionInfo(exedirpath + "\\Update Files\\DS4Windows\\DS4Updater.exe").FileVersion.CompareTo(version) != 0)
                {
                    File.Move(currentUpdaterPath, tempNewUpdaterPath);
                    //Directory.Delete(exepath + "\\Update Files", true);

                    //string tempFilePath = Path.GetTempFileName();
                    string tempFilePath = Path.Combine(Path.GetTempPath(), "UpdateReplacer.bat");
                    using (StreamWriter w = new StreamWriter(new FileStream(tempFilePath,
                        FileMode.Create, FileAccess.Write)))
                    {
                        w.WriteLine("@echo off"); // Turn off echo
                        w.WriteLine("@echo Attempting to replace updater, please wait...");
                        w.WriteLine("@ping -n 4 127.0.0.1 > nul"); //Its silly but its the most compatible way to call for a timeout in a batch file, used to give the main updater time to cleanup and exit.
                        w.WriteLine("@del \"" + exedirpath + "\\DS4Updater.exe" + "\"");
                        w.WriteLine("@ren \"" + exedirpath + "\\DS4Updater NEW.exe" + "\" \"DS4Updater.exe\"");
                        w.Close();
                    }

                    Process.Start(tempFilePath);
                }
                else if (File.Exists(tempNewUpdaterPath))
                {
                    File.Delete(tempNewUpdaterPath);
                }

                if (Directory.Exists(exedirpath + "\\Update Files"))
                {
                    Directory.Delete(exedirpath + "\\Update Files", true);
                }
            };

            this.Exit += (s, e) =>
            {
                if (portableSession) return;
                if (openingDS4W)
                {
                    AutoOpenDS4();
                }
            };
        }

        private void AutoOpenDS4()
        {
            string finalLaunchExePath = Path.Combine(exedirpath, "DS4Windows.exe");
            if (File.Exists(launchExePath))
                finalLaunchExePath = launchExePath;

            if (mwd.forceLaunchDS4WUser)
            {
                // Attempt to launch program as a normal user
                Util.StartProcessInExplorer(finalLaunchExePath);
            }
            else
            {
                // Attempt to launch Explorer with folder open
                ProcessStartInfo startInfo = new ProcessStartInfo(finalLaunchExePath);
                startInfo.WorkingDirectory = exedirpath;
                using (Process tempProc = Process.Start(startInfo))
                {
                }
            }
        }
    }
}

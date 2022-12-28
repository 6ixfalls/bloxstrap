﻿using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;

using Microsoft.Win32;

using Bloxstrap.Enums;
using Bloxstrap.Dialogs.BootstrapperDialogs;
using Bloxstrap.Helpers;
using Bloxstrap.Helpers.Integrations;
using Bloxstrap.Helpers.RSMM;
using Bloxstrap.Models;
using System.Net;

namespace Bloxstrap
{
    public partial class Bootstrapper
    {
        #region Properties

        // https://learn.microsoft.com/en-us/windows/win32/msi/error-codes
        public const int ERROR_SUCCESS = 0;
        public const int ERROR_INSTALL_USEREXIT = 1602;
        public const int ERROR_INSTALL_FAILURE = 1603;
        public const int ERROR_PRODUCT_UNINSTALLED = 1614;

        // in case a new package is added, you can find the corresponding directory
        // by opening the stock bootstrapper in a hex editor
        // TODO - there ideally should be a less static way to do this that's not hardcoded?
        private static readonly IReadOnlyDictionary<string, string> PackageDirectories = new Dictionary<string, string>()
        {
            { "RobloxApp.zip",                 @"" },
            { "shaders.zip",                   @"shaders\" },
            { "ssl.zip",                       @"ssl\" },

            { "content-avatar.zip",            @"content\avatar\" },
            { "content-configs.zip",           @"content\configs\" },
            { "content-fonts.zip",             @"content\fonts\" },
            { "content-sky.zip",               @"content\sky\" },
            { "content-sounds.zip",            @"content\sounds\" },
            { "content-textures2.zip",         @"content\textures\" },
            { "content-models.zip",            @"content\models\" },

            { "content-textures3.zip",         @"PlatformContent\pc\textures\" },
            { "content-terrain.zip",           @"PlatformContent\pc\terrain\" },
            { "content-platform-fonts.zip",    @"PlatformContent\pc\fonts\" },

            { "extracontent-luapackages.zip",  @"ExtraContent\LuaPackages\" },
            { "extracontent-translations.zip", @"ExtraContent\translations\" },
            { "extracontent-models.zip",       @"ExtraContent\models\" },
            { "extracontent-textures.zip",     @"ExtraContent\textures\" },
            { "extracontent-places.zip",       @"ExtraContent\places\" },
        };

        private static readonly string AppSettings =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<Settings>\n" +
            "	<ContentFolder>content</ContentFolder>\n" +
            "	<BaseUrl>http://www.roblox.com</BaseUrl>\n" +
            "</Settings>\n";

        private static readonly string ModReadme =
            "This is where you can modify your Roblox files while preserving modifications\n" +
            "whenever Roblox updates.\n" +
            "\n" +
            "For example, Modifications\\content\\sounds\\ouch.ogg will\n" +
            "automatically overwrite Versions\\version-xx...\\content\\sounds\\ouch.ogg\n" +
            "\n" +
            "If you remove a file mod from here, Bloxstrap will restore the stock version\n" +
            "of the file the next time it's launched.\n" +
            "\n" +
            "Any files added here to the root modification directory are ignored, so be sure\n" +
            "that they're inside a folder.";

        private string? LaunchCommandLine;

        private string VersionGuid = null!;
        private PackageManifest VersionPackageManifest = null!;
        private string VersionFolder = null!;

        private readonly bool FreshInstall;

        private int ProgressIncrement;
        private bool CancelFired = false;

        public IBootstrapperDialog Dialog = null!;
        #endregion

        #region Core
        public Bootstrapper(string? launchCommandLine = null)
        {
            LaunchCommandLine = launchCommandLine;
            FreshInstall = String.IsNullOrEmpty(Program.Settings.VersionGuid);
        }

        // this is called from BootstrapperStyleForm.SetupDialog()
        public async Task Run()
        {
            if (Program.IsQuiet)
                Dialog.CloseDialog();

            if (Program.IsUninstall)
            {
                Uninstall();
                return;
            }

            await CheckLatestVersion();

            // if bloxstrap is installing for the first time but is running, prompt to close roblox
            // if roblox needs updating but is running, ignore update for now
            if (!Directory.Exists(VersionFolder) && CheckIfRunning(true) || Program.Settings.VersionGuid != VersionGuid && !CheckIfRunning(false))
                await InstallLatestVersion();

            ApplyModifications();

            if (Program.IsFirstRun)
                Program.SettingsManager.ShouldSave = true;

            if (Program.IsFirstRun || FreshInstall)
                Register();

            CheckInstall();

            await RbxFpsUnlocker.CheckInstall();
            
            Program.SettingsManager.Save();

            
            if (Program.IsFirstRun && Program.IsNoLaunch)
                Dialog.ShowSuccess($"{Program.ProjectName} has successfully installed");
            else if (!Program.IsNoLaunch)
                await StartRoblox();

            Program.Exit();
        }

        private async Task CheckLatestVersion()
        {
            Dialog.Message = "Connecting to Roblox...";

            ClientVersion clientVersion = await DeployManager.GetLastDeploy(Program.Settings.Channel);
            VersionGuid = clientVersion.VersionGuid;
            VersionFolder = Path.Combine(Directories.Versions, VersionGuid);
            VersionPackageManifest = await PackageManifest.Get(VersionGuid);
        }

        private bool CheckIfRunning(bool shutdown)
        {
            Process[] processes = Process.GetProcessesByName("RobloxPlayerBeta");

            if (processes.Length == 0)
                return false;

            if (shutdown)
            {
                Dialog.PromptShutdown();

                try
                {
                    // try/catch just in case process was closed before prompt was answered

                    foreach (Process process in processes)
                    {
                        process.CloseMainWindow();
                        process.Close();
                    }
                }
                catch (Exception) { }
            }

            return true;
        }

        private async Task StartRoblox()
        {
            string startEventName = Program.ProjectName.Replace(" ", "") + "StartEvent";

            Dialog.Message = "Starting Roblox...";

            // launch time isn't really required for all launches, but it's usually just safest to do this
            LaunchCommandLine += " --launchtime=" + DateTimeOffset.Now.ToUnixTimeMilliseconds();

            if (Program.Settings.Channel.ToLower() != DeployManager.DefaultChannel.ToLower())
                LaunchCommandLine += " -channel " + Program.Settings.Channel.ToLower();

            LaunchCommandLine  += " -startEvent " + startEventName;

            using (SystemEvent startEvent = new(startEventName))
            {
                bool shouldWait = false;

                Process gameClient = Process.Start(Path.Combine(VersionFolder, "RobloxPlayerBeta.exe"), LaunchCommandLine);
                Process? rbxFpsUnlocker = null;
                DiscordRichPresence? richPresence = null;

                bool startEventFired = await startEvent.WaitForEvent();

                startEvent.Close();

                if (!startEventFired)
                    return;

                if (Program.Settings.RFUEnabled && Process.GetProcessesByName("rbxfpsunlocker").Length == 0)
                {
                    ProcessStartInfo startInfo = new();
                    startInfo.FileName = Path.Combine(Directories.Integrations, @"rbxfpsunlocker\rbxfpsunlocker.exe");
                    startInfo.WorkingDirectory = Path.Combine(Directories.Integrations, "rbxfpsunlocker");

                    rbxFpsUnlocker = Process.Start(startInfo);

                    if (Program.Settings.RFUAutoclose)
                        shouldWait = true;
                }

                // event fired, wait for 3 seconds then close
                await Task.Delay(3000);

                // now we move onto handling rich presence
                // this is going to be an issue for desktop app launches as this gets the place id from the command line,
                // but the desktop app launch can switch games without having to close the client
                // i might be able to experiment with reading from the latest log file in realtime to circumvent this,
                // but i have no idea how reliable it will be. todo?
                if (Program.Settings.UseDiscordRichPresence)
                {
                    richPresence = new DiscordRichPresence();
                    richPresence.MonitorGameActivity();

                    shouldWait = true;
                }

                if (!shouldWait)
                    return;

                // keep bloxstrap open in the background
                Dialog.CloseDialog();
                await gameClient.WaitForExitAsync();

                if (richPresence is not null)
                    richPresence.Dispose();

                if (Program.Settings.RFUAutoclose && rbxFpsUnlocker is not null)
                    rbxFpsUnlocker.Kill();
            }
        }

        public void CancelButtonClicked()
        {
            if (!Dialog.CancelEnabled)
            {
                Program.Exit(ERROR_INSTALL_USEREXIT);
                return;
            }

            CancelFired = true;

            try
            {
                if (Program.IsFirstRun)
                    Directory.Delete(Directories.Base, true);
                else if (Directory.Exists(VersionFolder))
                    Directory.Delete(VersionFolder, true);
            }
            catch (Exception) { }
 
            Program.Exit(ERROR_INSTALL_USEREXIT);
        }
        #endregion

        #region App Install
        public static void Register()
        {
            RegistryKey applicationKey = Registry.CurrentUser.CreateSubKey($@"Software\{Program.ProjectName}");

            // new install location selected, delete old one
            string? oldInstallLocation = (string?)applicationKey.GetValue("OldInstallLocation");
            if (!String.IsNullOrEmpty(oldInstallLocation) && oldInstallLocation != Directories.Base)
            {
                try
                {
                    if (Directory.Exists(oldInstallLocation))
                        Directory.Delete(oldInstallLocation, true);
                }
                catch (Exception) { }

                applicationKey.DeleteValue("OldInstallLocation");
            }

            applicationKey.SetValue("InstallLocation", Directories.Base);
            applicationKey.Close();

            // set uninstall key
            RegistryKey uninstallKey = Registry.CurrentUser.CreateSubKey($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{Program.ProjectName}");
            uninstallKey.SetValue("DisplayIcon", $"{Directories.App},0");
            uninstallKey.SetValue("DisplayName", Program.ProjectName);
            uninstallKey.SetValue("DisplayVersion", Program.Version);

            if (uninstallKey.GetValue("InstallDate") is null)
                uninstallKey.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));

            uninstallKey.SetValue("InstallLocation", Directories.Base);
            uninstallKey.SetValue("NoRepair", 1);
            uninstallKey.SetValue("Publisher", "6ixfalls");
            uninstallKey.SetValue("ModifyPath", $"\"{Directories.App}\" -preferences");
            uninstallKey.SetValue("QuietUninstallString", $"\"{Directories.App}\" -uninstall -quiet");
            uninstallKey.SetValue("UninstallString", $"\"{Directories.App}\" -uninstall");
            uninstallKey.SetValue("URLInfoAbout", $"https://github.com/{Program.ProjectRepository}");
            uninstallKey.SetValue("URLUpdateInfo", $"https://github.com/{Program.ProjectRepository}/releases/latest");
            uninstallKey.Close();
        }

        public static void CheckInstall()
        {
            // check if launch uri is set to our bootstrapper
            // this doesn't go under register, so we check every launch
            // just in case the stock bootstrapper changes it back

            Protocol.Register("roblox", "Roblox", Directories.App);
            Protocol.Register("roblox-player", "Roblox", Directories.App);

            // in case the user is reinstalling
            if (File.Exists(Directories.App) && Program.IsFirstRun)
                File.Delete(Directories.App);

            // check to make sure bootstrapper is in the install folder
            if (!File.Exists(Directories.App) && Environment.ProcessPath is not null)
                File.Copy(Environment.ProcessPath, Directories.App);

            // this SHOULD go under Register(),
            // but then people who have Bloxstrap v1.0.0 installed won't have this without a reinstall
            // maybe in a later version?
            if (!Directory.Exists(Program.StartMenu))
            {
                Directory.CreateDirectory(Program.StartMenu);

                ShellLink.Shortcut.CreateShortcut(Directories.App, "", Directories.App, 0)
                    .WriteToFile(Path.Combine(Program.StartMenu, "Play Roblox.lnk"));

                ShellLink.Shortcut.CreateShortcut(Directories.App, "-preferences", Directories.App, 0)
                    .WriteToFile(Path.Combine(Program.StartMenu, $"Configure {Program.ProjectName}.lnk"));
            }
        }

        private void Uninstall()
        {
            CheckIfRunning(true);

            Dialog.Message = $"Uninstalling {Program.ProjectName}...";

            Program.SettingsManager.ShouldSave = false;

            // check if stock bootstrapper is still installed
            RegistryKey? bootstrapperKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\roblox-player");
            if (bootstrapperKey is null)
            {
                Protocol.Unregister("roblox");
                Protocol.Unregister("roblox-player");
            }
            else
            {
                // revert launch uri handler to stock bootstrapper

                string bootstrapperLocation = (string?)bootstrapperKey.GetValue("InstallLocation") + "RobloxPlayerLauncher.exe";

                Protocol.Register("roblox", "Roblox", bootstrapperLocation);
                Protocol.Register("roblox-player", "Roblox", bootstrapperLocation);
            }

            try
            {
                // delete application key
                Registry.CurrentUser.DeleteSubKey($@"Software\{Program.ProjectName}");

                // delete start menu folder
                Directory.Delete(Program.StartMenu, true);

                // delete uninstall key
                Registry.CurrentUser.DeleteSubKey($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{Program.ProjectName}");

                // delete installation folder
                // (should delete everything except bloxstrap itself)
                Directory.Delete(Directories.Base, true);
            }
            catch (Exception) { }

            Dialog.ShowSuccess($"{Program.ProjectName} has been uninstalled");

            Environment.Exit(ERROR_PRODUCT_UNINSTALLED);
        }
        #endregion

        #region Roblox Install
        private async Task InstallLatestVersion()
        {
            if (FreshInstall)
                Dialog.Message = "Installing Roblox...";
            else
                Dialog.Message = "Upgrading Roblox...";

            Directory.CreateDirectory(Directories.Base);

            Dialog.CancelEnabled = true;

            // i believe the bootstrapper bases the progress bar off
            // bytes downloaded / bytes total according to rbxPkgManifest?
            // i'm too lazy for that, so here it's just based off how many packages
            // have finished downloading

            Dialog.ProgressStyle = ProgressBarStyle.Continuous;

            ProgressIncrement = (int)Math.Floor((decimal)1 / VersionPackageManifest.Count * 100);

            Directory.CreateDirectory(Directories.Downloads);

            foreach (Package package in VersionPackageManifest)
            {
                // download all the packages at once
                DownloadPackage(package);
            }

            do
            {
                // wait for download to finish (and also round off the progress bar if needed)

                if (Dialog.ProgressValue == ProgressIncrement * VersionPackageManifest.Count)
                    Dialog.ProgressValue = 100;

                await Task.Delay(1000);
            }
            while (Dialog.ProgressValue != 100);

            Dialog.ProgressStyle = ProgressBarStyle.Marquee;

            Debug.WriteLine("Finished downloading");

            Dialog.Message = "Configuring Roblox...";

            Directory.CreateDirectory(Directories.Versions);

            foreach (Package package in VersionPackageManifest)
            {
                // extract all the packages at once (shouldn't be too heavy on cpu?)
                ExtractPackage(package);
            }

            Debug.WriteLine("Finished extracting packages");

            string appSettingsLocation = Path.Combine(VersionFolder, "AppSettings.xml");
            await File.WriteAllTextAsync(appSettingsLocation, AppSettings);

            if (!FreshInstall)
            {
                // let's take this opportunity to delete any packages we don't need anymore
                foreach (string filename in Directory.GetFiles(Directories.Downloads))
                {
                    if (!VersionPackageManifest.Exists(package => filename.Contains(package.Signature)))
                        File.Delete(filename);
                }

                string oldVersionFolder = Path.Combine(Directories.Versions, Program.Settings.VersionGuid);

                if (VersionGuid != Program.Settings.VersionGuid && Directory.Exists(oldVersionFolder))
                {
                    // and also to delete our old version folder
                    Directory.Delete(oldVersionFolder, true);
                }
            }

            Dialog.CancelEnabled = false;

            Program.Settings.VersionGuid = VersionGuid;
        }

        private void ApplyModifications()
        {
            Dialog.Message = "Applying Roblox modifications...";

            string modFolder = Path.Combine(Directories.Modifications);
            string manifestFile = Path.Combine(Directories.Base, "ModManifest.txt");

            List<string> manifestFiles = new();
            List<string> modFolderFiles = new();

            if (!Directory.Exists(modFolder))
            {
                Directory.CreateDirectory(modFolder);
                File.WriteAllText(Path.Combine(modFolder, "README.txt"), ModReadme);
            }

            CheckModPreset(Program.Settings.UseOldDeathSound, @"content\sounds\ouch.ogg", Program.Base64OldDeathSound);
            CheckModPreset(Program.Settings.UseOldMouseCursor, @"content\textures\Cursors\KeyboardMouse\ArrowCursor.png", Program.Base64OldArrowCursor);
            CheckModPreset(Program.Settings.UseOldMouseCursor, @"content\textures\Cursors\KeyboardMouse\ArrowFarCursor.png", Program.Base64OldArrowFarCursor);
            CheckModPreset(Program.Settings.UseDisableAppPatch, @"ExtraContent\places\Mobile.rbxl", "");

            foreach (string file in Directory.GetFiles(modFolder, "*.*", SearchOption.AllDirectories))
            {
                // get relative directory path
                string relativeFile = file.Substring(modFolder.Length + 1);

                // ignore files placed in the root directory
                if (!relativeFile.Contains('\\'))
                    continue;

                modFolderFiles.Add(relativeFile);
            }

            // the manifest is primarily here to keep track of what files have been
            // deleted from the modifications folder, so that we know when to restore the
            // original files from the downloaded packages

            if (File.Exists(manifestFile))
                manifestFiles = File.ReadAllLines(manifestFile).ToList<string>();
            else
                manifestFiles = modFolderFiles;

            // copy and overwrite
            foreach (string file in modFolderFiles)
            {
                string fileModFolder = Path.Combine(modFolder, file);
                string fileVersionFolder = Path.Combine(VersionFolder, file);

                if (File.Exists(fileVersionFolder))
                {
                    if (Utilities.MD5File(fileModFolder) == Utilities.MD5File(fileVersionFolder))
                        continue;
                }

                string? directory = Path.GetDirectoryName(fileVersionFolder);

                if (directory is null)
                    continue;

                Directory.CreateDirectory(directory);

                File.Copy(fileModFolder, fileVersionFolder, true);
                File.SetAttributes(fileVersionFolder, File.GetAttributes(fileModFolder) & ~FileAttributes.ReadOnly);
            }

            // now check for files that have been deleted from the mod folder
            // according to the manifest
            foreach (string fileLocation in manifestFiles)
            {
                if (modFolderFiles.Contains(fileLocation))
                    continue;

                KeyValuePair<string, string> packageDirectory;

                try
                {
                    packageDirectory = PackageDirectories.First(x => x.Key != "RobloxApp.zip" && fileLocation.StartsWith(x.Value));
                }
                catch (InvalidOperationException)
                {
                    // package doesn't exist, likely mistakenly placed file
                    continue;
                }

                // restore original file
                string fileName = fileLocation.Substring(packageDirectory.Value.Length);
                ExtractFileFromPackage(packageDirectory.Key, fileName);
            }

            File.WriteAllLines(manifestFile, modFolderFiles);
        }

        private static void CheckModPreset(bool condition, string location, string base64Contents)
        {
            string modFolderLocation = Path.Combine(Directories.Modifications, location);
            byte[] binaryData = Convert.FromBase64String(base64Contents);

            if (condition)
            {
                if (!File.Exists(modFolderLocation))
                {
                    string? directory = Path.GetDirectoryName(modFolderLocation);

                    if (directory is null)
                        return;

                    Directory.CreateDirectory(directory);

                    File.WriteAllBytes(modFolderLocation, binaryData);
                }
            }
            else if (File.Exists(modFolderLocation) && Utilities.MD5File(modFolderLocation) == Utilities.MD5Data(binaryData))
            {
                File.Delete(modFolderLocation);
            }
        }

        private async void DownloadPackage(Package package)
        {
            string packageUrl = $"{DeployManager.BaseUrl}/{VersionGuid}-{package.Name}";
            string packageLocation = Path.Combine(Directories.Downloads, package.Signature);
            string robloxPackageLocation = Path.Combine(Program.LocalAppData, "Roblox", "Downloads", package.Signature);

            if (File.Exists(packageLocation))
            {
                FileInfo file = new(packageLocation);

                string calculatedMD5 = Utilities.MD5File(packageLocation);
                if (calculatedMD5 != package.Signature)
                {
                    Debug.WriteLine($"{package.Name} is corrupted ({calculatedMD5} != {package.Signature})! Deleting and re-downloading...");
                    file.Delete();
                }
                else
                {
                    Debug.WriteLine($"{package.Name} is already downloaded, skipping...");
                    Dialog.ProgressValue += ProgressIncrement;
                    return;
                }
            }
            else if (File.Exists(robloxPackageLocation))
            {
                // let's cheat! if the stock bootstrapper already previously downloaded the file,
                // then we can just copy the one from there

                Debug.WriteLine($"Found existing version of {package.Name} ({robloxPackageLocation})! Copying to Downloads folder...");
                File.Copy(robloxPackageLocation, packageLocation);
                Dialog.ProgressValue += ProgressIncrement;
                return;
            }

            if (!File.Exists(packageLocation))
            {
                Debug.WriteLine($"Downloading {package.Name}...");

                var response = await Program.HttpClient.GetAsync(packageUrl);

                if (CancelFired)
                    return;

                using (var fileStream = new FileStream(packageLocation, FileMode.CreateNew))
                {
                    await response.Content.CopyToAsync(fileStream);
                }

                Debug.WriteLine($"Finished downloading {package.Name}!");
                Dialog.ProgressValue += ProgressIncrement;
            }
        }

        private void ExtractPackage(Package package)
        {
            if (CancelFired)
                return;

            string packageLocation = Path.Combine(Directories.Downloads, package.Signature);
            string packageFolder = Path.Combine(VersionFolder, PackageDirectories[package.Name]);
            string extractPath;
            string? directory;

            Debug.WriteLine($"Extracting {package.Name} to {packageFolder}...");

            using (ZipArchive archive = ZipFile.OpenRead(packageLocation))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (CancelFired)
                        return;

                    if (entry.FullName.EndsWith('\\'))
                        continue;

                    extractPath = Path.Combine(packageFolder, entry.FullName);

                    Debug.WriteLine($"[{package.Name}] Writing {extractPath}...");

                    directory = Path.GetDirectoryName(extractPath);

                    if (directory is null)
                        continue;

                    Directory.CreateDirectory(directory);

                    if (File.Exists(extractPath))
                        File.Delete(extractPath);

                    entry.ExtractToFile(extractPath);
                }
            }
        }

        private void ExtractFileFromPackage(string packageName, string fileName)
        {
            Package? package = VersionPackageManifest.Find(x => x.Name == packageName);

            if (package is null)
                return;

            DownloadPackage(package);

            string packageLocation = Path.Combine(Directories.Downloads, package.Signature);
            string packageFolder = Path.Combine(VersionFolder, PackageDirectories[package.Name]);

            using (ZipArchive archive = ZipFile.OpenRead(packageLocation))
            {
                ZipArchiveEntry? entry = archive.Entries.Where(x => x.FullName == fileName).FirstOrDefault();

                if (entry is null)
                    return;

                string fileLocation = Path.Combine(packageFolder, entry.FullName);

                if (File.Exists(fileLocation))
                    File.Delete(fileLocation);

                entry.ExtractToFile(fileLocation);
            }
        }
        #endregion
    }
}

﻿using System;
using System.ComponentModel.Design;
using System.Globalization;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System.Windows.Forms;
using Microsoft.VisualStudio;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
using Microsoft.Win32;
using System.Linq;
using EnvDTE;
using System.Diagnostics;
using System.Text;
using Microsoft.VisualStudio.Settings;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell.Settings;

namespace VSShortcutsManager
{
    /// <summary>
    /// Command handler
    /// </summary>
    public sealed class VSShortcutsManager
    {
        /// <summary>
        /// Match with symbols in VSCT file.
        /// </summary>
        public static readonly Guid VSShortcutsManagerCmdSetGuid = new Guid("cca0811b-addf-4d7b-9dd6-fdb412c44d8a");
        public const int BackupShortcutsCmdId = 0x1200;
        public const int RestoreShortcutsCmdId = 0x1300;
        public const int ResetShortcutsCmdId = 0x1400;
        public const int ImportMappingSchemeCmdId = 0x1500;
        public const int ShortcutSchemesMenu = 0x2002;
        public const int DynamicThemeStartCmdId = 0x2A00;
        public const int UserShortcutsMenu = 0x1080;
        public const int ImportUserShortcutsCmdId = 0x1130;
        public const int ManageUserShortcutsCmdId = 0x1140;
        public const int DynamicUserShortcutsStartCmdId = 0x3A00;

        private const string BACKUP_FILE_PATH = "BackupFilePath";
        private const string MSG_CAPTION_RESTORE = "Import Keyboard Shortcuts";
        private const string MSG_CAPTION_BACKUP = "Save Keyboard Shortcuts";
        private const string MSG_CAPTION_RESET = "Reset Keyboard Shortcuts";
        private const string MSG_CAPTION_IMPORT = "Import Keyboard Mapping Scheme";
        private const string DEFAULT_MAPPING_SCHEME_NAME = "(Default)";

        // UserSettingsStore constants
        private const string VSK_IMPORTS_REGISTRY_KEY = "VskImportsRegistry";
        //private const string USER_SHORTCUTS_DEFS = "UserShortcutsDefs";
        //private const string DATETIME_FORMAT = "yyyy'-'MM'-'dd'T'HH':'mm':'ss";

        /// <summary>
        /// VS Package that provides this command, not null.
        /// </summary>
        private readonly Package package;
        private readonly int UPDATE_NEVER = 0;
        private readonly int UPDATE_PROMPT = 1;
        private readonly int UPDATE_ALWAYS = 2;
        private List<string> MappingSchemes;

        private ShellSettingsManager ShellSettingsManager;
        private UserShortcutsManager userShortcutsManager;
        private List<VskMappingInfo> VskImportsRegistry;
        private List<UserShortcutsDef> UserShortcutsRegistry;


        #region
        private string _AllUsersExtensionsPath;
        private string AllUsersExtensionsPath
        {
            get
            {
                if (_AllUsersExtensionsPath == null)
                {
                    _AllUsersExtensionsPath = GetAllUsersExtensionsPath();
                }
                return _AllUsersExtensionsPath;
            }
        }

        private string _LocalUserExtensionsPath;

        private string LocalUserExtensionsPath
        {
            get
            {
                if (_LocalUserExtensionsPath == null)
                {
                    // TODO: Replace with SettingsManager.GetLocalApplicationData
                    _LocalUserExtensionsPath = GetExtensionsPath(Environment.SpecialFolder.LocalApplicationData);
                }
                return _LocalUserExtensionsPath;
            }
        }

        public string _RoamingAppDataVSPath;

        private string RoamingAppDataVSPath
        {
            get
            {
                if (_RoamingAppDataVSPath == null)
                {
                    _RoamingAppDataVSPath = GetExtensionsPath(Environment.SpecialFolder.ApplicationData);
                }
                return _RoamingAppDataVSPath;
            }
        }
        #endregion

        /// <summary>
        /// Gets the service provider from the owner package.
        /// </summary>
        private IServiceProvider ServiceProvider
        {
            get
            {
                return this.package;
            }
        }

        /// <summary>
        /// Gets the instance of the command.
        /// </summary>
        public static VSShortcutsManager Instance
        {
            get;
            private set;
        }

        /// <summary>
        /// Initializes the singleton instance of the command.
        /// </summary>
        /// <param name="package">Owner package, not null.</param>
        public static void Initialize(Package package)
        {
            Instance = new VSShortcutsManager(package);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="VSShortcutsManager"/> class.
        /// Adds our command handlers for menu (commands must exist in the command table file)
        /// </summary>
        /// <param name="package">Owner package, not null.</param>
        private VSShortcutsManager(Package package)
        {
            this.package = package ?? throw new ArgumentNullException("package");

            // Register all the command handlers with the Global Command Service
            RegisterCommandHandlers();

            ShellSettingsManager = new ShellSettingsManager(package);
            userShortcutsManager = new UserShortcutsManager(ShellSettingsManager);

            // Initialise path for AppDataRoaming and AppDataLocal (Optional - alternative method)
            _RoamingAppDataVSPath = Path.Combine(ShellSettingsManager.GetApplicationDataFolder(ApplicationDataFolder.ApplicationExtensions), "Extensions");
            _LocalUserExtensionsPath = Path.Combine(ShellSettingsManager.GetApplicationDataFolder(ApplicationDataFolder.LocalSettings), "Extensions");

            // Load user shortcut registries
            //userShortcutsManager.ResetUserShortcutsRegistry();
            //userShortcutsManager.DeleteUserShortcutsDef("WindowHideShortcuts");
            UserShortcutsRegistry = userShortcutsManager.FetchUserShortcutsRegistry();
            // Load imported VSKs registry
            VskImportsRegistry = FetchVskImportsRegistry();

            if (RequiresNewScanOfExtensionsDir())
            {
                ScanForAllExtensionShortcuts();
            }

        }

        private void RegisterCommandHandlers()
        {
            if (ServiceProvider.GetService(typeof(IMenuCommandService)) is OleMenuCommandService commandService)
            {
                commandService.AddCommand(CreateMenuItem(BackupShortcutsCmdId, this.BackupShortcuts));
                commandService.AddCommand(CreateMenuItem(RestoreShortcutsCmdId, this.RestoreShortcuts));
                commandService.AddCommand(CreateMenuItem(ResetShortcutsCmdId, this.ResetShortcuts));
                commandService.AddCommand(CreateMenuItem(ImportMappingSchemeCmdId, this.ImportMappingScheme));
                // Add a dummy entry for the mapping scheme menu (you can't execute a "menu")
                commandService.AddCommand(CreateMenuItem(ShortcutSchemesMenu, null));
                // Add an entry for the dyanmic/expandable menu item for mapping schemes
                CommandID dynamicItemRootId = new CommandID(VSShortcutsManagerCmdSetGuid, DynamicThemeStartCmdId);
                commandService.AddCommand(new DynamicItemMenuCommand(dynamicItemRootId,
                    IsValidMappingSchemeItem,
                    ExecuteMappingSchemeCommand,
                    OnBeforeQueryStatusMappingSchemeDynamicItem));

                // Add a dummy entry for the user shortcuts menu
                commandService.AddCommand(CreateMenuItem(UserShortcutsMenu, null));
                // Add an entry for the dyanmic/expandable menu item for user shortcuts
                commandService.AddCommand(new DynamicItemMenuCommand(new CommandID(VSShortcutsManagerCmdSetGuid, DynamicUserShortcutsStartCmdId),
                    this.IsValidUserShortcutsItem,
                    this.ExecuteUserShortcutsCommand,
                    this.OnBeforeQueryStatusUserShortcutsDynamicItem));
            }
        }

        private List<VskMappingInfo> FetchVskImportsRegistry()
        {
            if ((VSShortcutsManagerPackage.SettingsManager.TryGetValue(VSK_IMPORTS_REGISTRY_KEY, out List<VskMappingInfo> storedData) != GetValueResult.Success) || storedData == null)
            {
                return new List<VskMappingInfo>();
            }
            return storedData;
        }

        private MenuCommand CreateMenuItem(int cmdId, EventHandler menuItemCallback)
        {
            return new MenuCommand(menuItemCallback, new CommandID(VSShortcutsManagerCmdSetGuid, cmdId));
        }

        //----------------  Command entry points -------------

        private void BackupShortcuts(object sender, EventArgs e)
        {
            ExecuteSaveShortcuts();
        }

        private void RestoreShortcuts(object sender, EventArgs e)
        {
            ExecuteRestoreShortcuts();
        }

        private void ResetShortcuts(object sender, EventArgs e)
        {
            // Confirm Reset operation
            if (MessageBox.Show("Reset keyboard shortcuts to default settings?", MSG_CAPTION_RESET, MessageBoxButtons.OKCancel) != DialogResult.OK)
            {
                return;
            }

            ResetShortcutsViaProfileManager();
            // Tools.ImportandExportSettings [/export:filename | /import:filename | /reset]   //https://msdn.microsoft.com/en-us/library/ms241277.aspx
        }

        private void ImportMappingScheme(object sender, EventArgs e)
        {
            const string Text = "Feature not implemented yet.";
            MessageBox.Show(Text, MSG_CAPTION_IMPORT, MessageBoxButtons.OK);
        }

        //-------- Reset Shortcuts --------

        private bool ResetShortcutsViaProfileManager()
        {
            IVsProfileDataManager vsProfileDataManager = (IVsProfileDataManager)ServiceProvider.GetService(typeof(SVsProfileDataManager));
            IVsProfileSettingsFileInfo profileSettingsFileInfo = GetDefaultProfileSettingsFileInfo(vsProfileDataManager);
            if (profileSettingsFileInfo == null)
            {
                MessageBox.Show("Unable to find Default Shortcuts file.");
                return false;
            }

            // Apply the Reset
            int result = vsProfileDataManager.ResetSettings(profileSettingsFileInfo, out IVsSettingsErrorInformation errorInfo);
            if (ErrorHandler.Failed(result))
            {
                // Something went wrong. TODO: Handle error.
                MessageBox.Show("Error occurred attempting to reset keyboard shortcuts.");
                return false;
            }

            MessageBox.Show("Success! Keyboard shortcuts have been reset.", MSG_CAPTION_RESET, MessageBoxButtons.OK);
            return true;
        }

        private IVsProfileSettingsFileInfo GetDefaultProfileSettingsFileInfo(IVsProfileDataManager manager)
        {
            const string DEFAULT_SHORTCUTS_FILENAME = "DefaultShortcuts.vssettings";
            string extensionDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string resetFilePath = Path.Combine(extensionDir, DEFAULT_SHORTCUTS_FILENAME);
            return GetProfileSettingsFileInfo(resetFilePath);
        }

        public static void ResetSettingsViaPostExecCmd()
        {
            IVsUIShell shell = (IVsUIShell)Package.GetGlobalService(typeof(SVsUIShell));
            if (shell == null)
            {
                return;
            }

            var group = VSConstants.CMDSETID.StandardCommandSet2K_guid;
            object arguments = "-reset";
            // NOTE: Call to PostExecCommand could fail. Callers should consider catching the exception. Otherwise, UI will show the error in a messagebox.
            shell.PostExecCommand(ref group, (uint)VSConstants.VSStd2KCmdID.ManageUserSettings, 0, ref arguments);
            MessageBox.Show($"Keyboard shortcuts Reset", MSG_CAPTION_RESET);
        }

        //-------- Backup Shortcuts --------

        public void ExecuteSaveShortcuts()
        {
            // Confirm Save operation
            //if (MessageBox.Show("Save current keyboard shortcuts?", MSG_CAPTION_BACKUP, MessageBoxButtons.OKCancel) != DialogResult.OK)
            //{
            //    return;
            //}

            IVsProfileDataManager vsProfileDataManager = (IVsProfileDataManager)ServiceProvider.GetService(typeof(SVsProfileDataManager));

            // Get the filename where the vssettings file will be saved
            // TODO: Prompt for user to name the settings file. (+including Browse for folder)
            string backupFilePath = GetExportFilePath(vsProfileDataManager);

            // Do the export
            IVsProfileSettingsTree keyboardOnlyExportSettings = GetKeyboardSettingsTree(vsProfileDataManager);
            int result = vsProfileDataManager.ExportSettings(backupFilePath, keyboardOnlyExportSettings, out IVsSettingsErrorInformation errorInfo);
            if (result != VSConstants.S_OK)
            {
                // Something went wrong. TODO: Handle error.
            }

            // Save Backup file path to SettingsManager and to UserShortcutsRegistry
            SaveBackupFilePath(backupFilePath);
            UserShortcutsDef userShortcutsDef = new UserShortcutsDef(backupFilePath);
            // Update the VSSettingsRegsitry
            UserShortcutsRegistry.Add(userShortcutsDef);
            // Update the SettingsStore
            userShortcutsManager.UpdateShortcutsDefInSettingsStore(userShortcutsDef);

            // Report success
            string Text = $"Your keyboard shortcuts have been saved to the following file:\n\n{backupFilePath}";
            MessageBox.Show(Text, MSG_CAPTION_BACKUP, MessageBoxButtons.OK);
        }

        private static string GetExportFilePath(IVsProfileDataManager vsProfileDataManager)
        {
            vsProfileDataManager.GetUniqueExportFileName((uint)__VSPROFILEGETFILENAME.PGFN_SAVECURRENT, out string exportFilePath);
            return exportFilePath;
        }

        private static IVsProfileSettingsTree GetKeyboardSettingsTree(IVsProfileDataManager vsProfileDataManager)
        {
            vsProfileDataManager.GetSettingsForExport(out IVsProfileSettingsTree profileSettingsTree);
            EnableKeyboardOnlyInProfileSettingsTree(profileSettingsTree);
            return profileSettingsTree;
        }

        private static void EnableKeyboardOnlyInProfileSettingsTree(IVsProfileSettingsTree profileSettingsTree)
        {
            // Disable all settings
            profileSettingsTree.SetEnabled(0, 1);
            // Enable Keyboard settings
            profileSettingsTree.FindChildTree("Environment_Group\\Environment_KeyBindings", out IVsProfileSettingsTree keyboardSettingsTree);
            if (keyboardSettingsTree != null)
            {
                int enabledValue = 1;  // true
                int setChildren = 0;  // true  (redundant)
                keyboardSettingsTree.SetEnabled(enabledValue, setChildren);
            }
            return;
        }

        //------------ Load User Shortcuts --------------

        public void ExecuteRestoreShortcuts()
        {
            string backupFilePath = GetSavedBackupFilePath();
            //if (String.IsNullOrEmpty(backupFilePath))
            //{
            //    MessageBox.Show("Unable to restore keyboard shortcuts.\n\nReason: No known backup file has been created.", MSG_CAPTION_RESTORE);
            //    return;
            //}

            string importFilePath = backupFilePath;
            if (!ShortcutsImport.ImportShortcuts(ref importFilePath))
            {
                // Cancel or ESC pressed
                return;
            }

            LoadKeyboardShortcutsFromVSSettingsFile(importFilePath);
        }

        public void LoadKeyboardShortcutsFromVSSettingsFile(string importFilePath)
        {
            if (!File.Exists(importFilePath))
            {
                MessageBox.Show($"File does not exist: {importFilePath}", MSG_CAPTION_RESTORE);
                return;
            }

            IVsProfileSettingsTree importShortcutsSettingsTree = GetShortcutsToImport(importFilePath);
            bool success = ImportSettingsFromSettingsTree(importShortcutsSettingsTree);
            if (success)
            {
                MessageBox.Show($"Keyboard shortcuts successfully imported: {Path.GetFileName(importFilePath)}", MSG_CAPTION_RESTORE);
            }
        }

        private IVsProfileSettingsTree GetShortcutsToImport(string importFilePath)
        {
            IVsProfileSettingsFileInfo profileSettingsFileInfo = GetProfileSettingsFileInfo(importFilePath);
            profileSettingsFileInfo.GetSettingsForImport(out IVsProfileSettingsTree profileSettingsTree);
            EnableKeyboardOnlyInProfileSettingsTree(profileSettingsTree);

            return profileSettingsTree;
        }

        private IVsProfileSettingsFileInfo GetProfileSettingsFileInfo(string importFilePath)
        {
            IVsProfileDataManager vsProfileDataManager = (IVsProfileDataManager)ServiceProvider.GetService(typeof(SVsProfileDataManager));
            vsProfileDataManager.GetSettingsFiles(uint.MaxValue, out IVsProfileSettingsFileCollection vsProfileSettingsFileCollection);
            vsProfileSettingsFileCollection.AddBrowseFile(importFilePath, out IVsProfileSettingsFileInfo profileSettingsFileInfo);
            return profileSettingsFileInfo;
        }

        private bool ImportSettingsFromSettingsTree(IVsProfileSettingsTree profileSettingsTree)
        {
            //EnableKeyboardOnlyInProfileSettingsTree(profileSettingsTree);
            IVsProfileDataManager vsProfileDataManager = (IVsProfileDataManager)ServiceProvider.GetService(typeof(SVsProfileDataManager));
            int result = vsProfileDataManager.ImportSettings(profileSettingsTree, out IVsSettingsErrorInformation errorInfo);
            if (ErrorHandler.Failed(result))
            {
                // Something went wrong. TODO: Handle error.
                MessageBox.Show("Error occurred attempting to import settings.");
                return false;
            }
            return true;
        }

        private void ImportUserSettings(string settingsFileName)
        {
            // import the settings file into Visual Studio
            var asmDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var settingsFilePath = Path.Combine(asmDirectory, settingsFileName);
            ImportSettingsFromFilePath(settingsFilePath);
        }

        public static void ImportSettingsFromFilePath(string settingsFilePath)
        {
            var group = VSConstants.CMDSETID.StandardCommandSet2K_guid;
            IVsUIShell shell = (IVsUIShell)Package.GetGlobalService(typeof(SVsUIShell));
            //if (ServiceProvider.GetService(typeof(SVsUIShell)) is IVsUIShell shell)
            if (shell != null)
            {
                object arguments = string.Format(CultureInfo.InvariantCulture, "-import:\"{0}\"", settingsFilePath);
                // NOTE: Call to PostExecCommand could fail. Callers should consider catching the exception. Otherwise, UI will show the error in a messagebox.
                try
                {
                    shell.PostExecCommand(ref group, (uint)VSConstants.VSStd2KCmdID.ManageUserSettings, 0, ref arguments);
                }
                catch (Exception)
                {
                    // TODO: This does not seem to be catching the exeption. Needs more work.
                    MessageBox.Show("Exception occurred trying to import shortcuts.", MSG_CAPTION_RESTORE);
                    return;
                }
                MessageBox.Show($"Keyboard shortcuts successfully restored: {Path.GetFileName(settingsFilePath)}", MSG_CAPTION_RESTORE);
            }
        }

        //---------- User Shortcuts ----------------

        private bool IsValidUserShortcutsItem(int commandId)
        {
            //It is a valid match if the command id is less than the total number of items the user has requested appear on our menu.
            int itemRange = (commandId - DynamicUserShortcutsStartCmdId);
            return itemRange >= 0 && itemRange < UserShortcutsRegistry.Count;
        }

        private void OnBeforeQueryStatusUserShortcutsDynamicItem(object sender, EventArgs args)
        {
            DynamicItemMenuCommand matchedCommand = (DynamicItemMenuCommand)sender;

            matchedCommand.Enabled = true;
            matchedCommand.Visible = true;

            //The root item in the expansion won't flow through IsValidDynamicItem as it will match against the actual DynamicItemMenuCommand based on the
            //'root' id given to that object on construction, only if that match fails will it try and call the dynamic id check, since it won't fail for
            //the root item we need to 'special case' it here as MatchedCommandId will be 0 in that case.
            bool isRootItem = (matchedCommand.MatchedCommandId == 0);
            int menuItemIndex = isRootItem ? 0 : (matchedCommand.MatchedCommandId - DynamicUserShortcutsStartCmdId);

            matchedCommand.Text = UserShortcutsRegistry[menuItemIndex].DisplayName;

            //Clear this out here as we are done with it for this item.
            matchedCommand.MatchedCommandId = 0;
        }

        private void ExecuteUserShortcutsCommand(object sender, EventArgs args)
        {
            DynamicItemMenuCommand invokedCommand = (DynamicItemMenuCommand)sender;
            string shortcutDefName = invokedCommand.Text;
            UserShortcutsDef userShortcutsDef = UserShortcutsRegistry.First(x => x.DisplayName.Equals(shortcutDefName));
            string importFilePath = userShortcutsDef.Filepath;
            if (!File.Exists(importFilePath))
            {
                if (MessageBox.Show($"File does not exist: {importFilePath}\nRemove from shortcuts registry?", MSG_CAPTION_RESTORE, MessageBoxButtons.YesNo) == DialogResult.Yes)
                {
                    UserShortcutsRegistry.Remove(userShortcutsDef);
                    userShortcutsManager.DeleteUserShortcutsDef(shortcutDefName);
                }
                return;
            }
            LoadKeyboardShortcutsFromVSSettingsFile(importFilePath);
        }

        //---------- Mapping Schemes ----------------

        private bool IsValidMappingSchemeItem(int commandId)
        {
            //It is a valid match if the command id is less than the total number of items the user has requested appear on our menu.
            List<string> mappingSchemes = GetMappingSchemes();
            // Returning an extra one to account for the "(Default)" mapping scheme - hence <= rather than <)
            int itemRange = (commandId - (int)DynamicThemeStartCmdId);
            return itemRange >= 0 && itemRange <= mappingSchemes.Count;
        }

        private List<string> GetMappingSchemes()
        {
            if (MappingSchemes == null)
            {
                MappingSchemes = new List<string>();
                PopulateMappingSchemes();
            }
            return MappingSchemes;
        }

        private void PopulateMappingSchemes()
        {
            MappingSchemes.AddRange(FetchListOfMappingSchemes());
        }

        private List<string> FetchListOfMappingSchemes()
        {
            // PERFORMS FILE IO! We want to minimize how often this occurs, plus delay this call as long as possible.
            return Directory.EnumerateFiles(GetVsInstallPath(), "*.vsk").Select(fn => Path.GetFileNameWithoutExtension(fn)).ToList();
        }

        internal string VSInstallationPath
        {
            get { return GetVsInstallPath(); }
        }

        string GetVsInstallPath()
        {
            string root = GetRegistryRoot();

            using (var key = Registry.LocalMachine.OpenSubKey(root))
            {
                var installDir = key.GetValue("InstallDir") as string;

                return Path.GetDirectoryName(installDir);
            }
        }

        private string GetRegistryRoot()
        {
            var reg = ServiceProvider.GetService(typeof(SLocalRegistry)) as ILocalRegistry2;
            reg.GetLocalRegistryRoot(out string root);
            return root;
        }

        private string GetMappingSchemeName(int itemIndex)
        {
            if (itemIndex >= 0 && itemIndex < GetMappingSchemes().Count)
            {
                return GetMappingSchemes()[itemIndex];
            }
            else
            {
                // It's the "(Default)" mapping scheme
                return DEFAULT_MAPPING_SCHEME_NAME;
            }
        }

        private void ExecuteMappingSchemeCommand(object sender, EventArgs args)
        {
            DynamicItemMenuCommand invokedCommand = (DynamicItemMenuCommand)sender;
            ApplyMappingScheme(invokedCommand.Text);
        }

        private void ApplyMappingScheme(string mappingSchemeName)
        {
            SetMappingScheme(mappingSchemeName);
            MessageBox.Show(string.Format("Mapping scheme changed to {0}", mappingSchemeName));
        }

        private bool IsSelected(string mappingSchemeName)
        {
            using (var vsKey = Registry.CurrentUser.OpenSubKey(GetRegistryRoot()))
            {
                if (vsKey != null)
                {
                    using (var keyboardKey = vsKey.OpenSubKey("Keyboard"))
                    {
                        if (keyboardKey != null)
                        {
                            var schemeName = keyboardKey.GetValue("SchemeName") as string;
                            if (string.IsNullOrEmpty(schemeName))
                            {
                                return mappingSchemeName == DEFAULT_MAPPING_SCHEME_NAME;
                            }

                            return string.Equals(mappingSchemeName + ".vsk", Path.GetFileName(schemeName), StringComparison.InvariantCultureIgnoreCase);
                        }
                    }
                }
            }
            return false;
        }

        private void SetMappingScheme(string mappingSchemeName)
        {
            DTE dte = (DTE)ServiceProvider.GetService(typeof(DTE));
            Properties props = dte.Properties["Environment", "Keyboard"];
            Property prop = props.Item("SchemeName");
            prop.Value = mappingSchemeName == DEFAULT_MAPPING_SCHEME_NAME ? "" : mappingSchemeName + ".vsk";
        }

        private void OnBeforeQueryStatusMappingSchemeDynamicItem(object sender, EventArgs args)
        {
            DynamicItemMenuCommand matchedCommand = (DynamicItemMenuCommand)sender;

            matchedCommand.Enabled = true;
            matchedCommand.Visible = true;

            //The root item in the expansion won't flow through IsValidDynamicItem as it will match against the actual DynamicItemMenuCommand based on the
            //'root' id given to that object on construction, only if that match fails will it try and call the dynamic id check, since it won't fail for
            //the root item we need to 'special case' it here as MatchedCommandId will be 0 in that case.
            bool isRootItem = (matchedCommand.MatchedCommandId == 0);
            int menuItemIndex = isRootItem ? 0 : (matchedCommand.MatchedCommandId - DynamicThemeStartCmdId);

            string mappingSchemeName = GetMappingSchemeName(menuItemIndex);
            matchedCommand.Text = mappingSchemeName;
            matchedCommand.Checked = IsSelected(mappingSchemeName);

            //Clear this out here as we are done with it for this item.
            matchedCommand.MatchedCommandId = 0;
        }

        //---------- Helper methods -------------------

        private static void SaveBackupFilePath(string exportFilePath)
        {
            try
            {
                VSShortcutsManagerPackage.SettingsManager.SetValueAsync(BACKUP_FILE_PATH, exportFilePath, isMachineLocal: true);
            }
            catch (Exception e)
            {
                // Unable to save backup file location. TODO: Handle error.
            }
        }

        private string GetSavedBackupFilePath()
        {
            VSShortcutsManagerPackage.SettingsManager.TryGetValue(BACKUP_FILE_PATH, out string backupFilePath);
            return backupFilePath;
        }

        //----------- Scanning ----------------------

        private bool RequiresNewScanOfExtensionsDir()
        {
            // TODO: Work out if there's been an update to anything in the user extensions dir or in the All-users extension dir
            // TODO: Include check for User setting to Scan/NotScan at startup.
            return true;
        }

        public void ScanForAllExtensionShortcuts()
        {
            // Tip: Best to scan for VSK files first, because then they are available if a VSSetting file wants it.

            // Scan for new VSK files
            //ScanForMappingSchemes();   - Disabled for now until it's working with the SettingsStore

            // Scan for new VSSettings files
            ScanForNewShortcutsDefs();
        }

        public void ScanForNewShortcutsDefs()
        {
            // Process VSSettings files
            // Scan All-Users and local-user extension directories for VSSettings files
            List<string> vsSettingsFilesInExtDirs = GetFilesFromFolder(AllUsersExtensionsPath, "*.vssettings");
            vsSettingsFilesInExtDirs.AddRange(GetFilesFromFolder(LocalUserExtensionsPath, "*.vssettings"));
            // For each VSSettings found, check VSSettings registry
            List<string> newVsSettings = new List<string>();
            List<string> updatedVsSettings = new List<string>();
            foreach (string vsSettingsFile in vsSettingsFilesInExtDirs)
            {
                var thisEntry = UserShortcutsRegistry.Find(x => x.Filepath.Equals(vsSettingsFile));
                if (thisEntry == null)
                {
                    // - New VSSettings file
                    // Add to VSSettings registry (update: prompt)
                    thisEntry = new UserShortcutsDef(vsSettingsFile);
                    // Add to NewVSSettingsList (to alert users)
                    newVsSettings.Add(vsSettingsFile);
                    // Update the VSSettingsRegsitry
                    UserShortcutsRegistry.Add(thisEntry);
                    // Update the SettingsStore
                    userShortcutsManager.UpdateShortcutsDefInSettingsStore(thisEntry);
                }
                else
                {
                    // We already know
                    if (thisEntry.NotifyFlag == UPDATE_NEVER) continue;

                    FileInfo vsSettingsFileInfo = new FileInfo(vsSettingsFile);
                    if (thisEntry.LastWriteTimeEquals(vsSettingsFileInfo.LastWriteTime)) continue;
                    // This entry has been updated since it was added to the registry. Update the entry.
                    thisEntry.LastWriteTime = vsSettingsFileInfo.LastWriteTime;
                    // Add to UpdatedVSSettingsList (to alert users)
                    updatedVsSettings.Add(vsSettingsFile);
                    // Update the SettingsStore
                    userShortcutsManager.UpdateShortcutsDefInSettingsStore(thisEntry);
                }
            }

            // Alert user of new and updated shortcut defs
            // If NewVSSettings.Count == 1
            if (newVsSettings.Count == 1)
            {
                // Prompt to load the new VSSettings
                // If confirmed: Load(newSettings)
                if (MessageBox.Show($"One new user shortcut definition was found.\n\n{PrintList(newVsSettings)}\n\nWould you like to load these shortcuts now?", MSG_CAPTION_IMPORT, MessageBoxButtons.YesNo) == DialogResult.Yes)
                {
                    // Load the settings
                    LoadKeyboardShortcutsFromVSSettingsFile(newVsSettings.First());
                }
            }
            else if (newVsSettings.Count > 1)
            {
                MessageBox.Show($"There were {newVsSettings.Count} new user shortcut files found.\n\n{PrintList(newVsSettings)}");
            }
            // Updated settings files
            if (updatedVsSettings.Count > 0)
            {
                MessageBox.Show($"There were {updatedVsSettings.Count} updated user shortcut files found.\n\n{PrintList(updatedVsSettings)}\n\nYou might want to reapply these shortcuts.\nTool->Keyboard Shortcuts");
            }
        }

        public void ScanForMappingSchemes()
        {
            List<VskMappingInfo> vskCopyList = new List<VskMappingInfo>();
            // Scan All-Users and local-user extension directories for VSK files
            List<string> vskFilesInExtDirs = GetFilesFromFolder(AllUsersExtensionsPath, "*.vsk");
            vskFilesInExtDirs.AddRange(GetFilesFromFolder(LocalUserExtensionsPath, "*.vsk"));
            // Check each VSK against VSK registry to see if it's new or updated.
            foreach (string vskFilePath in vskFilesInExtDirs)
            {
                FileInfo fileInfo = new FileInfo(vskFilePath);

                // Check existing VSK registry
                // Compare date/time to existing datetime of VSK. If dates same, skip.
                VskMappingInfo vskMappingInfo = GetMappingFileInfo(vskFilePath);
                if (vskMappingInfo != null && vskMappingInfo.lastWriteTime.Equals(fileInfo.LastWriteTime))
                {
                    // This entry is already registered and has not changed.
                    continue;
                }

                // Add to VSK copy list (consider name)
                VskMappingInfo item = GenerateNewVskMappingInfo(fileInfo);
                vskCopyList.Add(item);
                // Add it to the registry
                AddVskToRegistry(item);
            }

            // Copy VSK files if VSKCopyList is not empty
            if (vskCopyList.Count > 0)
            {
                MessageBox.Show($"There are {vskCopyList.Count} new VSKs to copy.");
                ConfirmAndCopyVSKs(vskCopyList);
            }
        }

        private void AddVskToRegistry(VskMappingInfo vskMappingInfo)
        {
            VskImportsRegistry.Add(vskMappingInfo);
            VSShortcutsManagerPackage.SettingsManager.SetValueAsync(VSK_IMPORTS_REGISTRY_KEY, vskMappingInfo, isMachineLocal: true);
        }

        private object PrintList(List<string> items)
        {
            StringBuilder sb = new StringBuilder();
            foreach (string item in items)
            {
                if (sb.Length > 0) sb.Append("\n");
                sb.Append(item);
            }
            return sb.ToString();
        }

        private void ConfirmAndCopyVSKs(List<VskMappingInfo> vskCopyList)
        {
            foreach (VskMappingInfo vskMappingInfo in vskCopyList)
            {
                // Confirm and Copy single VSK
                if (MessageBox.Show($"Import mapping scheme file?\n{vskMappingInfo.filepath}", MSG_CAPTION_IMPORT, MessageBoxButtons.OKCancel) != DialogResult.OK)
                {
                    continue;
                }
                string name = vskMappingInfo.name;  // TODO: Prompt user for name
                CopyVSKToIDEDir(vskMappingInfo.filepath, name);
            }
        }

        private void CopyVSKToIDEDir(string filepath, string name)
        {
            CopyVskUsingXCopy(filepath);
        }

        private void CopyVskUsingXCopy(string installPath)
        {
            var process = new System.Diagnostics.Process();
            process.StartInfo.FileName = @"xcopy.exe";
            process.StartInfo.Arguments = string.Format(@"""{0}"" ""{1}""", installPath, GetVsInstallPath());
            if (Environment.OSVersion.Platform == PlatformID.Win32NT && Environment.OSVersion.Version.Major > 5)
            {
                process.StartInfo.Verb = "runas";
            }
            process.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
            process.Start();
        }

        private static VskMappingInfo GenerateNewVskMappingInfo(FileInfo fileInfo)
        {
            return new VskMappingInfo
            {
                filepath = fileInfo.FullName,
                name = Path.GetFileNameWithoutExtension(fileInfo.FullName),
                updateFlag = 1,
                lastWriteTime = fileInfo.LastWriteTime
            };
        }

        private bool IsRegisteredMappingFile(string vskFilePath)
        {
            // Check Mapping File Registry for entry with same vskFilePath
            return VskImportsRegistry.Exists(x => x.filepath.Equals(vskFilePath));
        }

        private VskMappingInfo GetMappingFileInfo(string vskFilePath)
        {
            foreach (var item in VskImportsRegistry)
            {
                if (item.filepath != null && item.filepath.Equals(vskFilePath))
                {
                    return item;
                }
            }
            return null;
            //return VskImports.First(x => x.filepath.Equals(vskFilePath));
        }

        private bool HasSameDates(VskMappingInfo vskInfo, DateTime lastWriteTime)
        {
            return vskInfo.lastWriteTime.Equals(lastWriteTime);
        }

        private List<string> GetFilesFromFolder(string folder, string searchPattern, SearchOption searchOption = SearchOption.TopDirectoryOnly)
        {
            // PERFORMS FILE IO! We want to minimize how often this occurs, plus delay this call as long as possible.
            List<string> allFiles = new List<string>();

            DirectoryInfo[] directories = new DirectoryInfo(folder).GetDirectories();
            foreach (DirectoryInfo extensionDir in directories)
            {
                //const SearchOption topDirectoryOnly = SearchOption.TopDirectoryOnly;
                List<string> matchingFiles = Directory.EnumerateFiles(extensionDir.FullName, searchPattern, searchOption).ToList();
                allFiles.AddRange(matchingFiles);
            }

            return allFiles;
        }

        private string GetAllUsersExtensionsPath()
        {
            return Path.Combine(GetVsInstallPath(), "Extensions");
        }

        private object GetVirtualRegistryRoot()
        {
            // Note: A different way of getting the registry root
            IVsShell shell = (IVsShell)Package.GetGlobalService(typeof(SVsShell));
            shell.GetProperty((int)__VSSPROPID.VSSPROPID_VirtualRegistryRoot, out object root);
            return root;
        }

        private string GetVSInstanceId()
        {
            return Path.GetFileName(GetVirtualRegistryRoot().ToString());
        }

        private void InitializePathVariables()
        {
            // Gets the version number with the /rootsuffix. Example: "15.0_6bb4f128Exp"
            string vsInstanceId = GetVSInstanceId();

            _LocalUserExtensionsPath = GetVisualStudioVersionPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), vsInstanceId);
            _RoamingAppDataVSPath = GetVisualStudioVersionPath(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), vsInstanceId);
        }

        private static string GetVisualStudioVersionPath(string appData, string version)
        {
            return Path.Combine(appData, "Microsoft\\VisualStudio", version);
        }

        private string GetExtensionsPath(Environment.SpecialFolder folder)
        {
            return Path.Combine(GetVisualStudioVersionPath(Environment.GetFolderPath(folder), GetVSInstanceId()), "Extensions");
        }

    }

    internal class VskMappingInfo
    {
        public string name;
        public string filepath;
        public DateTime lastWriteTime;
        public int updateFlag; // 0 = never; 1 = prompt; 2 = always
    }

}
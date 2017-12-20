﻿using System;
using System.ComponentModel.Design;
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
        public const int ResetShortcutsCmdId = 0x1400;
        public const int ImportMappingSchemeCmdId = 0x1500;
        public const int ShortcutSchemesMenu = 0x2002;
        public const int DynamicThemeStartCmdId = 0x2A00;
        public const int UserShortcutsMenu = 0x1080;
        public const int ImportUserShortcutsCmdId = 0x1130;
        public const int ManageUserShortcutsCmdId = 0x1140;
        public const int DynamicUserShortcutsStartCmdId = 0x3A00;
        public const int ClearUserShortcutsCmdId = 0x1210;
        public const int ScanExtensionsCmdId = 0x1300;

        private const string BACKUP_FILE_PATH = "BackupFilePath";
        private const string MSG_CAPTION_IMPORT = "Import Keyboard Shortcuts";
        private const string MSG_CAPTION_BACKUP = "Save Keyboard Shortcuts";
        private const string MSG_CAPTION_RESET = "Reset Keyboard Shortcuts";
        private const string MSG_CAPTION_IMPORT_VSK = "Import Keyboard Mapping Scheme";
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
        //private List<ShortcutFileInfo> VskImportsRegistry;
        //private List<ShortcutFileInfo> UserShortcutsRegistry;

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

            //ShellSettingsManager = new ShellSettingsManager(package);
            userShortcutsManager = UserShortcutsManager.Instance;

            //// Initialise path for AppDataRoaming and AppDataLocal (Optional - alternative method)
            //_RoamingAppDataVSPath = Path.Combine(ShellSettingsManager.GetApplicationDataFolder(ApplicationDataFolder.ApplicationExtensions), "Extensions");
            //_LocalUserExtensionsPath = Path.Combine(ShellSettingsManager.GetApplicationDataFolder(ApplicationDataFolder.LocalSettings), "Extensions");

            // Load user shortcut registries
            //userShortcutsManager.DeleteUserShortcutsDef("WindowHideShortcuts");
            //UserShortcutsRegistry = userShortcutsManager.GetUserShortcutsRegistry();
            // Load imported VSKs registry
            //VskImportsRegistry = userShortcutsManager.GetVskImportsRegistry();

            if (ShortcutsScanner.Instance.ExtensionsNeedRescan())
            {
                ShortcutsScanner.Instance.ScanForAllExtensionShortcuts();
            }

        }

        private void RegisterCommandHandlers()
        {
            if (ServiceProvider.GetService(typeof(IMenuCommandService)) is OleMenuCommandService commandService)
            {
                commandService.AddCommand(CreateMenuItem(BackupShortcutsCmdId, this.BackupShortcuts));
                commandService.AddCommand(CreateMenuItem(ResetShortcutsCmdId, this.ResetShortcuts));
                commandService.AddCommand(CreateMenuItem(ScanExtensionsCmdId, this.ScanUserShortcuts));
                OleMenuCommand clearUserShortcutsCmd = CreateMenuItem(ClearUserShortcutsCmdId, this.ClearUserShortcuts);
                clearUserShortcutsCmd.BeforeQueryStatus += new EventHandler(OnBeforeQueryStatusClearUserShortcuts);
                commandService.AddCommand(clearUserShortcutsCmd);

                // User Shortcuts
                commandService.AddCommand(CreateMenuItem(ImportUserShortcutsCmdId, this.ImportShortcuts));
                // Add a dummy entry for the user shortcuts menu
                commandService.AddCommand(CreateMenuItem(UserShortcutsMenu, null));
                // Add an entry for the dyanmic/expandable menu item for user shortcuts
                commandService.AddCommand(new DynamicItemMenuCommand(new CommandID(VSShortcutsManagerCmdSetGuid, DynamicUserShortcutsStartCmdId),
                    IsValidUserShortcutsItem,
                    ExecuteUserShortcutsCommand,
                    OnBeforeQueryStatusUserShortcutsDynamicItem));

                // Mapping Scheme
                commandService.AddCommand(CreateMenuItem(ImportMappingSchemeCmdId, this.ImportMappingScheme));
                // Add a dummy entry for the mapping scheme menu (you can't execute a "menu")
                commandService.AddCommand(CreateMenuItem(ShortcutSchemesMenu, null));
                // Add an entry for the dyanmic/expandable menu item for mapping schemes
                CommandID dynamicItemRootId = new CommandID(VSShortcutsManagerCmdSetGuid, DynamicThemeStartCmdId);
                commandService.AddCommand(new DynamicItemMenuCommand(dynamicItemRootId,
                    IsValidMappingSchemeItem,
                    ExecuteMappingSchemeCommand,
                    OnBeforeQueryStatusMappingSchemeDynamicItem));

            }
        }

        private OleMenuCommand CreateMenuItem(int cmdId, EventHandler menuItemCallback)
        {
            return new OleMenuCommand(menuItemCallback, new CommandID(VSShortcutsManagerCmdSetGuid, cmdId));
        }

        //----------------  Command entry points -------------

        private void BackupShortcuts(object sender, EventArgs e)
        {
            ExecuteSaveShortcuts();
        }

        private void ImportShortcuts(object sender, EventArgs e)
        {
            ExecuteImportShortcuts();
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
            MessageBox.Show(Text, MSG_CAPTION_IMPORT_VSK, MessageBoxButtons.OK);
        }

        private void ScanUserShortcuts(object sender, EventArgs e)
        {
            bool foundShortcuts = ShortcutsScanner.Instance.ScanForAllExtensionShortcuts();
            if (!foundShortcuts)
            {
                MessageBox.Show("Scan complete.\n\nNo new shortcut definitions were found in the extensions directories.");
            }
        }

        private void OnBeforeQueryStatusClearUserShortcuts(object sender, EventArgs e)
        {
            OleMenuCommand command = (OleMenuCommand)sender;
            bool hasShortcutDefs = userShortcutsManager.GetUserShortcutsRegistry().Count > 0;
            command.Visible = hasShortcutDefs;
            command.Enabled = hasShortcutDefs;
        }

        private void ClearUserShortcuts(object sender, EventArgs e)
        {
            userShortcutsManager.ResetUserShortcutsRegistry();
            MessageBox.Show("User shortcuts list has been reset.");
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
            IVsProfileSettingsTree keyboardOnlyExportSettings = GetShortcutsSettingsTreeForExport(vsProfileDataManager);
            int result = vsProfileDataManager.ExportSettings(backupFilePath, keyboardOnlyExportSettings, out IVsSettingsErrorInformation errorInfo);
            if (result != VSConstants.S_OK)
            {
                // Something went wrong. TODO: Handle error.
            }

            // Save Backup file path to SettingsManager and to UserShortcutsRegistry
            SaveBackupFilePath(backupFilePath);
            AddUserShortcutsFileToRegistry(backupFilePath);

            // Report success
            string Text = $"Your keyboard shortcuts have been saved to the following file:\n\n{backupFilePath}";
            MessageBox.Show(Text, MSG_CAPTION_BACKUP, MessageBoxButtons.OK);
        }

        private static string GetExportFilePath(IVsProfileDataManager vsProfileDataManager)
        {
            vsProfileDataManager.GetUniqueExportFileName((uint)__VSPROFILEGETFILENAME.PGFN_SAVECURRENT, out string exportFilePath);
            return exportFilePath;
        }

        private static IVsProfileSettingsTree GetShortcutsSettingsTreeForExport(IVsProfileDataManager vsProfileDataManager)
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

        public void ExecuteImportShortcuts()
        {
            // Open UI to let user browse for a file to import
            string importFilePath = GetSavedBackupFilePath();
            if (!ShortcutsImport.ImportShortcuts(ref importFilePath))
            {
                // Cancel or ESC pressed
                return;
            }

            LoadKeyboardShortcutsFromVSSettingsFile(importFilePath);

            AddUserShortcutsFileToRegistry(importFilePath);
        }

        public static void LoadKeyboardShortcutsFromVSSettingsFile(string importFilePath)
        {
            if (!File.Exists(importFilePath))
            {
                MessageBox.Show($"File does not exist: {importFilePath}", MSG_CAPTION_IMPORT);
                return;
            }

            IVsProfileSettingsTree importShortcutsSettingsTree = GetShortcutsSettingsTreeForImport(importFilePath);
            bool success = ImportSettingsFromSettingsTree(importShortcutsSettingsTree);
            if (success)
            {
                MessageBox.Show($"Keyboard shortcuts successfully imported: {Path.GetFileName(importFilePath)}", MSG_CAPTION_IMPORT);
            }
        }

        private static IVsProfileSettingsTree GetShortcutsSettingsTreeForImport(string importFilePath)
        {
            IVsProfileSettingsFileInfo profileSettingsFileInfo = GetProfileSettingsFileInfo(importFilePath);
            profileSettingsFileInfo.GetSettingsForImport(out IVsProfileSettingsTree profileSettingsTree);
            EnableKeyboardOnlyInProfileSettingsTree(profileSettingsTree);

            return profileSettingsTree;
        }

        private static IVsProfileSettingsFileInfo GetProfileSettingsFileInfo(string importFilePath)
        {
            IVsProfileDataManager vsProfileDataManager = (IVsProfileDataManager)Package.GetGlobalService(typeof(SVsProfileDataManager));
            vsProfileDataManager.GetSettingsFiles(uint.MaxValue, out IVsProfileSettingsFileCollection vsProfileSettingsFileCollection);
            vsProfileSettingsFileCollection.AddBrowseFile(importFilePath, out IVsProfileSettingsFileInfo profileSettingsFileInfo);
            return profileSettingsFileInfo;
        }

        private static bool ImportSettingsFromSettingsTree(IVsProfileSettingsTree profileSettingsTree)
        {
            //EnableKeyboardOnlyInProfileSettingsTree(profileSettingsTree);
            IVsProfileDataManager vsProfileDataManager = (IVsProfileDataManager)Package.GetGlobalService(typeof(SVsProfileDataManager));
            int result = vsProfileDataManager.ImportSettings(profileSettingsTree, out IVsSettingsErrorInformation errorInfo);
            if (ErrorHandler.Failed(result))
            {
                // Something went wrong. TODO: Handle error.
                MessageBox.Show("Error occurred attempting to import settings.");
                return false;
            }
            return true;
        }

        private void AddUserShortcutsFileToRegistry(string importFilePath)
        {
            ShortcutFileInfo userShortcutsDef = new ShortcutFileInfo(importFilePath);
            userShortcutsManager.AddUserShortcutsDef(userShortcutsDef);
        }

        //---------- User Shortcuts ----------------

        private bool IsValidUserShortcutsItem(int commandId)
        {
            //It is a valid match if the command id is less than the total number of items the user has requested appear on our menu.
            int itemRange = (commandId - DynamicUserShortcutsStartCmdId);
            return itemRange >= 0 && itemRange < userShortcutsManager.GetUserShortcutsRegistry().Count;
        }

        private void OnBeforeQueryStatusUserShortcutsDynamicItem(object sender, EventArgs args)
        {
            List<ShortcutFileInfo> userShortcutsRegistry = userShortcutsManager.GetUserShortcutsRegistry();

            bool userShortcutsExist = userShortcutsRegistry.Count > 0;

            DynamicItemMenuCommand matchedCommand = (DynamicItemMenuCommand)sender;
            matchedCommand.Enabled = userShortcutsExist;
            matchedCommand.Visible = userShortcutsExist;

            if (userShortcutsExist)
            {
                //The root item in the expansion won't flow through IsValidDynamicItem as it will match against the actual DynamicItemMenuCommand based on the
                //'root' id given to that object on construction, only if that match fails will it try and call the dynamic id check, since it won't fail for
                //the root item we need to 'special case' it here as MatchedCommandId will be 0 in that case.
                bool isRootItem = (matchedCommand.MatchedCommandId == 0);
                int menuItemIndex = isRootItem ? 0 : (matchedCommand.MatchedCommandId - DynamicUserShortcutsStartCmdId);

                // Add an & to the front of the menu text so that the first letter becomes the accellerator key.
                matchedCommand.Text = GetMenuTextWithAccelerator(userShortcutsRegistry[menuItemIndex].DisplayName);
            }

            //Clear this out here as we are done with it for this item.
            matchedCommand.MatchedCommandId = 0;
        }

        private void ExecuteUserShortcutsCommand(object sender, EventArgs args)
        {

            DynamicItemMenuCommand invokedCommand = (DynamicItemMenuCommand)sender;
            string shortcutDefName = invokedCommand.Text.Replace("&", "");  // Remove the & (keyboard accelerator) from of the menu text
            List<ShortcutFileInfo> userShortcutsRegistry = userShortcutsManager.GetUserShortcutsRegistry();
            ShortcutFileInfo userShortcutsDef = userShortcutsRegistry.First(x => x.DisplayName.Equals(shortcutDefName));
            string importFilePath = userShortcutsDef.Filepath;
            if (!File.Exists(importFilePath))
            {
                if (MessageBox.Show($"File does not exist: {importFilePath}\nRemove from shortcuts registry?", MSG_CAPTION_IMPORT, MessageBoxButtons.YesNo) == DialogResult.Yes)
                {
                    userShortcutsRegistry.Remove(userShortcutsDef);
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
            return Directory.EnumerateFiles(VSPathUtils.GetVsInstallPath(), "*.vsk").Select(fn => Path.GetFileNameWithoutExtension(fn)).ToList();
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
            ApplyMappingScheme(invokedCommand.Text.Replace("&", ""));
        }

        private void ApplyMappingScheme(string mappingSchemeName)
        {
            SetMappingScheme(mappingSchemeName);
            MessageBox.Show(string.Format("Mapping scheme changed to {0}", mappingSchemeName));
        }

        private bool IsSelected(string mappingSchemeName)
        {
            using (var vsKey = Registry.CurrentUser.OpenSubKey(VSPathUtils.GetRegistryRoot()))
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
            matchedCommand.Text = GetMenuTextWithAccelerator(mappingSchemeName);
            matchedCommand.Checked = IsSelected(mappingSchemeName);

            //Clear this out here as we are done with it for this item.
            matchedCommand.MatchedCommandId = 0;
        }

        private static string GetMenuTextWithAccelerator(string mappingSchemeName)
        {
            // Add an "&" to the front of the menu text so that the first letter becomes the accelerator key.
            return $"&{mappingSchemeName}";
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

    }

}
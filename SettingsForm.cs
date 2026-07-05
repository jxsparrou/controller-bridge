using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

partial class Program
{
    // === SettingsForm class ===
    public class SettingsForm : Form
    {
        private CheckBox chkEnableSisr;
        private TextBox txtSisr;
        private TextBox txtSisrArgs;
        private Button btnBrowseSisr;
        private Label lblSisrWarning;
        private TextBox txtSgdbKey;

        private TabControl tabControl;
        private TabPage pageSteam;
        private TabPage pageUwp;

        // Tab 3 Controls (Add Custom Games)
        private TextBox txtCustomName;
        private TextBox txtCustomPath;
        private TextBox txtCustomArgs;
        private TextBox txtCustomWatch;
        private ComboBox cmbCustomSisr;

        // Tab 1 Per-Game SISR controls
        private ComboBox cmbGameSisr;
        private TextBox txtGameWatch;
        private Label lblGameSisr;
        private Label lblGameWatch;
        private Label lblSelectPrompt;
        private bool isUpdatingUi = false;

        // Tab 1 Controls (Steam Shortcuts)
        private ListView lstGames;
        private Label lblSteamStatus;
        private Button btnCloseSteam;
        private Button btnRemoveSelected;
        private Button btnToggleSisr;

        // Tab 2 Controls (Add UWP Games)
        private ListView lstApps;
        private Label lblUwpStatus;
        private Button btnScanUWP;
        private Button btnAddSelected;

        private List<SteamShortcutItem> currentShortcuts = new List<SteamShortcutItem>();
        private List<UWPAppInfo> scannedApps = new List<UWPAppInfo>();

        // Colors
        private Color bgDark = Color.FromArgb(28, 28, 30);
        private Color bgPanel = Color.FromArgb(36, 36, 40);
        private Color bgInput = Color.FromArgb(44, 44, 48);
        private Color textLight = Color.FromArgb(240, 240, 240);
        private Color textMuted = Color.FromArgb(170, 170, 170);
        private Color accentBlue = Color.FromArgb(0, 122, 204);
        private Color accentGreen = Color.FromArgb(46, 125, 50);
        private Color accentRed = Color.FromArgb(198, 40, 40);

        public SettingsForm()
        {
            this.Text = "Controller Bridge Settings";
            this.Size = new Size(680, 540);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.BackColor = bgDark;
            this.ForeColor = textLight;

            InitializeComponents();
            LoadPaths();
            RefreshShortcutsList();
        }

        private void InitializeComponents()
        {
            // Title
            Label lblTitle = new Label();
            lblTitle.Text = "Controller Bridge Settings";
            lblTitle.Font = new Font("Segoe UI", 14, FontStyle.Bold);
            lblTitle.ForeColor = Color.White;
            lblTitle.Location = new Point(15, 15);
            lblTitle.Size = new Size(400, 30);
            this.Controls.Add(lblTitle);

            // Tab Control
            tabControl = new TabControl();
            tabControl.Location = new Point(15, 50);
            tabControl.Size = new Size(635, 380);
            tabControl.Font = new Font("Segoe UI", 9, FontStyle.Regular);
            this.Controls.Add(tabControl);

            // Tab 1: Steam Shortcuts
            pageSteam = new TabPage("Steam Shortcuts");
            pageSteam.BackColor = bgPanel;
            tabControl.TabPages.Add(pageSteam);

            // Tab 1 Content: Steam status and Close Steam button
            lblSteamStatus = new Label();
            lblSteamStatus.Text = "Checking Steam status...";
            lblSteamStatus.Font = new Font("Segoe UI", 9, FontStyle.Bold);
            lblSteamStatus.Location = new Point(15, 15);
            lblSteamStatus.Size = new Size(440, 20);
            lblSteamStatus.ForeColor = Color.White;
            pageSteam.Controls.Add(lblSteamStatus);

            btnCloseSteam = new Button();
            btnCloseSteam.Text = "Close Steam";
            btnCloseSteam.Font = new Font("Segoe UI", 9, FontStyle.Bold);
            btnCloseSteam.FlatStyle = FlatStyle.Flat;
            btnCloseSteam.FlatAppearance.BorderSize = 0;
            btnCloseSteam.BackColor = accentRed;
            btnCloseSteam.ForeColor = Color.White;
            btnCloseSteam.Location = new Point(465, 10);
            btnCloseSteam.Size = new Size(145, 25);
            btnCloseSteam.Click += (s, e) => CloseSteam();
            pageSteam.Controls.Add(btnCloseSteam);

            // Tab 1 Content: Shortcuts ListView
            lstGames = new ListView();
            lstGames.View = View.Details;
            lstGames.CheckBoxes = true;
            lstGames.FullRowSelect = true;
            lstGames.GridLines = false;
            lstGames.BackColor = bgInput;
            lstGames.ForeColor = Color.White;
            lstGames.BorderStyle = BorderStyle.FixedSingle;
            lstGames.Font = new Font("Segoe UI", 9, FontStyle.Regular);
            lstGames.Location = new Point(15, 45);
            lstGames.Size = new Size(595, 200);
            lstGames.Columns.Add("Game Name", 220);
            lstGames.Columns.Add("Current Target", 220);
            lstGames.Columns.Add("Status", 140);
            pageSteam.Controls.Add(lstGames);

            // Tab 1 Content: Selection Prompt Label
            lblSelectPrompt = new Label();
            lblSelectPrompt.Text = "Select a game above to configure per-game overrides.";
            lblSelectPrompt.Font = new Font("Segoe UI", 9, FontStyle.Italic);
            lblSelectPrompt.ForeColor = textMuted;
            lblSelectPrompt.Location = new Point(15, 256);
            lblSelectPrompt.Size = new Size(505, 23);
            pageSteam.Controls.Add(lblSelectPrompt);

            // Tab 1 Content: SISR Support label and ComboBox
            lblGameSisr = new Label();
            lblGameSisr.Text = "SISR Support:";
            lblGameSisr.Font = new Font("Segoe UI", 9, FontStyle.Regular);
            lblGameSisr.Location = new Point(15, 256);
            lblGameSisr.Size = new Size(85, 20);
            lblGameSisr.ForeColor = textLight;
            lblGameSisr.Visible = false;
            pageSteam.Controls.Add(lblGameSisr);

            cmbGameSisr = new ComboBox();
            cmbGameSisr.Font = new Font("Segoe UI", 9, FontStyle.Regular);
            cmbGameSisr.BackColor = bgInput;
            cmbGameSisr.ForeColor = Color.White;
            cmbGameSisr.FlatStyle = FlatStyle.Flat;
            cmbGameSisr.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbGameSisr.Items.AddRange(new object[] { "Use Global Setting", "SISR Enabled", "SISR Disabled" });
            cmbGameSisr.SelectedIndex = -1;
            cmbGameSisr.Location = new Point(105, 252);
            cmbGameSisr.Size = new Size(160, 23);
            cmbGameSisr.Enabled = false;
            cmbGameSisr.Visible = false;
            cmbGameSisr.SelectedIndexChanged += cmbGameSisr_SelectedIndexChanged;
            pageSteam.Controls.Add(cmbGameSisr);

            // Tab 1 Content: Watch Process label and TextBox
            lblGameWatch = new Label();
            lblGameWatch.Text = "Watch Process:";
            lblGameWatch.Font = new Font("Segoe UI", 9, FontStyle.Regular);
            lblGameWatch.Location = new Point(280, 256);
            lblGameWatch.Size = new Size(90, 20);
            lblGameWatch.ForeColor = textLight;
            lblGameWatch.Visible = false;
            pageSteam.Controls.Add(lblGameWatch);

            txtGameWatch = new TextBox();
            txtGameWatch.Font = new Font("Segoe UI", 9, FontStyle.Regular);
            txtGameWatch.BackColor = bgInput;
            txtGameWatch.ForeColor = Color.White;
            txtGameWatch.BorderStyle = BorderStyle.FixedSingle;
            txtGameWatch.Location = new Point(375, 252);
            txtGameWatch.Size = new Size(145, 23);
            txtGameWatch.Enabled = false;
            txtGameWatch.Visible = false;
            txtGameWatch.TextChanged += txtGameWatch_TextChanged;
            pageSteam.Controls.Add(txtGameWatch);

            // Tab 1 Content: Action Buttons (Row 2 - aligned at y=295)
            btnToggleSisr = new Button();
            btnToggleSisr.Text = "Toggle SISR";
            btnToggleSisr.Font = new Font("Segoe UI", 9, FontStyle.Bold);
            btnToggleSisr.FlatStyle = FlatStyle.Flat;
            btnToggleSisr.FlatAppearance.BorderSize = 0;
            btnToggleSisr.BackColor = accentBlue;
            btnToggleSisr.ForeColor = Color.White;
            btnToggleSisr.Location = new Point(15, 295);
            btnToggleSisr.Size = new Size(185, 30);
            btnToggleSisr.Click += (s, e) => ToggleSelectedSisr();
            pageSteam.Controls.Add(btnToggleSisr);

            btnRemoveSelected = new Button();
            btnRemoveSelected.Text = "Remove Selected";
            btnRemoveSelected.Font = new Font("Segoe UI", 9, FontStyle.Bold);
            btnRemoveSelected.FlatStyle = FlatStyle.Flat;
            btnRemoveSelected.FlatAppearance.BorderSize = 0;
            btnRemoveSelected.BackColor = accentRed;
            btnRemoveSelected.ForeColor = Color.White;
            btnRemoveSelected.Location = new Point(220, 295);
            btnRemoveSelected.Size = new Size(185, 30);
            btnRemoveSelected.Click += (s, e) => RemoveSelectedShortcuts();
            pageSteam.Controls.Add(btnRemoveSelected);

            Button btnRefresh = new Button();
            btnRefresh.Text = "Refresh";
            btnRefresh.Font = new Font("Segoe UI", 9, FontStyle.Bold);
            btnRefresh.FlatStyle = FlatStyle.Flat;
            btnRefresh.FlatAppearance.BorderSize = 0;
            btnRefresh.BackColor = Color.FromArgb(60, 60, 64);
            btnRefresh.ForeColor = Color.White;
            btnRefresh.Location = new Point(425, 295);
            btnRefresh.Size = new Size(185, 30);
            btnRefresh.Click += (s, e) => RefreshShortcutsList();
            pageSteam.Controls.Add(btnRefresh);

            lstGames.SelectedIndexChanged += lstGames_SelectedIndexChanged;

            // Tab 2: Add UWP Games
            pageUwp = new TabPage("Add UWP Games");
            pageUwp.BackColor = bgPanel;
            tabControl.TabPages.Add(pageUwp);

            // Tab 2 Content: Status and Scan button
            lblUwpStatus = new Label();
            lblUwpStatus.Text = "Click \"Scan for UWP Apps\" to find installed UWP games.";
            lblUwpStatus.Font = new Font("Segoe UI", 9, FontStyle.Regular);
            lblUwpStatus.Location = new Point(15, 15);
            lblUwpStatus.Size = new Size(440, 20);
            lblUwpStatus.ForeColor = textMuted;
            pageUwp.Controls.Add(lblUwpStatus);

            btnScanUWP = new Button();
            btnScanUWP.Text = "Scan for UWP Apps";
            btnScanUWP.Font = new Font("Segoe UI", 9, FontStyle.Bold);
            btnScanUWP.FlatStyle = FlatStyle.Flat;
            btnScanUWP.FlatAppearance.BorderSize = 0;
            btnScanUWP.BackColor = accentBlue;
            btnScanUWP.ForeColor = Color.White;
            btnScanUWP.Location = new Point(465, 10);
            btnScanUWP.Size = new Size(145, 25);
            btnScanUWP.Click += (s, e) => PerformScan();
            pageUwp.Controls.Add(btnScanUWP);

            // Tab 2 Content: ListView
            lstApps = new ListView();
            lstApps.View = View.Details;
            lstApps.CheckBoxes = true;
            lstApps.FullRowSelect = true;
            lstApps.GridLines = false;
            lstApps.BackColor = bgInput;
            lstApps.ForeColor = Color.White;
            lstApps.BorderStyle = BorderStyle.FixedSingle;
            lstApps.Font = new Font("Segoe UI", 9, FontStyle.Regular);
            lstApps.Location = new Point(15, 45);
            lstApps.Size = new Size(595, 200);
            lstApps.Columns.Add("App Name", 220);
            lstApps.Columns.Add("AUMID", 230);
            lstApps.Columns.Add("Status", 130);
            pageUwp.Controls.Add(lstApps);

            // Tab 2 Content: Add button
            btnAddSelected = new Button();
            btnAddSelected.Text = "Add Selected to Steam";
            btnAddSelected.Font = new Font("Segoe UI", 9, FontStyle.Bold);
            btnAddSelected.FlatStyle = FlatStyle.Flat;
            btnAddSelected.FlatAppearance.BorderSize = 0;
            btnAddSelected.BackColor = accentGreen;
            btnAddSelected.ForeColor = Color.White;
            btnAddSelected.Location = new Point(15, 255);
            btnAddSelected.Size = new Size(300, 30);
            btnAddSelected.Click += (s, e) => AddSelectedToSteam();
            pageUwp.Controls.Add(btnAddSelected);

            // Tab 3: Add Custom Game
            TabPage pageCustom = new TabPage("Add Custom Game");
            pageCustom.BackColor = bgPanel;
            tabControl.TabPages.Add(pageCustom);

            Label lblCustomTitle = new Label();
            lblCustomTitle.Text = "Add a Custom Non-UWP Game to Steam";
            lblCustomTitle.Font = new Font("Segoe UI", 9, FontStyle.Bold);
            lblCustomTitle.ForeColor = textLight;
            lblCustomTitle.Location = new Point(15, 15);
            lblCustomTitle.Size = new Size(595, 20);
            pageCustom.Controls.Add(lblCustomTitle);

            // Game Name
            Label lblCustomName = new Label();
            lblCustomName.Text = "Game Name:";
            lblCustomName.Font = new Font("Segoe UI", 9, FontStyle.Regular);
            lblCustomName.Location = new Point(15, 50);
            lblCustomName.Size = new Size(120, 20);
            lblCustomName.ForeColor = textLight;
            pageCustom.Controls.Add(lblCustomName);

            txtCustomName = new TextBox();
            txtCustomName.Font = new Font("Segoe UI", 9, FontStyle.Regular);
            txtCustomName.BackColor = bgInput;
            txtCustomName.ForeColor = Color.White;
            txtCustomName.BorderStyle = BorderStyle.FixedSingle;
            txtCustomName.Location = new Point(150, 47);
            txtCustomName.Size = new Size(450, 23);
            pageCustom.Controls.Add(txtCustomName);

            // Executable Path
            Label lblCustomPath = new Label();
            lblCustomPath.Text = "Executable Path:";
            lblCustomPath.Font = new Font("Segoe UI", 9, FontStyle.Regular);
            lblCustomPath.Location = new Point(15, 90);
            lblCustomPath.Size = new Size(120, 20);
            lblCustomPath.ForeColor = textLight;
            pageCustom.Controls.Add(lblCustomPath);

            txtCustomPath = new TextBox();
            txtCustomPath.Font = new Font("Segoe UI", 9, FontStyle.Regular);
            txtCustomPath.BackColor = bgInput;
            txtCustomPath.ForeColor = Color.White;
            txtCustomPath.BorderStyle = BorderStyle.FixedSingle;
            txtCustomPath.Location = new Point(150, 87);
            txtCustomPath.Size = new Size(340, 23);
            pageCustom.Controls.Add(txtCustomPath);

            Button btnBrowseCustom = new Button();
            btnBrowseCustom.Text = "Browse...";
            btnBrowseCustom.Font = new Font("Segoe UI", 9, FontStyle.Bold);
            btnBrowseCustom.FlatStyle = FlatStyle.Flat;
            btnBrowseCustom.FlatAppearance.BorderSize = 0;
            btnBrowseCustom.BackColor = Color.FromArgb(60, 60, 64);
            btnBrowseCustom.ForeColor = Color.White;
            btnBrowseCustom.Location = new Point(500, 86);
            btnBrowseCustom.Size = new Size(100, 25);
            btnBrowseCustom.Click += (s, e) => BrowseCustomGame();
            pageCustom.Controls.Add(btnBrowseCustom);

            // Launch Arguments
            Label lblCustomArgs = new Label();
            lblCustomArgs.Text = "Launch Arguments:";
            lblCustomArgs.Font = new Font("Segoe UI", 9, FontStyle.Regular);
            lblCustomArgs.Location = new Point(15, 130);
            lblCustomArgs.Size = new Size(120, 20);
            lblCustomArgs.ForeColor = textLight;
            pageCustom.Controls.Add(lblCustomArgs);

            txtCustomArgs = new TextBox();
            txtCustomArgs.Font = new Font("Segoe UI", 9, FontStyle.Regular);
            txtCustomArgs.BackColor = bgInput;
            txtCustomArgs.ForeColor = Color.White;
            txtCustomArgs.BorderStyle = BorderStyle.FixedSingle;
            txtCustomArgs.Location = new Point(150, 127);
            txtCustomArgs.Size = new Size(450, 23);
            pageCustom.Controls.Add(txtCustomArgs);

            // Watch Process
            Label lblCustomWatch = new Label();
            lblCustomWatch.Text = "Watch Process:";
            lblCustomWatch.Font = new Font("Segoe UI", 9, FontStyle.Regular);
            lblCustomWatch.Location = new Point(15, 170);
            lblCustomWatch.Size = new Size(120, 20);
            lblCustomWatch.ForeColor = textLight;
            pageCustom.Controls.Add(lblCustomWatch);

            txtCustomWatch = new TextBox();
            txtCustomWatch.Font = new Font("Segoe UI", 9, FontStyle.Regular);
            txtCustomWatch.BackColor = bgInput;
            txtCustomWatch.ForeColor = Color.White;
            txtCustomWatch.BorderStyle = BorderStyle.FixedSingle;
            txtCustomWatch.Location = new Point(150, 167);
            txtCustomWatch.Size = new Size(450, 23);
            pageCustom.Controls.Add(txtCustomWatch);

            // SISR Setting
            Label lblCustomSisr = new Label();
            lblCustomSisr.Text = "SISR Support:";
            lblCustomSisr.Font = new Font("Segoe UI", 9, FontStyle.Regular);
            lblCustomSisr.Location = new Point(15, 210);
            lblCustomSisr.Size = new Size(120, 20);
            lblCustomSisr.ForeColor = textLight;
            pageCustom.Controls.Add(lblCustomSisr);

            cmbCustomSisr = new ComboBox();
            cmbCustomSisr.Font = new Font("Segoe UI", 9, FontStyle.Regular);
            cmbCustomSisr.BackColor = bgInput;
            cmbCustomSisr.ForeColor = Color.White;
            cmbCustomSisr.FlatStyle = FlatStyle.Flat;
            cmbCustomSisr.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbCustomSisr.Items.AddRange(new object[] { "Use Global Setting", "SISR Enabled", "SISR Disabled" });
            cmbCustomSisr.SelectedIndex = 0;
            cmbCustomSisr.Location = new Point(150, 207);
            cmbCustomSisr.Size = new Size(200, 23);
            pageCustom.Controls.Add(cmbCustomSisr);

            // Add Button
            Button btnAddCustom = new Button();
            btnAddCustom.Text = "Add Custom Game to Steam";
            btnAddCustom.Font = new Font("Segoe UI", 10, FontStyle.Bold);
            btnAddCustom.FlatStyle = FlatStyle.Flat;
            btnAddCustom.FlatAppearance.BorderSize = 0;
            btnAddCustom.BackColor = accentGreen;
            btnAddCustom.ForeColor = Color.White;
            btnAddCustom.Location = new Point(150, 250);
            btnAddCustom.Size = new Size(450, 35);
            btnAddCustom.Click += (s, e) => AddCustomGameToSteam();
            pageCustom.Controls.Add(btnAddCustom);

            // Tab 4: Global Settings
            TabPage pageSettings = new TabPage("Global Settings");
            pageSettings.BackColor = bgPanel;
            tabControl.TabPages.Add(pageSettings);

            // Global Settings Tab Content
            chkEnableSisr = new CheckBox();
            chkEnableSisr.Text = "Enable SISR Controller Integration";
            chkEnableSisr.Font = new Font("Segoe UI", 9, FontStyle.Bold);
            chkEnableSisr.Location = new Point(15, 15);
            chkEnableSisr.Size = new Size(300, 20);
            chkEnableSisr.ForeColor = textLight;
            chkEnableSisr.CheckedChanged += (s, e) => {
                UpdateSisrStatus();
                SavePaths();
            };
            pageSettings.Controls.Add(chkEnableSisr);

            // SISR Path Label & TextBox
            Label lblSisr = new Label();
            lblSisr.Text = "SISR Path:";
            lblSisr.Font = new Font("Segoe UI", 9, FontStyle.Regular);
            lblSisr.Location = new Point(15, 45);
            lblSisr.Size = new Size(100, 20);
            lblSisr.ForeColor = textLight;
            pageSettings.Controls.Add(lblSisr);

            txtSisr = new TextBox();
            txtSisr.Font = new Font("Segoe UI", 9, FontStyle.Regular);
            txtSisr.BackColor = bgInput;
            txtSisr.ForeColor = Color.White;
            txtSisr.BorderStyle = BorderStyle.FixedSingle;
            txtSisr.Location = new Point(15, 65);
            txtSisr.Size = new Size(490, 23);
            txtSisr.TextChanged += (s, e) => UpdateSisrStatus();
            txtSisr.Leave += (s, e) => SavePaths();
            pageSettings.Controls.Add(txtSisr);

            btnBrowseSisr = new Button();
            btnBrowseSisr.Text = "Browse...";
            btnBrowseSisr.Font = new Font("Segoe UI", 9, FontStyle.Bold);
            btnBrowseSisr.FlatStyle = FlatStyle.Flat;
            btnBrowseSisr.FlatAppearance.BorderSize = 0;
            btnBrowseSisr.BackColor = Color.FromArgb(60, 60, 64);
            btnBrowseSisr.ForeColor = Color.White;
            btnBrowseSisr.Location = new Point(515, 64);
            btnBrowseSisr.Size = new Size(105, 25);
            btnBrowseSisr.Click += (s, e) => BrowseSisr();
            pageSettings.Controls.Add(btnBrowseSisr);

            // SISR Arguments
            Label lblSisrArgs = new Label();
            lblSisrArgs.Text = "SISR Arguments (passed when launching SISR):";
            lblSisrArgs.Font = new Font("Segoe UI", 9, FontStyle.Regular);
            lblSisrArgs.Location = new Point(15, 95);
            lblSisrArgs.Size = new Size(300, 20);
            lblSisrArgs.ForeColor = textLight;
            pageSettings.Controls.Add(lblSisrArgs);

            txtSisrArgs = new TextBox();
            txtSisrArgs.Font = new Font("Segoe UI", 9, FontStyle.Regular);
            txtSisrArgs.BackColor = bgInput;
            txtSisrArgs.ForeColor = Color.White;
            txtSisrArgs.BorderStyle = BorderStyle.FixedSingle;
            txtSisrArgs.Location = new Point(15, 115);
            txtSisrArgs.Size = new Size(605, 23);
            txtSisrArgs.Leave += (s, e) => SavePaths();
            pageSettings.Controls.Add(txtSisrArgs);

            // SteamGridDB API Key
            Label lblSgdbKey = new Label();
            lblSgdbKey.Text = "SteamGridDB API Key (optional, for game artwork):";
            lblSgdbKey.Font = new Font("Segoe UI", 9, FontStyle.Regular);
            lblSgdbKey.Location = new Point(15, 145);
            lblSgdbKey.Size = new Size(300, 20);
            lblSgdbKey.ForeColor = textLight;
            pageSettings.Controls.Add(lblSgdbKey);

            txtSgdbKey = new TextBox();
            txtSgdbKey.Font = new Font("Segoe UI", 9, FontStyle.Regular);
            txtSgdbKey.BackColor = bgInput;
            txtSgdbKey.ForeColor = Color.White;
            txtSgdbKey.BorderStyle = BorderStyle.FixedSingle;
            txtSgdbKey.Location = new Point(15, 165);
            txtSgdbKey.Size = new Size(605, 23);
            txtSgdbKey.Leave += (s, e) => SavePaths();
            pageSettings.Controls.Add(txtSgdbKey);

            // Global SISR Warning Label
            lblSisrWarning = new Label();
            lblSisrWarning.Text = "Checking SISR status...";
            lblSisrWarning.Font = new Font("Segoe UI", 9, FontStyle.Bold);
            lblSisrWarning.Location = new Point(15, 205);
            lblSisrWarning.Size = new Size(605, 20);
            pageSettings.Controls.Add(lblSisrWarning);

            // Migrate Button
            Button btnMigrate = new Button();
            btnMigrate.Text = "Migrate From UWPHook";
            btnMigrate.Font = new Font("Segoe UI", 9, FontStyle.Bold);
            btnMigrate.FlatStyle = FlatStyle.Flat;
            btnMigrate.FlatAppearance.BorderSize = 0;
            btnMigrate.BackColor = Color.FromArgb(44, 44, 48);
            btnMigrate.ForeColor = Color.White;
            btnMigrate.Location = new Point(15, 245);
            btnMigrate.Size = new Size(605, 35);
            btnMigrate.Click += (s, e) => MigrateFromUwpHook();
            pageSettings.Controls.Add(btnMigrate);

            // Bottom Close Button
            Button btnClose = new Button();
            btnClose.Text = "Close";
            btnClose.Font = new Font("Segoe UI", 10, FontStyle.Bold);
            btnClose.FlatStyle = FlatStyle.Flat;
            btnClose.FlatAppearance.BorderSize = 0;
            btnClose.BackColor = Color.FromArgb(44, 44, 48);
            btnClose.ForeColor = Color.White;
            btnClose.Location = new Point(15, 445);
            btnClose.Size = new Size(635, 35);
            btnClose.Click += (s, e) => this.Close();
            this.Controls.Add(btnClose);
        }

        private void UpdateSisrStatus()
        {
            bool sisrEnabled = chkEnableSisr.Checked;
            txtSisr.Enabled = sisrEnabled;
            txtSisrArgs.Enabled = sisrEnabled;
            btnBrowseSisr.Enabled = sisrEnabled;

            if (!sisrEnabled)
            {
                lblSisrWarning.Text = "✔️ Standalone UWP Launcher mode (SISR disabled)";
                lblSisrWarning.ForeColor = accentGreen;
            }
            else
            {
                string path = txtSisr.Text.Trim();
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    lblSisrWarning.Text = "⚠️ SISR path is not configured. Redirecting controller inputs will not work.";
                    lblSisrWarning.ForeColor = accentRed;
                }
                else
                {
                    lblSisrWarning.Text = "✔️ SISR integration configured and enabled.";
                    lblSisrWarning.ForeColor = accentGreen;
                }
            }
        }

        private void LoadPaths()
        {
            chkEnableSisr.Checked = Program.sisrEnabled;
            txtSisr.Text = Program.sisrPath;
            txtSisrArgs.Text = Program.sisrArguments;
            txtSgdbKey.Text = Program.sgdbApiKey;
            UpdateSisrStatus();
        }

        private void SavePaths()
        {
            Program.sisrEnabled = chkEnableSisr.Checked;
            Program.sisrPath = txtSisr.Text.Trim();
            Program.sisrArguments = txtSisrArgs.Text.Trim();
            Program.sgdbApiKey = txtSgdbKey.Text.Trim();
            Program.SaveConfig();
        }

        private void SavePathsAndClose()
        {
            SavePaths();
            this.Close();
        }

        private void BrowseSisr()
        {
            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.Filter = "Executables (*.exe)|*.exe|All Files (*.*)|*.*";
                ofd.Title = "Select SISR.exe";
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    txtSisr.Text = ofd.FileName;
                    UpdateSisrStatus();
                    SavePaths();
                }
            }
        }

        private void CloseSteam()
        {
            try
            {
                Process[] processes = Process.GetProcessesByName("steam");
                if (processes.Length > 0)
                {
                    foreach (var p in processes)
                    {
                        p.Kill();
                        p.WaitForExit(5000);
                    }
                    MessageBox.Show("Steam client has been closed.", "Steam Closed", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to close Steam: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            RefreshShortcutsList();
        }

        private void RefreshShortcutsList()
        {
            // Check Steam status
            bool steamRunning = Process.GetProcessesByName("steam").Length > 0;
            if (steamRunning)
            {
                lblSteamStatus.Text = "⚠️ Warning: Steam is running! Close it before editing shortcuts.";
                lblSteamStatus.ForeColor = accentRed;
                btnCloseSteam.Visible = true;
            }
            else
            {
                lblSteamStatus.Text = "✔️ Steam is not running. Safe to edit shortcuts.";
                lblSteamStatus.ForeColor = accentGreen;
                btnCloseSteam.Visible = false;
            }

            lstGames.Items.Clear();
            
            // Save paths first in memory so we can compare correctly
            Program.sisrPath = txtSisr.Text.Trim();

            currentShortcuts = Program.LoadSteamShortcuts();
            string myExe = Process.GetCurrentProcess().MainModule.FileName;

            foreach (var item in currentShortcuts)
            {
                string trimmedExe = item.Exe.Replace("\"", "").Trim();
                bool isUWPHook = trimmedExe.EndsWith("UWPHook.exe", StringComparison.OrdinalIgnoreCase);
                bool isBridge = trimmedExe.EndsWith("uwphook-bridge.exe", StringComparison.OrdinalIgnoreCase) || 
                                trimmedExe.EndsWith("sBridge.exe", StringComparison.OrdinalIgnoreCase) ||
                                trimmedExe.Equals(myExe, StringComparison.OrdinalIgnoreCase);
                bool hasAumidPattern = !string.IsNullOrEmpty(item.LaunchOptions) && item.LaunchOptions.Contains("_") && item.LaunchOptions.Contains("!");

                // Show UWP/UWPHook games or bridged ones
                if (isUWPHook || isBridge || hasAumidPattern)
                {
                    ListViewItem lvItem = new ListViewItem(item.AppName);
                    
                    string targetStr = "";
                    if (isBridge && !string.IsNullOrEmpty(item.LaunchOptions))
                    {
                        targetStr = Program.ParseFirstArgument(item.LaunchOptions);
                    }
                    else
                    {
                        targetStr = Path.GetFileName(trimmedExe);
                    }
                    lvItem.SubItems.Add(targetStr);
                    
                    string statusStr = "Direct UWP";
                    if (isBridge)
                    {
                        string gameId = Program.ParseFirstArgument(item.LaunchOptions);
                        bool gameSisr = Program.sisrEnabled;
                        bool overrideVal;
                        if (!string.IsNullOrEmpty(gameId) && Program.perGameSisr.TryGetValue(gameId, out overrideVal))
                        {
                            gameSisr = overrideVal;
                        }
                        statusStr = gameSisr ? "SISR Enabled" : "SISR Disabled";
                    }
                    else if (isUWPHook)
                    {
                        statusStr = "Via UWPHook (migrate)";
                    }

                    lvItem.UseItemStyleForSubItems = false;
                    var subItem = lvItem.SubItems.Add(statusStr);
                    if (isBridge)
                    {
                        string gameId = Program.ParseFirstArgument(item.LaunchOptions);
                        bool gameSisr = Program.sisrEnabled;
                        bool overrideVal;
                        if (!string.IsNullOrEmpty(gameId) && Program.perGameSisr.TryGetValue(gameId, out overrideVal))
                        {
                            gameSisr = overrideVal;
                        }
                        subItem.ForeColor = gameSisr ? accentGreen : accentRed;
                    }
                    else if (isUWPHook)
                    {
                        subItem.ForeColor = Color.FromArgb(243, 156, 18);
                    }
                    else
                    {
                        subItem.ForeColor = textLight;
                    }

                    lvItem.Tag = item;
                    lvItem.Checked = false; // Kept unchecked to avoid accidental removal
                    lstGames.Items.Add(lvItem);
                }
            }
            
            // Clear selection and disable/hide ComboBox / TextBox
            isUpdatingUi = true;
            cmbGameSisr.SelectedIndex = -1;
            cmbGameSisr.Enabled = false;
            cmbGameSisr.Visible = false;
            txtGameWatch.Text = "";
            txtGameWatch.Enabled = false;
            txtGameWatch.Visible = false;
            lblGameSisr.Visible = false;
            lblGameWatch.Visible = false;
            lblSelectPrompt.Visible = true;
            isUpdatingUi = false;
            
            if (lstGames.Items.Count == 0)
            {
                ListViewItem emptyItem = new ListViewItem("No UWP/UWPHook games found in Steam shortcuts.");
                emptyItem.SubItems.Add("");
                emptyItem.SubItems.Add("");
                lstGames.Items.Add(emptyItem);
            }
        }

        private void MigrateFromUwpHook()
        {
            bool steamRunning = Process.GetProcessesByName("steam").Length > 0;
            if (steamRunning)
            {
                DialogResult res = MessageBox.Show(
                    "Steam is currently running. If you migrate shortcuts while Steam is open, Steam will overwrite your changes when it closes.\n\n" +
                    "Would you like to close Steam and proceed?",
                    "Steam is Running",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);

                if (res == DialogResult.Yes)
                {
                    CloseSteam();
                }
                else
                {
                    return;
                }
            }

            DialogResult confirm = MessageBox.Show(
                "This will scan your Steam shortcuts and automatically update any entries pointing to 'UWPHook.exe' to use 'uwphook-bridge.exe' instead.\n\n" +
                "This preserves your game names, play time, and custom artwork.\n\n" +
                "Do you want to proceed with the migration?",
                "Migrate From UWPHook",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (confirm != DialogResult.Yes) return;

            SavePaths();

            string myExe = Process.GetCurrentProcess().MainModule.FileName;
            string myDir = AppDomain.CurrentDomain.BaseDirectory;

            List<SteamShortcutItem> modifiedItems = new List<SteamShortcutItem>();
            int count = 0;

            var shortcuts = Program.LoadSteamShortcuts();
            foreach (var item in shortcuts)
            {
                if (item.ShortcutElement != null)
                {
                    var exeChild = item.ShortcutElement.Children.Find(c => c.Name.Equals("Exe", StringComparison.OrdinalIgnoreCase));
                    var startDirChild = item.ShortcutElement.Children.Find(c => c.Name.Equals("StartDir", StringComparison.OrdinalIgnoreCase));

                    if (exeChild != null)
                    {
                        string trimmedExe = exeChild.StringValue.Replace("\"", "").Trim();
                        if (trimmedExe.EndsWith("UWPHook.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            exeChild.StringValue = "\"" + myExe + "\"";
                            if (startDirChild != null)
                            {
                                startDirChild.StringValue = "\"" + myDir.TrimEnd('\\') + "\"";
                            }
                            modifiedItems.Add(item);
                            count++;
                        }
                    }
                }
            }

            if (count > 0)
            {
                Program.SaveSteamShortcuts(modifiedItems);
                MessageBox.Show(string.Format("Successfully migrated {0} shortcuts from UWPHook to Controller Bridge!", count), "Migration Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show("No existing UWPHook shortcuts were found in Steam.", "Migration Info", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            RefreshShortcutsList();
        }

        private void ToggleSelectedSisr()
        {
            List<ListViewItem> targets = new List<ListViewItem>();
            foreach (ListViewItem lvItem in lstGames.Items)
            {
                if (lvItem.Checked) targets.Add(lvItem);
            }

            if (targets.Count == 0)
            {
                foreach (ListViewItem lvItem in lstGames.SelectedItems)
                {
                    targets.Add(lvItem);
                }
            }

            if (targets.Count == 0)
            {
                MessageBox.Show("Please select or check at least one game to toggle SISR support.", "Info", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            isUpdatingUi = true;
            foreach (ListViewItem lvItem in targets)
            {
                var shortcut = lvItem.Tag as SteamShortcutItem;
                if (shortcut == null) continue;

                string gameId = Program.ParseFirstArgument(shortcut.LaunchOptions);
                if (string.IsNullOrEmpty(gameId)) continue;

                bool overrideVal;
                if (Program.perGameSisr.TryGetValue(gameId, out overrideVal))
                {
                    if (overrideVal)
                    {
                        Program.perGameSisr[gameId] = false;
                    }
                    else
                    {
                        Program.perGameSisr.Remove(gameId);
                    }
                }
                else
                {
                    Program.perGameSisr[gameId] = true;
                }

                bool gameSisr = Program.sisrEnabled;
                if (Program.perGameSisr.TryGetValue(gameId, out overrideVal))
                {
                    gameSisr = overrideVal;
                }
                lvItem.SubItems[2].Text = gameSisr ? "SISR Enabled" : "SISR Disabled";
                lvItem.SubItems[2].ForeColor = gameSisr ? accentGreen : accentRed;
            }
            isUpdatingUi = false;

            if (lstGames.SelectedItems.Count > 0)
            {
                var firstItem = lstGames.SelectedItems[0];
                var shortcut = firstItem.Tag as SteamShortcutItem;
                if (shortcut != null)
                {
                    string gameId = Program.ParseFirstArgument(shortcut.LaunchOptions);
                    if (!string.IsNullOrEmpty(gameId))
                    {
                        isUpdatingUi = true;
                        bool overrideVal;
                        if (Program.perGameSisr.TryGetValue(gameId, out overrideVal))
                        {
                            cmbGameSisr.SelectedIndex = overrideVal ? 1 : 2;
                        }
                        else
                        {
                            cmbGameSisr.SelectedIndex = 0;
                        }
                        isUpdatingUi = false;
                    }
                }
            }

            SavePaths();
        }

        private void RemoveSelectedShortcuts()
        {
            // 1. Check if Steam is running (same pattern as ApplyBridge — warn and offer to close)
            bool steamRunning = Process.GetProcessesByName("steam").Length > 0;
            if (steamRunning)
            {
                DialogResult res = MessageBox.Show(
                    "Steam is currently running. Close Steam before modifying shortcuts.\n\nWould you like to close Steam and proceed?",
                    "Steam is Running", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (res == DialogResult.Yes) CloseSteam();
                else return;
            }

            // 2. Count checked items
            int count = 0;
            foreach (ListViewItem lvItem in lstGames.Items)
            {
                if (lvItem.Checked && lvItem.Tag != null) count++;
            }

            if (count == 0)
            {
                MessageBox.Show("No shortcuts selected.", "Info", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // 3. Confirm
            DialogResult confirm = MessageBox.Show(
                string.Format("Are you sure you want to remove {0} shortcut(s) from Steam?\n\nThis cannot be undone (a backup of shortcuts.vdf will be created).", count),
                "Confirm Removal", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;

            // 4. Remove each checked shortcut from its root VDF element
            var modifiedRoots = new Dictionary<string, KeyValuePair<VdfElement, string>>();
            int removed = 0;

            foreach (ListViewItem lvItem in lstGames.Items)
            {
                var item = lvItem.Tag as SteamShortcutItem;
                if (lvItem.Checked && item != null)
                {
                    Program.RemoveShortcutFromSteam(item.RootElement, item.ShortcutElement);
                    if (!modifiedRoots.ContainsKey(item.VdfPath))
                    {
                        modifiedRoots[item.VdfPath] = new KeyValuePair<VdfElement, string>(item.RootElement, item.VdfPath);
                    }
                    removed++;
                }
            }

            // 5. Save modified VDFs
            if (removed > 0)
            {
                var itemsToSave = new List<SteamShortcutItem>();
                foreach (var pair in modifiedRoots)
                {
                    itemsToSave.Add(new SteamShortcutItem { VdfPath = pair.Key, RootElement = pair.Value.Key });
                }
                Program.SaveSteamShortcuts(itemsToSave);
                MessageBox.Show(string.Format("Successfully removed {0} shortcut(s) from Steam.", removed),
                    "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            RefreshShortcutsList();
        }

        private void PerformScan()
        {
            lblUwpStatus.Text = "Scanning... this may take a moment.";
            lblUwpStatus.ForeColor = Color.FromArgb(255, 193, 7); // Yellow/amber
            btnScanUWP.Enabled = false;
            lstApps.Items.Clear();
            this.Refresh(); // Force UI repaint

            scannedApps = Program.ScanInstalledUWPApps();

            // Build a set of AUMIDs already in Steam for quick lookup
            var existingAumids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var shortcut in currentShortcuts)
            {
                if (!string.IsNullOrEmpty(shortcut.LaunchOptions))
                {
                    string firstToken = shortcut.LaunchOptions.Split(' ')[0];
                    existingAumids.Add(firstToken);
                }
            }

            foreach (var app in scannedApps)
            {
                bool alreadyInSteam = existingAumids.Contains(app.AUMID);

                ListViewItem item = new ListViewItem(app.Name);
                item.SubItems.Add(app.AUMID);
                item.SubItems.Add(alreadyInSteam ? "Already in Steam" : "Not added");
                item.Tag = app;
                item.Checked = false; // Nothing checked by default
                lstApps.Items.Add(item);
            }

            lblUwpStatus.Text = string.Format("Found {0} UWP apps. Select the ones you want to add.", scannedApps.Count);
            lblUwpStatus.ForeColor = accentGreen;
            btnScanUWP.Enabled = true;
        }

        private void AddSelectedToSteam()
        {
            SavePaths(); // Ensure API Key and path configs are saved before importing

            // Check Steam is not running
            bool steamRunning = Process.GetProcessesByName("steam").Length > 0;
            if (steamRunning)
            {
                MessageBox.Show(
                    "Steam is currently running. Please close Steam before adding shortcuts.",
                    "Steam is Running", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var vdfFiles = Program.FindShortcutsVdfFiles();
            if (vdfFiles.Count == 0)
            {
                MessageBox.Show(
                    "Could not find Steam's shortcuts.vdf file. Make sure Steam has been run at least once.",
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            string vdfPath = vdfFiles[0];

            VdfElement root;
            try
            {
                byte[] bytes = File.ReadAllBytes(vdfPath);
                using (var ms = new MemoryStream(bytes))
                using (var reader = new BinaryReader(ms))
                {
                    byte firstByte = reader.ReadByte();
                    string rootName = Program.ReadNullTerminatedString(reader);
                    root = Program.ReadMap(reader, rootName);
                }
            }
            catch (Exception ex)
            {
                root = new VdfElement { Type = 0x00, Name = "shortcuts" };
                Program.Log("Creating new shortcuts.vdf structure: " + ex.Message);
            }

            int count = 0;
            foreach (ListViewItem lvItem in lstApps.Items)
            {
                var app = lvItem.Tag as UWPAppInfo;
                if (lvItem.Checked && app != null)
                {
                    if (lvItem.SubItems[2].Text != "Already in Steam")
                    {
                        Program.AddShortcutToSteam(vdfPath, root, app.Name, app.AUMID, app.Executable);
                        count++;
                    }
                }
            }

            if (count > 0)
            {
                var dummyItem = new SteamShortcutItem { VdfPath = vdfPath, RootElement = root };
                Program.SaveSteamShortcuts(new List<SteamShortcutItem> { dummyItem });

                MessageBox.Show(
                    string.Format("Successfully added {0} app(s) to Steam!\n\nRestart Steam to see them in your library.", count),
                    "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show("No new apps were selected to add.", "Info", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            RefreshShortcutsList();
        }

        private void lstGames_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (lstGames.SelectedItems.Count == 0)
            {
                isUpdatingUi = true;
                cmbGameSisr.SelectedIndex = -1;
                cmbGameSisr.Enabled = false;
                cmbGameSisr.Visible = false;
                txtGameWatch.Text = "";
                txtGameWatch.Enabled = false;
                txtGameWatch.Visible = false;
                lblGameSisr.Visible = false;
                lblGameWatch.Visible = false;
                lblSelectPrompt.Visible = true;
                isUpdatingUi = false;
                return;
            }

            var firstItem = lstGames.SelectedItems[0];
            var shortcut = firstItem.Tag as SteamShortcutItem;
            if (shortcut == null)
            {
                isUpdatingUi = true;
                cmbGameSisr.SelectedIndex = -1;
                cmbGameSisr.Enabled = false;
                cmbGameSisr.Visible = false;
                txtGameWatch.Text = "";
                txtGameWatch.Enabled = false;
                txtGameWatch.Visible = false;
                lblGameSisr.Visible = false;
                lblGameWatch.Visible = false;
                lblSelectPrompt.Visible = true;
                isUpdatingUi = false;
                return;
            }

            string gameId = Program.ParseFirstArgument(shortcut.LaunchOptions);

            isUpdatingUi = true;
            lblSelectPrompt.Visible = false;
            lblGameSisr.Visible = true;
            cmbGameSisr.Enabled = true;
            cmbGameSisr.Visible = true;
            lblGameWatch.Visible = true;
            txtGameWatch.Enabled = true;
            txtGameWatch.Visible = true;

            if (string.IsNullOrEmpty(gameId))
            {
                cmbGameSisr.SelectedIndex = 0; // Use Global
                txtGameWatch.Text = "";
            }
            else
            {
                bool overrideVal;
                if (Program.perGameSisr.TryGetValue(gameId, out overrideVal))
                {
                    cmbGameSisr.SelectedIndex = overrideVal ? 1 : 2;
                }
                else
                {
                    cmbGameSisr.SelectedIndex = 0; // Use Global
                }

                string watchVal;
                if (Program.perGameWatch.TryGetValue(gameId, out watchVal))
                {
                    txtGameWatch.Text = watchVal;
                }
                else
                {
                    txtGameWatch.Text = "";
                }
            }
            isUpdatingUi = false;
        }

        private void txtGameWatch_TextChanged(object sender, EventArgs e)
        {
            if (isUpdatingUi) return;
            if (lstGames.SelectedItems.Count == 0) return;

            foreach (ListViewItem lvItem in lstGames.SelectedItems)
            {
                var shortcut = lvItem.Tag as SteamShortcutItem;
                if (shortcut == null) continue;

                string gameId = Program.ParseFirstArgument(shortcut.LaunchOptions);
                if (string.IsNullOrEmpty(gameId)) continue;

                string val = txtGameWatch.Text.Trim();
                if (string.IsNullOrEmpty(val))
                {
                    if (Program.perGameWatch.ContainsKey(gameId))
                    {
                        Program.perGameWatch.Remove(gameId);
                    }
                }
                else
                {
                    Program.perGameWatch[gameId] = val;
                }
            }

            SavePaths();
        }

        private void cmbGameSisr_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (isUpdatingUi) return;
            if (lstGames.SelectedItems.Count == 0) return;

            foreach (ListViewItem lvItem in lstGames.SelectedItems)
            {
                var shortcut = lvItem.Tag as SteamShortcutItem;
                if (shortcut == null) continue;

                string gameId = Program.ParseFirstArgument(shortcut.LaunchOptions);
                if (string.IsNullOrEmpty(gameId)) continue;

                int sel = cmbGameSisr.SelectedIndex;
                if (sel == 1) // SISR Enabled
                {
                    Program.perGameSisr[gameId] = true;
                }
                else if (sel == 2) // SISR Disabled
                {
                    Program.perGameSisr[gameId] = false;
                }
                else // Use Global
                {
                    if (Program.perGameSisr.ContainsKey(gameId))
                    {
                        Program.perGameSisr.Remove(gameId);
                    }
                }

                bool gameSisr = Program.sisrEnabled;
                bool overrideVal;
                if (Program.perGameSisr.TryGetValue(gameId, out overrideVal))
                {
                    gameSisr = overrideVal;
                }
                lvItem.SubItems[2].Text = gameSisr ? "SISR Enabled" : "SISR Disabled";
                lvItem.SubItems[2].ForeColor = gameSisr ? accentGreen : accentRed;
            }

            SavePaths();
        }

        private void BrowseCustomGame()
        {
            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.Filter = "Executables (*.exe)|*.exe|All Files (*.*)|*.*";
                ofd.Title = "Select Game Executable";
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    txtCustomPath.Text = ofd.FileName;
                    if (string.IsNullOrEmpty(txtCustomName.Text.Trim()))
                    {
                        try
                        {
                            txtCustomName.Text = Path.GetFileNameWithoutExtension(ofd.FileName);
                        }
                        catch {}
                    }
                }
            }
        }

        private void AddCustomGameToSteam()
        {
            string appName = txtCustomName.Text.Trim();
            string gamePath = txtCustomPath.Text.Trim();
            string gameArgs = txtCustomArgs.Text.Trim();
            string watchName = txtCustomWatch.Text.Trim();

            if (string.IsNullOrEmpty(appName))
            {
                MessageBox.Show("Please enter a game name.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (string.IsNullOrEmpty(gamePath) || !File.Exists(gamePath))
            {
                MessageBox.Show("Please enter a valid executable path.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            bool steamRunning = Process.GetProcessesByName("steam").Length > 0;
            if (steamRunning)
            {
                MessageBox.Show(
                    "Steam is currently running. Please close Steam before adding shortcuts.",
                    "Steam is Running", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var vdfFiles = Program.FindShortcutsVdfFiles();
            if (vdfFiles.Count == 0)
            {
                MessageBox.Show(
                    "Could not find Steam's shortcuts.vdf file. Make sure Steam has been run at least once.",
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            string vdfPath = vdfFiles[0];

            VdfElement root;
            try
            {
                byte[] bytes = File.ReadAllBytes(vdfPath);
                using (var ms = new MemoryStream(bytes))
                using (var reader = new BinaryReader(ms))
                {
                    byte firstByte = reader.ReadByte();
                    string rootName = Program.ReadNullTerminatedString(reader);
                    root = Program.ReadMap(reader, rootName);
                }
            }
            catch (Exception ex)
            {
                root = new VdfElement { Type = 0x00, Name = "shortcuts" };
                Program.Log("Creating new shortcuts.vdf structure: " + ex.Message);
            }

            string unquotedPath = gamePath;
            int sisrSel = cmbCustomSisr.SelectedIndex;
            if (sisrSel == 1) // Enabled
            {
                Program.perGameSisr[unquotedPath] = true;
            }
            else if (sisrSel == 2) // Disabled
            {
                Program.perGameSisr[unquotedPath] = false;
            }
            else // Use Global
            {
                if (Program.perGameSisr.ContainsKey(unquotedPath))
                {
                    Program.perGameSisr.Remove(unquotedPath);
                }
            }

            if (!string.IsNullOrEmpty(watchName))
            {
                Program.perGameWatch[unquotedPath] = watchName;
            }
            else
            {
                if (Program.perGameWatch.ContainsKey(unquotedPath))
                {
                    Program.perGameWatch.Remove(unquotedPath);
                }
            }

            SavePaths();

            string quotedPath = gamePath;
            if (!quotedPath.StartsWith("\""))
            {
                quotedPath = "\"" + quotedPath + "\"";
            }

            Program.AddShortcutToSteam(vdfPath, root, appName, quotedPath, gameArgs);

            var dummyItem = new SteamShortcutItem { VdfPath = vdfPath, RootElement = root };
            Program.SaveSteamShortcuts(new List<SteamShortcutItem> { dummyItem });

            MessageBox.Show(
                string.Format("Successfully added '{0}' to Steam!\n\nRestart Steam to see it in your library.", appName),
                "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);

            txtCustomName.Text = "";
            txtCustomPath.Text = "";
            txtCustomArgs.Text = "";
            txtCustomWatch.Text = "";
            cmbCustomSisr.SelectedIndex = 0;

            RefreshShortcutsList();
        }
    }
}

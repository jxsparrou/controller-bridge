// Legacy null contracts are migrated with their subsystem, not the SDK switch.
#nullable disable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using SBridge.Steam;
using SBridge.Launching;
using SBridge.Core;
using SBridge.Providers;
using System.Threading;
using System.Threading.Tasks;

partial class Program
{
    // === SettingsForm class ===
    public class SettingsForm : Form
    {
        private CheckBox chkEnableSisr;
        private CheckBox chkManagedSisr;
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
        private ListView lstEpicGames;
        private Label lblEpicStatus;
        private Button btnScanEpic;
        private Button btnAddEpic;
        private CancellationTokenSource scanCancellation;
        private Task scanTask;
        private bool closingAfterScan;
        private readonly CancellationTokenSource artworkCancellation = new CancellationTokenSource();
        private readonly SemaphoreSlim artworkSlots = new SemaphoreSlim(2);
        private readonly List<Task> artworkTasks = new List<Task>();
        private Label lblArtworkStatus;
        private int artworkFinished;
        private int artworkWarnings;
        private int artworkPending;
        private bool artworkDisposalStarted;
        private readonly SBridge.Artwork.ArtworkService artworkService;
        private Task diagnosticsTask;
        private TextBox txtDiagnostics;
        private Button btnRefreshDiagnostics;
        private Button btnCopyDiagnostics;
        private string diagnosticReport;
        private CheckedListBox lstSteamAccounts;
        private ListBox lstLibrary;
        private TextBox txtLibraryName, txtLibraryTarget, txtLibraryArguments, txtLibraryHint, txtLibraryInstall, txtLibraryWatch;
        private ComboBox cmbLibrarySisr;
        private Button btnLibrarySave;
        private Label lblLibraryIdentity, lblLibraryStatus;
        private Game libraryOriginal;
        private GameProfile libraryOriginalProfile;
        private ListBox lstControllerGames;
        private CheckBox chkControllerOverride, chkControllerGyro, chkControllerTouch, chkControllerBack;
        private ComboBox cmbControllerType;
        private Button btnControllerSave;
        private Label lblControllerStatus;
        private Game controllerOriginal;
        private GameProfile controllerOriginalProfile;
        private sealed record LibraryRow(Guid Id, string Label) { public override string ToString() => Label; }
        private sealed record AccountChoice(string Id, string Label)
        {
            public override string ToString() => Label;
        }

        // Colors
        private Color bgDark = Color.FromArgb(28, 28, 30);
        private Color bgPanel = Color.FromArgb(36, 36, 40);
        private Color bgInput = Color.FromArgb(44, 44, 48);
        private Color textLight = Color.FromArgb(240, 240, 240);
        private Color textMuted = Color.FromArgb(170, 170, 170);
        private Color accentBlue = Color.FromArgb(0, 122, 204);
        private Color accentGreen = Color.FromArgb(46, 125, 50);
        private Color accentRed = Color.FromArgb(198, 40, 40);

        public SettingsForm() : this(Program.Artwork) { }

        internal SettingsForm(SBridge.Artwork.ArtworkService artworkService)
        {
            this.artworkService = artworkService;
            this.Text = "sBridge Settings";
            this.Size = new Size(680, 540);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.BackColor = bgDark;
            this.ForeColor = textLight;

            try
            {
                string exeDir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
                string iconPath = Path.Combine(exeDir, "sBridge.ico");
                if (File.Exists(iconPath))
                {
                    this.Icon = new Icon(iconPath);
                }
            }
            catch {}

            InitializeComponents();
            LoadPaths();
            RefreshShortcutsList();
            ReloadLibrary();
            ReloadControllerGames();
        }

        private void InitializeComponents()
        {
            // Title
            Label lblTitle = new Label();
            lblTitle.Text = "sBridge Settings";
            lblTitle.Font = new Font("Segoe UI", 14, FontStyle.Bold);
            lblTitle.ForeColor = Color.White;
            lblTitle.Location = new Point(15, 15);
            lblTitle.Size = new Size(400, 30);
            this.Controls.Add(lblTitle);
            lblArtworkStatus = new Label { Name = "ArtworkStatus", ForeColor = textMuted, Font = new Font("Segoe UI", 8),
                Location = new Point(305, 20), Size = new Size(340, 25), Text = "" };
            this.Controls.Add(lblArtworkStatus);

            // Tab Control
            tabControl = new TabControl();
            tabControl.Location = new Point(15, 50);
            tabControl.Size = new Size(635, 380);
            tabControl.Font = new Font("Segoe UI", 9, FontStyle.Regular);
            tabControl.Multiline = true;
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
            lstGames.Columns.Add("Account", 100);
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
            pageUwp = new TabPage("Add Xbox / Store Games");
            pageUwp.BackColor = bgPanel;
            tabControl.TabPages.Add(pageUwp);

            // Tab 2 Content: Status and Scan button
            lblUwpStatus = new Label();
            lblUwpStatus.Text = "Scan installed Xbox / Microsoft Store packaged games.";
            lblUwpStatus.Font = new Font("Segoe UI", 9, FontStyle.Regular);
            lblUwpStatus.Location = new Point(15, 15);
            lblUwpStatus.Size = new Size(440, 20);
            lblUwpStatus.ForeColor = textMuted;
            pageUwp.Controls.Add(lblUwpStatus);

            btnScanUWP = new Button();
            btnScanUWP.Text = "Scan Packaged Games";
            btnScanUWP.Font = new Font("Segoe UI", 9, FontStyle.Bold);
            btnScanUWP.FlatStyle = FlatStyle.Flat;
            btnScanUWP.FlatAppearance.BorderSize = 0;
            btnScanUWP.BackColor = accentBlue;
            btnScanUWP.ForeColor = Color.White;
            btnScanUWP.Location = new Point(465, 10);
            btnScanUWP.Size = new Size(145, 25);
            btnScanUWP.Click += async (s, e) =>
            {
                if (scanCancellation != null) { scanCancellation.Cancel(); return; }
                scanTask = PerformScanAsync(Program.XboxGames, lblUwpStatus, btnScanUWP, btnAddSelected, lstApps, "Scan Packaged Games");
                await scanTask;
            };
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
            btnAddSelected.Click += (s, e) => AddSelectedToSteam(lstApps);
            pageUwp.Controls.Add(btnAddSelected);

            CreateEpicPage();

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
            txtCustomName.Name = "CustomGameName";
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
            txtCustomPath.Name = "CustomGamePath";
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
            txtCustomArgs.Name = "CustomGameArguments";
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
                if (!isUpdatingUi) SavePaths();
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
            txtSgdbKey.UseSystemPasswordChar = true;
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
            chkManagedSisr = new CheckBox { Name = "ManagedSisrStartup", Text = "Managed SISR startup (owned config + API readiness, v0.6.1+)",
                Font = new Font("Segoe UI", 9), Location = new Point(15, 225), Size = new Size(605, 20), ForeColor = textLight };
            chkManagedSisr.CheckedChanged += (s, e) => { if (!isUpdatingUi) SavePaths(); };
            pageSettings.Controls.Add(chkManagedSisr);

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
            CreateSteamAccountsPage();
            CreateDiagnosticsPage();
            CreateLibraryPage();
            CreateControllerPage();

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
            isUpdatingUi = true;
            chkEnableSisr.Checked = Program.Settings.SisrEnabled;
            txtSisr.Text = Program.Settings.SisrPath;
            txtSisrArgs.Text = Program.Settings.SisrArguments;
            txtSgdbKey.Text = Program.Settings.SteamGridDbApiKey;
            chkManagedSisr.Checked = Program.Settings.ManagedSisrStartup;
            RefreshSteamAccounts();
            UpdateSisrStatus();
            isUpdatingUi = false;
        }

        private bool SavePaths()
        {
            if (isUpdatingUi) return true;
            Program.Settings.SisrEnabled = chkEnableSisr.Checked;
            Program.Settings.ManagedSisrStartup = chkManagedSisr.Checked;
            Program.Settings.SisrPath = txtSisr.Text.Trim();
            Program.Settings.SisrArguments = txtSisrArgs.Text.Trim();
            Program.Settings.SteamGridDbApiKey = txtSgdbKey.Text.Trim();
            return Program.SaveConfig();
        }

        private void SavePathsAndClose()
        {
            SavePaths();
            this.Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            var pending = new List<Task>();
            if (scanTask != null && !scanTask.IsCompleted) pending.Add(scanTask);
            if (diagnosticsTask != null && !diagnosticsTask.IsCompleted) pending.Add(diagnosticsTask);
            foreach (var task in artworkTasks) if (!task.IsCompleted) pending.Add(task);
            if (pending.Count != 0)
            {
                e.Cancel = true;
                if (!closingAfterScan)
                {
                    closingAfterScan = true;
                    scanCancellation?.Cancel();
                    artworkCancellation.Cancel();
                    _ = CloseAfterScanAsync(Task.WhenAll(pending));
                }
                return;
            }
            SavePaths();
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !artworkDisposalStarted)
            {
                artworkDisposalStarted = true;
                artworkCancellation.Cancel();
                _ = DisposeArtworkAsync();
            }
            base.Dispose(disposing);
        }

        private async Task DisposeArtworkAsync()
        {
            try { await Task.WhenAll(artworkTasks).ConfigureAwait(false); }
            finally { artworkCancellation.Dispose(); artworkSlots.Dispose(); }
        }

        private void QueueArtwork(SBridge.Artwork.ArtworkRequest request)
        {
            artworkTasks.RemoveAll(task => task.IsCompleted);
            if (closingAfterScan || artworkTasks.Count >= 32)
            {
                artworkWarnings++;
                Program.Log("Artwork queue is full or closing; shortcut saved without artwork: " + request.Name);
                UpdateArtworkStatus(); return;
            }
            // Snapshot the in-memory key for this job; never persist or log it.
            artworkPending++;
            artworkTasks.Add(RunArtworkAsync(request, Program.Settings.SteamGridDbApiKey));
            UpdateArtworkStatus();
        }

        private async Task RunArtworkAsync(SBridge.Artwork.ArtworkRequest request, string apiKey)
        {
            bool acquired = false;
            try
            {
                await artworkSlots.WaitAsync(artworkCancellation.Token); acquired = true;
                var result = await Task.Run(() => artworkService.DownloadAsync(request, apiKey, artworkCancellation.Token));
                foreach (string warning in result.Warnings) Program.Log("Artwork " + request.Name + ": " + warning);
                if (result.Warnings.Length != 0 && !result.Cancelled) artworkWarnings++;
                if (!closingAfterScan && !IsDisposed && !Disposing && !result.Cancelled && result.IconPath != null)
                {
                    var save = Program.CompleteArtworkIcon(request, result.IconPath);
                    if (save.Succeeded) RefreshShortcutsList();
                    else { artworkWarnings++; Program.Log("Artwork icon metadata not saved: " + save.Error); }
                }
                Program.Log("Artwork job finished: " + request.Name + ", assets=" + result.Saved + ", cancelled=" + result.Cancelled);
            }
            catch (OperationCanceledException) { Program.Log("Queued artwork cancelled: " + request.Name); }
            catch (Exception) { artworkWarnings++; Program.Log("Artwork job failed; shortcut remains saved: " + request.Name); }
            finally
            {
                if (acquired) artworkSlots.Release();
                artworkFinished++;
                artworkPending--;
                if (!IsDisposed && !Disposing) UpdateArtworkStatus();
            }
        }

        private void UpdateArtworkStatus()
        {
            lblArtworkStatus.Text = string.Format("Artwork: {0} pending, {1} finished, {2} warnings", artworkPending, artworkFinished, artworkWarnings);
        }

        private async Task CloseAfterScanAsync(Task pendingScan)
        {
            // Keep pumping the UI while the owned command/pipes/temp script are
            // cleaned up, then close once instead of abandoning a background scan.
            await pendingScan;
            if (!IsDisposed && !Disposing) Close();
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
            Program.Settings.SisrPath = txtSisr.Text.Trim();

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
                        targetStr = Program.ShortcutTarget(item.LaunchOptions);
                    }
                    else
                    {
                        targetStr = Path.GetFileName(trimmedExe);
                    }
                    lvItem.SubItems.Add(targetStr);
                    
                    string statusStr = "Direct UWP";
                    if (isBridge)
                    {
                        string gameId = Program.ShortcutProfileKey(item.LaunchOptions);
                        bool gameSisr = Program.Settings.IsSisrEnabledFor(gameId);
                        statusStr = gameId.Length == 0 ? "Missing registration" : gameSisr ? "SISR Enabled" : "SISR Disabled";
                    }
                    else if (isUWPHook)
                    {
                        statusStr = "Via UWPHook (migrate)";
                    }

                    lvItem.UseItemStyleForSubItems = false;
                    var subItem = lvItem.SubItems.Add(statusStr);
                    lvItem.SubItems.Add(Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(item.VdfPath))));
                    lvItem.ToolTipText = item.VdfPath;
                    lstGames.ShowItemToolTips = true;
                    if (isBridge)
                    {
                        string gameId = Program.ShortcutProfileKey(item.LaunchOptions);
                        bool gameSisr = Program.Settings.IsSisrEnabledFor(gameId);
                        subItem.ForeColor = gameId.Length == 0 ? accentRed : gameSisr ? accentGreen : accentRed;
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
            IReadOnlyList<SteamAccount> selectedAccounts;
            if (!TrySelectedAccounts(out selectedAccounts)) return;
            var selectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var account in selectedAccounts) selectedPaths.Add(account.ShortcutPath);
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
                if (!selectedPaths.Contains(item.VdfPath)) continue;
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
                ReportShortcutSaveResults(Program.SaveSteamShortcuts(modifiedItems),
                    string.Format("Successfully migrated {0} shortcuts from UWPHook to sBridge!", count));
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

                string gameId = Program.ShortcutProfileKey(shortcut.LaunchOptions);
                if (string.IsNullOrEmpty(gameId)) continue;

                bool gameSisr = !Program.Settings.IsSisrEnabledFor(gameId);
                Program.Settings.SetSteamInputMode(gameId, gameSisr ? SteamInputMode.Enabled : SteamInputMode.Disabled);
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
                    string gameId = Program.ShortcutProfileKey(shortcut.LaunchOptions);
                    if (!string.IsNullOrEmpty(gameId))
                    {
                        isUpdatingUi = true;
                        cmbGameSisr.SelectedIndex = (int)Program.Settings.GetProfile(gameId).SteamInput;
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
            var modifiedRoots = new Dictionary<string, SteamShortcutDocument>(StringComparer.OrdinalIgnoreCase);
            int removed = 0;

            foreach (ListViewItem lvItem in lstGames.Items)
            {
                var item = lvItem.Tag as SteamShortcutItem;
                if (lvItem.Checked && item != null)
                {
                    Program.RemoveShortcutFromSteam(item.RootElement, item.ShortcutElement);
                    if (!modifiedRoots.ContainsKey(item.VdfPath))
                    {
                        modifiedRoots[item.VdfPath] = item.Document;
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
                    itemsToSave.Add(new SteamShortcutItem { VdfPath = pair.Key, RootElement = pair.Value.Root, Document = pair.Value });
                }
                ReportShortcutSaveResults(Program.SaveSteamShortcuts(itemsToSave),
                    string.Format("Successfully removed {0} shortcut(s) from Steam.", removed));
            }

            RefreshShortcutsList();
        }

        private void CreateLibraryPage()
        {
            var page = new TabPage("Registered Games") { BackColor = bgPanel };
            tabControl.TabPages.Add(page);
            page.Controls.Add(new Label { Text = "Local launch registry. Edits apply after Save; existing Steam names/AppIDs/artwork are retained.",
                ForeColor = textLight, Font = new Font("Segoe UI", 9), Location = new Point(15, 8), Size = new Size(595, 30) });
            lstLibrary = new ListBox { Name = "RegisteredGames", Font = new Font("Segoe UI", 9), BackColor = bgInput, ForeColor = Color.White,
                Location = new Point(15, 40), Size = new Size(195, 240), HorizontalScrollbar = true };
            lstLibrary.SelectedIndexChanged += (s, e) => LoadLibrarySelection(); page.Controls.Add(lstLibrary);
            TextBox Field(string name, string label, int y, bool multiline = false)
            {
                page.Controls.Add(new Label { Text = label, ForeColor = textLight, Font = new Font("Segoe UI", 9), Location = new Point(220, y + 3), Size = new Size(90, 20) });
                var field = new TextBox { Name = name, Font = new Font("Segoe UI", 9), BackColor = bgInput, ForeColor = Color.White,
                    Location = new Point(310, y), Size = new Size(300, multiline ? 36 : 23), Multiline = multiline };
                page.Controls.Add(field); return field;
            }
            txtLibraryName = Field("LibraryName", "Name", 40);
            txtLibraryTarget = Field("LibraryTarget", "Target", 68);
            txtLibraryArguments = Field("LibraryArguments", "Arguments", 96, true);
            txtLibraryHint = Field("LibraryHint", "Process hint", 137);
            txtLibraryInstall = Field("LibraryInstall", "Install folder", 165);
            txtLibraryWatch = Field("LibraryWatch", "Watch override", 193);
            page.Controls.Add(new Label { Text = "SISR", ForeColor = textLight, Font = new Font("Segoe UI", 9), Location = new Point(220, 225), Size = new Size(90, 20) });
            cmbLibrarySisr = new ComboBox { Name = "LibrarySisr", DropDownStyle = ComboBoxStyle.DropDownList, BackColor = bgInput, ForeColor = Color.White,
                Location = new Point(310, 221), Size = new Size(300, 23) };
            cmbLibrarySisr.Items.AddRange(new object[] { "Automatic (global)", "Enabled", "Disabled" }); page.Controls.Add(cmbLibrarySisr);
            lblLibraryIdentity = new Label { Name = "LibraryIdentity", ForeColor = textMuted, Font = new Font("Segoe UI", 8), Location = new Point(220, 249), Size = new Size(390, 30) };
            page.Controls.Add(lblLibraryIdentity);
            btnLibrarySave = new Button { Text = "Save Game", Name = "SaveLibraryGame", FlatStyle = FlatStyle.Flat, BackColor = accentGreen, ForeColor = Color.White,
                Location = new Point(310, 281), Size = new Size(145, 28), Enabled = false };
            btnLibrarySave.Click += (s, e) => SaveLibraryGame(); page.Controls.Add(btnLibrarySave);
            var reload = new Button { Text = "Reload Games", FlatStyle = FlatStyle.Flat, BackColor = accentBlue, ForeColor = Color.White,
                Location = new Point(15, 281), Size = new Size(195, 28) };
            reload.Click += (s, e) => ReloadLibrary(); page.Controls.Add(reload);
            lblLibraryStatus = new Label { Name = "LibraryStatus", ForeColor = textMuted, Font = new Font("Segoe UI", 8), Location = new Point(465, 280), Size = new Size(145, 34) };
            page.Controls.Add(lblLibraryStatus);
        }

        private void ReloadLibrary(Guid? selected = null)
        {
            lstLibrary.Items.Clear();
            var games = new List<Game>(Program.Settings.Games.Values);
            games.Sort((left, right) => { int name = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name); return name != 0 ? name : left.Id.CompareTo(right.Id); });
            foreach (var game in games)
            {
                int index = lstLibrary.Items.Add(new LibraryRow(game.Id, game.Name + " [" + game.Provider + "; " + game.Id.ToString("N").Substring(0, 8) + "]"));
                if (selected == game.Id) lstLibrary.SelectedIndex = index;
            }
            if (lstLibrary.SelectedIndex < 0) LoadLibrarySelection();
        }

        private void LoadLibrarySelection()
        {
            libraryOriginal = null; libraryOriginalProfile = null;
            if (lstLibrary.SelectedItem is LibraryRow row) Program.Settings.Games.TryGetValue(row.Id, out libraryOriginal);
            bool available = libraryOriginal != null;
            foreach (var control in new Control[] { txtLibraryName, txtLibraryTarget, txtLibraryArguments, txtLibraryHint, txtLibraryInstall, txtLibraryWatch, cmbLibrarySisr, btnLibrarySave }) control.Enabled = available;
            if (!available)
            {
                foreach (var field in new[] { txtLibraryName, txtLibraryTarget, txtLibraryArguments, txtLibraryHint, txtLibraryInstall, txtLibraryWatch }) field.Text = "";
                cmbLibrarySisr.SelectedIndex = -1; lblLibraryIdentity.Text = "Select a registered game, or import one first."; lblLibraryStatus.Text = ""; return;
            }
            var game = libraryOriginal; libraryOriginalProfile = Program.Settings.GetProfile(game.ProfileKey);
            txtLibraryName.Text = game.Name; txtLibraryTarget.Text = game.Target; txtLibraryArguments.Text = WindowsCommandLine.Join(game.Arguments);
            txtLibraryHint.Text = game.ProcessHint; txtLibraryInstall.Text = game.InstallDirectory ?? ""; txtLibraryWatch.Text = libraryOriginalProfile.WatchProcess;
            cmbLibrarySisr.SelectedIndex = (int)libraryOriginalProfile.SteamInput;
            txtLibraryTarget.ReadOnly = game.LaunchKind == GameLaunchKind.EpicLauncher;
            txtLibraryArguments.ReadOnly = game.LaunchKind == GameLaunchKind.EpicLauncher;
            lblLibraryIdentity.Text = game.LaunchKind + " | " + game.Id.ToString("D");
            lblLibraryStatus.Text = game.LaunchKind == GameLaunchKind.EpicLauncher ? "Epic args are configured in its launcher." : "Draft: click Save to apply.";
        }

        private void SaveLibraryGame()
        {
            if (libraryOriginal == null) return;
            try
            {
                string target = txtLibraryTarget.Text.Trim(), install = txtLibraryInstall.Text.Trim();
                if (libraryOriginal.LaunchKind == GameLaunchKind.Executable)
                {
                    if (!Path.IsPathFullyQualified(target)) throw new ArgumentException("Executable targets must be absolute Windows paths.");
                    target = Path.GetFullPath(target);
                }
                else if (libraryOriginal.LaunchKind == GameLaunchKind.PackagedApplication)
                {
                    int separator = target.IndexOf('!');
                    if (separator <= 0 || separator != target.LastIndexOf('!') || separator == target.Length - 1 || target.IndexOfAny(new[] { ' ', '\t', '\r', '\n', '\\', '/' }) >= 0)
                        throw new ArgumentException("Packaged targets require a package-family!application identity.");
                }
                if (install.Length != 0)
                {
                    if (!Path.IsPathFullyQualified(install)) throw new ArgumentException("Install folders must be absolute Windows paths or empty.");
                    install = Path.GetFullPath(install);
                }
                var definition = GameLibraryEditor.Definition(libraryOriginal, txtLibraryName.Text.Trim(), target,
                    new List<string>(WindowsCommandLine.Split(txtLibraryArguments.Text)).ToArray(), txtLibraryHint.Text.Trim(), install.Length == 0 ? null : install);
                var profile = libraryOriginalProfile with { SteamInput = (SteamInputMode)cmbLibrarySisr.SelectedIndex, WatchProcess = txtLibraryWatch.Text.Trim() };
                if (!Program.TryEditGame(libraryOriginal, libraryOriginalProfile, definition, profile)) return;
                ReloadLibrary(definition.Id); RefreshShortcutsList();
                lblLibraryStatus.Text = "Saved. UUID retained.";
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException)
            { MessageBox.Show(ex.Message, "Registered Game Edit", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        }

        private void CreateControllerPage()
        {
            var page = new TabPage("Controller Profiles") { BackColor = bgPanel }; tabControl.TabPages.Add(page);
            page.Controls.Add(new Label { Text = "Per-game SISR emulation options. Requires managed startup; Steam bindings stay configured in Steam.",
                ForeColor = textLight, Font = new Font("Segoe UI", 9), Location = new Point(15, 8), Size = new Size(595, 35) });
            lstControllerGames = new ListBox { Name = "ControllerGames", Font = new Font("Segoe UI", 9), BackColor = bgInput, ForeColor = Color.White,
                Location = new Point(15, 45), Size = new Size(195, 230), HorizontalScrollbar = true };
            lstControllerGames.SelectedIndexChanged += (s, e) => LoadControllerSelection(); page.Controls.Add(lstControllerGames);
            CheckBox Option(string name, string text, int y)
            {
                var control = new CheckBox { Name = name, Text = text, ForeColor = textLight, Font = new Font("Segoe UI", 9),
                    Location = new Point(230, y), Size = new Size(380, 25) }; page.Controls.Add(control); return control;
            }
            chkControllerOverride = Option("ControllerOverride", "Override controller options for this game", 45);
            cmbControllerType = new ComboBox { Name = "ControllerType", DropDownStyle = ComboBoxStyle.DropDownList, BackColor = bgInput, ForeColor = Color.White,
                Location = new Point(230, 82), Size = new Size(370, 25) };
            cmbControllerType.Items.AddRange(new object[] { "Xbox 360", "DualShock 4", "DualSense", "DualSense Edge", "Switch 2 Pro" }); page.Controls.Add(cmbControllerType);
            chkControllerGyro = Option("ControllerGyro", "Gyro passthrough", 122);
            chkControllerTouch = Option("ControllerTouch", "Touchpad passthrough", 157);
            chkControllerBack = Option("ControllerBack", "Back-button passthrough (supported controllers)", 192);
            chkControllerOverride.CheckedChanged += (s, e) => UpdateControllerInputs();
            lblControllerStatus = new Label { Name = "ControllerProfileStatus", ForeColor = textMuted, Font = new Font("Segoe UI", 9),
                Location = new Point(230, 227), Size = new Size(370, 45) }; page.Controls.Add(lblControllerStatus);
            var reload = new Button { Text = "Reload Profiles", FlatStyle = FlatStyle.Flat, BackColor = accentBlue, ForeColor = Color.White,
                Location = new Point(15, 281), Size = new Size(195, 28) };
            reload.Click += (s, e) => ReloadControllerGames(); page.Controls.Add(reload);
            btnControllerSave = new Button { Name = "SaveControllerProfile", Text = "Save Profile", FlatStyle = FlatStyle.Flat, BackColor = accentGreen, ForeColor = Color.White,
                Location = new Point(230, 281), Size = new Size(200, 28), Enabled = false };
            btnControllerSave.Click += (s, e) => SaveControllerProfile(); page.Controls.Add(btnControllerSave);
        }

        private void ReloadControllerGames(Guid? selected = null)
        {
            lstControllerGames.Items.Clear(); var games = new List<Game>(Program.Settings.Games.Values);
            games.Sort((left, right) => { int name = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name); return name != 0 ? name : left.Id.CompareTo(right.Id); });
            foreach (var game in games)
            {
                int index = lstControllerGames.Items.Add(new LibraryRow(game.Id, game.Name + " [" + game.Provider + "; " + game.Id.ToString("N").Substring(0, 8) + "]"));
                if (selected == game.Id) lstControllerGames.SelectedIndex = index;
            }
            if (lstControllerGames.SelectedIndex < 0) LoadControllerSelection();
        }

        private void LoadControllerSelection()
        {
            controllerOriginal = null; controllerOriginalProfile = null;
            if (lstControllerGames.SelectedItem is LibraryRow row) Program.Settings.Games.TryGetValue(row.Id, out controllerOriginal);
            var profile = controllerOriginal == null ? new GameProfile() : Program.Settings.GetProfile(controllerOriginal.ProfileKey);
            controllerOriginalProfile = profile;
            var options = profile.Controller ?? new SisrControllerProfile();
            chkControllerOverride.Checked = profile.Controller != null; cmbControllerType.SelectedIndex = (int)options.ControllerType;
            chkControllerGyro.Checked = options.GyroPassthrough; chkControllerTouch.Checked = options.TouchpadPassthrough; chkControllerBack.Checked = options.BackButtonPassthrough;
            UpdateControllerInputs();
            lblControllerStatus.Text = controllerOriginal == null ? "Select a registered game." :
                "Draft: Save Profile to apply. Inherit leaves SISR's defaults/advanced controller options unchanged.";
        }

        private void UpdateControllerInputs()
        {
            bool selected = controllerOriginal != null; chkControllerOverride.Enabled = selected;
            foreach (var control in new Control[] { cmbControllerType, chkControllerGyro, chkControllerTouch, chkControllerBack }) control.Enabled = selected && chkControllerOverride.Checked;
            btnControllerSave.Enabled = selected;
        }

        private void SaveControllerProfile()
        {
            if (controllerOriginal == null) return;
            var options = chkControllerOverride.Checked ? new SisrControllerProfile((SisrControllerType)cmbControllerType.SelectedIndex,
                chkControllerGyro.Checked, chkControllerTouch.Checked, chkControllerBack.Checked) : null;
            var profile = controllerOriginalProfile with { Controller = options };
            if (!Program.TryEditGame(controllerOriginal, controllerOriginalProfile, controllerOriginal, profile)) return;
            ReloadControllerGames(controllerOriginal.Id); lblControllerStatus.Text = options == null ? "Saved: inheriting controller options." : "Saved. Managed SISR startup is required when integration is enabled.";
        }

        private void CreateDiagnosticsPage()
        {
            var page = new TabPage("Diagnostics") { BackColor = bgPanel };
            tabControl.TabPages.Add(page);
            page.Controls.Add(new Label { Text = "Read-only status summary. Refresh does not start SISR, change Steam, or contact the network.",
                ForeColor = textLight, Font = new Font("Segoe UI", 9), Location = new Point(15, 15), Size = new Size(595, 35) });
            txtDiagnostics = new TextBox { Name = "DiagnosticReport", ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical,
                Font = new Font("Consolas", 9), BackColor = bgInput, ForeColor = Color.White, Location = new Point(15, 55), Size = new Size(595, 220),
                Text = "Click Refresh Diagnostics to collect a safe summary." };
            btnRefreshDiagnostics = new Button { Text = "Refresh Diagnostics", FlatStyle = FlatStyle.Flat, BackColor = accentBlue, ForeColor = Color.White,
                Location = new Point(15, 285), Size = new Size(220, 30) };
            btnCopyDiagnostics = new Button { Text = "Copy Diagnostics", FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(60, 60, 64), ForeColor = Color.White,
                Location = new Point(250, 285), Size = new Size(220, 30), Enabled = false };
            btnRefreshDiagnostics.Click += async (s, e) => { diagnosticsTask = RefreshDiagnosticsAsync(); await diagnosticsTask; };
            btnCopyDiagnostics.Click += (s, e) =>
            {
                if (diagnosticReport == null) return;
                try { Clipboard.SetText(diagnosticReport); }
                catch (System.Runtime.InteropServices.ExternalException) { txtDiagnostics.Text = diagnosticReport + "\r\nClipboard is busy; select and copy this text manually."; }
            };
            page.Controls.AddRange(new Control[] { txtDiagnostics, btnRefreshDiagnostics, btnCopyDiagnostics });
        }

        private async Task RefreshDiagnosticsAsync()
        {
            btnRefreshDiagnostics.Enabled = false; btnCopyDiagnostics.Enabled = false;
            txtDiagnostics.Text = "Collecting read-only diagnostic summary...";
            var settings = Program.Settings.Clone();
            try
            {
                var snapshot = await Task.Run(() => Program.CollectDiagnostics(settings));
                diagnosticReport = snapshot.SafeReport();
                if (!IsDisposed && !Disposing && !closingAfterScan) { txtDiagnostics.Text = diagnosticReport; btnCopyDiagnostics.Enabled = true; }
            }
            catch (Exception)
            {
                diagnosticReport = null;
                if (!IsDisposed && !Disposing) txtDiagnostics.Text = "Diagnostics could not be collected. No integration state was changed.";
            }
            finally { if (!IsDisposed && !Disposing) btnRefreshDiagnostics.Enabled = true; }
        }

        private void CreateSteamAccountsPage()
        {
            var page = new TabPage("Steam Accounts") { BackColor = bgPanel };
            tabControl.TabPages.Add(page);
            page.Controls.Add(new Label { Text = "Choose accounts for imports and UWPHook migration. No selection means no writes.",
                ForeColor = textLight, Font = new Font("Segoe UI", 9), Location = new Point(15, 15), Size = new Size(595, 35) });
            lstSteamAccounts = new CheckedListBox { CheckOnClick = true, BackColor = bgInput, ForeColor = Color.White,
                Font = new Font("Segoe UI", 9), Location = new Point(15, 55), Size = new Size(595, 180) };
            lstSteamAccounts.ItemCheck += (s, e) =>
            {
                if (isUpdatingUi) return;
                var choice = (AccountChoice)lstSteamAccounts.Items[e.Index];
                if (e.NewValue == CheckState.Checked) Program.Settings.SelectedSteamAccountIds.Add(choice.Id);
                else Program.Settings.SelectedSteamAccountIds.Remove(choice.Id);
                SavePaths();
            };
            page.Controls.Add(lstSteamAccounts);
            var refresh = new Button { Text = "Refresh Accounts", Font = new Font("Segoe UI", 9, FontStyle.Bold), FlatStyle = FlatStyle.Flat,
                BackColor = accentBlue, ForeColor = Color.White, Location = new Point(15, 245), Size = new Size(220, 30) };
            refresh.Click += (s, e) => RefreshSteamAccounts(); page.Controls.Add(refresh);
            page.Controls.Add(new Label { Text = "Accounts with no shortcuts.vdf can receive their first import.\nRemoval affects only checked shortcut rows and their displayed account.",
                ForeColor = textMuted, Font = new Font("Segoe UI", 9), Location = new Point(15, 285), Size = new Size(595, 40) });
        }

        private void RefreshSteamAccounts()
        {
            bool wasUpdating = isUpdatingUi; isUpdatingUi = true;
            try
            {
                lstSteamAccounts.Items.Clear();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var account in Program.FindSteamAccounts())
                {
                    seen.Add(account.Id);
                    lstSteamAccounts.Items.Add(new AccountChoice(account.Id, account.DisplayName), Program.Settings.SelectedSteamAccountIds.Contains(account.Id));
                }
                foreach (string id in Program.Settings.SelectedSteamAccountIds)
                    if (!seen.Contains(id)) lstSteamAccounts.Items.Add(new AccountChoice(id, "Unavailable account " + id + " (uncheck or restore it)"), true);
            }
            finally { isUpdatingUi = wasUpdating; }
        }

        private bool TrySelectedAccounts(out IReadOnlyList<SteamAccount> accounts)
        {
            try { accounts = Program.SelectedSteamAccounts(); return true; }
            catch (InvalidOperationException ex)
            {
                accounts = Array.Empty<SteamAccount>();
                MessageBox.Show(ex.Message, "Steam Account Selection", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
        }

        private void CreateEpicPage()
        {
            var page = new TabPage("Add Epic Games") { BackColor = bgPanel };
            tabControl.TabPages.Add(page);
            lblEpicStatus = new Label { Text = "Scan installed Epic Launcher games (.item manifests).", Font = new Font("Segoe UI", 9),
                Location = new Point(15, 15), Size = new Size(440, 20), ForeColor = textMuted };
            btnScanEpic = new Button { Text = "Scan Epic Games", Font = new Font("Segoe UI", 9, FontStyle.Bold), FlatStyle = FlatStyle.Flat,
                BackColor = accentBlue, ForeColor = Color.White, Location = new Point(465, 10), Size = new Size(145, 25) };
            btnScanEpic.FlatAppearance.BorderSize = 0;
            btnScanEpic.Click += async (s, e) =>
            {
                if (scanCancellation != null) { scanCancellation.Cancel(); return; }
                scanTask = PerformScanAsync(Program.EpicGames, lblEpicStatus, btnScanEpic, btnAddEpic, lstEpicGames, "Scan Epic Games");
                await scanTask;
            };
            lstEpicGames = new ListView { View = View.Details, CheckBoxes = true, FullRowSelect = true, GridLines = false,
                BackColor = bgInput, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 9),
                Location = new Point(15, 45), Size = new Size(595, 200) };
            lstEpicGames.Columns.Add("Game Name", 220);
            lstEpicGames.Columns.Add("Epic Catalog Identity", 230);
            lstEpicGames.Columns.Add("Status", 130);
            btnAddEpic = new Button { Text = "Add Selected Epic to Steam", Font = new Font("Segoe UI", 9, FontStyle.Bold), FlatStyle = FlatStyle.Flat,
                BackColor = accentGreen, ForeColor = Color.White, Location = new Point(15, 255), Size = new Size(300, 30) };
            btnAddEpic.FlatAppearance.BorderSize = 0;
            btnAddEpic.Click += (s, e) => AddSelectedToSteam(lstEpicGames);
            var note = new Label { Text = "Launches through Epic Launcher. Configure extra game arguments there.", Font = new Font("Segoe UI", 9),
                ForeColor = textMuted, Location = new Point(15, 295), Size = new Size(595, 40) };
            page.Controls.AddRange(new Control[] { lblEpicStatus, btnScanEpic, lstEpicGames, btnAddEpic, note });
        }

        private async Task PerformScanAsync(IGameProvider provider, Label status, Button scanButton, Button addButton, ListView apps, string scanText)
        {
            if (scanCancellation != null)
            {
                scanCancellation.Cancel();
                return;
            }
            var cancellation = new CancellationTokenSource();
            scanCancellation = cancellation;
            status.Text = "Scanning games... you can cancel or keep using settings.";
            status.ForeColor = Color.FromArgb(255, 193, 7);
            scanButton.Text = "Cancel Scan";
            btnScanUWP.Enabled = scanButton == btnScanUWP;
            btnScanEpic.Enabled = scanButton == btnScanEpic;
            addButton.Enabled = false;
            try
            {
                var result = await provider.DiscoverAsync(cancellation.Token);
                if (IsDisposed || Disposing) return;
                cancellation.Token.ThrowIfCancellationRequested();
                var scannedApps = result.Games;
                var existingAumids = new HashSet<string>(provider.Id == "epic" ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
                foreach (var shortcut in currentShortcuts)
                    if (!string.IsNullOrEmpty(shortcut.LaunchOptions)) existingAumids.Add(Program.ShortcutTarget(shortcut.LaunchOptions));
                apps.BeginUpdate();
                try
                {
                    apps.Items.Clear();
                    foreach (var app in scannedApps)
                    {
                        bool alreadyInSteam = existingAumids.Contains(app.Target);
                        var item = new ListViewItem(app.Name);
                        item.SubItems.Add(app.ProviderId);
                        item.SubItems.Add(alreadyInSteam ? "In some account(s)" : "Not added");
                        item.Tag = app; item.Checked = false; apps.Items.Add(item);
                    }
                }
                finally { apps.EndUpdate(); }
                foreach (string warning in result.Warnings) Program.Log(provider.Id + " discovery warning: " + warning);
                status.Text = string.Format("Found {0} apps. Select games to add.{1}", scannedApps.Count,
                    result.Warnings.Count == 0 ? "" : " Some manifests/hints were unavailable; see log.");
                status.ForeColor = result.Warnings.Count == 0 ? accentGreen : Color.FromArgb(255, 193, 7);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                if (!IsDisposed && !Disposing) { status.Text = "Scan cancelled; previous results retained."; status.ForeColor = textMuted; }
            }
            catch (Exception ex)
            {
                Program.Log(provider.Id + " discovery failed: " + ex.Message);
                if (!IsDisposed && !Disposing) { status.Text = "Scan failed; previous results retained. " + ex.Message; status.ForeColor = accentRed; }
            }
            finally
            {
                scanCancellation = null; cancellation.Dispose();
                if (!IsDisposed && !Disposing)
                {
                    scanButton.Text = scanText; addButton.Enabled = true;
                    btnScanUWP.Enabled = true; btnScanEpic.Enabled = true;
                }
            }
        }

        private void AddSelectedToSteam(ListView apps)
        {
            if (scanCancellation != null) return;
            if (!SavePaths()) return; // Commit global settings before registering a shortcut target.

            // Check Steam is not running
            bool steamRunning = Process.GetProcessesByName("steam").Length > 0;
            if (steamRunning)
            {
                MessageBox.Show(
                    "Steam is currently running. Please close Steam before adding shortcuts.",
                    "Steam is Running", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            IReadOnlyList<SteamAccount> accounts;
            if (!TrySelectedAccounts(out accounts)) return;

            var definitions = new List<Game>();
            var choices = new List<GameProfile>();
            foreach (ListViewItem lvItem in apps.Items)
            {
                var app = lvItem.Tag as DiscoveredGame;
                if (lvItem.Checked && app != null)
                {
                    definitions.Add(app.CreateRegistration());
                    choices.Add(null); // Rediscovery retains the UUID profile; deduplication is per selected account.
                }
            }

            if (definitions.Count > 0)
            {
                List<Game> registered;
                if (!Program.TryRegisterGames(definitions, choices, out registered)) return;
                ReportShortcutSaveResults(Program.ImportGamesToSteam(accounts, registered, QueueArtwork),
                    string.Format("Imported or retained {0} game(s) in {1} selected account(s).\n\nRestart Steam to see changes.", registered.Count, accounts.Count));
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

            string gameId = Program.ShortcutProfileKey(shortcut.LaunchOptions);

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
                cmbGameSisr.Enabled = false;
                txtGameWatch.Enabled = false;
            }
            else
            {
                var profile = Program.Settings.GetProfile(gameId);
                cmbGameSisr.SelectedIndex = (int)profile.SteamInput;
                txtGameWatch.Text = profile.WatchProcess;
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

                string gameId = Program.ShortcutProfileKey(shortcut.LaunchOptions);
                if (string.IsNullOrEmpty(gameId)) continue;

                Program.Settings.SetWatchProcess(gameId, txtGameWatch.Text.Trim());
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

                string gameId = Program.ShortcutProfileKey(shortcut.LaunchOptions);
                if (string.IsNullOrEmpty(gameId)) continue;

                int sel = cmbGameSisr.SelectedIndex;
                Program.Settings.SetSteamInputMode(gameId, sel == 1 ? SteamInputMode.Enabled : sel == 2 ? SteamInputMode.Disabled : SteamInputMode.Automatic);
                bool gameSisr = Program.Settings.IsSisrEnabledFor(gameId);
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

            IReadOnlyList<SteamAccount> accounts;
            if (!TrySelectedAccounts(out accounts)) return;

            int sisrSel = cmbCustomSisr.SelectedIndex;
            if (!SavePaths()) return;
            var definition = Win32Provider.CustomRegistration(appName, gamePath, gameArgs).CreateRegistration();
            var choice = new GameProfile(sisrSel == 1 ? SteamInputMode.Enabled : sisrSel == 2 ? SteamInputMode.Disabled : SteamInputMode.Automatic, watchName);
            List<Game> registered;
            if (!Program.TryRegisterGames(new[] { definition }, new[] { choice }, out registered)) return;
            if (!ReportShortcutSaveResults(Program.ImportGamesToSteam(accounts, registered, QueueArtwork),
                string.Format("Imported or retained '{0}' in {1} selected account(s).\n\nRestart Steam to see changes.", appName, accounts.Count)))
            {
                RefreshShortcutsList();
                return;
            }

            txtCustomName.Text = "";
            txtCustomPath.Text = "";
            txtCustomArgs.Text = "";
            txtCustomWatch.Text = "";
            cmbCustomSisr.SelectedIndex = 0;

            RefreshShortcutsList();
        }

        private bool ReportShortcutSaveResults(List<SteamShortcutSaveResult> results, string successMessage)
        {
            if (results.Count > 0 && results.TrueForAll(result => result.Succeeded))
            {
                MessageBox.Show(successMessage, "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return true;
            }
            var details = new System.Text.StringBuilder("Shortcut changes were not saved to every target.\n\n");
            foreach (var result in results)
            {
                details.AppendLine(result.Path);
                details.AppendLine(result.Succeeded ? "Saved." : "Not saved: " + result.Error);
                details.AppendLine();
            }
            MessageBox.Show(details.ToString(), "Shortcut Save Results", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }
}

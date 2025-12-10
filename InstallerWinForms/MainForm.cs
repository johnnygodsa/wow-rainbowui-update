using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Threading;
using System.Windows.Forms;
using System.Collections.Generic;
using System.Drawing;

namespace InstallerWinForms
{
    // 視窗主程式：負責 UI 初始化、下載更新流程、Token 管理與日誌顯示
    public class MainForm : Form
    {
        TextBox pathBox; // 選擇並顯示魔獸世界安裝路徑
        Button selectButton; // 開啟檔案對話框以選擇路徑
        Button startButton; // 執行安裝/更新流程的按鈕
        ProgressBar progressBar; // 顯示整體進度
        Label statusLabel; // 顯示目前狀態文字
        TextBox logList; // 可複製的日誌輸出區
        Label downloadInfoLabel; // 顯示下載速度/ETA資訊
        ListView componentsList; // UI 資料夾清單與狀態
        bool updateReady; // 保留欄位（目前未使用）
        int pendingUpdateCount; // 保留欄位（目前未使用）
        Strings strings; // 介面文字資源
        Config config; // 使用者設定（路徑、提交資訊、Token）

        // 構造函數：載入設定與字串、初始化 UI 控件與事件
        public MainForm()
        {
            strings = Strings.Load(Path.Combine(AppContext.BaseDirectory, "strings.zh-TW.json"));
            config = Config.Load(Path.Combine(AppContext.BaseDirectory, "rainbow_config.json"));

            Text = strings.Title;
            StartPosition = FormStartPosition.CenterScreen;
            Width = 740;
            Height = 650;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;

            // 建立與配置主要 UI 控件（路徑、狀態、進度、組件、日誌）
            var pathLabel = new Label { Text = strings.WowPath, Left = 10, Top = 10, AutoSize = true };
            pathBox = new TextBox { Left = 10, Top = 30, Width = 440, ReadOnly = true, Text = config.WowPath ?? "" };
            selectButton = new Button { Text = strings.Select, Left = 460, Top = 30, Width = 100, Height = pathBox.Height };
            statusLabel = new Label { Left = 10, Top = 85, Width = 700, Text = "", TextAlign = ContentAlignment.MiddleCenter };
            progressBar = new ProgressBar { Left = 10, Top = 115, Width = 700, Minimum = 0, Maximum = 100 };
            downloadInfoLabel = new Label { Left = 10, Top = 145, Width = 700, Text = "", Visible = false, TextAlign = ContentAlignment.MiddleCenter };
            var tokenCheckBox = new CheckBox { Text = strings.UseGitHubTokenLabel, Left = 570, Top = 32, Width = 150, Checked = !string.IsNullOrWhiteSpace(config.GitHubToken), ForeColor = !string.IsNullOrWhiteSpace(config.GitHubToken) ? Color.Green : Color.Black };
            var customDownloadCheckBox = new CheckBox { Text = "自定義下載方式", Left = 570, Top = 54, Width = 150, Checked = (config.ParallelDownloads != 3 || config.BufferSizeKB != 8 || config.ConnectionTimeout != 30), ForeColor = (config.ParallelDownloads != 3 || config.BufferSizeKB != 8 || config.ConnectionTimeout != 30) ? Color.Green : Color.Black };
            startButton = new Button { Text = strings.UpdateButtonChecking, Top = 265, Width = 100, Height = 32, Enabled = false };
            componentsList = new ListView { Left = 10, Top = 305, Width = 700, Height = 265, View = View.Details, FullRowSelect = true, GridLines = true };
            componentsList.Columns.Add(strings.ComponentsHeader, 200);
            componentsList.Columns.Add(strings.StatusHeader, 80);
            componentsList.Columns.Add("更新時間", 160);
            componentsList.Columns.Add("Commit 訊息", 240);
            logList = new TextBox { Left = 10, Top = 175, Width = 700, Height = 80, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White, Font = new Font("Consolas", 9) };

            var f = statusLabel.Font;
            statusLabel.Font = new Font(f.FontFamily, f.Size * 1.5f, FontStyle.Bold);

            

            // Token 功能切換：勾選開啟對話框，取消則停用 Token
            tokenCheckBox.CheckedChanged += (s, e) =>
            {
                if (tokenCheckBox.Checked)
                {
                    ShowTokenDialog(tokenCheckBox);
                }
                else
                {
                    var result = MessageBox.Show("確定要停用 GitHub Token 嗎?\n將使用較低的 API 速率限制 (60次/小時)", "確認", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (result == DialogResult.Yes)
                    {
                        config.GitHubToken = null;
                        config.Save(Path.Combine(AppContext.BaseDirectory, "rainbow_config.json"));
                        tokenCheckBox.ForeColor = Color.Black;
                        Log("已停用 Token，API 限制: 60次/小時");
                    }
                    else
                    {
                        tokenCheckBox.Checked = true;
                    }
                }
            };

            
            customDownloadCheckBox.CheckedChanged += (s, e) =>
            {
                if (customDownloadCheckBox.Checked)
                {
                    ShowCustomDownloadDialog(customDownloadCheckBox);
                }
                else
                {
                    var result = MessageBox.Show(
                        "確定要恢復預設下載設定嗎？\n並行數：3\n緩衝區：8KB\n逾時：30秒",
                        "確認",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question);
                    if (result == DialogResult.Yes)
                    {
                        config.ParallelDownloads = 3;
                        config.BufferSizeKB = 8;
                        config.ConnectionTimeout = 30;
                        config.Save(Path.Combine(AppContext.BaseDirectory, "rainbow_config.json"));
                        customDownloadCheckBox.ForeColor = Color.Black;
                        Log("已恢復預設下載設定：並行 3、緩衝 8KB、逾時 30s");
                    }
                    else
                    {
                        customDownloadCheckBox.Checked = true;
                    }
                }
            };

            // 選擇 WoW 安裝路徑（僅允許 Launcher.exe）
            selectButton.Click += (s, e) =>
            {
                try
                {
                    using var dlg = new OpenFileDialog { Filter = "World of Warcraft Launcher|World of Warcraft Launcher.exe", Multiselect = false, Title = strings.SelectWowTitle };
                    var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                    var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                    if (Directory.Exists(pf86)) dlg.InitialDirectory = Path.Combine(pf86, "World of Warcraft"); else if (Directory.Exists(pf)) dlg.InitialDirectory = Path.Combine(pf, "World of Warcraft");
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                    {
                        var sel = dlg.FileName;
                        var fname = Path.GetFileName(sel);
                        if (!string.Equals(fname, "World of Warcraft Launcher.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            statusLabel.Text = strings.InvalidWowFolder;
                            return;
                        }
                        pathBox.Text = sel;
                        config.WowPath = sel;
                        config.Save(Path.Combine(AppContext.BaseDirectory, "rainbow_config.json"));
                        Log(strings.LogWoWPath + sel);
                    }
                    else
                    {
                        statusLabel.Text = strings.DialogCancel;
                    }
                }
                catch (Exception ex)
                {
                    statusLabel.Text = ex.Message;
                }
            };

            // 按下開始執行安裝/更新
            startButton.Click += async (s, e) => { await RunUpdateFlow(); };

            Controls.Add(pathLabel);
            Controls.Add(pathBox);
            Controls.Add(selectButton);
            Controls.Add(statusLabel);
            Controls.Add(progressBar);
            Controls.Add(downloadInfoLabel);
            Controls.Add(tokenCheckBox);
            Controls.Add(customDownloadCheckBox);
            Controls.Add(startButton);
            Controls.Add(componentsList);
            Controls.Add(logList);
            Shown += (s, e) => Activate();
            // 視窗顯示後：依是否首次安裝決定提示或執行初始檢查
            Shown += async (s, e) =>
            {
                statusLabel.ForeColor = Color.Gray;
                statusLabel.Text = strings.StatusReadingLocal;
                if (string.IsNullOrWhiteSpace(config.WowPath))
                {
                    statusLabel.Text = strings.StatusFirstRun;
                    selectButton.PerformClick();
                }
                else
                {
                    var isFirstInstall = string.IsNullOrEmpty(config.InstalledCommitSha);
                    if (isFirstInstall)
                    {
                        statusLabel.ForeColor = Color.Goldenrod;
                        statusLabel.Text = strings.StatusFirstRun;
                        startButton.Text = strings.UpdateButtonClickToUpdate;
                        startButton.Enabled = true;
                    }
                    else
                    {
                        await RunInitialCheck();
                    }
                }
            };
            // 程式啟動後：若已設定 Token，顯示目前 API 速率額度剩餘
            Load += async (s, e) =>
            {
                await Task.Delay(500);
                if (!string.IsNullOrWhiteSpace(config.GitHubToken))
                {
                    try
                    {
                        using var client = CreateGitHubHttpClient(config.GitHubToken);
                        var resp = await client.GetStringAsync("https://api.github.com/rate_limit");
                        using var doc = JsonDocument.Parse(resp);
                        var rate = doc.RootElement.GetProperty("rate");
                        var limit = rate.GetProperty("limit").GetInt32();
                        var remaining = rate.GetProperty("remaining").GetInt32();
                        Log($"✅ GitHub Token 已啟用: {remaining}/{limit} 次額度剩餘");
                    }
                    catch
                    {
                        Log("⚠ GitHub Token 驗證失敗或失效");
                    }
                }
            };
            Resize += (s, e) => {
                startButton.Left = (ClientSize.Width - startButton.Width) / 2;
            };
            startButton.Left = (ClientSize.Width - startButton.Width) / 2;
        }

        // 寫入日誌：附加時間戳並自動捲動到底部
        void Log(string message)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            var line = "[" + timestamp + "] " + message;
            if (logList.Text.Length > 0) logList.AppendText(Environment.NewLine);
            logList.AppendText(line);
            logList.SelectionStart = logList.Text.Length;
            logList.ScrollToCaret();
        }

        // GitHub Token 設定對話框：提供速率限制說明、取得步驟與 Token 驗證
        void ShowTokenDialog(CheckBox tokenCheckBox)
        {
            var dialog = new Form
            {
                Text = "GitHub Token 設定",
                Width = 600,
                Height = 520,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MaximizeBox = false,
                MinimizeBox = false
            };

            var errorLabel = new Label { Text = "當日誌出現 403 (rate limit exceeded)，代表免費向 GitHub 請求的額度用完了", Left = 15, Top = 15, Width = 560, Height = 20, ForeColor = Color.DarkRed, Font = new Font(Font, FontStyle.Bold) };
            var limitLabel = new Label { Text = "• 免費 API: 60 次/小時   • 使用 Token: 5000 次/小時 (快 83 倍！)", Left = 15, Top = 40, Width = 560, Height = 20, ForeColor = Color.DarkBlue };
            var solutionLabel = new Label { Text = "你需要等待 1 小時或是取得 GitHub Token 獲得更多次的額度", Left = 15, Top = 65, Width = 560, Height = 20 };
            var separator = new Label { Text = "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━", Left = 15, Top = 90, Width = 560, Height = 15, ForeColor = Color.Gray };
            var guideLabel = new Label { Text = "請依照以下步驟取得 GitHub Token:", Left = 15, Top = 110, Width = 560, Height = 20, Font = new Font(Font, FontStyle.Bold) };
            var step1 = new Label { Text = "1. 註冊並登入 GitHub 並取得 Token", Left = 15, Top = 140, Width = 560, AutoSize = true };
            var linkLabel = new LinkLabel { Text = "https://github.com/settings/tokens", Left = 30, Top = 163, Width = 300, AutoSize = true };
            linkLabel.LinkClicked += (s, e) => { try { Process.Start(new ProcessStartInfo { FileName = "https://github.com/settings/tokens", UseShellExecute = true }); } catch { } };
            var step2 = new Label { Text = "2. 點選 \"Generate new token\" (Classic)", Left = 15, Top = 190, Width = 560, AutoSize = true };
            var step3 = new Label { Text = "3. Token name: 隨意輸入, Expiration: 可選擇到期日期", Left = 15, Top = 215, Width = 560, AutoSize = true };
            var step4 = new Label { Text = "4. 不需要勾選任何權限,直接按下 \"Generate token\"", Left = 15, Top = 240, Width = 560, AutoSize = true };
            var step5 = new Label { Text = "5. 複製一大串的英數組合到下面貼上", Left = 15, Top = 265, Width = 560, AutoSize = true, ForeColor = Color.DarkGreen, Font = new Font(Font, FontStyle.Bold) };
            var tokenLabel = new Label { Text = "GitHub Token:", Left = 15, Top = 305, Width = 100, AutoSize = true };
            var tokenTextBox = new TextBox { Left = 15, Top = 330, Width = 560, PasswordChar = '*', Text = config.GitHubToken ?? "", Font = new Font("Consolas", 9) };
            var statusLabel2 = new Label { Left = 15, Top = 360, Width = 560, Height = 40, Text = "", ForeColor = Color.Green };
            if (!string.IsNullOrEmpty(config.GitHubToken)) statusLabel2.Text = "✓ 目前已儲存 Token (5000次/小時)";
            var saveButton = new Button { Text = "儲存", Left = 350, Top = 420, Width = 100, Height = 35 };
            var cancelButton = new Button { Text = "取消", Left = 465, Top = 420, Width = 100, Height = 35 };

            saveButton.Click += async (s, e) =>
            {
                var token = tokenTextBox.Text.Trim();
                if (string.IsNullOrEmpty(token)) { statusLabel2.ForeColor = Color.Red; statusLabel2.Text = "✗ Token 不能為空"; return; }
                if (!token.StartsWith("ghp_") && !token.StartsWith("github_pat_"))
                {
                    statusLabel2.ForeColor = Color.Orange;
                    statusLabel2.Text = "⚠ Token 格式可能不正確 (應以 ghp_ 或 github_pat_ 開頭)";
                    var confirm = MessageBox.Show(
                        "Token 格式似乎不正確，確定要儲存嗎?\n\n正確的 Token 格式:\n• Classic Token: ghp_xxxx\n• Fine-grained Token: github_pat_xxxx",
                        "確認",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning
                    );
                    if (confirm != DialogResult.Yes) return;
                }
                statusLabel2.ForeColor = Color.Blue;
                statusLabel2.Text = "🔄 正在驗證 Token...";
                saveButton.Enabled = false;
                try
                {
                    using var testClient = CreateGitHubHttpClient(token);
                    var testUrl = "https://api.github.com/rate_limit";
                    var testResp = await testClient.GetStringAsync(testUrl);
                    using var doc = JsonDocument.Parse(testResp);
                    var rate = doc.RootElement.GetProperty("rate");
                    var limit = rate.GetProperty("limit").GetInt32();
                    var remaining = rate.GetProperty("remaining").GetInt32();
                    config.GitHubToken = token;
                    config.Save(Path.Combine(AppContext.BaseDirectory, "rainbow_config.json"));
                    tokenCheckBox.Checked = true;
                    tokenCheckBox.ForeColor = Color.Green;
                    Log($"✅ 已啟用 Token，API 限制: {limit} 次/小時 (剩餘 {remaining} 次)");
                    statusLabel2.ForeColor = Color.Green;
                    statusLabel2.Text = $"✓ Token 驗證成功! ({remaining}/{limit} 次剩餘)";
                    MessageBox.Show(
                        $"Token 已成功儲存並驗證!\n\nAPI 速率限制: {limit} 次/小時\n目前剩餘: {remaining} 次\n\n比免費版快 {limit/60}x 倍!",
                        "成功",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information
                    );
                    dialog.DialogResult = DialogResult.OK;
                    dialog.Close();
                    // 自動使用 Token 重新連線並檢測更新
                    Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(500);
                            this.Invoke(new Action(async () =>
                            {
                                statusLabel.ForeColor = Color.Blue;
                                statusLabel.Text = "正在使用 Token 重新檢測更新...";
                                startButton.Enabled = false;
                                await RunInitialCheck();
                                Log("✓ 已使用 Token 重新連線至 GitHub API");
                            }));
                        }
                        catch (Exception ex)
                        {
                            this.Invoke(new Action(() =>
                            {
                                Log($"重新檢測失敗：{ex.Message}");
                            }));
                        }
                    });
                }
                catch (Exception ex)
                {
                    statusLabel2.ForeColor = Color.Red;
                    statusLabel2.Text = "✗ Token 無效或網路錯誤";
                    MessageBox.Show(
                        $"Token 驗證失敗:\n{ex.Message}\n\n請檢查:\n1. Token 是否正確\n2. 網路連線是否正常\n3. Token 是否已過期",
                        "驗證失敗",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error
                    );
                }
                finally
                {
                    saveButton.Enabled = true;
                }
            };

            cancelButton.Click += (s, e) =>
            {
                if (string.IsNullOrEmpty(config.GitHubToken)) { tokenCheckBox.Checked = false; tokenCheckBox.ForeColor = Color.Black; }
                dialog.DialogResult = DialogResult.Cancel;
                dialog.Close();
            };

            dialog.Controls.Add(errorLabel);
            dialog.Controls.Add(limitLabel);
            dialog.Controls.Add(solutionLabel);
            dialog.Controls.Add(separator);
            dialog.Controls.Add(guideLabel);
            dialog.Controls.Add(step1);
            dialog.Controls.Add(linkLabel);
            dialog.Controls.Add(step2);
            dialog.Controls.Add(step3);
            dialog.Controls.Add(step4);
            dialog.Controls.Add(step5);
            dialog.Controls.Add(tokenLabel);
            dialog.Controls.Add(tokenTextBox);
            dialog.Controls.Add(statusLabel2);
            dialog.Controls.Add(saveButton);
            dialog.Controls.Add(cancelButton);
            dialog.ShowDialog(this);
        }

        void ShowCustomDownloadDialog(CheckBox customCheckBox)
        {
            var dialog = new Form
            {
                Text = "自定義下載設定",
                Width = 600,
                Height = 520,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MaximizeBox = false,
                MinimizeBox = false
            };

            var titleLabel = new Label { Text = "⚙ 根據你的網路環境調整下載參數以獲得最佳效能", Left = 15, Top = 15, Width = 540, Height = 30, Font = new Font(Font, FontStyle.Bold), ForeColor = Color.DarkBlue };

            var parallelLabel = new Label { Text = "並行下載資料夾數量：", Left = 15, Top = 60, Width = 150, AutoSize = true };
            var parallelNumeric = new NumericUpDown { Left = 170, Top = 57, Width = 80, Minimum = 1, Maximum = 10, Value = config.ParallelDownloads };
            var parallelHint = new Label { Text = "• 10Mbps 以下網路：建議 1-2\n• 10-50Mbps 網路：建議 2-3\n• 50-100Mbps 網路：建議 3-5\n• 100Mbps 以上網路：建議 5-8\n• 設定過高可能導致連線不穩或 API 限制", Left = 30, Top = 85, Width = 540, Height = 95, ForeColor = Color.Gray, Font = new Font(Font.FontFamily, 9.5f) };

            var bufferLabel = new Label { Text = "下載緩衝區大小 (KB)：", Left = 15, Top = 180, Width = 150, AutoSize = true };
            var bufferNumeric = new NumericUpDown { Left = 170, Top = 177, Width = 80, Minimum = 4, Maximum = 512, Value = config.BufferSizeKB, Increment = 4 };
            var bufferHint = new Label { Text = "• 預設：8KB（適合大多數環境）\n• 網路穩定且快速：可調至 64-128KB\n• 網路不穩或延遲高：保持 8-16KB\n• 過大的緩衝區可能增加記憶體使用", Left = 30, Top = 205, Width = 540, Height = 70, ForeColor = Color.Gray, Font = new Font(Font.FontFamily, 9.5f) };

            var timeoutLabel = new Label { Text = "連線逾時 (秒)：", Left = 15, Top = 290, Width = 150, AutoSize = true };
            var timeoutNumeric = new NumericUpDown { Left = 170, Top = 287, Width = 80, Minimum = 10, Maximum = 120, Value = config.ConnectionTimeout, Increment = 5 };
            var timeoutHint = new Label { Text = "• 預設：30 秒\n• 網路穩定：可縮短至 20 秒\n• 網路不穩或使用代理：延長至 60-90 秒", Left = 30, Top = 315, Width = 540, Height = 55, ForeColor = Color.Gray, Font = new Font(Font.FontFamily, 9.5f) };

            var saveButton = new Button { Text = "儲存設定", Left = 330, Top = 430, Width = 100, Height = 35 };
            var cancelButton = new Button { Text = "取消", Left = 445, Top = 430, Width = 100, Height = 35 };

            saveButton.Click += (s, e) =>
            {
                var newParallel = (int)parallelNumeric.Value;
                var newBuffer = (int)bufferNumeric.Value;
                var newTimeout = (int)timeoutNumeric.Value;

                config.ParallelDownloads = newParallel;
                config.BufferSizeKB = newBuffer;
                config.ConnectionTimeout = newTimeout;
                config.Save(Path.Combine(AppContext.BaseDirectory, "rainbow_config.json"));

                customCheckBox.Checked = true;
                customCheckBox.ForeColor = Color.Green;

                Log($"✓ 下載設定已更新：並行 {newParallel}、緩衝 {newBuffer}KB、逾時 {newTimeout}s");

                MessageBox.Show(
                    $"下載設定已儲存！\n\n並行下載數：{newParallel}\n緩衝區大小：{newBuffer} KB\n連線逾時：{newTimeout} 秒\n\n新設定將在下次下載時生效。",
                    "設定已儲存",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);

                dialog.DialogResult = DialogResult.OK;
                dialog.Close();

                Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(500);
                        this.Invoke(new Action(async () =>
                        {
                            statusLabel.ForeColor = Color.Blue;
                            statusLabel.Text = "正在套用新的下載設定...";
                            startButton.Enabled = false;
                            await RunInitialCheck();
                            Log("下載設定已套用，可以開始更新");
                        }));
                    }
                    catch (Exception ex)
                    {
                        this.Invoke(new Action(() => Log(ex.Message)));
                    }
                });
            };

            cancelButton.Click += (s, e) =>
            {
                if (config.ParallelDownloads == 3 && config.BufferSizeKB == 8 && config.ConnectionTimeout == 30)
                {
                    customCheckBox.Checked = false;
                    customCheckBox.ForeColor = Color.Black;
                }
                dialog.DialogResult = DialogResult.Cancel;
                dialog.Close();
            };

            dialog.Controls.Add(titleLabel);
            dialog.Controls.Add(parallelLabel);
            dialog.Controls.Add(parallelNumeric);
            dialog.Controls.Add(parallelHint);
            dialog.Controls.Add(bufferLabel);
            dialog.Controls.Add(bufferNumeric);
            dialog.Controls.Add(bufferHint);
            dialog.Controls.Add(timeoutLabel);
            dialog.Controls.Add(timeoutNumeric);
            dialog.Controls.Add(timeoutHint);
            dialog.Controls.Add(saveButton);
            dialog.Controls.Add(cancelButton);

            dialog.ShowDialog(this);
        }

        // 遠端 AddOns 資料夾資訊（名稱與樹狀 SHA）
        class FolderInfo
        {
            public string Name = "";
            public string Sha = "";
            public string LastCommitDate = "";
            public string LastCommitMessage = "";
        }

        // 資料夾比對結果（已不於更新流程使用）
        class FolderCompareResult
        {
            public List<string> NewFolders = new List<string>();
            public List<string> ChangedFolders = new List<string>();
            public List<string> UpToDateFolders = new List<string>();
        }

        // 取得遠端 AddOns 第一層資料夾名稱與對應樹狀 SHA（優先透過 git tree；失敗時改用 contents）
        static async Task<List<FolderInfo>> GetAddOnsFoldersWithSha(string branch, string? token = null)
        {
            var folders = new List<FolderInfo>();
            using var client = CreateGitHubHttpClient(token);
            try
            {
                var branchUrl = "https://api.github.com/repos/WOWRainbowUI/RainbowUI-Retail/branches/" + branch;
                var branchResp = await client.GetStringAsync(branchUrl);
                using var branchDoc = JsonDocument.Parse(branchResp);
                var commitSha = branchDoc.RootElement.GetProperty("commit").GetProperty("sha").GetString() ?? "";
                if (string.IsNullOrEmpty(commitSha)) return folders;
                var treeUrl = "https://api.github.com/repos/WOWRainbowUI/RainbowUI-Retail/git/trees/" + commitSha + "?recursive=1";
                var treeResp = await client.GetStringAsync(treeUrl);
                using var treeDoc = JsonDocument.Parse(treeResp);
                if (!treeDoc.RootElement.TryGetProperty("tree", out var tree)) return folders;
                var addonsPatterns = new[] { "Interface/AddOns", "AddOns" };
                var folderShas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var node in tree.EnumerateArray())
                {
                    var type = node.TryGetProperty("type", out var t) ? t.GetString() : null;
                    var path = node.TryGetProperty("path", out var p) ? p.GetString() : null;
                    var sha = node.TryGetProperty("sha", out var s) ? s.GetString() : null;
                    if (type != "tree" || string.IsNullOrEmpty(path) || string.IsNullOrEmpty(sha)) continue;
                    foreach (var pattern in addonsPatterns)
                    {
                        var idx = path.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
                        if (idx < 0) continue;
                        var rest = path.Substring(idx + pattern.Length).TrimStart('/');
                        var parts = rest.Split('/');
                        if (parts.Length == 1 && !string.IsNullOrEmpty(parts[0]))
                        {
                            var folderName = parts[0];
                            if (!folderShas.ContainsKey(folderName)) folderShas[folderName] = sha;
                        }
                        break;
                    }
                }
                var maxConcurrent = string.IsNullOrWhiteSpace(token) ? 3 : 10;
                var semaphore = new SemaphoreSlim(maxConcurrent);
                var tasks = new List<Task<FolderInfo>>();
                foreach (var kvp in folderShas)
                {
                    await semaphore.WaitAsync();
                    var folderName = kvp.Key;
                    var folderSha = kvp.Value;
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            string commitDate = "";
                            string commitMessage = "";
                            foreach (var prefix in new[] { "AddOns", "Interface/AddOns" })
                            {
                                try
                                {
                                    var folderPath = prefix + "/" + folderName;
                                    var commitUrl = "https://api.github.com/repos/WOWRainbowUI/RainbowUI-Retail/commits?path=" + folderPath + "&per_page=1&sha=" + branch;
                                    var commitResp = await client.GetStringAsync(commitUrl);
                                    using var commitDoc = JsonDocument.Parse(commitResp);
                                    if (commitDoc.RootElement.ValueKind == JsonValueKind.Array && commitDoc.RootElement.GetArrayLength() > 0)
                                    {
                                        var lastCommit = commitDoc.RootElement[0];
                                        var commit = lastCommit.GetProperty("commit");
                                        var committer = commit.GetProperty("committer");
                                        var dateStr = committer.TryGetProperty("date", out var dateEl) ? (dateEl.GetString() ?? "") : "";
                                        if (DateTime.TryParse(dateStr, out var dt)) commitDate = dt.ToLocalTime().ToString("yyyy/MM/dd HH:mm");
                                        var fullMessage = commit.TryGetProperty("message", out var msgEl) ? (msgEl.GetString() ?? "") : "";
                                        if (!string.IsNullOrEmpty(fullMessage))
                                        {
                                            var lines = fullMessage.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                                            commitMessage = lines.Length > 0 ? lines[0] : fullMessage;
                                            if (commitMessage.Length > 60) commitMessage = commitMessage.Substring(0, 57) + "...";
                                        }
                                        break;
                                    }
                                }
                                catch
                                {
                                }
                            }
                            return new FolderInfo { Name = folderName, Sha = folderSha, LastCommitDate = commitDate, LastCommitMessage = commitMessage };
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }));
                }
                var results = await Task.WhenAll(tasks);
                folders.AddRange(results);
            }
            catch
            {
                var names = await GetRemoteAddOnDirs(branch, token);
                foreach (var name in names)
                {
                    folders.Add(new FolderInfo { Name = name, Sha = "", LastCommitDate = "", LastCommitMessage = "" });
                }
            }
            return folders;
        }

        // 比對遠端與本地資料夾狀態（注意：若未提供 localFolderShas，現有資料夾可能被誤判為 Changed）
        static FolderCompareResult CompareFolders(List<FolderInfo> remoteFolders, string addonsPath, Dictionary<string, string>? localFolderShas = null)
        {
            var result = new FolderCompareResult();
            var localDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(addonsPath))
            {
                foreach (var dir in Directory.GetDirectories(addonsPath))
                {
                    var name = Path.GetFileName(dir);
                    if (!string.IsNullOrEmpty(name)) localDirs.Add(name);
                }
            }
            foreach (var remote in remoteFolders)
            {
                if (!localDirs.Contains(remote.Name))
                {
                    result.NewFolders.Add(remote.Name);
                }
                else
                {
                    if (localFolderShas != null && localFolderShas.TryGetValue(remote.Name, out var localSha) && !string.IsNullOrEmpty(remote.Sha) && localSha == remote.Sha)
                    {
                        result.UpToDateFolders.Add(remote.Name);
                    }
                    else
                    {
                        result.ChangedFolders.Add(remote.Name);
                    }
                }
            }
            return result;
        }

        // 下載單一 UI 資料夾：先嘗試 AddOns/<name>，若無則改用 Interface/AddOns/<name>
        static async Task DownloadFolder(string folderName, string branch, string addonsPath, string? token, Action<int> onProgress, Action<string> onLog, Action<double, int, int>? onSpeed = null, int bufferSizeKB = 8, int timeoutSeconds = 30)
        {
            var folderPath = "AddOns/" + folderName;
            var files = await GetFolderFilesRecursive(folderPath, branch, token);
            if (files.Count == 0)
            {
                folderPath = "Interface/AddOns/" + folderName;
                files = await GetFolderFilesRecursive(folderPath, branch, token);
            }
            if (files.Count == 0)
            {
                onLog("資料夾無檔案: " + folderName);
                return;
            }
            using var client = CreateGitHubHttpClient(token, timeoutSeconds);
            int downloaded = 0;
            var sw = Stopwatch.StartNew();
            long totalBytes = 0;
            foreach (var file in files)
            {
                try
                {
                    using var resp = await client.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead);
                    resp.EnsureSuccessStatusCode();
                    using var stream = await resp.Content.ReadAsStreamAsync();
                    var rel = file.Path.Replace('\\', '/');
                    var idx = rel.IndexOf("AddOns/", StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0) rel = rel.Substring(idx + "AddOns/".Length);
                    var dest = Path.Combine(addonsPath, rel.Replace('/', Path.DirectorySeparatorChar));
                    var ddir = Path.GetDirectoryName(dest);
                    if (!string.IsNullOrEmpty(ddir) && !Directory.Exists(ddir)) Directory.CreateDirectory(ddir);
                    using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
                    var buffer = new byte[Math.Max(1024, bufferSizeKB * 1024)];
                    int read;
                    while ((read = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await fs.WriteAsync(buffer, 0, read);
                        totalBytes += read;
                    }
                    downloaded++;
                    var pct = (int)(downloaded * 100 / files.Count);
                    onProgress(pct);
                    var speedMBps = totalBytes / Math.Max(sw.Elapsed.TotalSeconds, 0.001) / 1048576.0;
                    onSpeed?.Invoke(speedMBps, downloaded, files.Count);
                }
                catch (Exception ex)
                {
                    onLog("下載失敗: " + file.Path + " - " + ex.Message);
                }
            }
            onLog(folderName + " 完成 (" + downloaded + "/" + files.Count + ")");
        }


        // 解析 WoW 安裝路徑並建立 _retail_/Interface/AddOns 目錄，回傳絕對路徑
        static string EnsureAddOns(string wowPath)
        {
            var basePath = wowPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? (Path.GetDirectoryName(wowPath) ?? wowPath) : wowPath;
            var retail = Path.Combine(basePath, "_retail_");
            var interfaceDir = Path.Combine(retail, "Interface");
            var addons = Path.Combine(interfaceDir, "AddOns");
            if (!Directory.Exists(addons)) Directory.CreateDirectory(addons);
            return addons;
        }

        // 更新流程：統一使用增量更新（分析缺失資料夾並逐一下載）
        async Task RunUpdateFlow()
        {
            try
            {
                var wowPath = pathBox.Text;
                if (string.IsNullOrWhiteSpace(wowPath)) throw new InvalidOperationException(strings.NeedPath);
                var addonsPath = EnsureAddOns(wowPath);
                var branch = "master";
                var token = config.GitHubToken;
                try { branch = await GetDefaultBranch(token); } catch { }
                startButton.Text = strings.UpdateButtonUpdating;
                startButton.Enabled = false;
                statusLabel.ForeColor = Color.Red;
                statusLabel.Text = strings.StatusUpdating;
                var basePath = wowPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? (Path.GetDirectoryName(wowPath) ?? wowPath) : wowPath;
                var interfaceDir = Path.Combine(Path.Combine(basePath, "_retail_"), "Interface");
                Directory.CreateDirectory(interfaceDir);
                // 統一：分析缺失資料夾並逐一下載
                statusLabel.ForeColor = Color.Orange;
                statusLabel.Text = "正在分析需要更新的資料夾...";
                progressBar.Value = 5;

                var remoteFolders = await GetAddOnsFoldersWithSha(branch, token);

                var localDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (Directory.Exists(addonsPath))
                {
                    foreach (var dir in Directory.GetDirectories(addonsPath))
                    {
                        var name = Path.GetFileName(dir);
                        if (!string.IsNullOrEmpty(name)) localDirs.Add(name);
                    }
                }

                var missingFolders = new List<string>();
                foreach (var remote in remoteFolders)
                {
                    if (!localDirs.Contains(remote.Name)) missingFolders.Add(remote.Name);
                }

                if (missingFolders.Count == 0)
                {
                    statusLabel.ForeColor = Color.Green;
                    statusLabel.Text = strings.StatusUpToDateAll;
                    progressBar.Value = 100;
                    Log("【✅ 已是最新】所有 UI 資料夾都已存在");
                    await RunInitialCheck();
                    return;
                }

                Log($"【📥 開始下載】需要下載 {missingFolders.Count} 個缺失的資料夾");

                statusLabel.Text = $"正在下載與移動 {missingFolders.Count} 個資料夾...";
                downloadInfoLabel.Visible = true;

                int completed = 0;
                var sw = Stopwatch.StartNew();

                var tasks = new List<Task>();
                var maxParallel = Math.Max(1, config.ParallelDownloads);
                var semaphore = new SemaphoreSlim(maxParallel);
                Log($"使用並行下載數：{maxParallel}");

                foreach (var folderName in missingFolders)
                {
                    await semaphore.WaitAsync();
                    var localFolderName = folderName;
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            await DownloadFolder(
                                localFolderName,
                                branch,
                                addonsPath,
                                token,
                                pct => { },
                                msg => Log(msg),
                                (speedMBps, done, total) =>
                                {
                                    this.Invoke(new Action(() =>
                                    {
                                        downloadInfoLabel.Text = $"下載中: {localFolderName} ({done}/{total}) | 速度: {speedMBps:F2} MB/s";
                                    }));
                                },
                                Math.Max(4, config.BufferSizeKB),
                                Math.Max(10, config.ConnectionTimeout)
                            );
                        }
                        finally
                        {
                            Interlocked.Increment(ref completed);
                            this.Invoke(new Action(() =>
                            {
                                var folderProgress = (int)((completed * 100.0) / Math.Max(1, missingFolders.Count));
                                progressBar.Value = Math.Max(0, Math.Min(100, folderProgress));
                            }));
                            semaphore.Release();
                        }
                    }));
                }

                await Task.WhenAll(tasks);

                sw.Stop();
                downloadInfoLabel.Visible = false;

                dynamic latest = new { sha = "", commit = new { committer = new { date = "" }, message = "" } };
                try { latest = await GetLatestCommit(branch, token); } catch { }

                var latestSha = ""; try { latestSha = latest.sha; } catch { }
                if (!string.IsNullOrEmpty(latestSha)) config.InstalledCommitSha = latestSha;

                var dateStr = ""; try { dateStr = latest.commit.committer.date; } catch { }
                if (!string.IsNullOrEmpty(dateStr)) config.InstalledCommitDate = dateStr;

                var commitMsg = ""; try { commitMsg = latest.commit.message; } catch { }
                if (!string.IsNullOrEmpty(commitMsg))
                {
                    config.InstalledCommitMessage = commitMsg;
                    var firstLine = commitMsg.Split('\n', '\r')[0];
                    Log(strings.LogCommitMessage + firstLine);
                }

                config.Save(Path.Combine(AppContext.BaseDirectory, "rainbow_config.json"));

                progressBar.Value = 100;
                statusLabel.Text = strings.StatusCompleted;
                statusLabel.ForeColor = Color.Green;

                Log($"【✅ 下載完成】成功下載 {completed} 個資料夾，耗時 {sw.Elapsed.TotalSeconds:F1} 秒");

                await RunInitialCheck();
            }
            catch (Exception ex)
            {
                var msg = ex.Message ?? "";
                if (msg.IndexOf("403", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    statusLabel.ForeColor = Color.DarkOrange;
                    if (string.IsNullOrWhiteSpace(config.GitHubToken))
                    {
                        statusLabel.Text = "❌ GitHub API 速率限制 (60次/小時已用完)";
                        Log("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                        Log("⚠️  達到 API 速率限制 (60次/小時)");
                        Log("📝 解決方法:");
                        Log("   1. 等待 1 小時後重試");
                        Log("   2. 或勾選上方『使用 GitHub Token』取得 5000次/小時額度");
                        Log("   步驟: 勾選 → 依照彈窗指引 → 貼上 Token → 儲存");
                        Log("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                    }
                    else
                    {
                        statusLabel.Text = "❌ GitHub API 速率限制 (Token 可能失效)";
                        Log("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                        Log("⚠️  即使使用 Token 仍達到速率限制");
                        Log("📝 可能原因:");
                        Log("   1. Token 已過期或無效");
                        Log("   2. Token 的 5000次/小時額度已用完");
                        Log("   3. GitHub API 暫時性問題");
                        Log("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                        Log("💡 建議: 重新產生 Token 或等待 1 小時");
                    }
                    startButton.Text = strings.UpdateButtonClickToUpdate;
                    startButton.Enabled = true;
                }
                else
                {
                    statusLabel.ForeColor = Color.Red;
                    statusLabel.Text = strings.StatusUnexpectedError;
                    Log("❌ 錯誤: " + msg);
                }
            }
        }

        // 建立 GitHub API 用的 HttpClient（TLS1.2、User-Agent、可選 Authorization）
        static HttpClient CreateGitHubHttpClient(string? token = null, int timeoutSeconds = 30)
        {
            var handler = new HttpClientHandler { SslProtocols = SslProtocols.Tls12, MaxConnectionsPerServer = 20 };
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
            client.DefaultRequestVersion = new Version(2, 0);
            client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RainbowUIInstaller", "1.0"));
            if (!string.IsNullOrWhiteSpace(token)) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return client;
        }

        static async Task<bool> DownloadSingleFileFromGitHub(string relativeFilePath, string savePath, string branch = "master", string? token = null)
        {
            using var client = CreateGitHubHttpClient(token);
            var url = "https://raw.githubusercontent.com/WOWRainbowUI/RainbowUI-Retail/" + branch + "/" + relativeFilePath.Replace('\\', '/');
            try
            {
                using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                if (!resp.IsSuccessStatusCode) return false;
                var ddir = Path.GetDirectoryName(savePath);
                if (!string.IsNullOrEmpty(ddir) && !Directory.Exists(ddir)) Directory.CreateDirectory(ddir);
                using var stream = await resp.Content.ReadAsStreamAsync();
                using var fs = new FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.None);
                await stream.CopyToAsync(fs);
                return true;
            }
            catch
            {
                return false;
            }
        }

        // 呼叫 GitHub API：成功不延遲；失敗時漸進重試（1s, 2s）
        static async Task<string> GetGitHubApiAsync(string url, string? token = null)
        {
            using var client = CreateGitHubHttpClient(token);
            for (int retry = 0; retry < 3; retry++)
            {
                try
                {
                    var response = await client.GetStringAsync(url);
                    return response;
                }
                catch (HttpRequestException) when (retry < 2)
                {
                    await Task.Delay(1000 * (retry + 1));
                }
                catch (OperationCanceledException) when (retry < 2)
                {
                    await Task.Delay(1000 * (retry + 1));
                }
            }
            throw new InvalidOperationException("Failed to fetch from GitHub API");
        }

        // 取得倉庫預設分支（預設 master）
        static async Task<string> GetDefaultBranch(string? token = null)
        {
            var resp = await GetGitHubApiAsync("https://api.github.com/repos/WOWRainbowUI/RainbowUI-Retail", token);
            using var doc = JsonDocument.Parse(resp);
            var root = doc.RootElement;
            if (root.TryGetProperty("default_branch", out var br)) return br.GetString() ?? "master";
            return "master";
        }

        // 取得指定分支的最新提交（含 sha、日期與 message）
        static async Task<dynamic> GetLatestCommit(string branch, string? token = null)
        {
            var url = "https://api.github.com/repos/WOWRainbowUI/RainbowUI-Retail/commits?sha=" + branch + "&per_page=1";
            var resp = await GetGitHubApiAsync(url, token);
            using var doc = JsonDocument.Parse(resp);
            var arr = doc.RootElement;
            var obj = arr[0];
            var sha = obj.GetProperty("sha").GetString();
            var commit = obj.GetProperty("commit");
            var committer = commit.GetProperty("committer");
            var date = committer.GetProperty("date").GetString();
            var message = commit.TryGetProperty("message", out var msgEl) ? (msgEl.GetString() ?? "") : "";
            return new { sha, commit = new { committer = new { date }, message } };
        }

        // 變更檔案資訊（compare API 用）
        class ChangeFile { public string Path = ""; public string Status = ""; }
        // 變更摘要：涉及的目錄/檔案清單（目前不於更新流程使用）
        class ChangeInfo { public HashSet<string> Dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase); public List<string> Files = new List<string>(); public List<ChangeFile> Details = new List<ChangeFile>(); }

        // 取得 base..head 差異中涉及的 AddOns 目錄與檔案（compare API）
        static async Task<ChangeInfo> GetChangedAddOnDirsAndFiles(string baseSha, string headSha, string? token = null)
        {
            var info = new ChangeInfo();
            try
            {
                var url = "https://api.github.com/repos/WOWRainbowUI/RainbowUI-Retail/compare/" + baseSha + "..." + headSha;
                var resp = await GetGitHubApiAsync(url, token);
                using var doc = JsonDocument.Parse(resp);
                var root = doc.RootElement;
                if (root.TryGetProperty("files", out var files))
                {
                    foreach (var f in files.EnumerateArray())
                    {
                        var fn = f.GetProperty("filename").GetString() ?? "";
                        var st = f.TryGetProperty("status", out var s) ? (s.GetString() ?? "") : "";
                        info.Files.Add(fn);
                        info.Details.Add(new ChangeFile { Path = fn, Status = st });
                        var idx = fn.IndexOf("Interface/AddOns/", StringComparison.OrdinalIgnoreCase);
                        if (idx >= 0)
                        {
                            var rest = fn.Substring(idx + "Interface/AddOns/".Length);
                            var slash = rest.IndexOf('/');
                            var dir = slash >= 0 ? rest.Substring(0, slash) : rest;
                            if (!string.IsNullOrEmpty(dir)) info.Dirs.Add(dir);
                        }
                        else
                        {
                            var parts = fn.Split('/', '\\');
                            if (parts.Length > 0) info.Dirs.Add(parts[0]);
                        }
                    }
                }
            }
            catch { }
            return info;
        }

        // 下載分支 ZIP 並彙報進度與速度；首次安裝 ETA 以 240MB 為基準
        static async Task DownloadZipWithProgress(string branch, string outZip, Action<int> onProgress, Action<long, long, double> onInfo)
        {
            using var handler = new HttpClientHandler { SslProtocols = SslProtocols.Tls12 };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RainbowUIInstaller", "1.0"));
            var url = "https://github.com/WOWRainbowUI/RainbowUI-Retail/archive/refs/heads/" + branch + ".zip";
            using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? -1L;
            using var stream = await resp.Content.ReadAsStreamAsync();
            using var fs = new FileStream(outZip, FileMode.Create, FileAccess.Write, FileShare.None, 81920);
            var buffer = new byte[81920];
            long downloadedBytes = 0;
            long lastReportedBytes = 0;
            var sw = Stopwatch.StartNew();
            var lastReportTime = sw.Elapsed.TotalSeconds;
            onInfo(0, total, 0);
            int read;
            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                downloadedBytes += read;
                var currentTime = sw.Elapsed.TotalSeconds;
                var timeDelta = currentTime - lastReportTime;
                if (timeDelta >= 0.5)
                {
                    var bytesDelta = downloadedBytes - lastReportedBytes;
                    var speed = bytesDelta / Math.Max(timeDelta, 0.001);
                    onInfo(downloadedBytes, total, speed);
                    lastReportedBytes = downloadedBytes;
                    lastReportTime = currentTime;
                }
                await fs.WriteAsync(buffer, 0, read);
                if (total > 0)
                {
                    var pct = (int)(downloadedBytes * 100 / total);
                    onProgress(pct);
                    Application.DoEvents();
                }
            }
            var finalSpeed = downloadedBytes / Math.Max(sw.Elapsed.TotalSeconds, 0.001);
            onInfo(downloadedBytes, total, finalSpeed);
            if (total <= 0) onProgress(100);
        }

        static string FindExtractedRoot(string tempDir, string branch)
        {
            var expected = Path.Combine(tempDir, "RainbowUI-Retail-" + branch);
            if (Directory.Exists(expected)) return expected;
            foreach (var d in Directory.GetDirectories(tempDir)) return d;
            throw new InvalidOperationException("Extracted root not found");
        }

        static string GetCopySource(string root)
        {
            var interfaceAddOns = Path.Combine(root, Path.Combine("Interface", "AddOns"));
            if (Directory.Exists(interfaceAddOns)) return interfaceAddOns;
            var rootAddOns = Path.Combine(root, "AddOns");
            if (Directory.Exists(rootAddOns)) return rootAddOns;
            return root;
        }

        static void ExtractZipWithProgress(string zipPath, string destDir, Action<int> onProgress, Action<string> onEntry)
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var total = archive.Entries.Count;
            var processed = 0;
            foreach (var entry in archive.Entries)
            {
                var fullPath = Path.Combine(destDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                var rel = entry.FullName;
                var dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(fullPath);
                }
                else
                {
                    entry.ExtractToFile(fullPath, true);
                    onEntry(rel);
                }
                processed++;
                var pct = total > 0 ? (int)(processed * 100 / total) : 100;
                onProgress(pct);
            }
        }

        static void ExtractZipCompatWithProgress(string zipPath, string destDir, Action<int> onProgress, Action<string> onEntry)
        {
            var exe = Find7zExe();
            if (!string.IsNullOrEmpty(exe))
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "x \"" + zipPath + "\" -o\"" + destDir + "\" -y",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                var _ = p.StandardOutput.ReadToEnd();
                var __ = p.StandardError.ReadToEnd();
                p.WaitForExit();
                onProgress(100);
            }
            else
            {
                ExtractZipWithProgress(zipPath, destDir, onProgress, onEntry);
            }
        }

        static string Find7zExe()
        {
            var paths = new List<string>
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip", "7z.exe")
            };
            foreach (var p in paths) if (File.Exists(p)) return p;
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                var candidate = Path.Combine(dir.Trim(), "7z.exe");
                if (File.Exists(candidate)) return candidate;
            }
            return "";
        }
        class CopyStats { public int Added; public int Updated; public int Deleted; public HashSet<string> UpdatedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase); }

        static CopyStats MirrorCopyWithProgress(string source, string dest, Action<int> onProgress, Action<string> onFile, Strings strings)
        {
            var stats = new CopyStats();
            foreach (var ddir in Directory.EnumerateDirectories(dest, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(dest, ddir);
                var sdir = Path.Combine(source, rel);
                if (!Directory.Exists(sdir)) { Directory.Delete(ddir, true); onFile(string.Format(strings.LogCopyDeletedDir, rel)); stats.Deleted++; }
            }
            foreach (var dfile in Directory.EnumerateFiles(dest, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(dest, dfile);
                var sfile = Path.Combine(source, rel);
                if (!File.Exists(sfile)) { File.Delete(dfile); onFile(string.Format(strings.LogCopyDeletedFile, rel)); stats.Deleted++; }
            }
            foreach (var sdir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(source, sdir);
                var ddir = Path.Combine(dest, rel);
                if (!Directory.Exists(ddir)) Directory.CreateDirectory(ddir);
            }
            var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories);
            long totalBytes = 0;
            foreach (var f in files) totalBytes += new FileInfo(f).Length;
            long copiedBytes = 0;
            foreach (var sfile in files)
            {
                var rel = Path.GetRelativePath(source, sfile);
                var dfile = Path.Combine(dest, rel);
                var ddir = Path.GetDirectoryName(dfile);
                if (!string.IsNullOrEmpty(ddir) && !Directory.Exists(ddir)) Directory.CreateDirectory(ddir);
                var existed = File.Exists(dfile);
                using (var src = new FileStream(sfile, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var dst = new FileStream(dfile, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[8192];
                    int read;
                    while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        dst.Write(buffer, 0, read);
                        copiedBytes += read;
                        var pct = totalBytes > 0 ? (int)(copiedBytes * 100 / totalBytes) : 100;
                        onProgress(pct);
                    }
                }
                var dirRel = Path.GetDirectoryName(rel) ?? "";
                if (!string.IsNullOrEmpty(dirRel)) stats.UpdatedDirs.Add(dirRel);
                if (existed) { stats.Updated++; onFile(string.Format(strings.LogCopyUpdated, rel)); } else { stats.Added++; onFile(string.Format(strings.LogCopyAdded, rel)); }
                File.SetLastWriteTime(dfile, File.GetLastWriteTime(sfile));
            }
            return stats;
        }
        static async Task<HashSet<string>> GetRemoteAddOnDirs(string branch, string? token = null)
        {
            using var handler = new HttpClientHandler { SslProtocols = SslProtocols.Tls12 };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RainbowUIInstaller", "1.0"));

            async Task<HashSet<string>> TryContents(string path)
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var url = "https://api.github.com/repos/WOWRainbowUI/RainbowUI-Retail/contents/" + path + "?ref=" + branch;
                    var resp = await client.GetStringAsync(url);
                    using var doc = JsonDocument.Parse(resp);
                    var root = doc.RootElement;
                    if (root.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var entry in root.EnumerateArray())
                        {
                            var type = entry.TryGetProperty("type", out var t) ? t.GetString() : null;
                            if (!string.Equals(type, "dir", StringComparison.OrdinalIgnoreCase)) continue;
                            var name = entry.TryGetProperty("name", out var n) ? n.GetString() : null;
                            if (!string.IsNullOrEmpty(name)) names.Add(name);
                        }
                    }
                }
                catch { }
                return names;
            }

            var set = await TryContents("Interface/AddOns");
            if (set.Count == 0) set = await TryContents("AddOns");

            if (set.Count == 0)
            {
                try
                {
                    var branchUrl = "https://api.github.com/repos/WOWRainbowUI/RainbowUI-Retail/branches/" + branch;
                    var bresp = await GetGitHubApiAsync(branchUrl, token);
                    using var bdoc = JsonDocument.Parse(bresp);
                    var commit = bdoc.RootElement.GetProperty("commit");
                    var sha = commit.GetProperty("sha").GetString() ?? "";
                    if (!string.IsNullOrEmpty(sha))
                    {
                        var treeUrl = "https://api.github.com/repos/WOWRainbowUI/RainbowUI-Retail/git/trees/" + sha + "?recursive=1";
                        var tresp = await GetGitHubApiAsync(treeUrl, token);
                        using var tdoc = JsonDocument.Parse(tresp);
                        if (tdoc.RootElement.TryGetProperty("tree", out var tree) && tree.ValueKind == JsonValueKind.Array)
                        {
                            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            foreach (var node in tree.EnumerateArray())
                            {
                                var type = node.TryGetProperty("type", out var tp) ? tp.GetString() : null;
                                var path = node.TryGetProperty("path", out var p) ? p.GetString() : null;
                                if (!string.Equals(type, "tree", StringComparison.OrdinalIgnoreCase)) continue;
                                if (string.IsNullOrEmpty(path)) continue;
                                var idx = path.IndexOf("Interface/AddOns/", StringComparison.OrdinalIgnoreCase);
                                if (idx < 0) idx = path.IndexOf("AddOns/", StringComparison.OrdinalIgnoreCase);
                                if (idx >= 0)
                                {
                                    var rest = path.Substring(idx);
                                    var parts = rest.Replace('\\', '/').Split('/');
                                    string? dir = null;
                                    if (parts.Length >= 3 && parts[parts.Length - 2].Equals("AddOns", StringComparison.OrdinalIgnoreCase))
                                    {
                                        dir = parts[parts.Length - 1];
                                    }
                                    else if (parts.Length >= 2)
                                    {
                                        dir = parts[parts.Length - 1];
                                    }
                                    if (!string.IsNullOrEmpty(dir)) names.Add(dir);
                                }
                            }
                            set = names;
                        }
                    }
                }
                catch { }
            }
            return set;
        }

        // 檔案下載描述（路徑、原始下載 URL、大小）
        class FileToDownload { public string Path = ""; public string Url = ""; public long Size; }
        class MissingFolderStatus { public string FolderName = ""; public bool Successful; public int FilesCount; public string ErrorMessage = ""; }

        // 遞迴列出指定資料夾的所有檔案（contents API），失敗時重試
        static async Task<List<FileToDownload>> GetFolderFilesRecursive(string folderPath, string branch, string? token = null)
        {
            var list = new List<FileToDownload>();
            using var client = CreateGitHubHttpClient(token);

            async Task Walk(string p, int depth = 0)
            {
                if (depth > 10) return;
                try
                {
                    var url = "https://api.github.com/repos/WOWRainbowUI/RainbowUI-Retail/contents/" + p + "?ref=" + branch;
                    string resp = null;
                    for (int retry = 0; retry < 3; retry++)
                    {
                        try
                        {
                            resp = await client.GetStringAsync(url);
                            break;
                        }
                        catch (HttpRequestException) when (retry < 2)
                        {
                            await Task.Delay(1000 * (retry + 1));
                        }
                        catch (OperationCanceledException) when (retry < 2)
                        {
                            await Task.Delay(1000 * (retry + 1));
                        }
                    }
                    if (string.IsNullOrEmpty(resp)) return;
                    using var doc = JsonDocument.Parse(resp);
                    foreach (var entry in doc.RootElement.EnumerateArray())
                    {
                        var type = entry.TryGetProperty("type", out var t) ? t.GetString() : null;
                        var path = entry.TryGetProperty("path", out var pa) ? pa.GetString() : null;
                        if (string.Equals(type, "dir", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!string.IsNullOrEmpty(path)) await Walk(path, depth + 1);
                        }
                        else if (string.Equals(type, "file", StringComparison.OrdinalIgnoreCase))
                        {
                            var size = entry.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0L;
                            if (!string.IsNullOrEmpty(path))
                            {
                                var raw = "https://raw.githubusercontent.com/WOWRainbowUI/RainbowUI-Retail/" + branch + "/" + path.Replace('\\', '/');
                                list.Add(new FileToDownload { Path = path, Url = raw, Size = size });
                            }
                        }
                    }
                }
                catch (JsonException)
                {
                }
                catch (HttpRequestException)
                {
                }
                catch (OperationCanceledException)
                {
                }
            }

            await Walk(folderPath);
            return list;
        }

        // 透過 git tree 取得所有檔案（僅包含 blob），篩選 AddOns 路徑
        static async Task<List<FileToDownload>> GetAllFilesFromTree(string branch, string? token = null)
        {
            var list = new List<FileToDownload>();
            using var client = CreateGitHubHttpClient(token);
            try
            {
                var branchUrl = "https://api.github.com/repos/WOWRainbowUI/RainbowUI-Retail/branches/" + branch;
                var bresp = await client.GetStringAsync(branchUrl);
                using var bdoc = JsonDocument.Parse(bresp);
                var commit = bdoc.RootElement.GetProperty("commit");
                var sha = commit.GetProperty("sha").GetString() ?? "";
                if (!string.IsNullOrEmpty(sha))
                {
                    var treeUrl = "https://api.github.com/repos/WOWRainbowUI/RainbowUI-Retail/git/trees/" + sha + "?recursive=1";
                    var tresp = await client.GetStringAsync(treeUrl);
                    using var tdoc = JsonDocument.Parse(tresp);
                    if (tdoc.RootElement.TryGetProperty("tree", out var tree) && tree.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var node in tree.EnumerateArray())
                        {
                            var type = node.TryGetProperty("type", out var tp) ? tp.GetString() : null;
                            var path = node.TryGetProperty("path", out var p) ? p.GetString() : null;
                            if (!string.Equals(type, "blob", StringComparison.OrdinalIgnoreCase)) continue;
                            if (string.IsNullOrEmpty(path)) continue;
                            var lower = path.Replace('\\', '/');
                            if (lower.IndexOf("Interface/AddOns/", StringComparison.OrdinalIgnoreCase) < 0 && lower.IndexOf("AddOns/", StringComparison.OrdinalIgnoreCase) < 0) continue;
                            var raw = "https://raw.githubusercontent.com/WOWRainbowUI/RainbowUI-Retail/" + branch + "/" + lower;
                            list.Add(new FileToDownload { Path = lower, Url = raw, Size = 0 });
                        }
                    }
                }
            }
            catch { }
            return list;
        }

        // 下載指定檔案清單並顯示整體進度與 ETA（非目前主路徑）
        static async Task DownloadSelectedFilesWithProgress(List<FileToDownload> files, string branch, string addonsPath, Action<int> onProgress, Action<double, string, double, double> onInfo)
        {
            using var handler = new HttpClientHandler { SslProtocols = SslProtocols.Tls12 };
            using var client = new HttpClient(handler);
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RainbowUIInstaller", "1.0"));
            long total = 0; foreach (var f in files) total += Math.Max(0, f.Size);
            long readTotal = 0;
            var sw = Stopwatch.StartNew();
            foreach (var f in files)
            {
                try
                {
                    using var resp = await client.GetAsync(f.Url, HttpCompletionOption.ResponseHeadersRead);
                    resp.EnsureSuccessStatusCode();
                    var contentLen = resp.Content.Headers.ContentLength ?? f.Size;
                    using var stream = await resp.Content.ReadAsStreamAsync();
                    var rel = f.Path.Replace('\\', '/');
                    var idx = rel.IndexOf("Interface/AddOns/", StringComparison.OrdinalIgnoreCase);
                    if (idx < 0) idx = rel.IndexOf("AddOns/", StringComparison.OrdinalIgnoreCase);
                    var trimmed = idx >= 0 ? rel.Substring(idx + (rel.Substring(idx).StartsWith("Interface/AddOns/") ? "Interface/AddOns/".Length : "AddOns/".Length)) : rel;
                    var dest = Path.Combine(addonsPath, trimmed.Replace('/', Path.DirectorySeparatorChar));
                    var ddir = Path.GetDirectoryName(dest);
                    if (!string.IsNullOrEmpty(ddir) && !Directory.Exists(ddir)) Directory.CreateDirectory(ddir);
                    using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
                    var buffer = new byte[8192];
                    int read;
                    long perFileRead = 0;
                    while ((read = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await fs.WriteAsync(buffer, 0, read);
                        perFileRead += read;
                        readTotal += read;
                        var pct = total > 0 ? (int)(readTotal * 100 / total) : 0;
                        onProgress(pct);
                        var speed = readTotal / Math.Max(sw.Elapsed.TotalSeconds, 0.001);
                        var eta = total > 0 && speed > 0 ? TimeSpan.FromSeconds((total - readTotal) / speed).ToString(@"mm\:ss") : "--:--";
                        var readMb = readTotal / 1048576.0;
                        var totalMb = total / 1048576.0;
                        onInfo(speed / 1048576.0, eta, readMb, totalMb);
                        Application.DoEvents();
                    }
                }
                catch { }
            }
            onProgress(100);
        }

        // 本地檔案狀態（相對路徑、是否存在、大小）
        class FileStatus { public string RelativePath = ""; public bool LocalExists; public long LocalSize; }

        // 判斷是否首次安裝（AddOns 無任何資料夾/檔案）
        static bool IsFirstTimeInstall(string wowPath)
        {
            var basePath = wowPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? (Path.GetDirectoryName(wowPath) ?? wowPath) : wowPath;
            var retail = Path.Combine(basePath, "_retail_");
            var interfaceDir = Path.Combine(retail, "Interface");
            var addons = Path.Combine(interfaceDir, "AddOns");
            if (!Directory.Exists(addons)) return true;
            try
            {
                var hasDirs = Directory.GetDirectories(addons).Length > 0;
                var hasFiles = Directory.GetFiles(addons, "*", SearchOption.AllDirectories).Length > 0;
                return !(hasDirs || hasFiles);
            }
            catch { }
            return true;
        }

        // 列出本地 AddOns 下所有檔案的狀態
        static List<FileStatus> GetLocalFileStatuses(string wowPath)
        {
            var basePath = wowPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? (Path.GetDirectoryName(wowPath) ?? wowPath) : wowPath;
            var retail = Path.Combine(basePath, "_retail_");
            var interfaceDir = Path.Combine(retail, "Interface");
            var addons = Path.Combine(interfaceDir, "AddOns");
            var list = new List<FileStatus>();
            if (!Directory.Exists(addons)) return list;
            foreach (var file in Directory.EnumerateFiles(addons, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(addons, file).Replace('\\', '/');
                var fi = new FileInfo(file);
                list.Add(new FileStatus { RelativePath = rel, LocalExists = true, LocalSize = fi.Length });
            }
            return list;
        }

        // 由遠端檔案清單比對本地狀態，找出缺失檔案
        static List<string> IdentifyMissingFiles(List<string> remoteFiles, List<FileStatus> localStatuses)
        {
            var missing = new List<string>();
            var localSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in localStatuses) localSet.Add(s.RelativePath);
            foreach (var rf in remoteFiles) if (!localSet.Contains(rf)) missing.Add(rf);
            return missing;
        }

        // 初始檢查：列出遠端 AddOns 資料夾，標示本地缺失（不做內容比對）
        async Task RunInitialCheck()
        {
            try
            {
                statusLabel.ForeColor = Color.Gray;
                statusLabel.Text = strings.StatusConnectingGitHub;
                var wowPath = config.WowPath ?? "";

                if (string.IsNullOrWhiteSpace(wowPath))
                {
                    startButton.Text = strings.UpdateButtonChecking;
                    startButton.Enabled = false;
                    return;
                }

                var addonsPath = EnsureAddOns(wowPath);
                Log(strings.LogAddOnsPath + addonsPath);

                var token = config.GitHubToken;
                var branch = await GetDefaultBranch(token);

                statusLabel.Text = "正在檢查 GitHub 更新資訊（可能需要 10-30 秒）...";
                Log("開始獲取各組件的最後更新時間...");

                var remoteFolders = await GetAddOnsFoldersWithSha(branch, token);

                Log($"已獲取 {remoteFolders.Count} 個組件的更新資訊");

                var localDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (Directory.Exists(addonsPath))
                {
                    foreach (var dir in Directory.GetDirectories(addonsPath))
                    {
                        var name = Path.GetFileName(dir);
                        if (!string.IsNullOrEmpty(name)) localDirs.Add(name);
                    }
                }

                var missingFolders = new List<string>();
                var existingFolders = new List<string>();

                foreach (var remote in remoteFolders)
                {
                    if (!localDirs.Contains(remote.Name)) missingFolders.Add(remote.Name);
                    else existingFolders.Add(remote.Name);
                }

                componentsList.Items.Clear();

                foreach (var folder in remoteFolders.OrderBy(f => f.Name))
                {
                    var item = new ListViewItem(folder.Name);
                    if (missingFolders.Contains(folder.Name))
                    {
                        item.SubItems.Add(strings.StatusItemNeedUpdate);
                        item.ForeColor = Color.Red;
                    }
                    else
                    {
                        item.SubItems.Add(strings.StatusItemUpToDate);
                        item.ForeColor = Color.Green;
                    }
                    item.SubItems.Add(folder.LastCommitDate);
                    item.SubItems.Add(folder.LastCommitMessage);
                    componentsList.Items.Add(item);
                }

                if (missingFolders.Count == 0)
                {
                    statusLabel.ForeColor = Color.Green;
                    statusLabel.Text = strings.StatusUpToDateAll;
                    startButton.Text = strings.UpdateButtonUpToDate;
                    startButton.Enabled = true;
                    Log($"【✅ 檢查完成】{remoteFolders.Count} 個 UI 都已安裝");
                }
                else
                {
                    statusLabel.ForeColor = Color.Goldenrod;
                    statusLabel.Text = string.Format(strings.StatusNeedUpdateX, missingFolders.Count);
                    startButton.Text = strings.UpdateButtonClickToUpdate;
                    startButton.Enabled = true;
                    Log($"【📥 需要更新】{missingFolders.Count}/{remoteFolders.Count} 個 UI 需要下載");

                    if (missingFolders.Count > 0)
                    {
                        var preview = string.Join(", ", missingFolders.Take(5));
                        if (missingFolders.Count > 5) preview += "...";
                        Log($"  缺失資料夾: {preview}");
                    }
                }
            }
            catch (Exception ex)
            {
                statusLabel.ForeColor = Color.Red;
                statusLabel.Text = strings.StatusUnexpectedError;
                Log(ex.Message ?? "");
                startButton.Text = strings.UpdateButtonChecking;
                startButton.Enabled = false;
            }
        }

        // 更新 UI 清單狀態：依變更資訊標示需要更新（保留函式）
        void PopulateComponentsStatus(string addonsPath, HashSet<string> remoteDirs, ChangeInfo changes)
        {
            componentsList.Items.Clear();
            var localDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in Directory.Exists(addonsPath) ? Directory.GetDirectories(addonsPath) : Array.Empty<string>())
            {
                var name = Path.GetFileName(d);
                localDirs.Add(name);
                var needs = changes.Dirs.Contains(name);
                var item = new ListViewItem(name);
                var status = needs ? strings.StatusItemNeedUpdate : strings.StatusItemUpToDate;
                item.SubItems.Add(status);
                item.ForeColor = needs ? Color.Red : Color.Green;
                componentsList.Items.Add(item);
            }
            foreach (var name in remoteDirs)
            {
                if (!localDirs.Contains(name))
                {
                    var item = new ListViewItem(name);
                    item.SubItems.Add(strings.StatusItemNeedUpdate);
                    item.ForeColor = Color.Red;
                    componentsList.Items.Add(item);
                }
            }
        }

        // 計算待更新數量（保留函式）
        int CountPendingUpdates(HashSet<string> remoteDirs, string addonsPath, ChangeInfo changes)
        {
            var count = 0;
            var localDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in Directory.Exists(addonsPath) ? Directory.GetDirectories(addonsPath) : Array.Empty<string>())
            {
                var name = Path.GetFileName(d);
                localDirs.Add(name);
                if (changes.Dirs.Contains(name)) count++;
            }
            foreach (var name in remoteDirs)
            {
                if (!localDirs.Contains(name)) count++;
            }
            return count;
        }
    }

    // 使用者設定：路徑、已安裝提交資訊、GitHub Token
        public class Config
        {
            public string? WowPath { get; set; }
            public string? InstalledCommitSha { get; set; }
            public string? InstalledCommitDate { get; set; }
            public string? InstalledCommitMessage { get; set; }
            public string? GitHubToken { get; set; }
            public int ParallelDownloads { get; set; } = 3;
            public int BufferSizeKB { get; set; } = 8;
            public int ConnectionTimeout { get; set; } = 30;

        public static Config Load(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path, Encoding.UTF8);
                    return JsonSerializer.Deserialize<Config>(json) ?? new Config();
                }
            }
            catch { }
            return new Config();
        }

        public void Save(string path)
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json, Encoding.UTF8);
        }
    }

        // 介面字串資源（繁體中文顯示）
        public class Strings
        {
            public string Title { get; set; } = "RainbowUI 安裝程式";
            public string WowPath { get; set; } = "魔獸世界路徑";
            public string Select { get; set; } = "選擇";
            public string InstallUpdate { get; set; } = "安裝/更新";
            public string UpdateButtonChecking { get; set; } = "正在檢查...";
            public string UpdateButtonUpToDate { get; set; } = "已是最新";
            public string UpdateButtonClickToUpdate { get; set; } = "點此更新";
            public string UpdateButtonUpdating { get; set; } = "更新中...";
            public string DialogDesc { get; set; } = "選擇您的魔獸世界安裝資料夾";
            public string SelectWowTitle { get; set; } = "選擇魔獸世界安裝目錄";
            public string DialogCancel { get; set; } = "已取消資料夾選擇";
            public string NeedPath { get; set; } = "需要指定魔獸世界路徑";
            public string InvalidWowFolder { get; set; } = "請選擇 World of Warcraft 資料夾";
            public string StatusChecking { get; set; } = "檢查中";
            public string StatusUpdating { get; set; } = "更新中...";
            public string StatusDownloading { get; set; } = "下載中";
            public string StatusExtracting { get; set; } = "解壓縮中";
            public string StatusCopying { get; set; } = "複製中";
            public string StatusCompleted { get; set; } = "已完成";
            public string StatusUptodate { get; set; } = "已是最新版本";
            public string StatusUpToDateAll { get; set; } = "目前UI都是最新的";
            public string StatusNeedUpdateX { get; set; } = "目前有{0}個UI需要更新";
            public string StatusReadingLocal { get; set; } = "正在讀取本地UI設定...";
            public string StatusConnectingGitHub { get; set; } = "正在連線 GitHub 檢查更新...";
            public string StatusFirstRun { get; set; } = "首次開啟需完整下載一次，請稍後(UI設定不會消失)";
            public string StatusRateLimited { get; set; } = "GitHub 速率限制，請按下更新以執行完整下載";
            public string ForceFirstInstall { get; set; } = "第一次必須強制安裝";
            public string LogWoWPath { get; set; } = "魔獸世界路徑: ";
            public string LogAddOnsPath { get; set; } = "AddOns 路徑: ";
            public string LogDefaultBranch { get; set; } = "預設分支: ";
            public string LogLatestCommit { get; set; } = "最新提交: ";
            public string LogLatestDate { get; set; } = "最新提交日期: ";
            public string LogInstalledCommit { get; set; } = "已安裝提交: ";
            public string LogCheckingDates { get; set; } = "已安裝提交日期: ";
            public string LogDownloading { get; set; } = "下載進度: {0}%";
            public string LogExtractingEntry { get; set; } = "解壓: {0}";
            public string LogCommitMessage { get; set; } = "更新內容: ";
            public string LogChangedFoldersPre { get; set; } = "預計更新資料夾: {0}";
            public string LogChangedFilePre { get; set; } = "變更: {0}";
            public string LogCompareUnavailable { get; set; } = "無法取得變更清單";
            public string LogCopyDeletedDir { get; set; } = "刪除資料夾: {0}";
            public string LogCopyDeletedFile { get; set; } = "刪除檔案: {0}";
            public string LogCopyUpdated { get; set; } = "更新: {0}";
            public string LogCopyAdded { get; set; } = "新增: {0}";
            public string LogSummary { get; set; } = "完成，新增 {0}、更新 {1}、刪除 {2}，更新資料夾: {3}";
            public string ComponentsHeader { get; set; } = "UI組件";
            public string StatusHeader { get; set; } = "更新狀態";
            public string StatusItemUpToDate { get; set; } = "最新";
            public string StatusItemNeedUpdate { get; set; } = "需要更新";
            public string DownloadInfo { get; set; } = "速度 {0} MB/s，剩餘 {1}，已下載 {2}/{3} MB";
            public string DownloadInfoUnknown { get; set; } = "速度 {0} MB/s，已下載 {1} MB";
            public string UseGitHubTokenLabel { get; set; } = "使用 GitHub Token（可選，無限制 API 呼叫）";
            public string GitHubTokenLabel { get; set; } = "GitHub Token:";
            public string TokenSavedStatus { get; set; } = "✓ Token 已儲存（5000/小時）";
            public string TokenInvalidStatus { get; set; } = "Token 格式可能不正確";
            public string StatusUnexpectedError { get; set; } = "發生未預期錯誤，請稍後重試或檢查日誌";
            public string GitHubTokenGuideText { get; set; } = "1. 註冊並登入 GitHub 並取得 Token (https://github.com/settings/tokens)\n2. 點選 \"Generate new token\"\n3. Token name：隨意輸入，Expiration：可選擇 Token 到期日期(到期即失效)\n4. 按下 \"Generate token\" 並再次確認按下 \"Generate token\"\n5. 複製一大串的英數組合到下面貼上";
            public string GitHubTokenGuideLinkText { get; set; } = "開啟 GitHub Token 頁面";

        public static Strings Load(string path)
        {
            return new Strings();
        }
    }
}

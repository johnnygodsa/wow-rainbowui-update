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
using System.Windows.Forms;
using System.Collections.Generic;
using System.Drawing;

namespace InstallerWinForms
{
    public class MainForm : Form
    {
        TextBox pathBox;
        Button selectButton;
        Button startButton;
        ProgressBar progressBar;
        Label statusLabel;
        ListBox logList;
        Label downloadInfoLabel;
        ListView componentsList;
        bool updateReady;
        int pendingUpdateCount;
        Strings strings;
        Config config;

        public MainForm()
        {
            strings = Strings.Load(Path.Combine(AppContext.BaseDirectory, "strings.zh-TW.json"));
            config = Config.Load(Path.Combine(AppContext.BaseDirectory, "rainbow_config.json"));

            Text = strings.Title;
            StartPosition = FormStartPosition.CenterScreen;
            Width = 740;
            Height = 600;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;

            var pathLabel = new Label { Text = strings.WowPath, Left = 10, Top = 10, AutoSize = true };
            pathBox = new TextBox { Left = 10, Top = 30, Width = 440, ReadOnly = true, Text = config.WowPath ?? "" };
            selectButton = new Button { Text = strings.Select, Left = 460, Top = 30, Width = 100, Height = pathBox.Height };
            statusLabel = new Label { Left = 10, Top = 70, Width = 700, Text = "", TextAlign = ContentAlignment.MiddleCenter };
            progressBar = new ProgressBar { Left = 10, Top = 100, Width = 700, Minimum = 0, Maximum = 100 };
            downloadInfoLabel = new Label { Left = 10, Top = 130, Width = 700, Text = "", Visible = false, TextAlign = ContentAlignment.MiddleCenter };
            var tokenCheckBox = new CheckBox { Text = strings.UseGitHubTokenLabel, Left = 570, Top = 32, Width = 150, Checked = !string.IsNullOrWhiteSpace(config.GitHubToken), ForeColor = !string.IsNullOrWhiteSpace(config.GitHubToken) ? Color.Green : Color.Black };
            startButton = new Button { Text = strings.UpdateButtonChecking, Top = 240, Width = 100, Height = 32, Enabled = false };
            componentsList = new ListView { Left = 10, Top = 280, Width = 700, Height = 260, View = View.Details, FullRowSelect = true, GridLines = true };
            componentsList.Columns.Add(strings.ComponentsHeader, 420);
            componentsList.Columns.Add(strings.StatusHeader, 250);
            logList = new ListBox { Left = 10, Top = 160, Width = 700, Height = 100 };

            var f = statusLabel.Font;
            statusLabel.Font = new Font(f.FontFamily, f.Size * 1.5f, FontStyle.Bold);

            

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

            

            selectButton.Click += (s, e) =>
            {
                try
                {
                    using var dlg = new OpenFileDialog { Filter = "Executable|*.exe", Multiselect = false, Title = strings.SelectWowTitle };
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

            startButton.Click += async (s, e) => { await RunUpdateFlow(); };

            Controls.Add(pathLabel);
            Controls.Add(pathBox);
            Controls.Add(selectButton);
            Controls.Add(statusLabel);
            Controls.Add(progressBar);
            Controls.Add(downloadInfoLabel);
            Controls.Add(tokenCheckBox);
            Controls.Add(startButton);
            Controls.Add(componentsList);
            Controls.Add(logList);
            Shown += (s, e) => Activate();
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
                    await RunInitialCheck();
                }
            };
            Resize += (s, e) => {
                startButton.Left = (ClientSize.Width - startButton.Width) / 2;
            };
            startButton.Left = (ClientSize.Width - startButton.Width) / 2;
        }

        void Log(string message)
        {
            logList.Items.Add(message);
            logList.TopIndex = logList.Items.Count - 1;
        }

        void ShowTokenDialog(CheckBox tokenCheckBox)
        {
            var dialog = new Form
            {
                Text = "GitHub Token 設定",
                Width = 550,
                Height = 420,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MaximizeBox = false,
                MinimizeBox = false
            };
            var guideLabel = new Label { Text = "請依照以下步驟取得 GitHub Token:", Left = 15, Top = 15, Width = 500, Height = 20 };
            var step1 = new Label { Text = "1. 註冊並登入 GitHub 並取得 Token", Left = 15, Top = 45, Width = 500, AutoSize = true };
            var linkLabel = new LinkLabel { Text = "https://github.com/settings/tokens", Left = 25, Top = 68, Width = 300, AutoSize = true };
            linkLabel.LinkClicked += (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo { FileName = "https://github.com/settings/tokens", UseShellExecute = true }); } catch { }
            };
            var step2 = new Label { Text = "2. 點選 \"Generate new token\" (Classic)", Left = 15, Top = 95, Width = 500, AutoSize = true };
            var step3 = new Label { Text = "3. Token name: 隨意輸入, Expiration: 可選擇到期日期", Left = 15, Top = 120, Width = 500, AutoSize = true };
            var step4 = new Label { Text = "4. 不需要勾選任何權限,直接按下 \"Generate token\"", Left = 15, Top = 145, Width = 500, AutoSize = true };
            var step5 = new Label { Text = "5. 複製一大串的英數組合到下面貼上", Left = 15, Top = 170, Width = 500, AutoSize = true };
            var tokenLabel = new Label { Text = "GitHub Token:", Left = 15, Top = 210, Width = 100, AutoSize = true };
            var tokenTextBox = new TextBox { Left = 15, Top = 235, Width = 500, PasswordChar = '*', Text = config.GitHubToken ?? "" };
            var statusLabel2 = new Label { Left = 15, Top = 265, Width = 500, Height = 40, Text = "", ForeColor = Color.Green };
            if (!string.IsNullOrEmpty(config.GitHubToken)) statusLabel2.Text = "✓ 目前已儲存 Token (5000次/小時)";
            var saveButton = new Button { Text = "儲存", Left = 300, Top = 320, Width = 100, Height = 35 };
            var cancelButton = new Button { Text = "取消", Left = 415, Top = 320, Width = 100, Height = 35 };
            saveButton.Click += (s, e) =>
            {
                var token = tokenTextBox.Text.Trim();
                if (string.IsNullOrEmpty(token)) { statusLabel2.ForeColor = Color.Red; statusLabel2.Text = "✗ Token 不能為空"; return; }
                if (!token.StartsWith("ghp_") && !token.StartsWith("github_pat_") && token.Length < 30)
                {
                    statusLabel2.ForeColor = Color.Orange;
                    statusLabel2.Text = "⚠ Token 格式可能不正確,但仍會儲存";
                }
                config.GitHubToken = token;
                config.Save(Path.Combine(AppContext.BaseDirectory, "rainbow_config.json"));
                tokenCheckBox.Checked = true;
                tokenCheckBox.ForeColor = Color.Green;
                Log("已啟用 Token，API 限制: 5000次/小時");
                MessageBox.Show("Token 已成功儲存!\nAPI 速率限制: 5000次/小時", "成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                dialog.DialogResult = DialogResult.OK;
                dialog.Close();
            };
            cancelButton.Click += (s, e) =>
            {
                if (string.IsNullOrEmpty(config.GitHubToken)) { tokenCheckBox.Checked = false; tokenCheckBox.ForeColor = Color.Black; }
                dialog.DialogResult = DialogResult.Cancel;
                dialog.Close();
            };
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


        static string EnsureAddOns(string wowPath)
        {
            var basePath = wowPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? (Path.GetDirectoryName(wowPath) ?? wowPath) : wowPath;
            var retail = Path.Combine(basePath, "_retail_");
            var interfaceDir = Path.Combine(retail, "Interface");
            var addons = Path.Combine(interfaceDir, "AddOns");
            if (!Directory.Exists(addons)) Directory.CreateDirectory(addons);
            return addons;
        }

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
                var interfaceDir = Path.Combine(Path.Combine(wowPath, "_retail_"), "Interface");
                Directory.CreateDirectory(interfaceDir);
                var isFirstInstall = string.IsNullOrEmpty(config.InstalledCommitSha);
                if (isFirstInstall)
                {
                    var zipName = "rainbowui-retail-" + branch + ".zip";
                    var zipPath = Path.Combine(interfaceDir, zipName);
                    try
                    {
                        statusLabel.Text = strings.StatusDownloading;
                        progressBar.Value = 0;
                        var lastPct = -1;
                        await DownloadZipWithProgress(branch, zipPath, p => { var pct = Math.Max(0, Math.Min(100, p)); progressBar.Value = (int)(pct * 0.4); if (pct - lastPct >= 5) { lastPct = pct; Log(string.Format(strings.LogDownloading, pct)); } }, (read, total, speed) => { var mbps = speed / 1048576.0; var readMb = read / 1048576.0; string info = total > 0 && speed > 0 ? string.Format(strings.DownloadInfo, mbps.ToString("0.00"), TimeSpan.FromSeconds((total - read) / speed).ToString(@"mm\:ss"), readMb.ToString("0.0"), (total / 1048576.0).ToString("0.0")) : string.Format(strings.DownloadInfoUnknown, mbps.ToString("0.00"), readMb.ToString("0.0")); downloadInfoLabel.Visible = true; downloadInfoLabel.Text = info; });
                        downloadInfoLabel.Visible = false;
                        statusLabel.Text = strings.StatusExtracting;
                        var extractDir = Path.Combine(interfaceDir, "rainbowui-extract-" + branch);
                        Directory.CreateDirectory(extractDir);
                        await Task.Run(() => ExtractZipCompatWithProgress(zipPath, extractDir, p => progressBar.Value = 40 + (int)(Math.Max(0, Math.Min(100, p)) * 0.3), entry => Log(string.Format(strings.LogExtractingEntry, entry))));
                        var root = FindExtractedRoot(extractDir, branch);
                        var src = GetCopySource(root);
                        statusLabel.Text = strings.StatusCopying;
                        var stats = await Task.Run(() => MirrorCopyWithProgress(src, addonsPath, p => progressBar.Value = 70 + (int)(Math.Max(0, Math.Min(100, p)) * 0.3), line => { Log(line); Application.DoEvents(); }, strings));
                        dynamic latest = new { sha = "", commit = new { committer = new { date = "" } } };
                        try { latest = await GetLatestCommit(branch, token); } catch { }
                        var latestSha = ""; try { latestSha = latest.sha; } catch { }
                        if (!string.IsNullOrEmpty(latestSha)) config.InstalledCommitSha = latestSha;
                        var dateStr2 = ""; try { dateStr2 = latest.commit.committer.date; } catch { }
                        if (!string.IsNullOrEmpty(dateStr2)) config.InstalledCommitDate = dateStr2;
                        config.Save(Path.Combine(AppContext.BaseDirectory, "rainbow_config.json"));
                        progressBar.Value = 100;
                        statusLabel.Text = strings.StatusCompleted;
                        var folders = string.Join(", ", stats.UpdatedDirs);
                        Log(string.Format(strings.LogSummary, stats.Added, stats.Updated, stats.Deleted, folders));
                    }
                    finally
                    {
                        try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
                        try { var ed = Path.Combine(interfaceDir, "rainbowui-extract-" + branch); if (Directory.Exists(ed)) Directory.Delete(ed, true); } catch { }
                    }
                }
                else
                {
                    statusLabel.ForeColor = Color.Gray;
                    statusLabel.Text = strings.StatusConnectingGitHub;
                    var treeList = await GetAllFilesFromTree(branch, token);
                    var remote1 = treeList.Count > 0 ? treeList : await GetFolderFilesRecursive("Interface/AddOns", branch, token);
                    var remote2 = treeList.Count > 0 ? new List<FileToDownload>() : await GetFolderFilesRecursive("AddOns", branch, token);
                    var map = new Dictionary<string, FileToDownload>(StringComparer.OrdinalIgnoreCase);
                    void Add(FileToDownload f)
                    {
                        var rel = f.Path.Replace('\\', '/');
                        var idx = rel.IndexOf("Interface/AddOns/", StringComparison.OrdinalIgnoreCase);
                        if (idx < 0) idx = rel.IndexOf("AddOns/", StringComparison.OrdinalIgnoreCase);
                        var trimmed = idx >= 0 ? rel.Substring(idx + (rel.Substring(idx).StartsWith("Interface/AddOns/") ? "Interface/AddOns/".Length : "AddOns/".Length)) : rel;
                        if (!map.ContainsKey(trimmed)) map[trimmed] = f;
                    }
                    foreach (var f in remote1) Add(f);
                    foreach (var f in remote2) Add(f);
                    var remoteFiles = new List<string>(map.Keys);
                    var localStatuses = GetLocalFileStatuses(wowPath);
                    var missingFiles = IdentifyMissingFiles(remoteFiles, localStatuses);
                    if (missingFiles.Count == 0)
                    {
                        statusLabel.ForeColor = Color.Green;
                        statusLabel.Text = strings.StatusUpToDateAll;
                        progressBar.Value = 100;
                    }
                    else
                    {
                        statusLabel.Text = strings.StatusDownloading;
                        progressBar.Value = 0;
                        downloadInfoLabel.Visible = true;
                        var toDownload = new List<FileToDownload>();
                        foreach (var rel in missingFiles)
                        {
                            if (map.TryGetValue(rel, out var ft)) toDownload.Add(ft);
                            else
                            {
                                var pathI = "Interface/AddOns/" + rel;
                                var urlI = "https://raw.githubusercontent.com/WOWRainbowUI/RainbowUI-Retail/" + branch + "/" + pathI.Replace('\\', '/');
                                toDownload.Add(new FileToDownload { Path = pathI, Url = urlI, Size = 0 });
                            }
                        }
                        using (var handler2 = new HttpClientHandler { SslProtocols = SslProtocols.Tls12 })
                        using (var client2 = new HttpClient(handler2) { Timeout = TimeSpan.FromSeconds(20) })
                        {
                            client2.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RainbowUIInstaller", "1.0"));
                            foreach (var f in toDownload)
                            {
                                if (f.Size > 0) continue;
                                try
                                {
                                    using var resp = await client2.GetAsync(f.Url, HttpCompletionOption.ResponseHeadersRead);
                                    f.Size = resp.Content.Headers.ContentLength ?? 100000;
                                }
                                catch
                                {
                                    f.Size = 100000;
                                }
                            }
                        }
                        await DownloadSelectedFilesWithProgress(toDownload, branch, addonsPath,
                            p => { progressBar.Value = Math.Max(0, Math.Min(100, p)); },
                            (mbps, eta, readMb, totalMb) => { downloadInfoLabel.Text = string.Format(strings.DownloadInfo, mbps.ToString("0.00"), eta, readMb.ToString("0.0"), totalMb.ToString("0.0")); });
                        downloadInfoLabel.Visible = false;
                        dynamic latest = new { sha = "", commit = new { committer = new { date = "" } } };
                        var latestSha = "";
                        try { latest = await GetLatestCommit(branch, token); latestSha = latest.sha; } catch { }
                        if (!string.IsNullOrEmpty(latestSha)) config.InstalledCommitSha = latestSha;
                        var dateStr = ""; try { dateStr = latest.commit.committer.date; } catch { }
                        if (!string.IsNullOrEmpty(dateStr)) config.InstalledCommitDate = dateStr;
                        config.Save(Path.Combine(AppContext.BaseDirectory, "rainbow_config.json"));
                        progressBar.Value = 100;
                        statusLabel.Text = strings.StatusCompleted;
                    }
                }
                await RunInitialCheck();
            }
            catch (Exception ex)
            {
                var msg = ex.Message ?? "";
                if (msg.IndexOf("403", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    statusLabel.ForeColor = Color.Goldenrod;
                    statusLabel.Text = strings.StatusRateLimited;
                    startButton.Text = strings.UpdateButtonClickToUpdate;
                    startButton.Enabled = true;
                }
                else
                {
                    statusLabel.ForeColor = Color.Red;
                    statusLabel.Text = strings.StatusUnexpectedError;
                    Log(msg);
                }
            }
        }

        static HttpClient CreateGitHubHttpClient(string? token = null)
        {
            var handler = new HttpClientHandler { SslProtocols = SslProtocols.Tls12 };
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
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

        static async Task<string> GetGitHubApiAsync(string url, string? token = null)
        {
            using var client = CreateGitHubHttpClient(token);
            for (int retry = 0; retry < 3; retry++)
            {
                try
                {
                    var response = await client.GetStringAsync(url);
                    await Task.Delay(string.IsNullOrWhiteSpace(token) ? 200 : 100);
                    return response;
                }
                catch (HttpRequestException) when (retry < 2)
                {
                    await Task.Delay(2000 * (retry + 1));
                }
                catch (OperationCanceledException) when (retry < 2)
                {
                    await Task.Delay(2000 * (retry + 1));
                }
            }
            throw new InvalidOperationException("Failed to fetch from GitHub API");
        }

        static async Task<string> GetDefaultBranch(string? token = null)
        {
            var resp = await GetGitHubApiAsync("https://api.github.com/repos/WOWRainbowUI/RainbowUI-Retail", token);
            using var doc = JsonDocument.Parse(resp);
            var root = doc.RootElement;
            if (root.TryGetProperty("default_branch", out var br)) return br.GetString() ?? "master";
            return "master";
        }

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
            return new { sha, commit = new { committer = new { date } } };
        }

        class ChangeFile { public string Path = ""; public string Status = ""; }
        class ChangeInfo { public HashSet<string> Dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase); public List<string> Files = new List<string>(); public List<ChangeFile> Details = new List<ChangeFile>(); }

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

        class FileToDownload { public string Path = ""; public string Url = ""; public long Size; }
        class MissingFolderStatus { public string FolderName = ""; public bool Successful; public int FilesCount; public string ErrorMessage = ""; }

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
                            if (retry == 0) await Task.Delay(string.IsNullOrWhiteSpace(token) ? 150 : 50);
                            break;
                        }
                        catch (HttpRequestException) when (retry < 2)
                        {
                            await Task.Delay(2000 * (retry + 1));
                        }
                        catch (OperationCanceledException) when (retry < 2)
                        {
                            await Task.Delay(2000 * (retry + 1));
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

        class FileStatus { public string RelativePath = ""; public bool LocalExists; public long LocalSize; }

        static bool IsFirstTimeInstall(string wowPath)
        {
            var retail = Path.Combine(wowPath, "_retail_");
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

        static List<FileStatus> GetLocalFileStatuses(string wowPath)
        {
            var retail = Path.Combine(wowPath, "_retail_");
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

        static List<string> IdentifyMissingFiles(List<string> remoteFiles, List<FileStatus> localStatuses)
        {
            var missing = new List<string>();
            var localSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in localStatuses) localSet.Add(s.RelativePath);
            foreach (var rf in remoteFiles) if (!localSet.Contains(rf)) missing.Add(rf);
            return missing;
        }

        async Task RunInitialCheck()
        {
            try
            {
                statusLabel.ForeColor = Color.Gray;
                statusLabel.Text = strings.StatusConnectingGitHub;
                var wowPath = config.WowPath ?? "";
                if (string.IsNullOrWhiteSpace(wowPath)) { startButton.Text = strings.UpdateButtonChecking; startButton.Enabled = false; return; }
                var addonsPath = EnsureAddOns(wowPath);
                Log(strings.LogAddOnsPath + addonsPath);
                downloadInfoLabel.Visible = true;
                downloadInfoLabel.Text = strings.LogAddOnsPath + addonsPath;
                var token = config.GitHubToken;
                var branch = await GetDefaultBranch(token);
                var latest = await GetLatestCommit(branch, token);
                var latestSha = latest.sha;
                Log(strings.LogLatestCommit + latestSha);
                var isFirstInstall = string.IsNullOrEmpty(config.InstalledCommitSha);
                var remoteDirs = await GetRemoteAddOnDirs(branch, token);
                var changes = new ChangeInfo();
                if (!isFirstInstall)
                {
                    changes = await GetChangedAddOnDirsAndFiles(config.InstalledCommitSha!, latestSha, token);
                }
                PopulateComponentsStatus(addonsPath, remoteDirs, changes);
                pendingUpdateCount = CountPendingUpdates(remoteDirs, addonsPath, changes);
                updateReady = pendingUpdateCount > 0 || isFirstInstall;
                if (pendingUpdateCount == 0 && !isFirstInstall)
                {
                    statusLabel.ForeColor = Color.Green;
                    statusLabel.Text = strings.StatusUpToDateAll;
                    startButton.Text = strings.UpdateButtonUpToDate;
                    startButton.Enabled = true;
                }
                else
                {
                    statusLabel.ForeColor = Color.Goldenrod;
                    statusLabel.Text = string.Format(strings.StatusNeedUpdateX, Math.Max(pendingUpdateCount, 1));
                    startButton.Text = strings.UpdateButtonClickToUpdate;
                    startButton.Enabled = true;
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

    public class Config
    {
        public string? WowPath { get; set; }
        public string? InstalledCommitSha { get; set; }
        public string? InstalledCommitDate { get; set; }
        public string? GitHubToken { get; set; }

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

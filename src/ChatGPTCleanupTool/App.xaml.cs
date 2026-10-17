using System.Windows;

namespace ChatGPTCleanupTool;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length == 2 && e.Args[0] == "--software-create-restore-point")
        { Shutdown(Services.SoftwareRestorePointService.RunHelper(e.Args[1])); return; }
        if (e.Args.SequenceEqual(new[] { "--verify-scan-engine" }) || e.Args.SequenceEqual(new[] { "--verify-media-engine" }) || e.Args.SequenceEqual(new[] { "--verify-development-engine" }) || e.Args.SequenceEqual(new[] { "--verify-software-engine" }) || e.Args.SequenceEqual(new[] { "--verify-context-menus" }) || e.Args.SequenceEqual(new[] { "--verify-nps-engine" }) || e.Args.SequenceEqual(new[] { "--verify-overview-engine" }))
        {
            try
            {
                // Packaging diagnostic: exercise the bundled native SQLite from the single EXE, without touching user data.
                using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
                connection.Open(); using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE verify_scan(value INTEGER); INSERT INTO verify_scan VALUES(42); SELECT value FROM verify_scan";
                if (Convert.ToInt64(command.ExecuteScalar()) != 42) throw new InvalidOperationException("索引组件验证失败");
                using var notice = typeof(App).Assembly.GetManifestResourceStream("ChatGPTCleanupTool.THIRD_PARTY_NOTICES.txt");
                if (notice is null || notice.Length < 5000) throw new InvalidOperationException("组件许可没有包含在软件内");
                if (e.Args[0] == "--verify-media-engine")
                {
                    Services.WindowsMediaDecoder.VerifyAvailability();
                    var pixels = Enumerable.Range(0, 64 * 64 * 4).Select(i => (byte)(i % 251)).ToArray();
                    if (Services.MediaVisualFingerprint.Create(pixels, 64, 64).Hashes.Length != 24)
                        throw new InvalidOperationException("视觉指纹组件验证失败");
                    var samples = Enumerable.Range(0, 11025 * 8).Select(i => Math.Sin(i * .13) * .5).ToArray();
                    if (Services.MediaAudioFingerprint.Create(samples, CancellationToken.None).Hashes.Length < 28)
                        throw new InvalidOperationException("声音指纹组件验证失败");
                }
                if (e.Args[0] == "--verify-development-engine")
                {
                    var center = new Controls.DevelopmentCenter();
                    center.SetResult(new Models.DevelopmentResult([], [], 0, 0, false, ""));
                    center.Measure(new Size(1400, 700)); center.Arrange(new Rect(0, 0, 1400, 700));
                    if (center.FindName("ResultGrid") is not System.Windows.Controls.DataGrid) throw new InvalidOperationException("开发环境页面未包含在软件内");
                }
                if (e.Args[0] == "--verify-software-engine")
                {
                    var center = new Controls.SoftwareCenter();
                    center.SetResult(new Models.SoftwareResult([], [], false, DateTime.UtcNow));
                    center.Measure(new Size(1400, 700)); center.Arrange(new Rect(0, 0, 1400, 700));
                    if (center.FindName("ResultGrid") is not System.Windows.Controls.DataGrid) throw new InvalidOperationException("软件占用页面未打包");
                }
                if (e.Args[0] == "--verify-context-menus")
                {
                    var grid = new System.Windows.Controls.DataGrid { ItemsSource = new[] { @"C:\menu-packaging-fixture" } };
                    Controls.ResultContextMenu.Attach(grid, _ => { }); grid.SelectedIndex = 0;
                    var menu = Controls.ResultContextMenu.BuildForTest(grid);
                    if (!menu.Items.OfType<System.Windows.Controls.MenuItem>().Any(item => item.Tag?.ToString() == "copy-paths")) throw new InvalidOperationException("统一菜单未打包");
                    var library = System.Runtime.InteropServices.NativeLibrary.Load(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shell32.dll"));
                    try { if (!System.Runtime.InteropServices.NativeLibrary.TryGetExport(library, "SHObjectProperties", out _)) throw new InvalidOperationException("系统属性入口不可用"); }
                    finally { System.Runtime.InteropServices.NativeLibrary.Free(library); }
                }
                if (e.Args[0] == "--verify-overview-engine")
                {
                    var overview = new Controls.OverviewCenter();
                    overview.SetSnapshot(new Models.StorageOverview([new(@"C:\", "打包自检夹具", "NTFS", 1000, 100)], [], DateTime.UtcNow));
                    overview.Measure(new Size(1100, 900)); overview.Arrange(new Rect(0, 0, 1100, 900)); overview.UpdateLayout();
                    if (overview.FindName("DriveCards") is not System.Windows.Controls.ListBox { Items.Count: 1 } || FindResource("DangerButton") is not Style || FindResource("NavigationShell") is not Style) throw new InvalidOperationException("新版首页/主题未正确打包");
                }
                if (e.Args[0] == "--verify-nps-engine")
                {
                    var center = new Controls.NpsCenter(); center.SetResult(new Models.NpsScanResult([], [], true, 0, 0, 0, DateTime.UtcNow, "", ""));
                    center.Measure(new Size(1400, 800)); center.Arrange(new Rect(0, 0, 1400, 800));
                    if (!center.AllDisks || center.FindName("ProjectGrid") is not System.Windows.Controls.DataGrid || center.FindName("PlanGrid") is not null || center.FindName("DeleteButton") is not System.Windows.Controls.Button)
                        throw new InvalidOperationException("NPS页面/默认范围未正确打包");
                }
                Shutdown(0);
            }
            catch { Shutdown(1); }
            return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                $"程序遇到未处理的错误：\n\n{args.Exception.Message}",
                "ChatGPT / Codex 文件清理",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        base.OnStartup(e);
    }
}

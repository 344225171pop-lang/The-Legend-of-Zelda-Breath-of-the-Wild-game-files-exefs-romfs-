using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Provider;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;
using AndroidX.Core.View;
using Extractor.Core;
using BotwExtractor.Ui;
using DynamicColors = Google.Android.Material.Color.DynamicColors;
using MaterialSwitch = Google.Android.Material.MaterialSwitch.MaterialSwitch;
using MaterialToolbar = Google.Android.Material.AppBar.MaterialToolbar;
using Uri = Android.Net.Uri;

namespace BotwExtractor
{
    [Activity(Label = "BOTW Extractor", MainLauncher = true,
        ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.KeyboardHidden)]
    public class MainActivity : AppCompatActivity
    {
        const int ReqProdKeys = 101;
        const int ReqTitleKeys = 102;
        const int ReqBase = 103;
        const int ReqUpdate = 104;
        const int ReqOutput = 105;
        const int ReqLegacyPermission = 200;

        string _prodKeys, _titleKeys, _basePath, _updatePath, _outputDir;
        TextView _txtProdKeys, _txtTitleKeys, _txtBase, _txtUpdate, _txtOutput, _txtStatus, _txtLog;
        Button _btnStart, _btnPermission, _btnProdKeys, _btnTitleKeys, _btnBase, _btnUpdate, _btnOutput;
        MaterialSwitch _chkRomfs, _chkExefs;
        ProgressBar _progress;
        ScrollView _logScroll;
        SystemBarsInsetsListener _insetsListener;
        CancellationTokenSource _cts;
        PowerManager.WakeLock _wakeLock;
        bool _running;
        bool _hasPermission;
        readonly List<string> _logLines = new List<string>();

        protected override void OnCreate(Bundle savedInstanceState)
        {
            ThemeManager.EnsureNightMode(this);
            base.OnCreate(savedInstanceState);

            // Material You: overlay wallpaper-based dynamic color on Android 12+.
            DynamicColors.ApplyToActivityIfAvailable(this);
            // Edge-to-edge: draw behind the system bars, pad content via insets below.
            WindowCompat.SetDecorFitsSystemWindows(Window, false);

            SetContentView(Resource.Layout.activity_main);

            var toolbar = FindViewById<MaterialToolbar>(Resource.Id.toolbar);
            toolbar.Title = "BOTW 提取器";
            toolbar.InflateMenu(Resource.Menu.main_menu);
            toolbar.MenuItemClick += (s, e) =>
            {
                if (e.Item.ItemId == Resource.Id.action_settings)
                    StartActivity(typeof(SettingsActivity));
            };

            var root = FindViewById(Resource.Id.root);
            _insetsListener = new SystemBarsInsetsListener();
            ViewCompat.SetOnApplyWindowInsetsListener(root, _insetsListener);

            bool night = (Resources.Configuration.UiMode & Android.Content.Res.UiMode.NightMask)
                == Android.Content.Res.UiMode.NightYes;
            var controller = WindowCompat.GetInsetsController(Window, Window.DecorView);
            if (controller != null)
            {
                controller.AppearanceLightStatusBars = !night;
                controller.AppearanceLightNavigationBars = !night;
            }

            _txtProdKeys = FindViewById<TextView>(Resource.Id.txtProdKeys);
            _txtTitleKeys = FindViewById<TextView>(Resource.Id.txtTitleKeys);
            _txtBase = FindViewById<TextView>(Resource.Id.txtBase);
            _txtUpdate = FindViewById<TextView>(Resource.Id.txtUpdate);
            _txtOutput = FindViewById<TextView>(Resource.Id.txtOutput);
            _txtStatus = FindViewById<TextView>(Resource.Id.txtStatus);
            _txtLog = FindViewById<TextView>(Resource.Id.txtLog);
            _btnStart = FindViewById<Button>(Resource.Id.btnStart);
            _btnPermission = FindViewById<Button>(Resource.Id.btnPermission);
            _btnProdKeys = FindViewById<Button>(Resource.Id.btnProdKeys);
            _btnTitleKeys = FindViewById<Button>(Resource.Id.btnTitleKeys);
            _btnBase = FindViewById<Button>(Resource.Id.btnBase);
            _btnUpdate = FindViewById<Button>(Resource.Id.btnUpdate);
            _btnOutput = FindViewById<Button>(Resource.Id.btnOutput);
            _chkRomfs = FindViewById<MaterialSwitch>(Resource.Id.chkRomfs);
            _chkExefs = FindViewById<MaterialSwitch>(Resource.Id.chkExefs);
            _progress = FindViewById<ProgressBar>(Resource.Id.progress);
            _logScroll = FindViewById<ScrollView>(Resource.Id.logScroll);

            _btnProdKeys.Click += (s, e) => PickFile(ReqProdKeys);
            _btnTitleKeys.Click += (s, e) => PickFile(ReqTitleKeys);
            _btnBase.Click += (s, e) => PickFile(ReqBase);
            _btnUpdate.Click += (s, e) => PickFile(ReqUpdate);
            _btnOutput.Click += (s, e) => PickDir(ReqOutput);
            _btnPermission.Click += (s, e) => RequestStorage();
            _btnStart.Click += (s, e) => { if (_running) _cts?.Cancel(); else StartExtract(); };

            RefreshPermission();
            AppState.Logs.Add("INFO", "应用启动 v1.4 · " + Build.Manufacturer + " " + Build.Model +
                " · Android " + Build.VERSION.Release);
        }

        protected override void OnResume()
        {
            base.OnResume();
            RefreshPermission();
            ThemeManager.Apply(this);
        }

        void RefreshPermission()
        {
            int sdk = (int)Build.VERSION.SdkInt;
            bool ok;
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
                ok = Android.OS.Environment.IsExternalStorageManager;
            else if (sdk >= 23)
                ok = CheckSelfPermission(Android.Manifest.Permission.WriteExternalStorage) == Permission.Granted;
            else
                ok = true;
            _hasPermission = ok;
            _btnPermission.Visibility = ok ? ViewStates.Gone : ViewStates.Visible;
            UpdateStartEnabled();
        }

        void RequestStorage()
        {
            int sdk = (int)Build.VERSION.SdkInt;
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                try
                {
                    var intent = new Intent(Settings.ActionManageAppAllFilesAccessPermission,
                        Uri.Parse("package:" + PackageName));
                    StartActivity(intent);
                }
                catch
                {
                    StartActivity(new Intent(Settings.ActionManageAllFilesAccessPermission));
                }
            }
            else if (sdk >= 23)
            {
                RequestPermissions(new[]
                {
                    Android.Manifest.Permission.WriteExternalStorage,
                    Android.Manifest.Permission.ReadExternalStorage
                }, ReqLegacyPermission);
            }
        }

        public override void OnRequestPermissionsResult(int requestCode, string[] permissions, [GeneratedEnum] Permission[] grantResults)
        {
            base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
            RefreshPermission();
        }

        void PickFile(int req)
        {
            var intent = new Intent(Intent.ActionOpenDocument);
            intent.AddCategory(Intent.CategoryOpenable);
            intent.SetType("*/*");
            StartActivityForResult(intent, req);
        }

        void PickDir(int req)
        {
            var intent = new Intent(Intent.ActionOpenDocumentTree);
            StartActivityForResult(intent, req);
        }

        protected override void OnActivityResult(int requestCode, [GeneratedEnum] Result resultCode, Intent data)
        {
            base.OnActivityResult(requestCode, resultCode, data);
            if (resultCode != Result.Ok || data?.Data == null) return;
            string path = UriToPath(this, data.Data);
            if (path == null) { AppendLog("无法解析所选路径: " + data.Data); return; }
            switch (requestCode)
            {
                case ReqProdKeys: _prodKeys = path; _txtProdKeys.Text = path; break;
                case ReqTitleKeys: _titleKeys = path; _txtTitleKeys.Text = path; break;
                case ReqBase: _basePath = path; _txtBase.Text = path; break;
                case ReqUpdate: _updatePath = path; _txtUpdate.Text = path; break;
                case ReqOutput: _outputDir = path; _txtOutput.Text = path; break;
            }
            UpdateStartEnabled();
        }

        static string UriToPath(Context ctx, Uri uri)
        {
            if (uri == null) return null;
            if (uri.Scheme == "file") return uri.Path;

            string docId = null;
            try
            {
                if (DocumentsContract.IsTreeUri(uri)) docId = DocumentsContract.GetTreeDocumentId(uri);
                else if (DocumentsContract.IsDocumentUri(ctx, uri)) docId = DocumentsContract.GetDocumentId(uri);
            }
            catch { }

            // Fallback: some pickers return document IDs containing unencoded '/'
            // (more than two path segments), which the DocumentsContract helpers
            // reject. Decode the raw path after /document/ or /tree/ ourselves.
            if (docId == null)
            {
                try
                {
                    string enc = uri.EncodedPath ?? "";
                    int di = enc.IndexOf("/document/", StringComparison.Ordinal);
                    int ti = enc.IndexOf("/tree/", StringComparison.Ordinal);
                    if (di >= 0)
                    {
                        docId = Uri.Decode(enc.Substring(di + "/document/".Length));
                    }
                    else if (ti >= 0)
                    {
                        string rest = enc.Substring(ti + "/tree/".Length);
                        int sub = rest.IndexOf("/document/", StringComparison.Ordinal);
                        if (sub >= 0) rest = rest.Substring(0, sub);
                        docId = Uri.Decode(rest);
                    }
                }
                catch { }
            }

            if (docId != null)
            {
                int idx = docId.IndexOf(':');
                if (idx >= 0)
                {
                    string root = docId.Substring(0, idx);
                    string rel = docId.Substring(idx + 1);
                    string mapped = MapDocPath(root, rel);
                    if (mapped != null) return mapped;
                }
            }

            try
            {
                using var cursor = ctx.ContentResolver.Query(uri, null, null, null, null);
                if (cursor != null && cursor.MoveToFirst())
                {
                    int col = cursor.GetColumnIndex("_data");
                    if (col >= 0)
                    {
                        string d = cursor.GetString(col);
                        if (!string.IsNullOrEmpty(d)) return d;
                    }
                }
            }
            catch { }
            return null;
        }

        // Maps a SAF document id (root:rel) to a real filesystem path. Some pickers
        // put a full storage path in rel (e.g. "primary:storage/emulated/0/Download/x"),
        // which would double the prefix if we blindly prepend the base dir. Build
        // candidates and prefer the one that actually exists.
        static string MapDocPath(string root, string rel)
        {
            string ext = Android.OS.Environment.ExternalStorageDirectory.AbsolutePath;
            if (rel.Length == 0)
                return root.Equals("primary", StringComparison.OrdinalIgnoreCase) ? ext : "/storage/" + root;

            var candidates = new List<string>();
            if (rel.StartsWith("/", StringComparison.Ordinal))
            {
                candidates.Add(rel);
            }
            else if (rel.StartsWith("storage/", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add("/" + rel);
            }
            else
            {
                string baseDir = root.Equals("primary", StringComparison.OrdinalIgnoreCase) ? ext : "/storage/" + root;
                candidates.Add(baseDir + "/" + rel);
                candidates.Add("/" + rel);
            }
            foreach (var c in candidates)
                if (File.Exists(c) || Directory.Exists(c)) return c;
            return candidates[0];
        }

        void UpdateStartEnabled()
        {
            if (_running) { _btnStart.Enabled = true; return; }
            _btnStart.Enabled = _hasPermission && _prodKeys != null && _basePath != null && _outputDir != null;
        }

        void SetUiRunning(bool running)
        {
            _running = running;
            AppState.IsExtracting = running;
            _btnStart.Text = running ? "取消提取" : "开始提取";
            bool en = !running;
            _btnProdKeys.Enabled = en; _btnTitleKeys.Enabled = en; _btnBase.Enabled = en;
            _btnUpdate.Enabled = en; _btnOutput.Enabled = en;
            _chkRomfs.Enabled = en; _chkExefs.Enabled = en;
            UpdateStartEnabled();
        }

        void StartExtract()
        {
            string tempDir = Path.Combine(GetExternalFilesDir(null).AbsolutePath, "temp");
            var opt = new ExtractOptions
            {
                ProdKeysPath = _prodKeys,
                TitleKeysPath = _titleKeys,
                BasePath = _basePath,
                UpdatePath = _updatePath,
                OutputDir = _outputDir,
                TempDir = tempDir,
                ExtractRomfs = _chkRomfs.Checked,
                ExtractExefs = _chkExefs.Checked,
            };

            _cts = new CancellationTokenSource();
            var progress = new Progress<ExtractProgress>(OnProgress);
            SetUiRunning(true);
            _progress.Progress = 0;
            AcquireWakeLock();
            Window.AddFlags(WindowManagerFlags.KeepScreenOn);
            AppendLog("开始提取：" + Path.GetFileName(_basePath));

            Task.Run(() =>
            {
                try
                {
                    GameExtractor.Run(opt, progress, _cts.Token);
                    RunOnUiThread(() => { _txtStatus.Text = "完成"; AppendLog("提取完成"); SetUiRunning(false); });
                }
                catch (System.OperationCanceledException)
                {
                    RunOnUiThread(() => { _txtStatus.Text = "已取消"; AppendLog("已取消"); SetUiRunning(false); });
                }
                catch (Exception ex)
                {
                    RunOnUiThread(() => { _txtStatus.Text = "出错"; AppendLog("错误: " + ex.Message); AppendLog(ex.ToString()); SetUiRunning(false); });
                }
                finally
                {
                    ReleaseWakeLock();
                    RunOnUiThread(() => Window.ClearFlags(WindowManagerFlags.KeepScreenOn));
                }
            });
        }

        void OnProgress(ExtractProgress p)
        {
            _txtStatus.Text = p.Message;
            if (p.Percent >= 0) _progress.Progress = (int)Math.Min(100, Math.Max(0, p.Percent));
            AppendLog("[" + p.Stage + "] " + p.Message);
        }

        void AppendLog(string line)
        {
            _logLines.Add(line);
            if (_logLines.Count > 250) _logLines.RemoveRange(0, _logLines.Count - 250);
            _txtLog.Text = string.Join("\n", _logLines);
            AppState.Logs.Add("INFO", line);
            _logScroll?.Post(() => _logScroll.FullScroll(FocusSearchDirection.Down));
        }

        void AcquireWakeLock()
        {
            try
            {
                var pm = (PowerManager)GetSystemService(PowerService);
                _wakeLock = pm.NewWakeLock(WakeLockFlags.Partial, "BotwExtractor::extract");
                _wakeLock.Acquire();
            }
            catch { }
        }

        void ReleaseWakeLock()
        {
            try { if (_wakeLock != null && _wakeLock.IsHeld) _wakeLock.Release(); } catch { }
            _wakeLock = null;
        }

        protected override void OnDestroy()
        {
            _cts?.Cancel();
            ReleaseWakeLock();
            base.OnDestroy();
        }
    }

    // Applies system-bar (status + navigation) insets as padding on the root view
    // so edge-to-edge content is not obscured.
    sealed class SystemBarsInsetsListener : Java.Lang.Object, AndroidX.Core.View.IOnApplyWindowInsetsListener
    {
        public AndroidX.Core.View.WindowInsetsCompat OnApplyWindowInsets(View v, AndroidX.Core.View.WindowInsetsCompat insets)
        {
            var bars = insets.GetInsets(AndroidX.Core.View.WindowInsetsCompat.Type.SystemBars());
            v.SetPadding(bars.Left, bars.Top, bars.Right, bars.Bottom);
            return insets;
        }
    }
}

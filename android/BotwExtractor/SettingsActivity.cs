using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using Android.Util;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;
using AndroidX.Core.View;
using BotwExtractor.Ui;
using Google.Android.Material.Dialog;
using DynamicColors = Google.Android.Material.Color.DynamicColors;
using MaterialToolbar = Google.Android.Material.AppBar.MaterialToolbar;
using TextInputEditText = Google.Android.Material.TextField.TextInputEditText;
using Uri = Android.Net.Uri;

namespace BotwExtractor
{
    [Activity(Label = "设置",
        ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.KeyboardHidden)]
    public class SettingsActivity : AppCompatActivity
    {
        const int ReqExport = 301;

        ThemePrefs _prefs;
        ThemePalette _palette;
        RadioGroup _rgNight;
        TextView _txtCacheSize, _txtHexPrimary, _txtHexSecondary, _txtHexTertiary;
        bool _syncingNight;
        string _pendingExport, _pendingName;
        SystemBarsInsetsListener _insetsListener;

        protected override void OnCreate(Bundle savedInstanceState)
        {
            ThemeManager.EnsureNightMode(this);
            base.OnCreate(savedInstanceState);
            DynamicColors.ApplyToActivityIfAvailable(this);
            WindowCompat.SetDecorFitsSystemWindows(Window, false);
            SetContentView(Resource.Layout.activity_settings);

            _prefs = new ThemePrefs(new PrefsStore(this));
            _palette = _prefs.GetPalette();

            var toolbar = FindViewById<MaterialToolbar>(Resource.Id.toolbar);
            toolbar.Title = "设置";
            toolbar.SetNavigationIcon(Resource.Drawable.abc_ic_ab_back_material);
            toolbar.NavigationClick += (s, e) => Finish();

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

            _rgNight = FindViewById<RadioGroup>(Resource.Id.rgNight);
            _txtCacheSize = FindViewById<TextView>(Resource.Id.txtCacheSize);
            _txtHexPrimary = FindViewById<TextView>(Resource.Id.txtHexPrimary);
            _txtHexSecondary = FindViewById<TextView>(Resource.Id.txtHexSecondary);
            _txtHexTertiary = FindViewById<TextView>(Resource.Id.txtHexTertiary);

            _syncingNight = true;
            switch (_prefs.GetNightMode())
            {
                case NightMode.Light: _rgNight.Check(Resource.Id.rbLight); break;
                case NightMode.Dark: _rgNight.Check(Resource.Id.rbDark); break;
                default: _rgNight.Check(Resource.Id.rbSystem); break;
            }
            _syncingNight = false;
            _rgNight.CheckedChange += OnNightChanged;

            FindViewById(Resource.Id.btnPickPrimary).Click += (s, e) => ShowColorPicker(0);
            FindViewById(Resource.Id.btnPickSecondary).Click += (s, e) => ShowColorPicker(1);
            FindViewById(Resource.Id.btnPickTertiary).Click += (s, e) => ShowColorPicker(2);
            FindViewById(Resource.Id.btnSavePalette).Click += (s, e) =>
            {
                _prefs.SetPalette(_palette);
                ThemeManager.Apply(this);
                Toast("配色已保存");
            };
            FindViewById(Resource.Id.btnResetPalette).Click += (s, e) =>
            {
                _prefs.ResetPalette();
                _palette = ThemePrefs.DefaultPalette();
                RefreshPaletteUi();
                ThemeManager.Apply(this);
                Toast("已重置为默认配色");
            };
            FindViewById(Resource.Id.btnExportLog).Click += (s, e) => ShowRangeDialog();
            FindViewById(Resource.Id.btnCleanCache).Click += (s, e) => ShowCacheConfirm();

            RefreshPaletteUi();
        }

        protected override void OnResume()
        {
            base.OnResume();
            ThemeManager.Apply(this);
            RefreshCacheSize();
        }

        void OnNightChanged(object sender, RadioGroup.CheckedChangeEventArgs e)
        {
            if (_syncingNight) return;
            string mode = e.CheckedId == Resource.Id.rbLight ? NightMode.Light
                : e.CheckedId == Resource.Id.rbDark ? NightMode.Dark
                : NightMode.System;
            _prefs.SetNightMode(mode);
            ThemeManager.EnsureNightMode(this);
            Recreate();
            OverridePendingTransition(Resource.Animation.fade_in, Resource.Animation.fade_out);
        }

        void RefreshPaletteUi()
        {
            _txtHexPrimary.Text = ColorMath.ToHex(_palette.Primary);
            _txtHexSecondary.Text = ColorMath.ToHex(_palette.Secondary);
            _txtHexTertiary.Text = ColorMath.ToHex(_palette.Tertiary);
        }

        // ---- 主题颜色取色器 ----
        void ShowColorPicker(int slot)
        {
            string title = slot == 0 ? "调整主色调" : slot == 1 ? "调整辅助色" : "调整强调色";
            int current = slot == 0 ? _palette.Primary : slot == 1 ? _palette.Secondary : _palette.Tertiary;

            var view = LayoutInflater.Inflate(Resource.Layout.dialog_color_picker, null);
            var preview = view.FindViewById<View>(Resource.Id.cpPreview);
            var hexTv = view.FindViewById<TextView>(Resource.Id.cpHex);
            var r = view.FindViewById<SeekBar>(Resource.Id.cpR);
            var g = view.FindViewById<SeekBar>(Resource.Id.cpG);
            var b = view.FindViewById<SeekBar>(Resource.Id.cpB);
            var hexInput = view.FindViewById<TextInputEditText>(Resource.Id.cpHexInput);

            bool sync = false;
            void ApplyColor(int c)
            {
                preview.SetBackgroundColor(new Android.Graphics.Color(c));
                hexTv.Text = ColorMath.ToHex(c);
            }
            void FromSliders()
            {
                int c = ColorMath.Rgb(r.Progress, g.Progress, b.Progress);
                ApplyColor(c);
                hexInput.Text = ColorMath.ToHex(c);
            }

            sync = true;
            r.Progress = ColorMath.Red(current);
            g.Progress = ColorMath.Green(current);
            b.Progress = ColorMath.Blue(current);
            hexInput.Text = ColorMath.ToHex(current);
            ApplyColor(current);
            sync = false;

            EventHandler<SeekBar.ProgressChangedEventArgs> onSlide = (s, e) =>
            {
                if (sync) return;
                sync = true; FromSliders(); sync = false;
            };
            r.ProgressChanged += onSlide; g.ProgressChanged += onSlide; b.ProgressChanged += onSlide;
            hexInput.TextChanged += (s, e) =>
            {
                if (sync) return;
                var p = ColorMath.ParseHex(hexInput.Text);
                if (p == null) return;
                sync = true;
                r.Progress = ColorMath.Red(p.Value);
                g.Progress = ColorMath.Green(p.Value);
                b.Progress = ColorMath.Blue(p.Value);
                ApplyColor(p.Value);
                sync = false;
            };

            new MaterialAlertDialogBuilder(this)
                .SetTitle(title)
                .SetView(view)
                .SetPositiveButton("应用", (s, e) =>
                {
                    int c = ColorMath.Rgb(r.Progress, g.Progress, b.Progress);
                    if (slot == 0) _palette.Primary = c;
                    else if (slot == 1) _palette.Secondary = c;
                    else _palette.Tertiary = c;
                    _prefs.SetPalette(_palette);
                    RefreshPaletteUi();
                    ThemeManager.Apply(this); // 实时全局生效，无需刷新页面
                    Toast("已应用并保存");
                })
                .SetNegativeButton("取消", (EventHandler<DialogClickEventArgs>)null)
                .Show();
        }

        // ---- 日志导出 ----
        void ShowRangeDialog()
        {
            string[] opts = { "全部日志", "最近 1 小时", "最近 24 小时" };
            int sel = 0;
            new MaterialAlertDialogBuilder(this)
                .SetTitle("选择日志范围")
                .SetSingleChoiceItems(opts, 0, (s, e) => sel = e.Which)
                .SetPositiveButton("预览", (s, e) => ShowPreview((LogRangePreset)sel))
                .SetNegativeButton("取消", (EventHandler<DialogClickEventArgs>)null)
                .Show();
        }

        LogContext BuildContext(LogRangePreset preset, DateTime from, DateTime to, DateTime now)
        {
            string version = "1.4";
            try { version = PackageManager.GetPackageInfo(PackageName, 0).VersionName; } catch { }
            return new LogContext
            {
                AppVersion = version,
                Device = Build.Manufacturer + " " + Build.Model,
                AndroidVersion = Build.VERSION.Release + " (API " + (int)Build.VERSION.SdkInt + ")",
                ThemeMode = NightMode.Describe(_prefs.GetNightMode()),
                Palette = _prefs.DescribePalette(),
                ExportedAt = now,
                RangeDescription = LogRange.Describe(preset),
                RangeFrom = from,
                RangeTo = to,
            };
        }

        void ShowPreview(LogRangePreset preset)
        {
            var now = DateTime.Now;
            var (from, to) = LogRange.Resolve(preset, now);
            var entries = AppState.Logs.Filter(from, to);
            string content = LogExporter.Format(entries, BuildContext(preset, from, to, now));
            _pendingExport = content;
            _pendingName = LogExporter.SuggestedFileName(now);

            var view = LayoutInflater.Inflate(Resource.Layout.dialog_log_preview, null);
            view.FindViewById<TextView>(Resource.Id.lpSummary).Text =
                LogRange.Describe(preset) + " · " + entries.Count + " 条 · " +
                CacheScanner.FormatSize(Encoding.UTF8.GetByteCount(content));
            view.FindViewById<TextView>(Resource.Id.lpContent).Text = content;

            new MaterialAlertDialogBuilder(this)
                .SetTitle("导出预览")
                .SetView(view)
                .SetPositiveButton("导出 .log", (s, e) => StartExport())
                .SetNegativeButton("取消", (EventHandler<DialogClickEventArgs>)null)
                .Show();
        }

        void StartExport()
        {
            var intent = new Intent(Intent.ActionCreateDocument);
            intent.AddCategory(Intent.CategoryOpenable);
            intent.SetType("text/plain");
            intent.PutExtra(Intent.ExtraTitle, _pendingName);
            StartActivityForResult(intent, ReqExport);
        }

        protected override void OnActivityResult(int requestCode, [GeneratedEnum] Result resultCode, Intent data)
        {
            base.OnActivityResult(requestCode, resultCode, data);
            if (requestCode != ReqExport || resultCode != Result.Ok || data?.Data == null) return;
            try
            {
                using var stream = ContentResolver.OpenOutputStream(data.Data, "w");
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.Write(_pendingExport);
                Toast("日志已导出");
                AppState.Logs.Add("INFO", "日志已导出: " + _pendingName);
            }
            catch (Exception ex)
            {
                Toast("导出失败: " + ex.Message);
                AppState.Logs.Add("ERROR", "日志导出失败: " + ex.Message);
            }
        }

        // ---- 缓存清理 ----
        List<CacheCandidate> Candidates()
        {
            var ext = GetExternalFilesDir(null);
            string temp = ext != null ? Path.Combine(ext.AbsolutePath, "temp") : null;
            return new List<CacheCandidate>
            {
                new CacheCandidate { Key = "temp", Label = "提取临时文件", Path = temp, Protected = AppState.IsExtracting },
                new CacheCandidate { Key = "internal", Label = "应用内部缓存", Path = CacheDir?.AbsolutePath },
                new CacheCandidate { Key = "external", Label = "外部缓存", Path = ExternalCacheDir?.AbsolutePath },
                new CacheCandidate { Key = "code", Label = "代码缓存", Path = CodeCacheDir?.AbsolutePath },
            };
        }

        void RefreshCacheSize()
        {
            var items = CacheScanner.Scan(Candidates());
            _txtCacheSize.Text = "当前缓存占用：" + CacheScanner.FormatSize(CacheScanner.TotalSize(items));
        }

        void ShowCacheConfirm()
        {
            var items = CacheScanner.Scan(Candidates());
            long total = CacheScanner.TotalSize(items);

            var list = new LinearLayout(this) { Orientation = Orientation.Vertical };
            int pad = Dp(8);
            list.SetPadding(pad, pad, pad, pad);
            foreach (var item in items)
            {
                var tv = new TextView(this);
                tv.Text = item.Label + "  ·  " + CacheScanner.FormatSize(item.SizeBytes) +
                    (item.Cleanable ? "" : "（" + item.SkipReason + "）");
                tv.SetTextColor(new Android.Graphics.Color(ThemeManager.ResolveAttrColor(this,
                    item.Cleanable ? Resource.Attribute.colorOnSurface : Resource.Attribute.colorOnSurfaceVariant)));
                tv.SetPadding(0, Dp(6), 0, Dp(6));
                list.AddView(tv);
            }
            var scroll = new ScrollView(this);
            scroll.AddView(list);

            new MaterialAlertDialogBuilder(this)
                .SetTitle("确认清理缓存")
                .SetMessage("总占用 " + CacheScanner.FormatSize(total) + "，仅清理下列可用项：")
                .SetView(scroll)
                .SetPositiveButton("确认清理", (s, e) => DoClean(items))
                .SetNegativeButton("取消", (EventHandler<DialogClickEventArgs>)null)
                .Show();
        }

        void DoClean(List<CacheItem> items)
        {
            // Re-check protection right before cleaning in case a job just started.
            if (AppState.IsExtracting)
                foreach (var i in items)
                    if (i.Key == "temp") { i.Cleanable = false; i.SkipReason = "任务进行中，暂不可清理"; }

            Task.Run(() =>
            {
                var res = CacheScanner.Clean(items);
                RunOnUiThread(() =>
                {
                    RefreshCacheSize();
                    string msg = "已释放 " + CacheScanner.FormatSize(res.FreedBytes) +
                        "（" + res.RemovedFiles + " 个文件）";
                    if (!res.Success) msg += "\n部分项清理失败：" + string.Join("；", res.Errors);
                    AppState.Logs.Add("INFO", "缓存清理完成: " + msg);
                    new MaterialAlertDialogBuilder(this)
                        .SetTitle(res.Success ? "清理完成" : "清理完成（有异常）")
                        .SetMessage(msg)
                        .SetPositiveButton("确定", (EventHandler<DialogClickEventArgs>)null)
                        .Show();
                });
            });
        }

        int Dp(int v) => (int)TypedValue.ApplyDimension(ComplexUnitType.Dip, v, Resources.DisplayMetrics);
        void Toast(string msg) => Android.Widget.Toast.MakeText(this, msg, ToastLength.Short).Show();
    }
}

using Android.App;
using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Util;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;
using AndroidX.Core.View;
using BotwExtractor.Ui;
using MaterialButton = Google.Android.Material.Button.MaterialButton;
using MaterialSwitch = Google.Android.Material.MaterialSwitch.MaterialSwitch;
using MaterialToolbar = Google.Android.Material.AppBar.MaterialToolbar;
using LinearProgressIndicator = Google.Android.Material.ProgressIndicator.LinearProgressIndicator;

namespace BotwExtractor
{
    // SharedPreferences-backed store for the pure ThemePrefs logic.
    public sealed class PrefsStore : ISettingsStore
    {
        readonly ISharedPreferences _prefs;
        public PrefsStore(Context ctx) =>
            _prefs = ctx.GetSharedPreferences("botw_settings", FileCreationMode.Private);
        public string Get(string key) => _prefs.GetString(key, null);
        public void Set(string key, string value) => _prefs.Edit().PutString(key, value).Apply();
        public void Remove(string key) => _prefs.Edit().Remove(key).Apply();
    }

    // Applies the persisted night mode and the (optional) custom palette to an
    // activity's live view tree. Idempotent: calling it with no custom palette
    // restores the theme defaults, so "reset" takes effect without a restart.
    public static class ThemeManager
    {
        public static void EnsureNightMode(Context ctx)
        {
            var prefs = new ThemePrefs(new PrefsStore(ctx));
            AppCompatDelegate.DefaultNightMode = NightMode.ToAppCompatMode(prefs.GetNightMode());
        }

        public static int ResolveAttrColor(Context ctx, int attr)
        {
            var tv = new TypedValue();
            ctx.Theme.ResolveAttribute(attr, tv, true);
            return tv.Data;
        }

        public static void Apply(Activity a)
        {
            if (a == null) return;
            var prefs = new ThemePrefs(new PrefsStore(a));
            bool custom = prefs.HasCustomPalette;
            var pal = prefs.GetPalette();

            int surface = ResolveAttrColor(a, Resource.Attribute.colorSurface);
            int primary = custom ? pal.Primary : ResolveAttrColor(a, Resource.Attribute.colorPrimary);
            int secondary = custom ? pal.Secondary : ResolveAttrColor(a, Resource.Attribute.colorSecondary);
            int tertiary = custom ? pal.Tertiary : ResolveAttrColor(a, Resource.Attribute.colorTertiary);
            int onPrimary = custom ? ColorMath.OnColor(primary) : ResolveAttrColor(a, Resource.Attribute.colorOnPrimary);
            int onSurface = ResolveAttrColor(a, Resource.Attribute.colorOnSurface);

            // Text drawn directly on the surface must stay readable in both modes.
            int headerColor = ColorMath.EnsureContrast(primary, surface, 4.5);

            int tonalBg = custom ? ColorMath.Blend(secondary, surface, 0.16)
                                 : ResolveAttrColor(a, Resource.Attribute.colorSecondaryContainer);
            int tonalText = custom ? ColorMath.EnsureContrast(secondary, tonalBg, 4.5)
                                   : ResolveAttrColor(a, Resource.Attribute.colorOnSecondaryContainer);

            // Toolbar: flat M3 style, no coloured fill. The app bar sits on the
            // same surface as the content, so title/icons use the on-surface
            // colours and the primary tone stays reserved for actions.
            var toolbar = a.FindViewById<MaterialToolbar>(Resource.Id.toolbar);
            if (toolbar != null)
            {
                int onSurfaceVariant = ResolveAttrColor(a, Resource.Attribute.colorOnSurfaceVariant);
                toolbar.SetBackgroundColor(new Color(surface));
                toolbar.SetTitleTextColor(new Color(onSurface));
                toolbar.SetNavigationIconTint(new Color(onSurfaceVariant));

                // Menu icons must follow the same contrast, otherwise the platform
                // glyph turns invisible on a dark surface.
                var menu = toolbar.Menu;
                if (menu != null)
                {
                    for (int i = 0; i < menu.Size(); i++)
                        MenuItemCompat.SetIconTintList(menu.GetItem(i), Tint(onSurfaceVariant));
                }
            }

            // Filled buttons
            foreach (int id in new[] { Resource.Id.btnStart, Resource.Id.btnExportLog,
                                       Resource.Id.btnCleanCache, Resource.Id.btnSavePalette })
                SetButton(a, id, primary, onPrimary);

            // Tonal buttons
            foreach (int id in new[] { Resource.Id.btnPermission, Resource.Id.btnProdKeys,
                                       Resource.Id.btnTitleKeys, Resource.Id.btnBase,
                                       Resource.Id.btnUpdate, Resource.Id.btnOutput,
                                       Resource.Id.btnResetPalette, Resource.Id.btnPickPrimary,
                                       Resource.Id.btnPickSecondary, Resource.Id.btnPickTertiary })
                SetButton(a, id, tonalBg, tonalText);

            // Switches
            foreach (int id in new[] { Resource.Id.chkRomfs, Resource.Id.chkExefs })
            {
                var sw = a.FindViewById<MaterialSwitch>(id);
                if (sw == null) continue;
                if (custom)
                {
                    sw.ThumbTintList = Tint(primary);
                    sw.TrackTintList = Tint(ColorMath.Blend(primary, surface, 0.3));
                }
                else
                {
                    sw.ThumbTintList = null;
                    sw.TrackTintList = null;
                }
            }

            // Progress indicator
            var progress = a.FindViewById<LinearProgressIndicator>(Resource.Id.progress);
            if (progress != null)
            {
                progress.SetIndicatorColor(primary);
                progress.TrackColor = custom ? ColorMath.Blend(primary, surface, 0.2)
                                             : ResolveAttrColor(a, Resource.Attribute.colorSurfaceVariant);
            }

            // Section headers
            foreach (int id in new[] { Resource.Id.txtSecKeys, Resource.Id.txtSecFiles,
                                       Resource.Id.txtSecOutput, Resource.Id.txtSecOptions,
                                       Resource.Id.txtSecLog, Resource.Id.setSecAppearance,
                                       Resource.Id.setSecTheme, Resource.Id.setSecLog,
                                       Resource.Id.setSecCache })
            {
                var tv = a.FindViewById<TextView>(id);
                if (tv != null) tv.SetTextColor(new Color(headerColor));
            }

            // Palette swatches in settings
            SetSwatch(a, Resource.Id.swPrimary, primary);
            SetSwatch(a, Resource.Id.swSecondary, secondary);
            SetSwatch(a, Resource.Id.swTertiary, tertiary);

            // System-bar icons follow the resolved surface colour: the content is
            // inset-padded, so both bars are drawn over colorSurface (not over the
            // coloured app bar). Deriving this from the live colour also keeps
            // dynamic colour / custom palettes readable instead of trusting the
            // night-mode flag alone.
            var insets = WindowCompat.GetInsetsController(a.Window, a.Window.DecorView);
            if (insets != null)
            {
                bool darkIcons = ColorMath.RelativeLuminance(surface) > 0.5;
                insets.AppearanceLightStatusBars = darkIcons;
                insets.AppearanceLightNavigationBars = darkIcons;
            }
        }

        static void SetButton(Activity a, int id, int bg, int text)
        {
            var b = a.FindViewById<MaterialButton>(id);
            if (b == null) return;
            b.BackgroundTintList = Tint(bg);
            b.SetTextColor(new Color(text));
        }

        static void SetSwatch(Activity a, int id, int color)
        {
            var v = a.FindViewById<View>(id);
            if (v != null) v.SetBackgroundColor(new Color(color));
        }

        static ColorStateList Tint(int c) =>
            new ColorStateList(new[] { new int[0] }, new[] { c });
    }
}

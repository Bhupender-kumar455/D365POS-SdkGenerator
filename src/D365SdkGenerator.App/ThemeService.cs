using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace D365SdkGenerator.App;

internal enum AppTheme
{
    Light,
    Dark
}

/// <summary>
/// Swaps the palette dictionary merged by App.xaml. Every themed brush is referenced with
/// DynamicResource, so replacing the dictionary updates the window without a restart.
/// </summary>
internal static class ThemeService
{
    private const string LightSource = "Themes/Light.xaml";
    private const string DarkSource = "Themes/Dark.xaml";

    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmUseImmersiveDarkModeBefore20H1 = 19;

    public static AppTheme Current { get; private set; } = AppTheme.Light;

    public static AppTheme Parse(string? value) =>
        string.Equals(value, nameof(AppTheme.Dark), StringComparison.OrdinalIgnoreCase)
            ? AppTheme.Dark
            : AppTheme.Light;

    public static void Apply(AppTheme theme)
    {
        var application = Application.Current;
        if (application is null)
        {
            return;
        }

        var merged = application.Resources.MergedDictionaries;
        var palette = new ResourceDictionary
        {
            Source = new Uri(theme == AppTheme.Dark ? DarkSource : LightSource, UriKind.Relative)
        };

        var index = -1;

        for (var candidate = 0; candidate < merged.Count; candidate++)
        {
            var source = merged[candidate].Source?.OriginalString;

            if (source is not null &&
                (source.EndsWith("Light.xaml", StringComparison.OrdinalIgnoreCase) ||
                 source.EndsWith("Dark.xaml", StringComparison.OrdinalIgnoreCase)))
            {
                index = candidate;
                break;
            }
        }

        if (index >= 0)
        {
            merged[index] = palette;
        }
        else
        {
            merged.Insert(0, palette);
        }

        Current = theme;

        foreach (Window window in application.Windows)
        {
            ApplyWindowChrome(window);
        }
    }

    /// <summary>
    /// Paints the native title bar to match the theme. Best effort: older Windows builds
    /// simply keep the default chrome.
    /// </summary>
    public static void ApplyWindowChrome(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;

            if (handle == IntPtr.Zero)
            {
                return;
            }

            var enabled = Current == AppTheme.Dark ? 1 : 0;

            if (DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(
                    handle,
                    DwmUseImmersiveDarkModeBefore20H1,
                    ref enabled,
                    sizeof(int));
            }
        }
        catch
        {
            // The title bar is cosmetic; never let it break startup or the toggle.
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref int value,
        int size);
}

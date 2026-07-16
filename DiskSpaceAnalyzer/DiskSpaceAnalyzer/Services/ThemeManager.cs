using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using WpfApplication = System.Windows.Application;

namespace DiskSpaceAnalyzer.Services;

public static class ThemeManager
{
    private const string LightThemePath = "Themes/LightTheme.xaml";
    private const string DarkThemePath = "Themes/DarkTheme.xaml";
    private const string ThemePackPrefix = "pack://application:,,,/DiskSpaceAnalyzer;component/";
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmUseImmersiveDarkModeBefore20H1 = 19;

    private static readonly string PreferenceFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "simplePC",
        "DiskSpaceAnalyzer",
        "theme.txt");

    public static bool IsDarkTheme { get; private set; }

    public static event EventHandler? ThemeChanged;

    public static void Initialize()
    {
        IsDarkTheme = LoadPreference();
        ReplaceThemeDictionary(IsDarkTheme);
    }

    public static void SetDarkTheme(bool isDarkTheme)
    {
        if (IsDarkTheme == isDarkTheme)
        {
            return;
        }

        IsDarkTheme = isDarkTheme;
        ReplaceThemeDictionary(isDarkTheme);
        SavePreference(isDarkTheme);
        ApplyThemeToOpenWindows();
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    public static void RegisterWindow(Window window)
    {
        window.SourceInitialized += (_, _) => ApplyTitleBarTheme(window);
    }

    private static void ReplaceThemeDictionary(bool isDarkTheme)
    {
        var dictionaries = WpfApplication.Current.Resources.MergedDictionaries;
        var currentTheme = dictionaries.FirstOrDefault(dictionary => IsThemeDictionary(dictionary.Source));
        var newTheme = new ResourceDictionary
        {
            Source = new Uri(
                ThemePackPrefix + (isDarkTheme ? DarkThemePath : LightThemePath),
                UriKind.Absolute)
        };

        if (currentTheme is null)
        {
            dictionaries.Insert(0, newTheme);
            return;
        }

        dictionaries[dictionaries.IndexOf(currentTheme)] = newTheme;
    }

    private static bool IsThemeDictionary(Uri? source)
    {
        var value = source?.OriginalString;
        return value?.EndsWith(LightThemePath, StringComparison.OrdinalIgnoreCase) == true ||
               value?.EndsWith(DarkThemePath, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool LoadPreference()
    {
        try
        {
            return File.Exists(PreferenceFilePath) &&
                   string.Equals(File.ReadAllText(PreferenceFilePath).Trim(), "dark", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void SavePreference(bool isDarkTheme)
    {
        try
        {
            var directory = Path.GetDirectoryName(PreferenceFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(PreferenceFilePath, isDarkTheme ? "dark" : "light");
        }
        catch
        {
            // Keep the selected theme for this session if settings cannot be persisted.
        }
    }

    private static void ApplyThemeToOpenWindows()
    {
        foreach (Window window in WpfApplication.Current.Windows)
        {
            ApplyTitleBarTheme(window);
        }
    }

    private static void ApplyTitleBarTheme(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var enabled = IsDarkTheme ? 1 : 0;
        if (DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(handle, DwmUseImmersiveDarkModeBefore20H1, ref enabled, sizeof(int));
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr windowHandle, int attribute, ref int value, int attributeSize);
}

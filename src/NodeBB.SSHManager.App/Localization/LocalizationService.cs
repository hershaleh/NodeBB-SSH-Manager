using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Windows;

namespace NodeBB.SSHManager.Localization;

public sealed class LocalizationService : INotifyPropertyChanged
{
    private static readonly Lazy<LocalizationService> LazyInstance = new(() => new LocalizationService());
    private readonly string _settingsPath;
    private string _language = "he";

    private static readonly IReadOnlyDictionary<string, (string He, string En)> Texts =
        new Dictionary<string, (string He, string En)>(StringComparer.Ordinal)
        {
            ["Dashboard_מרכז_שליטה_מאובטח"] = ("מרכז שליטה מאובטח", "Secure control center"),
            ["Dashboard_הוספת_שרת_חדש"] = ("＋  הוספת שרת חדש", "＋  Add new server"),
            ["Dashboard_סביבת_עבודה"] = ("סביבת עבודה", "Workspace"),
            ["Dashboard_שרתים_שמורים"] = ("שרתים שמורים", "Saved servers"),
            ["Dashboard_היסטוריית_פעולות"] = ("◷   היסטוריית פעולות", "◷   Activity history"),
            ["Dashboard_ניטור_שרת"] = ("⌁   ניטור שרת", "⌁   Server monitoring"),
            ["Dashboard_אבחון_שרת"] = ("אבחון שרת", "Server diagnostics"),
            ["Dashboard_שירותי_מערכת"] = ("⚙   שירותי מערכת", "⚙   System services"),
            ["Dashboard_כלים"] = ("כלים", "Tools"),
            ["Dashboard_השוואת_שרתים"] = ("⇄   השוואת שרתים", "⇄   Compare servers"),
            ["Dashboard_דוח_אבחון"] = ("▤   דוח אבחון", "▤   Diagnostics report"),
            ["Dashboard_תפריט_מתקדם"] = ("⋯   תפריט מתקדם", "⋯   More options"),
            ["Dashboard_ייבוא_פרופילי_שרתים"] = ("ייבוא פרופילי שרתים", "Import server profiles"),
            ["Dashboard_ייצוא_פרופילי_שרתים"] = ("ייצוא פרופילי שרתים", "Export server profiles"),
            ["Dashboard_בדיקת_עדכונים"] = ("בדיקת עדכונים", "Check for updates"),
            ["Dashboard_היסטוריית_פעולות_מכל_השרתים"] = ("היסטוריית פעולות מכל השרתים", "All-server activity history"),
            ["Dashboard_דוח_אבחון_האפליקציה"] = ("דוח אבחון האפליקציה", "Application diagnostics report"),
            ["Dashboard_השוואת_שרתים_מחוברים"] = ("השוואת שרתים מחוברים", "Compare connected servers"),
            ["Dashboard_בדיקת_ניטור_שרת"] = ("בדיקת ניטור שרת", "Server monitoring check"),
            ["Dashboard_אימות_SSH_קשיח"] = ("אימות SSH קשיח", "Strict SSH verification"),
            ["Dashboard_טביעת_מפתח_השרת_נבדקת_לפני_כל_חיבור"] = ("טביעת מפתח השרת נבדקת לפני כל חיבור.", "The server host key is verified before every connection."),
            ["Dashboard_לוח_בקרה"] = ("לוח בקרה", "Dashboard"),
            ["Dashboard_ניהול_שרתי_NodeBB_חיבורי_SSH_ופעולות_תחזוקה"] = ("ניהול שרתי NodeBB, חיבורי SSH ופעולות תחזוקה", "Manage NodeBB servers, SSH connections, and maintenance tasks"),
            ["Dashboard_ניהול_מקומי_מאובטח"] = ("ניהול מקומי מאובטח", "Secure local management"),
            ["Dashboard_הוסף_שרת"] = ("＋  הוסף שרת", "＋  Add server"),
            ["Dashboard_שרתים_במאגר"] = ("שרתים במאגר", "Servers in inventory"),
            ["Dashboard_חיבורים_פעילים"] = ("חיבורים פעילים", "Active connections"),
            ["Dashboard_חיבורי_SSH_פתוחים"] = ("חיבורי SSH פתוחים", "Open SSH sessions"),
            ["Dashboard_אישורים_שמורים"] = ("אישורים שמורים", "Saved credentials"),
            ["Dashboard_מוגנים_באמצעות_DPAPI"] = ("מוגנים באמצעות DPAPI", "Protected with DPAPI"),
            ["Dashboard_בוא_נתחיל_לנהל_את_השרתים_שלך"] = ("בוא נתחיל לנהל את השרתים שלך", "Start managing your servers"),
            ["Dashboard_הוסף_פרטי_חיבור_לשרת_NodeBB_כדי_לקבל_גישה_לניהול"] = ("הוסף פרטי חיבור לשרת NodeBB כדי לקבל גישה לניהול תוספים, גיבויים, שירותים, קבצים וניטור — ממקום אחד.", "Add a NodeBB server connection to manage plugins, backups, services, files, and monitoring from one place."),
            ["Dashboard_הוספת_שרת_ראשון"] = ("＋  הוספת שרת ראשון", "＋  Add your first server"),
            ["Dashboard_הסיסמאות_נשמרות_בהצפנת_Windows_DPAPI_אם_תבחר_בכך"] = ("הסיסמאות נשמרות בהצפנת Windows DPAPI, אם תבחר בכך.", "Passwords are protected with Windows DPAPI if you choose to save them."),
            ["Dashboard_השרתים_שלך"] = ("השרתים שלך", "Your servers"),
            ["Dashboard_בחר_שרת_כדי_להתחבר_לפתוח_את_מרכז_הניהול_או_לנהל_"] = ("בחר שרת כדי להתחבר, לפתוח את מרכז הניהול או לנהל קבצים.", "Select a server to connect, open its management center, or manage files."),
            ["Dashboard_סינון_שרתים_לפי_קבוצה"] = ("סינון שרתים לפי קבוצה", "Filter servers by group"),
            ["Dashboard_חיפוש_לפי_שם_שרת_כתובת_או_משתמש"] = ("חיפוש לפי שם שרת, כתובת או משתמש", "Search by server name, host, or username"),
            ["Dashboard_0_שרתים"] = ("0 שרתים", "0 servers"),
            ["Dashboard_בדיקת_כל_השרתים"] = ("בדיקת כל השרתים", "Check all servers"),
            ["Dashboard_הוסף_למועדפים"] = ("הוסף למועדפים", "Add to favorites"),
            ["Dashboard_פתח_סביבת_ניהול"] = ("פתח סביבת ניהול", "Open management"),
            ["Dashboard_בדיקת_חיבור"] = ("בדיקת חיבור", "Test connection"),
            ["Dashboard_עריכת_פרופיל"] = ("עריכת פרופיל", "Edit profile"),
            ["Dashboard_שכפול_פרופיל"] = ("שכפול פרופיל", "Duplicate profile"),
            ["Dashboard_העתק_פקודת_SSH"] = ("העתק פקודת SSH", "Copy SSH command"),
            ["Dashboard_שכח_פרטי_התחברות"] = ("שכח פרטי התחברות", "Forget credentials"),
            ["Dashboard_הסר_פרופיל_מהמחשב"] = ("הסר פרופיל מהמחשב", "Remove profile from this computer"),
            ["Dashboard_פעולות_לשרת_הנבחר"] = ("פעולות לשרת הנבחר", "Actions for selected server"),
            ["Dashboard_בחר_שרת_מהרשימה"] = ("בחר שרת מהרשימה", "Select a server from the list"),
            ["Dashboard_התחבר"] = ("התחבר", "Connect"),
            ["Dashboard_נתק"] = ("נתק", "Disconnect"),
            ["Dashboard_מרכז_ניהול"] = ("מרכז ניהול", "Management center"),
            ["Dashboard_קבצים"] = ("קבצים", "Files"),
            ["Dashboard_טרמינל"] = ("טרמינל", "Terminal"),
            ["Dashboard_עריכה"] = ("עריכה", "Edit"),
            ["Dashboard_שכח_אישור"] = ("שכח אישור", "Forget credential"),
            ["Dashboard_הסר"] = ("הסר", "Remove"),
        };

    public static LocalizationService Instance => LazyInstance.Value;
    public event PropertyChangedEventHandler? PropertyChanged;

    public LocalizationService(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NodeBB SSH Manager", "language.json");
        Load();
        ApplyCulture();
    }

    public string Language => _language;
    public FlowDirection FlowDirection => _language == "en" ? FlowDirection.LeftToRight : FlowDirection.RightToLeft;

    public string this[string key] =>
        Texts.TryGetValue(key, out var text) ? (_language == "en" ? text.En : text.He) : key;

    public void SetLanguage(string language)
    {
        if (language is not ("he" or "en"))
            throw new ArgumentOutOfRangeException(nameof(language), "Supported languages are 'he' and 'en'.");
        if (_language == language) return;
        _language = language;
        ApplyCulture();
        Save();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FlowDirection)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return;
            using var document = JsonDocument.Parse(File.ReadAllText(_settingsPath));
            if (document.RootElement.TryGetProperty("language", out var value) &&
                value.ValueKind == JsonValueKind.String &&
                value.GetString() is "he" or "en")
                _language = value.GetString()!;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _language = "he";
        }
    }

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_settingsPath);
            if (string.IsNullOrWhiteSpace(directory)) return;
            Directory.CreateDirectory(directory);
            var temporary = _settingsPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { language = _language }));
            File.Move(temporary, _settingsPath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void ApplyCulture()
    {
        var culture = CultureInfo.GetCultureInfo(_language == "en" ? "en-US" : "he-IL");
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }
}

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
            ["View_7b27caba"] = ("הוספת שרת", "Add server"),
            ["View_4986861e"] = ("הוספת שרת NodeBB", "Add a NodeBB server"),
            ["View_b7fa1e4e"] = ("יצירת פרופיל חיבור SSH מאובטח", "Create a secure SSH connection profile"),
            ["View_48cd3954"] = ("סודות אינם נשמרים בפרופיל השרת", "Secrets are not stored in the server profile"),
            ["View_c602cc93"] = ("זיהוי השרת", "Server identity"),
            ["View_9d668988"] = ("שם לזיהוי", "Display name"),
            ["View_26b55ee6"] = ("קבוצה (לא חובה)", "Group (optional)"),
            ["View_4e8343b2"] = ("למשל: ייצור, בדיקות, לקוחות או פרויקטים", "For example: Production, Testing, Clients, or Projects"),
            ["View_59db7204"] = ("פרטי חיבור", "Connection details"),
            ["View_be23c9f5"] = ("כתובת שרת או IP", "Server address or IP"),
            ["View_68c96b34"] = ("פורט SSH", "SSH port"),
            ["View_22223bc1"] = ("שם משתמש SSH", "SSH username"),
            ["View_338c25a1"] = ("אימות וזהות", "Authentication and identity"),
            ["View_7c1fb1c9"] = ("שיטת אימות", "Authentication method"),
            ["View_96f6490a"] = ("מפתח SSH", "SSH key"),
            ["View_c70acef8"] = ("סיסמה", "Password"),
            ["View_1bf5e8e7"] = ("נתיב לקובץ מפתח פרטי", "Private key file path"),
            ["View_5d796ef6"] = ("בחירת קובץ...", "Browse..."),
            ["View_55e03a1"] = ("סיסמה או Passphrase יתבקשו בעת בדיקת החיבור ולא יישמרו בקובץ הפרופיל. יש לאמת את טביעת מפתח השרת לפני אמון בחיבור.", "A password or passphrase is requested when testing the connection and is not stored in the profile. Verify the server host-key fingerprint before trusting the connection."),
            ["View_7765c0d0"] = ("שמור פרופיל שרת", "Save server profile"),
            ["View_5b36e7e4"] = ("ביטול", "Cancel"),
            ["View_e27597b"] = ("גיבויים", "Backups"),
            ["View_240265a6"] = ("מרכז הגיבויים", "Backup center"),
            ["View_2c4c4f43"] = ("יצירה, הצפנה ושחזור של קובצי ההתקנה", "Create, encrypt, and restore installation archives"),
            ["View_fd7d51ad"] = ("יצירת גיבוי", "Create backup"),
            ["View_1950b118"] = ("גיבוי התקנה מלא", "Full installation backup"),
            ["View_b80ba14b"] = ("ארכיון של כל תיקיית NodeBB, כולל קבצים ותלויות. מתאים לשמירת תמונת מצב מלאה; הקובץ עשוי להיות גדול.", "An archive of the entire NodeBB directory, including files and dependencies. Suitable for a complete snapshot; the archive may be large."),
            ["View_f91633f"] = ("יצירת גיבוי מלא", "Create full backup"),
            ["View_977fec9f"] = ("גיבוי הגדרות והעלאות", "Configuration and uploads backup"),
            ["View_4fc4fee"] = ("כולל config.json, קובצי package/lock וקובצי public/uploads שנמצאו. מסד הנתונים אינו נכלל.", "Includes config.json, package/lock files, and detected public/uploads files. The database is not included."),
            ["View_fce7068"] = ("יצירת גיבוי ממוקד", "Create focused backup"),
            ["View_8caec710"] = ("הגדרות גיבוי ואבטחה", "Backup and security settings"),
            ["View_22ef24ed"] = ("מדיניות שמירת גיבויים", "Backup retention policy"),
            ["View_1c0abbc7"] = ("הניקוי חל רק על גיבויים של השרת הזה ובאותו סוג גיבוי. ברירת המחדל אינה מוחקת גיבויים.", "Cleanup applies only to this server and backup type. By default, backups are never deleted."),
            ["View_e7f1e06b"] = ("ללא מחיקה אוטומטית", "No automatic deletion"),
            ["View_669d6bda"] = ("לשמור 5 גיבויים אחרונים", "Keep the 5 most recent backups"),
            ["View_310fc59a"] = ("לשמור 10 גיבויים אחרונים", "Keep the 10 most recent backups"),
            ["View_38d5ef7d"] = ("לשמור 20 גיבויים אחרונים", "Keep the 20 most recent backups"),
            ["View_f738bd4f"] = ("הצפנת קובץ הגיבוי", "Encrypt backup file"),
            ["View_695bc2a3"] = ("מומלץ להצפין גיבויים לפני שמירתם במחשב. הסיסמה אינה נשמרת, ובלעדיה לא ניתן יהיה לפענח את הקובץ.", "Encrypt backups before saving them on this computer. The password is not stored; without it, the archive cannot be decrypted."),
            ["View_3c1c3d4e"] = ("סיסמת הצפנה (לפחות 12 תווים)", "Encryption password (at least 12 characters)"),
            ["View_fe307a6c"] = ("אימות סיסמה", "Confirm password"),
            ["View_305878ca"] = ("גיבוי מתוזמן אוטומטי", "Automatic scheduled backup"),
            ["View_16c4f328"] = ("תדירות", "Frequency"),
            ["View_4ac81aff"] = ("כל יום", "Daily"),
            ["View_bb37fbbc"] = ("פעם בשבוע", "Weekly"),
            ["View_b8f5e88d"] = ("שעה (שעון מקומי)", "Time (local)"),
            ["View_88a81fb9"] = ("יום בשבוע (לתזמון שבועי)", "Day of week (for weekly schedules)"),
            ["View_ed174715"] = ("יום ראשון", "Sunday"),
            ["View_20c2982"] = ("יום שני", "Monday"),
            ["View_87676f7c"] = ("יום שלישי", "Tuesday"),
            ["View_1d7db2fd"] = ("יום רביעי", "Wednesday"),
            ["View_e6f2b2f8"] = ("יום חמישי", "Thursday"),
            ["View_7747f2ea"] = ("יום שישי", "Friday"),
            ["View_1bba3ff"] = ("שבת", "Saturday"),
            ["View_6419e3bf"] = ("סוג הגיבוי", "Backup type"),
            ["View_4601f848"] = ("קובצי התקנה מלאים (ללא מסד נתונים)", "Full installation files (excluding database)"),
            ["View_f303c912"] = ("הגדרות והעלאות בלבד", "Configuration and uploads only"),
            ["View_fc460576"] = ("תיקיית יעד מקומית", "Local destination folder"),
            ["View_6b5e51a"] = ("בחירת תיקייה", "Choose folder"),
            ["View_31eb32ab"] = ("כלול גם גיבוי מסד נתונים (קובץ מוצפן נפרד)", "Also include a database backup (separate encrypted file)"),
            ["View_e5e77227"] = ("יצירת תזמון גיבוי", "Create backup schedule"),
            ["View_4dfc59bf"] = ("תזמונים קיימים להתקנה זו", "Existing schedules for this installation"),
            ["View_33617566"] = ("הפעלת הגיבוי כעת", "Run backup now"),
            ["View_e8ee3b36"] = ("הסרת התזמון הנבחר", "Remove selected schedule"),
            ["View_af768ef6"] = ("שחזור גיבוי הגדרות והעלאות", "Restore configuration and uploads backup"),
            ["View_a2f4463d"] = ("סיסמת הגיבוי (רק אם הקובץ מוצפן)", "Backup password (only if encrypted)"),
            ["View_172a11b6"] = ("בחירת גיבוי ושחזור", "Select backup and restore"),
            ["View_b30a36e8"] = ("שחזור התקנה מלאה (החלפת כל תיקיית NodeBB)", "Restore full installation (replace entire NodeBB directory)"),
            ["View_e0b88daf"] = ("סגור", "Close"),
            ["View_3a27971e"] = ("מסד נתונים", "Database"),
            ["View_1d70a888"] = ("ניהול מסד הנתונים", "Database management"),
            ["View_ab414473"] = ("תצורה, גיבויים ושחזור — ללא חשיפת סיסמאות", "Configuration, backup, and restore — without exposing passwords"),
            ["View_2530da07"] = ("↻  רענון תצורה", "↻  Refresh configuration"),
            ["View_6d2ba710"] = ("תצורת החיבור", "Connection configuration"),
            ["View_cc80de82"] = ("סוג מסד נתונים", "Database type"),
            ["View_c50b982d"] = ("שרת מארח", "Host"),
            ["View_199bfb9"] = ("יציאה", "Port"),
            ["View_4ed6dc52"] = ("שם מסד נתונים", "Database name"),
            ["View_559e139"] = ("שם משתמש מוגדר", "Username configured"),
            ["View_1bb55bfc"] = ("סיסמה מוגדרת", "Password configured"),
            ["View_4905c002"] = ("כתובת URI מוגדרת", "URI configured"),
            ["View_44e75afe"] = ("גיבוי מסד הנתונים", "Database backup"),
            ["View_d076e34f"] = ("יצירת גיבוי MongoDB מוצפן", "Create encrypted MongoDB backup"),
            ["View_3d3b70c9"] = ("יצירת גיבוי PostgreSQL מוצפן", "Create encrypted PostgreSQL backup"),
            ["View_5ae66b8e"] = ("יצירת גיבוי Redis מוצפן", "Create encrypted Redis backup"),
            ["View_8041e2c7"] = ("שחזור מסד נתונים", "Database restore"),
            ["View_d393e18c"] = ("סיסמת הגיבוי", "Backup password"),
            ["View_74d55278"] = ("בחירת גיבוי ושחזור PostgreSQL", "Select and restore PostgreSQL backup"),
            ["View_661c2e9c"] = ("בחירת גיבוי ושחזור MongoDB", "Select and restore MongoDB backup"),
            ["View_db1d4897"] = ("פעולה זו משנה את הנתונים במסד היעד. יש לוודא שנבחר גיבוי השייך לשרת ולמסד הנכונים.", "This operation changes data in the target database. Verify that the selected backup belongs to the correct server and database."),
            ["View_32452b5d"] = ("אבחון ויומני פעילות", "Diagnostics and activity logs"),
            ["View_2c7a9d06"] = ("מרכז האבחון", "Diagnostics center"),
            ["View_56fd87e0"] = ("איסוף מידע טכני לצורך איתור תקלות", "Collect technical information for troubleshooting"),
            ["View_f554d4b7"] = ("הדוח נוצר מקומית ונשאר בשליטתך", "The report is created locally and remains under your control"),
            ["View_4636ba71"] = ("תוכן דוח האבחון", "Diagnostic report contents"),
            ["View_307c2aca"] = ("יומני אירועים ושגיאות", "Event and error logs"),
            ["View_15551014"] = ("רכיבים, פעולות, תאריכים ופרטי חריגה", "Components, actions, dates, and exception details"),
            ["View_aab01e0a"] = ("מידע על סביבת העבודה", "Environment information"),
            ["View_d6cc3b14"] = ("פרטיות ואבטחה", "Privacy and security"),
            ["View_cf23c74b"] = ("מוכן לייצוא.", "Ready to export."),
            ["View_7b19099d"] = ("יצירת דוח אבחון ZIP", "Create ZIP diagnostic report"),
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

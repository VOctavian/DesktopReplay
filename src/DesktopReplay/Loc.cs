using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Markup;

namespace DesktopReplay;

/// <summary>RU/EN strings. XAML: {l:T Key}. Code: Loc.T("Key").</summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc I { get; } = new();
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Language { get; private set; } = "ru";

    public void SetLanguage(string lang)
    {
        Language = lang == "en" ? "en" : "ru";
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }

    public string this[string key] => Strings.TryGetValue(key, out var v) ? (Language == "en" ? v.En : v.Ru) : key;

    public static string T(string key) => I[key];
    public static string F(string key, params object[] args) => string.Format(I[key], args);

    private static readonly Dictionary<string, (string Ru, string En)> Strings = new()
    {
        // Common
        ["AppName"] = ("DesktopReplay", "DesktopReplay"),
        ["On"] = ("ВКЛ", "ON"),
        ["Off"] = ("ВЫКЛ", "OFF"),
        ["Save"] = ("Сохранить", "Save"),
        ["Cancel"] = ("Отмена", "Cancel"),
        ["Close"] = ("Закрыть", "Close"),
        ["Browse"] = ("Обзор…", "Browse…"),
        ["Add"] = ("Добавить", "Add"),
        ["Remove"] = ("Удалить", "Remove"),
        ["Open"] = ("Открыть", "Open"),
        ["Exit"] = ("Выход", "Exit"),

        // Home
        ["Replay"] = ("Мгновенный повтор", "Instant Replay"),
        ["SaveReplay"] = ("Сохранить повтор", "Save replay"),
        ["TurnOn"] = ("Включить", "Turn on"),
        ["TurnOff"] = ("Выключить", "Turn off"),
        ["Buffered"] = ("В буфере {0} из {1}", "Buffered {0} of {1}"),
        ["Record"] = ("Запись", "Record"),
        ["StartRecording"] = ("Начать запись", "Start recording"),
        ["StopRecording"] = ("Остановить", "Stop"),
        ["RecordingFor"] = ("Идёт запись · {0}", "Recording · {0}"),
        ["NotRecording"] = ("Запись не идёт", "Not recording"),
        ["Screenshot"] = ("Скриншот", "Screenshot"),
        ["TakeScreenshot"] = ("Сделать снимок", "Take screenshot"),
        ["Gallery"] = ("Папка записей", "Recordings folder"),
        ["OpenFolder"] = ("Открыть папку", "Open folder"),
        ["StorageUsed"] = ("{0} из {1}", "{0} of {1}"),
        ["StorageNoLimit"] = ("{0} · лимит не задан", "{0} · no limit"),
        ["Settings"] = ("Настройки", "Settings"),
        ["SettingsHint"] = ("Качество, звук, клавиши", "Quality, audio, hotkeys"),
        ["ActiveApp"] = ("Активное приложение: {0}", "Active app: {0}"),
        ["EscToClose"] = ("Esc или {0} — закрыть", "Esc or {0} to close"),
        ["EngineError"] = ("Ошибка захвата: {0}", "Capture error: {0}"),

        // Tracks
        ["Track1"] = ("Всё вместе", "Mix"),
        ["Track2"] = ("Игра", "Game"),
        ["Track3"] = ("Микрофон", "Microphone"),
        ["Track4"] = ("Система", "Desktop"),
        ["Track5"] = ("Discord", "Discord"),

        // Notifications
        ["N.ReplaySaved"] = ("Повтор сохранён", "Replay saved"),
        ["N.RecordingStarted"] = ("Запись начата", "Recording started"),
        ["N.RecordingStopped"] = ("Запись сохранена", "Recording saved"),
        ["N.ScreenshotSaved"] = ("Скриншот сохранён", "Screenshot saved"),
        ["N.ReplayOn"] = ("Мгновенный повтор включён", "Instant Replay on"),
        ["N.ReplayOff"] = ("Мгновенный повтор выключен", "Instant Replay off"),
        ["N.ReplayNotActive"] = ("Мгновенный повтор выключен — нечего сохранять", "Instant Replay is off — nothing to save"),
        ["N.Error"] = ("Ошибка", "Error"),
        ["N.Storage"] = ("Папка записей заполнена на {0:F0}%", "Recordings folder is {0:F0}% full"),
        ["N.StorageDetail"] = ("Занято {0} из {1}. Осталось {2} (≈ {3} мин записи)", "Used {0} of {1}. {2} left (≈ {3} min of recording)"),
        ["N.HotkeyConflict"] = ("Горячая клавиша {0} занята другой программой — используется перехват клавиатуры", "Hotkey {0} is taken by another app — using a keyboard hook instead"),
        ["N.UpdateReady"] = ("Обновление {0} загружено — установится при перезапуске", "Update {0} downloaded — installs on restart"),

        // Tray
        ["Tray.Overlay"] = ("Открыть оверлей", "Open overlay"),
        ["Tray.Replay"] = ("Мгновенный повтор", "Instant Replay"),
        ["Tray.Save"] = ("Сохранить повтор", "Save replay"),
        ["Tray.Record"] = ("Начать / остановить запись", "Start / stop recording"),
        ["Tray.Screenshot"] = ("Скриншот", "Screenshot"),
        ["Tray.Folder"] = ("Открыть папку записей", "Open recordings folder"),
        ["Tray.TooltipOn"] = ("DesktopReplay — повтор включён", "DesktopReplay — replay on"),
        ["Tray.TooltipOff"] = ("DesktopReplay — повтор выключен", "DesktopReplay — replay off"),
        ["Tray.TooltipRec"] = ("DesktopReplay — идёт запись", "DesktopReplay — recording"),

        // Settings: sections
        ["S.General"] = ("Общие", "General"),
        ["S.Video"] = ("Видео", "Video"),
        ["S.Audio"] = ("Звук", "Audio"),
        ["S.Storage"] = ("Файлы", "Files"),
        ["S.Hotkeys"] = ("Горячие клавиши", "Hotkeys"),
        ["S.Privacy"] = ("Исключения", "Exclusions"),
        ["S.About"] = ("О программе", "About"),
        ["S.RestartNote"] = ("Изменение видео-, звуковых настроек или папки буфера перезапускает захват — текущий буфер повтора будет очищен.", "Changing video/audio settings or the buffer folder restarts capture — the current replay buffer is cleared."),

        // General
        ["S.Language"] = ("Язык", "Language"),
        ["S.AutoStart"] = ("Запускать вместе с Windows", "Start with Windows"),
        ["S.AutoStartHint"] = ("Приложение стартует свёрнутым в трей и сразу включает буфер повтора.", "Starts minimized to tray and turns on the replay buffer."),
        ["S.ReplayEnabled"] = ("Мгновенный повтор включён", "Instant Replay enabled"),
        ["S.ReplayLength"] = ("Длина повтора", "Replay length"),
        ["S.ReplayLengthHint"] = ("Сколько последних минут сохраняется по горячей клавише. Буфер хранится на диске, размер ≈ {0}.", "How much is saved by the hotkey. The buffer lives on disk, size ≈ {0}."),
        ["S.NotifySound"] = ("Звук при сохранении", "Sound on save"),
        ["S.NotifyPopup"] = ("Всплывающее уведомление", "Pop-up notification"),
        ["S.NotifyHint"] = ("Уведомления не попадают в запись.", "Notifications never appear in recordings."),
        ["S.CheckUpdates"] = ("Проверять обновления (GitHub)", "Check for updates (GitHub)"),

        // Video
        ["S.Monitors"] = ("Мониторы", "Monitors"),
        ["S.MonitorsHint"] = ("Можно выбрать несколько — на каждый монитор пишется отдельное видео со всеми звуковыми дорожками.", "Pick several to get a separate video per monitor, each with all audio tracks."),
        ["S.Fps"] = ("Частота кадров", "Frame rate"),
        ["S.Resolution"] = ("Разрешение", "Resolution"),
        ["S.ResNative"] = ("Как у монитора", "Native"),
        ["S.ResolutionHint"] = ("Меньше разрешение — меньше файлы. Пропорции монитора сохраняются; увеличения выше родного нет.", "Lower resolution = smaller files. Aspect ratio is kept; never upscales."),
        ["S.Codec"] = ("Кодек", "Codec"),
        ["S.Quality"] = ("Качество", "Quality"),
        ["S.Bitrate"] = ("Битрейт, Мбит/с", "Bitrate, Mbps"),
        ["S.Estimate"] = ("≈ {0} МБ за минуту записи · буфер повтора ≈ {1}", "≈ {0} MB per minute · replay buffer ≈ {1}"),
        ["S.Cursor"] = ("Записывать курсор мыши", "Capture mouse cursor"),
        ["S.Hdr"] = ("HDR → SDR", "HDR → SDR"),
        ["S.HdrHint"] = ("Только для мониторов с включённым HDR: захват в HDR и преобразование в обычный цветовой диапазон, чтобы видео не было блёклым.", "HDR monitors only: capture in HDR and convert to SDR so the video doesn't look washed out."),

        ["Fps24"] = ("24 к/с — кинематографично, самые маленькие файлы", "24 fps — cinematic, smallest files"),
        ["Fps30"] = ("30 к/с — по умолчанию, хороший баланс размера и плавности", "30 fps — default, good size/smoothness balance"),
        ["Fps60"] = ("60 к/с — плавно для динамичных игр, файлы ≈ в 1,7 раза больше", "60 fps — smooth for fast games, ≈1.7× bigger files"),
        ["Fps120"] = ("120 к/с — для замедленного воспроизведения, большие файлы", "120 fps — for slow motion, big files"),
        ["Fps144"] = ("144 к/с — максимум, нужен быстрый диск", "144 fps — maximum, needs a fast disk"),

        ["CodecH264"] = ("H.264 — работает везде (Discord, Telegram, браузеры, любые редакторы). Рекомендуется.", "H.264 — plays everywhere (Discord, Telegram, browsers, any editor). Recommended."),
        ["CodecHevc"] = ("HEVC (H.265) — на 30–40% меньше файлы при том же качестве, но не везде проигрывается в браузере и Discord.", "HEVC (H.265) — 30–40% smaller at the same quality, but not every browser/Discord plays it."),
        ["CodecAv1"] = ("AV1 — ещё компактнее HEVC. Только видеокарты RTX 40xx и новее, хуже поддержка в редакторах.", "AV1 — even smaller than HEVC. RTX 40xx or newer only; weaker editor support."),
        ["CodecUnavailable"] = ("{0} — не поддерживается вашей видеокартой", "{0} — not supported by your GPU"),

        ["QLow"] = ("Низкое — экономия места, заметные артефакты в динамике", "Low — saves space, visible artifacts in motion"),
        ["QMedium"] = ("Среднее — нормально для Discord/соцсетей", "Medium — fine for Discord/social"),
        ["QHigh"] = ("Высокое — по умолчанию, почти без потерь на глаз", "High — default, visually near lossless"),
        ["QUltra"] = ("Максимум — для монтажа, большие файлы", "Ultra — for editing, big files"),
        ["QCustom"] = ("Своё — задать битрейт вручную", "Custom — set bitrate manually"),

        // Audio
        ["S.TracksInfo"] = ("Дорожки в файле: 1 — всё вместе (её проигрывают Discord/браузеры), 2 — игра/активное приложение, 3 — микрофон, 4 — системные звуки (без игры и Discord), 5 — Discord.", "Tracks: 1 — mix (what Discord/browsers play), 2 — game/active app, 3 — microphone, 4 — desktop sounds (without game and Discord), 5 — Discord."),
        ["S.Mic"] = ("Микрофон", "Microphone"),
        ["S.MicDefault"] = ("Микрофон по умолчанию (Windows)", "Default microphone (Windows)"),
        ["S.MicMode"] = ("Режим микрофона", "Microphone mode"),
        ["MicAlways"] = ("Всегда включён", "Always on"),
        ["MicPtt"] = ("Push-to-talk — пишется только пока зажата клавиша", "Push-to-talk — recorded only while the key is held"),
        ["MicPtm"] = ("Push-to-mute — выключается, пока зажата клавиша", "Push-to-mute — muted while the key is held"),
        ["S.PushKey"] = ("Клавиша", "Key"),
        ["S.PushKeyHint"] = ("Нажмите клавишу или боковую кнопку мыши. Клавиша не перехватывается — игра её тоже получит.", "Press a key or a mouse side button. The key is not blocked — the game still gets it."),
        ["S.Noise"] = ("Шумоподавление микрофона", "Microphone noise suppression"),
        ["S.NoiseHint"] = ("Убирает постоянный фон (вентиляторы, гул). Немного добавляет нагрузки на процессор.", "Removes steady background noise (fans, hum). Slight CPU cost."),
        ["S.Volumes"] = ("Громкость дорожек", "Track volume"),
        ["S.InMix"] = ("в общей", "in mix"),
        ["S.AudioBitrate"] = ("Битрейт звука (на дорожку)", "Audio bitrate (per track)"),
        ["S.AudioBitrateHint"] = ("192 кбит/с — прозрачное качество. 128 — экономнее, 320 — для монтажа.", "192 kbps is transparent. 128 saves space, 320 for editing."),

        // Storage
        ["S.Output"] = ("Папка повторов", "Replays folder"),
        ["S.RecordingsFolder"] = ("Папка записей (старт/стоп)", "Recordings folder (start/stop)"),
        ["S.ScreenshotsFolder"] = ("Папка скриншотов", "Screenshots folder"),
        ["S.SameAsReplays"] = ("Пусто — та же, что для повторов", "Empty = same as replays"),
        ["S.Reset"] = ("Сбросить", "Reset"),
        ["S.Subfolders"] = ("Подпапки по играм", "Subfolders per game"),
        ["S.MaxSizeAll"] = ("Лимит считается по всем трём папкам вместе.", "The limit counts all three folders together."),
        ["S.Buffer"] = ("Папка буфера повтора", "Replay buffer folder"),
        ["S.BufferHint"] = ("Лучше SSD. Буфер постоянно перезаписывается; при выходе из программы удаляется.", "SSD recommended. Constantly rewritten; deleted on exit."),
        ["S.MaxSize"] = ("Лимит места, ГБ (0 — без лимита)", "Space limit, GB (0 = none)"),
        ["S.WarnAt"] = ("Предупреждать при заполнении, %", "Warn when full, %"),
        ["S.MaxSizeHint"] = ("Файлы не удаляются автоматически. После каждого сохранения и раз в минуту во время записи вы увидите, сколько места осталось.", "Nothing is deleted automatically. After each save and every minute while recording you'll see how much space is left."),
        ["S.KeepMkv"] = ("Сохранять исходный MKV после перепаковки в MP4", "Keep the source MKV after remuxing to MP4"),
        ["S.KeepMkvHint"] = ("Запись идёт в MKV (не портится при сбое), затем без перекодирования перепаковывается в MP4.", "Recording goes to MKV (crash-safe), then is remuxed to MP4 without re-encoding."),
        ["S.ScreenshotFormat"] = ("Формат скриншотов", "Screenshot format"),
        ["PngHint"] = ("PNG — без потерь", "PNG — lossless"),
        ["JpgHint"] = ("JPG — меньше размер", "JPG — smaller"),
        ["S.Usage"] = ("Сейчас занято: {0}", "Currently used: {0}"),

        // Hotkeys
        ["H.SaveReplay"] = ("Сохранить повтор", "Save replay"),
        ["H.ToggleReplay"] = ("Включить/выключить повтор", "Toggle Instant Replay"),
        ["H.ToggleRecording"] = ("Начать/остановить запись", "Start/stop recording"),
        ["H.Screenshot"] = ("Скриншот", "Screenshot"),
        ["H.Overlay"] = ("Открыть оверлей", "Open overlay"),
        ["H.Hint"] = ("Щёлкните поле и нажмите сочетание. Backspace — очистить. Если NVIDIA Overlay включён, отключите его горячие клавиши, иначе будет конфликт.", "Click a field and press a combination. Backspace clears. If NVIDIA Overlay is on, disable its hotkeys to avoid conflicts."),
        ["H.PressKeys"] = ("Нажмите клавиши…", "Press keys…"),
        ["H.Duplicate"] = ("Одно сочетание назначено на несколько действий", "Same combination used for several actions"),

        // Exclusions
        ["X.Title"] = ("Окна-исключения", "Excluded windows"),
        ["X.Hint"] = ("Окна этих программ закрашиваются чёрным в видео и на скриншотах (например, менеджер паролей).", "Windows of these apps are blacked out in videos and screenshots (e.g. a password manager)."),
        ["X.Running"] = ("Выбрать из запущенных…", "Pick from running apps…"),
        ["X.Manual"] = ("или имя процесса, например KeePass.exe", "or process name, e.g. KeePass.exe"),

        // About
        ["A.Version"] = ("Версия {0}", "Version {0}"),
        ["A.CheckNow"] = ("Проверить обновления", "Check for updates"),
        ["A.UpToDate"] = ("Установлена последняя версия", "You're up to date"),
        ["A.NotInstalled"] = ("Обновления работают только в установленной версии", "Updates work only in the installed version"),
        ["A.Checking"] = ("Проверка…", "Checking…"),
        ["A.Downloading"] = ("Загрузка {0}…", "Downloading {0}…"),
        ["A.RestartToUpdate"] = ("Перезапустить и обновить", "Restart to update"),
        ["A.Logs"] = ("Открыть логи", "Open logs"),
        ["A.Repo"] = ("Страница проекта", "Project page"),
    };
}

/// <summary>XAML markup extension: Text="{l:T Replay}" — updates live when the language changes.</summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }
    public TExtension(string key) => Key = key;
    [ConstructorArgument("key")] public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider sp) =>
        new Binding($"[{Key}]") { Source = Loc.I, Mode = BindingMode.OneWay }.ProvideValue(sp);
}

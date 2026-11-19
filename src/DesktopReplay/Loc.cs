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

        // Gallery
        ["G.Title"] = ("Галерея", "Gallery"),
        ["G.Search"] = ("Поиск по имени или игре…", "Search by name or game…"),
        ["G.TileSize"] = ("Размер плиток (Ctrl + колесо)", "Tile size (Ctrl + wheel)"),
        ["G.Refresh"] = ("Обновить (F5)", "Refresh (F5)"),
        ["G.AllGames"] = ("Все игры", "All games"),
        ["G.Other"] = ("Без игры", "No game"),
        ["G.Sort"] = ("Сортировка", "Sort"),
        ["G.SortNewest"] = ("Сначала новые", "Newest first"),
        ["G.SortOldest"] = ("Сначала старые", "Oldest first"),
        ["G.SortShortest"] = ("Сначала короткие", "Shortest first"),
        ["G.SortLongest"] = ("Сначала длинные", "Longest first"),
        ["G.SortSize"] = ("По размеру", "By size"),
        ["G.SortName"] = ("По имени", "By name"),
        ["G.Today"] = ("Сегодня", "Today"),
        ["G.Yesterday"] = ("Вчера", "Yesterday"),
        ["G.ThisWeek"] = ("Эта неделя", "This week"),
        ["G.LastWeek"] = ("Прошлая неделя", "Last week"),
        ["G.Older"] = ("Старше 2 недель", "Older than 2 weeks"),
        ["G.Date"] = ("Дата", "Date"),
        ["G.PickDate"] = ("Выбрать дату", "Pick a date"),
        ["G.Clear"] = ("Очистить", "Clear"),
        ["G.Length"] = ("Длительность", "Length"),
        ["G.From"] = ("от", "from"),
        ["G.To"] = ("до", "to"),
        ["G.Sec"] = ("сек", "sec"),
        ["G.Min"] = ("мин", "min"),
        ["G.Hour"] = ("ч", "h"),
        ["G.ResetFilters"] = ("Сбросить фильтры", "Reset filters"),
        ["G.Count"] = ("{0} видео · {1}", "{0} videos · {1}"),
        ["G.EmptyFolder"] = ("Здесь пока нет видео", "No videos yet"),
        ["G.NothingFound"] = ("Ничего не найдено — измените фильтры", "Nothing found — adjust the filters"),
        ["G.Preview"] = ("Просмотр", "Preview"),
        ["G.Cut"] = ("Обрезать", "Cut"),
        ["G.OpenPlayer"] = ("Открыть в плеере", "Open in player"),
        ["G.Delete"] = ("Удалить", "Delete"),
        ["G.TabVideos"] = ("Видео", "Videos"),
        ["G.TabShots"] = ("Скриншоты", "Screenshots"),
        ["G.CountShots"] = ("{0} скриншотов · {1}", "{0} screenshots · {1}"),
        ["G.EmptyShots"] = ("Скриншотов пока нет", "No screenshots yet"),
        ["G.SelectHint"] = ("Выбрать (Ctrl+клик, Shift+клик — диапазон)", "Select (Ctrl+click, Shift+click for a range)"),
        ["G.Selected"] = ("Выбрано: {0} · {1}", "Selected: {0} · {1}"),
        ["G.SelectAll"] = ("Выбрать все", "Select all"),
        ["G.ClearSelection"] = ("Снять выделение (Esc)", "Clear selection (Esc)"),
        ["G.Copy"] = ("Копировать", "Copy"),
        ["G.CopyHint"] = ("Копировать файл — вставьте в Discord или Telegram через Ctrl+V", "Copy the file — paste into Discord or Telegram with Ctrl+V"),
        ["G.Copied"] = ("Скопировано файлов: {0} — вставьте через Ctrl+V", "Copied {0} file(s) — paste with Ctrl+V"),
        ["G.CopyFailed"] = ("Не удалось скопировать в буфер обмена", "Could not copy to the clipboard"),
        ["G.DeleteManyConfirm"] = ("Переместить {0} файлов ({1}) в корзину?", "Move {0} files ({1}) to the Recycle Bin?"),
        ["G.DeleteFailed"] = ("Не удалось удалить:\n{0}", "Could not delete:\n{0}"),
        ["G.DeleteConfirm"] = ("Переместить «{0}» в корзину?", "Move \"{0}\" to the Recycle Bin?"),

        // Clip editor
        ["E.ShortsDrag"] = ("Перетащите рамку · двойной клик — по центру", "Drag the frame · double-click to center"),
        ["E.ShortsCentered"] = ("По центру · перетащите, чтобы сдвинуть", "Centered · drag to move"),
        ["E.ShortsCenter"] = ("По центру", "Center"),
        ["E.ShortsCenterHint"] = ("Вернуть рамку 9:16 в центр кадра", "Put the 9:16 frame back in the middle"),
        ["E.ToStart"] = ("К началу фрагмента (Home)", "To fragment start (Home)"),
        ["E.PrevFrame"] = ("Предыдущий кадр (Ctrl+←) · ← / Shift+← — назад на 5 / 15 с", "Previous frame (Ctrl+←) · ← / Shift+← — back 5 / 15 s"),
        ["E.NextFrame"] = ("Следующий кадр (Ctrl+→) · → / Shift+→ — вперёд на 5 / 15 с", "Next frame (Ctrl+→) · → / Shift+→ — forward 5 / 15 s"),
        ["E.Play"] = ("Воспроизвести / пауза (Пробел)", "Play / pause (Space)"),
        ["E.Volume"] = ("Громкость", "Volume"),
        ["E.Listen"] = ("Слушать", "Listen"),
        ["E.ListenHint"] = ("Какой звук играть при просмотре", "Which audio to play in the preview"),
        ["E.ListenResult"] = ("Как в итоговом файле", "As in the saved file"),
        ["E.Fullscreen"] = ("Полный экран (F)", "Full screen (F)"),
        ["E.Selection"] = ("Фрагмент", "Fragment"),
        ["E.Length"] = ("Длина фрагмента", "Fragment length"),
        ["E.Seconds"] = ("сек", "sec"),
        ["E.Whole"] = ("Всё видео", "Whole video"),
        ["E.Shorts"] = ("Shorts — вертикальное 9:16", "Shorts — vertical 9:16"),
        ["E.Name"] = ("Имя файла", "File name"),
        ["E.Format"] = ("Формат", "Format"),
        ["E.PresetOriginal"] = ("Исходное качество · MP4", "Original quality · MP4"),
        ["E.PresetDiscord"] = ("Discord · до 20 МБ · MP4 H.264", "Discord · up to 20 MB · MP4 H.264"),
        ["E.PresetGif"] = ("GIF · без звука · 15 к/с", "GIF · no sound · 15 fps"),
        ["E.DiscordHint"] = ("Битрейт и разрешение подбираются под 20 МБ, звук — одна дорожка", "Bitrate and resolution fit 20 MB, one audio track"),
        ["D.Button"] = ("Discord", "Discord"),
        ["D.Hint"] = ("Для Discord: сжать до 20 МБ и скопировать — затем Ctrl+V в чате. Файл хранится только во временной папке",
                      "For Discord: compress to 20 MB and copy — then Ctrl+V in the chat. The file is kept only in the temp folder"),
        ["D.EditorHint"] = ("Для Discord: выбранный фрагмент с вашими правками, до 20 МБ, сразу в буфер обмена",
                            "For Discord: the selected fragment with your edits, up to 20 MB, straight to the clipboard"),
        ["D.Working"] = ("Готовлю для Discord… {0}%", "Preparing for Discord… {0}%"),
        ["D.Done"] = ("Готово для Discord ({0}) — вставьте в чат через Ctrl+V", "Ready for Discord ({0}) — paste into the chat with Ctrl+V"),
        ["D.Failed"] = ("Не удалось подготовить для Discord: {0}", "Could not prepare for Discord: {0}"),
        ["E.DiscordLong"] = ("Фрагмент длинный — качество будет низким. Укоротите до 1–2 минут", "Long fragment — quality will be low. Trim to 1–2 minutes"),
        ["E.GifHint"] = ("480 px по ширине (Shorts — 640 px по высоте)", "480 px wide (Shorts: 640 px tall)"),
        ["E.GifLong"] = ("GIF длиннее 30 с получится очень тяжёлым", "A GIF longer than 30 s will be very large"),
        ["E.Tracks"] = ("Звуковые дорожки", "Audio tracks"),
        ["E.Merge"] = ("Свести выбранные в одну", "Merge selected into one"),
        ["E.NoAudio"] = ("В файле нет звука", "No audio in this file"),
        ["E.Track"] = ("Дорожка {0}", "Track {0}"),
        ["E.TrackInclude"] = ("Снимите галочку, чтобы убрать дорожку", "Untick to remove the track"),
        ["E.TrackVolume"] = ("Громкость дорожки", "Track volume"),
        ["E.MergedTitle"] = ("Сведённый звук", "Merged audio"),
        ["E.SaveCopy"] = ("Сохранить отдельно", "Save as new"),
        ["E.Replace"] = ("Заменить оригинал", "Replace original"),
        ["E.Exporting"] = ("Сохранение…", "Saving…"),
        ["E.ExportingPct"] = ("Сохранение… {0}%", "Saving… {0}%"),
        ["E.Cancelled"] = ("Отменено", "Cancelled"),
        ["E.ExportFailed"] = ("Не удалось сохранить: {0}", "Save failed: {0}"),
        ["E.KeptTemp"] = ("Результат сохранён как {0}", "Result kept as {0}"),
        ["E.Saved"] = ("Сохранено: {0}", "Saved: {0}"),
        ["E.OpenFailed"] = ("Не удалось открыть файл: {0}", "Cannot open the file: {0}"),
        ["E.PlayerFailed"] = ("Windows не может воспроизвести этот файл. Для HEVC установите «Расширения для видео HEVC» из Microsoft Store. Обрезка и сохранение всё равно работают.",
                              "Windows cannot play this file. For HEVC install \"HEVC Video Extensions\" from the Microsoft Store. Cutting and saving still work."),

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
        ["Gallery"] = ("Галерея", "Gallery"),
        ["OpenGallery"] = ("Открыть галерею", "Open gallery"),
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
        ["N.Folder"] = ("Папка", "Folder"),
        ["N.DiskBufferPaused"] = ("Мало места на диске {0} — буфер повтора приостановлен", "Low disk space on {0} — replay buffer paused"),
        ["N.DiskBufferResumed"] = ("Место освободилось — буфер повтора снова пишет", "Disk space freed — replay buffer resumed"),
        ["N.DiskRecordingStopped"] = ("Мало места на диске {0} — запись остановлена и сохранена", "Low disk space on {0} — recording stopped and saved"),
        ["N.DiskNoSpaceForSave"] = ("Повтор не сохранён: мало места на диске {0}", "Replay not saved: low disk space on {0}"),
        ["N.DiskFree"] = ("Свободно {0}. Освободите место — продолжится автоматически", "{0} free. Free up space — it resumes automatically"),
        ["N.DiskNeed"] = ("Нужно примерно {0}, свободно {1}", "Needs about {0}, {1} free"),
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
        ["S.GpuPriority"] = ("Приоритет захвата на видеокарте", "Capture priority on the GPU"),
        ["S.GpuPriorityHint"] = ("Без него игра, загружающая видеокарту на 100%, «съедает» кадры записи (видео дёргается, 15–20 к/с вместо заданных). Захват занимает 1–2 мс на кадр. Если всё же заметите просадку FPS в игре — выключите.", "Without it a game at 100% GPU starves the recorder (choppy video, 15–20 fps instead of the target). Capture costs 1–2 ms per frame. Turn off only if you notice an in-game FPS drop."),
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

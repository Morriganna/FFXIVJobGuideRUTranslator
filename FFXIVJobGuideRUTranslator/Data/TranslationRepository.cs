using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Game;
using Dalamud.Plugin.Services;

namespace FFXIVJobGuideRUTranslator.Data;

/// <summary>
/// Держит в памяти весь загруженный перевод и его сопоставление с реальными ID умений игры.
///
/// Единственный источник данных - файлы, скачанные с GitHub (репозиторий FFXIVJobGuideRU, см.
/// TranslationUpdater) и сохранённые в конфиг-папке плагина. В сборку JSON не вшит - при первом
/// запуске (пока файлов ещё нет) Plugin сам вызывает скачивание, см. Plugin.
///
/// Ничего не подменяется в самих названиях умений — они используются только как ключ поиска.
///
/// Сопоставление с ActionId/CraftActionId, в порядке приоритета:
///  1) напрямую из поля "id" исходного JSON, если оно там есть (TranslationEntry.SourceActionId) -
///     100% точно, проверено сверкой с реальными данными игры;
///  2) поиск по точному совпадению английского названия с листами Action/CraftAction (Lumina) -
///     резервный вариант для более старых веток источника, где поля "id" ещё нет.
/// </summary>
public sealed class TranslationRepository
{
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private readonly string overrideDirectory;

    /// <summary>Все распознанные записи перевода, у которых удалось найти ActionId в данных игры.</summary>
    private Dictionary<uint, List<TranslationEntry>> byActionId = new();

    /// <summary>english name (lower, trimmed) -> все записи с таким именем (до и после разрешения ID).</summary>
    private Dictionary<string, List<TranslationEntry>> byEnglishName = new(StringComparer.OrdinalIgnoreCase);

    public int TotalParsed { get; private set; }
    public int TotalResolved { get; private set; }
    public DateTime LoadedAtUtc { get; private set; }

    /// <summary>ActionId -> доп. статы (иконка/каст/дальность и т.п.) из листа Action - см. ActionStatsLookup/TranslationOverlay.</summary>
    public IReadOnlyDictionary<uint, ActionStats> ActionStatsById { get; private set; } = new Dictionary<uint, ActionStats>();

    public TranslationRepository(IDataManager dataManager, IPluginLog log, string overrideDirectory)
    {
        this.dataManager = dataManager;
        this.log = log;
        this.overrideDirectory = overrideDirectory;
    }

    /// <summary>Перезагружает перевод из скачанных файлов на диске (если есть), и заново разрешает ID.</summary>
    public void Reload()
    {
        var parsed = new List<TranslationEntry>();

        var overrideFiles = Directory.Exists(overrideDirectory)
            ? Directory.GetFiles(overrideDirectory, "*.json", SearchOption.AllDirectories)
            : Array.Empty<string>();

        if (overrideFiles.Length > 0)
        {
            log.Information($"[JobGuideRU] Загружаю перевод из скачанных файлов: {overrideFiles.Length} шт. ({overrideDirectory})");
            foreach (var file in overrideFiles)
            {
                try
                {
                    var json = File.ReadAllText(file);
                    parsed.AddRange(RawSourceParsing.ParseFile(json));
                }
                catch (Exception ex)
                {
                    log.Warning(ex, $"[JobGuideRU] Не удалось разобрать {file}");
                }
            }
        }
        else
        {
            // Первый запуск - ничего ещё не скачано (в сборку JSON не вшит). Plugin сам вызовет
            // UpdateTranslationsAsync и перезагрузит репозиторий по завершении - здесь просто
            // остаёмся с пустым списком до этого момента.
            log.Information("[JobGuideRU] Скачанного перевода пока нет - жду первой загрузки с GitHub.");
        }

        TotalParsed = parsed.Count;

        byEnglishName = new Dictionary<string, List<TranslationEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in parsed)
        {
            if (!byEnglishName.TryGetValue(entry.EnglishName, out var list))
                byEnglishName[entry.EnglishName] = list = new List<TranslationEntry>();
            list.Add(entry);
        }

        // Строим один раз за перезагрузку - используется и для показа доп. статов (иконка/каст/
        // дальность и т.п., см. TranslationOverlay), и ниже как проверка принадлежности id именно
        // листу Action (у CraftAction/Gathering/Fisher своя, отдельная нумерация ID - те же числа
        // МОГУТ случайно совпасть с чужим RowId листа Action, поэтому просто "ID нашёлся в
        // словаре" не значит "это боевое умение", если это не проверить заранее).
        ActionStatsById = ActionStatsLookup.BuildAll(dataManager, log);

        // Сначала - записи, где ActionId уже пришёл прямо из исходного JSON (SourceActionId,
        // см. RawSourceParsing/TranslationEntry) - это надёжнее сопоставления по имени, поэтому
        // для них поиск по листам Action/CraftAction ниже просто пропускается (см. IsResolved
        // внутри ResolveActionIds - уже сопоставленные записи там не трогаются).
        var resolvedById = 0;
        var rejectedStaleId = 0;
        foreach (var entry in parsed)
        {
            if (entry.SourceActionId is not { } sourceId || sourceId == 0)
                continue;

            if (ActionStatsById.TryGetValue(sourceId, out var actionRow))
            {
                // Имя строки НЕ совпадает с EnglishName записи - в текущей версии игры этот
                // числовой id сместился и указывает уже на другое умение (источник не успел
                // обновиться под патч).
                var nameMismatch = !string.Equals(actionRow.Name, entry.EnglishName, StringComparison.OrdinalIgnoreCase);

                // Имя совпадает, но строка выглядит НЕ как умение игрока (уровень 0 и ни одной
                // работы) - листы Action нередко содержат несколько строк с одинаковым именем:
                // настоящее умение игрока и атаку монстра/NPC с тем же названием (пример: "Leg
                // Sweep" - RowId 82 это монстр, RowId 7863 - игрок). Числовой id может указывать
                // именно на монстра - такое совпадение имени не спасает.
                var looksLikeNonPlayerRow = actionRow.Level == 0 && actionRow.Jobs.Count == 0;

                if (nameMismatch || looksLikeNonPlayerRow)
                {
                    rejectedStaleId++;
                    continue;
                }
            }

            entry.ActionId = sourceId;
            // "Action" только если ID точно существует в этом листе - иначе нейтральная метка,
            // чтобы TranslationOverlay не показал доп. статы боевого умения для крафта/сбора по
            // случайно совпавшему номеру ID из другого листа (см. комментарий выше).
            entry.ResolvedSheet = ActionStatsById.ContainsKey(sourceId) ? "Action" : "id (источник)";
            resolvedById++;
        }

        if (rejectedStaleId > 0)
            log.Warning($"[JobGuideRU] Отклонено id из исходного JSON, не совпадающих с именем умения в текущей версии игры: {rejectedStaleId} (сопоставлены по имени вместо id).");

        if (resolvedById > 0)
            log.Information($"[JobGuideRU] Сопоставлено напрямую по id из исходного JSON: {resolvedById}.");

        ResolveActionIds(parsed);

        byActionId = new Dictionary<uint, List<TranslationEntry>>();
        foreach (var entry in parsed.Where(e => e.IsResolved))
        {
            if (!byActionId.TryGetValue(entry.ActionId, out var list))
                byActionId[entry.ActionId] = list = new List<TranslationEntry>();
            list.Add(entry);
        }

        TotalResolved = parsed.Count(e => e.IsResolved);
        LoadedAtUtc = DateTime.UtcNow;

        log.Information($"[JobGuideRU] Перевод загружен: {TotalParsed} записей, из них сопоставлено с ID умений игры: {TotalResolved}.");
    }

    /// <summary>
    /// Сопоставляет разобранные записи перевода с реальными ID умений игры по английскому названию.
    /// Смотрит листы Action (бой) и CraftAction (крафт/сбор). Если в вашей версии Lumina/Dalamud
    /// имена свойств отличаются - поправьте их здесь, ориентируясь на подсказки IDE (IntelliSense).
    /// </summary>
    private void ResolveActionIds(List<TranslationEntry> parsed)
    {
        // В отличие от CraftAction, лист Action не хранит текст описания сам - тот лежит
        // в отдельном листе ActionTransient (та же нумерация RowId), таким уж устроена игра.
        var actionDescriptions = BuildActionTransientDescriptions();

        TryResolveSheet<Lumina.Excel.Sheets.Action>(
            "Action",
            row => row.RowId,
            row => row.Name.ToString(),
            row => actionDescriptions.TryGetValue(row.RowId, out var description) ? description : string.Empty,
            // Некоторые названия задублированы между настоящим умением игрока и NPC-способностью
            // монстра с тем же именем (пример: "Leg Sweep" - RowId 82 это атака монстра,
            // ClassJobLevel 0, без работ; RowId 7863 - настоящее умение игрока, уровень 10).
            // ClassJobLevel у игрока всегда > 0 - предпочитаем строку с более высоким значением.
            row => row.ClassJobLevel);

        TryResolveSheet<Lumina.Excel.Sheets.CraftAction>(
            "CraftAction",
            row => row.RowId,
            row => row.Name.ToString(),
            row => row.Description.ToString());

        void TryResolveSheet<T>(
            string sheetLabel,
            Func<T, uint> getRowId,
            Func<T, string> getName,
            Func<T, string> getDescription,
            Func<T, int>? priority = null)
            where T : struct, Lumina.Excel.IExcelRow<T>
        {
            try
            {
                var sheet = dataManager.GetExcelSheet<T>(ClientLanguage.English);
                if (sheet is null)
                {
                    log.Warning($"[JobGuideRU] Лист {sheetLabel} недоступен (English) - пропускаю.");
                    return;
                }

                // Сначала выбираем ЛУЧШУЮ строку на каждое английское имя (по priority, если
                // задан), а не первую попавшуюся - иначе при задублированных именах (см.
                // комментарий у Action выше) могли бы сопоставить с посторонней строкой.
                var bestByName = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
                foreach (var row in sheet)
                {
                    var name = getName(row);
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    var key = name.Trim();
                    if (!byEnglishName.ContainsKey(key))
                        continue;

                    if (!bestByName.TryGetValue(key, out var existing) ||
                        (priority?.Invoke(row) ?? 0) > (priority?.Invoke(existing) ?? 0))
                    {
                        bestByName[key] = row;
                    }
                }

                var matched = 0;
                foreach (var (name, row) in bestByName)
                {
                    var rowId = getRowId(row);
                    var description = getDescription(row) ?? string.Empty;

                    foreach (var candidate in byEnglishName[name])
                    {
                        // Если запись уже разрешена другим листом - не перезаписываем.
                        if (candidate.IsResolved)
                            continue;

                        candidate.ActionId = rowId;
                        candidate.ResolvedSheet = sheetLabel;
                        candidate.GameEnglishDescription = description;
                        matched++;
                    }
                }

                log.Information($"[JobGuideRU] Лист {sheetLabel}: сопоставлено {matched} записей.");
            }
            catch (Exception ex)
            {
                log.Warning(ex, $"[JobGuideRU] Ошибка при сопоставлении с листом {sheetLabel}.");
            }
        }
    }

    /// <summary>RowId -> английское описание из ActionTransient (там же нумерация, что и в Action).</summary>
    private Dictionary<uint, string> BuildActionTransientDescriptions()
    {
        var result = new Dictionary<uint, string>();
        try
        {
            var sheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.ActionTransient>(ClientLanguage.English);
            if (sheet is null)
            {
                log.Warning("[JobGuideRU] Лист ActionTransient недоступен (English) - описания боевых умений искать будет не по чему.");
                return result;
            }

            foreach (var row in sheet)
                result[row.RowId] = row.Description.ToString();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[JobGuideRU] Ошибка при чтении листа ActionTransient.");
        }

        return result;
    }

    public bool TryGetByActionId(uint actionId, out IReadOnlyList<TranslationEntry> entries)
    {
        if (byActionId.TryGetValue(actionId, out var list))
        {
            entries = list;
            return true;
        }

        entries = Array.Empty<TranslationEntry>();
        return false;
    }

    /// <summary>Поиск по английскому названию умения (точное совпадение, без учёта регистра) - используется эвристикой замены текста.</summary>
    public bool TryGetByEnglishName(string englishName, out IReadOnlyList<TranslationEntry> entries)
    {
        if (byEnglishName.TryGetValue(englishName.Trim(), out var list))
        {
            entries = list;
            return true;
        }

        entries = Array.Empty<TranslationEntry>();
        return false;
    }
}

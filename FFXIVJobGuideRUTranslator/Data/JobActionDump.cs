using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Dalamud.Game;
using Dalamud.Plugin.Services;

namespace FFXIVJobGuideRUTranslator.Data;

/// <summary>Одна строка debug-дампа: умение, доступное текущей работе игрока, и статус его перевода.</summary>
public sealed class JobActionDumpRow
{
    public required uint ActionId { get; init; }
    public required string EnglishName { get; init; }

    /// <summary>"Обычный класс" или "PvP" - см. <see cref="JobActionDump.GroupPve"/>/<see cref="JobActionDump.GroupPvp"/>.</summary>
    public required string Group { get; init; }

    public required bool IsResolved { get; init; }
    public string? RussianPreview { get; init; }
}

/// <summary>
/// Дебаг-утилита (окно /jgru -> вкладка "Debug: умения"): собирает список боевых умений
/// (лист Action), УНИКАЛЬНЫХ для работы, на которой сейчас находится персонаж - то есть именно
/// то, что в игре показано во вкладке "Job" окна Actions&amp;Traits (обычный класс) и во вкладке
/// "Job" раздела PvP Actions, а НЕ общие для нескольких работ одной роли умения (вкладка "Role" в
/// обеих секциях) и не Quick Chat. "Уникальность" определяем по ClassJobCategory: если в её
/// bool-флагах по работам (см. CategoryIncludesJob) истинна РОВНО одна работа - и это наша, значит
/// умение принадлежит только этой работе, а не общей Role-категории на несколько работ.
///
/// Крафт/сбор (CraftAction) сюда намеренно не включены - у них другая, менее очевидная привязка
/// к работе (не через ClassJobCategory), это не проверялось на живом клиенте (см. README).
/// </summary>
public static class JobActionDump
{
    public const string GroupPve = "Обычный класс";
    public const string GroupPvp = "PvP";

    // Все актуальные трёхбуквенные коды работ/базовых классов (боевые + DoH/DoL - последние тут
    // не помешают, для Action они всё равно не будут true, зато не собьют подсчёт "сколько работ
    // отмечено на этой строке ClassJobCategory" у боевых категорий). Список стабилен между патчами
    // (новые коды добавляются, старые не переименовываются), в отличие от точных имён C#-свойств
    // Lumina, поэтому не полагаемся тут на рефлексию списка целиком - только на поиск конкретного
    // свойства по имени (см. CategoryIncludesJob).
    // internal, не private - переиспользуется ActionStatsLookup (Affinity: тот же список кодов и
    // тот же способ чтения bool-флагов ClassJobCategory через рефлексию, см. CategoryIncludesJob).
    internal static readonly string[] AllJobAbbreviations =
    {
        // Tank
        "GLA", "PLD", "MRD", "WAR", "DRK", "GNB",
        // Healer
        "CNJ", "WHM", "SCH", "AST", "SGE",
        // Melee DPS
        "PGL", "MNK", "LNC", "DRG", "ROG", "NIN", "SAM", "RPR", "VPR",
        // Physical Ranged DPS
        "ARC", "BRD", "MCH", "DNC",
        // Magical Ranged DPS
        "THM", "BLM", "ACN", "SMN", "RDM", "PCT", "BLU",
    };

    /// <summary>
    /// Строит дамп для работы, на которой сейчас находится персонаж.
    /// Возвращает пустой список и null в jobAbbreviation, если персонаж не в игре
    /// или работу не удалось прочитать.
    /// </summary>
    public static List<JobActionDumpRow> BuildForCurrentJob(
        IDataManager dataManager,
        IPlayerState playerState,
        TranslationRepository repository,
        IPluginLog log,
        out string? jobAbbreviation)
    {
        jobAbbreviation = null;
        var result = new List<JobActionDumpRow>();

        // IClientState.LocalPlayer устарел начиная с API 14 - атрибуты текущего персонажа
        // (в т.ч. работу) с этой версии положено читать через IPlayerState.
        if (!playerState.IsLoaded)
            return result;

        string abbreviation;
        try
        {
            // ClassJob - RowRef на лист ClassJob; Abbreviation - трёхбуквенный код вида "WAR", "SAM".
            abbreviation = playerState.ClassJob.Value.Abbreviation.ToString().Trim();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[JobGuideRU] Не удалось прочитать текущую работу персонажа (ClassJob) - проверьте API в IntelliSense, если тут ошибка компиляции.");
            return result;
        }

        if (string.IsNullOrEmpty(abbreviation))
            return result;

        jobAbbreviation = abbreviation;

        var sheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>(ClientLanguage.English);
        if (sheet is null)
        {
            log.Warning("[JobGuideRU] Лист Action недоступен (English) - дамп умений работы невозможен.");
            return result;
        }

        foreach (var row in sheet)
        {
            try
            {
                var name = row.Name.ToString();
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                if (row.ClassJobCategory.RowId == 0)
                    continue; // не привязано к конкретной работе (системное/неигровое умение)

                var category = row.ClassJobCategory.Value;
                if (!IsUniqueToJob(category, abbreviation))
                    continue; // общая Role-категория на несколько работ (или чужая работа) - не то, что просил пользователь

                // IsPvP - как и ClassJobCategory, обычное bool-поле листа Action в текущих версиях
                // Lumina; используется, чтобы отделить обычный класс от вкладки PvP Actions.
                // Если поля вдруг не окажется в вашей версии - просто всё попадёт в "Обычный класс".
                var isPvp = TryGetBoolProperty(row, "IsPvP");
                var group = isPvp ? GroupPvp : GroupPve;

                var isResolved = repository.TryGetByActionId(row.RowId, out var entries);
                result.Add(new JobActionDumpRow
                {
                    ActionId = row.RowId,
                    EnglishName = name.Trim(),
                    Group = group,
                    IsResolved = isResolved,
                    RussianPreview = isResolved ? entries[0].Content : null,
                });
            }
            catch (Exception ex)
            {
                log.Warning(ex, $"[JobGuideRU] Пропускаю строку Action при построении debug-дампа (RowId={row.RowId}).");
            }
        }

        return result
            .OrderBy(r => r.Group == GroupPvp) // сначала обычный класс, потом PvP
            .ThenBy(r => r.ActionId)
            .ToList();
    }

    /// <summary>
    /// true, если среди всех известных работ (<see cref="AllJobAbbreviations"/>) в этой категории
    /// отмечена РОВНО одна - и это jobAbbreviation. Значит умение уникально для данной работы, а
    /// не общая Role-способность (у той в категории отмечено сразу несколько работ одной роли).
    /// </summary>
    private static bool IsUniqueToJob<TCategory>(TCategory category, string jobAbbreviation)
        where TCategory : struct
    {
        string? onlyMatch = null;
        foreach (var code in AllJobAbbreviations)
        {
            if (!CategoryIncludesJob(category, code))
                continue;

            if (onlyMatch is not null)
                return false; // уже нашли одну работу - вторая означает "общее на несколько", не уникальное

            onlyMatch = code;
        }

        return onlyMatch == jobAbbreviation;
    }

    /// <summary>
    /// Лист ClassJobCategory хранит доступность умения по работам как набор bool-колонок,
    /// по одной на каждый трёхбуквенный код работы (PLD, WAR, ...). Ищем через рефлексию, а не
    /// напрямую по имени свойства - структура строки генерируется Lumina и может отличаться
    /// между версиями API; если свойство не нашлось, считаем, что работа не отмечена.
    /// </summary>
    internal static bool CategoryIncludesJob<TCategory>(TCategory category, string jobAbbreviation)
        where TCategory : struct
    {
        var property = typeof(TCategory).GetProperty(jobAbbreviation, BindingFlags.Public | BindingFlags.Instance);
        if (property is null || property.PropertyType != typeof(bool))
            return false;

        return property.GetValue(category) is true;
    }

    /// <summary>Достаёт произвольное bool-свойство через рефлексию; false, если его нет/не bool - см. использование у IsPvP.</summary>
    private static bool TryGetBoolProperty<TRow>(TRow row, string propertyName)
        where TRow : struct
    {
        var property = typeof(TRow).GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        if (property is null || property.PropertyType != typeof(bool))
            return false;

        return property.GetValue(row) is true;
    }
}

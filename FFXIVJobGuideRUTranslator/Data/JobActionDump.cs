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
    public required string Sheet { get; init; }
    public required bool IsResolved { get; init; }
    public string? RussianPreview { get; init; }
}

/// <summary>
/// Дебаг-утилита (окно /jgru -> вкладка "Debug: умения"): собирает список боевых умений
/// (лист Action) работы, на которой сейчас находится персонаж, и помечает, для каких из них
/// есть сопоставленный перевод в <see cref="TranslationRepository"/>. Помогает быстро увидеть
/// живьём, что именно не сопоставилось для конкретной работы, не копаясь в логах.
///
/// Крафт/сбор (CraftAction) сюда намеренно не включены - у них другая, менее очевидная привязка
/// к работе (не через ClassJobCategory), это не проверялось на живом клиенте (см. README).
/// </summary>
public static class JobActionDump
{
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
                if (!CategoryIncludesJob(category, abbreviation))
                    continue;

                var isResolved = repository.TryGetByActionId(row.RowId, out var entries);
                result.Add(new JobActionDumpRow
                {
                    ActionId = row.RowId,
                    EnglishName = name.Trim(),
                    Sheet = "Action",
                    IsResolved = isResolved,
                    RussianPreview = isResolved ? entries[0].Content : null,
                });
            }
            catch (Exception ex)
            {
                log.Warning(ex, $"[JobGuideRU] Пропускаю строку Action при построении debug-дампа (RowId={row.RowId}).");
            }
        }

        return result.OrderBy(r => r.ActionId).ToList();
    }

    /// <summary>
    /// Лист ClassJobCategory хранит доступность умения по работам как набор bool-колонок,
    /// по одной на каждый трёхбуквенный код работы (PLD, WAR, ...). Ищем через рефлексию, а не
    /// напрямую по имени свойства - структура строки генерируется Lumina и может отличаться
    /// между версиями API; если свойство не нашлось, умение просто не попадёт в дамп.
    /// </summary>
    private static bool CategoryIncludesJob<TCategory>(TCategory category, string jobAbbreviation)
        where TCategory : struct
    {
        var property = typeof(TCategory).GetProperty(jobAbbreviation, BindingFlags.Public | BindingFlags.Instance);
        if (property is null || property.PropertyType != typeof(bool))
            return false;

        return property.GetValue(category) is true;
    }
}

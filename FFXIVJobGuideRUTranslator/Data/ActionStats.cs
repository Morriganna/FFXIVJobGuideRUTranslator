using System.Collections.Generic;
using System.Linq;
using Dalamud.Game;
using Dalamud.Plugin.Services;

namespace FFXIVJobGuideRUTranslator.Data;

/// <summary>
/// Доп. статы умения (иконка/каст/восстановление/дальность/радиус/уровень/работы), напрямую из
/// листа Lumina Action - см. TranslationOverlay. Переводить тут нечего - это числа и коды,
/// игра их и так показывает "языко-независимо" в родном окне рядом с нашим переводом; мы их
/// просто дублируем в своём окне, чтобы не заставлять игрока смотреть в оба одновременно.
/// Только для боевых умений (TranslationEntry.ResolvedSheet == "Action") - у CraftAction (крафт/
/// сбор) другая структура листа, этих полей там нет.
/// </summary>
public readonly record struct ActionStats(
    uint IconId,
    int CastHundredMs,
    int RecastHundredMs,
    int Range,
    int Radius,
    int Level,
    IReadOnlyList<string> Jobs);

public static class ActionStatsLookup
{
    /// <summary>
    /// Строит ActionId -> ActionStats по ВСЕМУ листу Action одним проходом (как
    /// TranslationRepository.BuildActionTransientDescriptions для ActionTransient) - лист
    /// сканируется один раз при загрузке/обновлении перевода, а не на каждый кадр наведения.
    /// </summary>
    public static Dictionary<uint, ActionStats> BuildAll(IDataManager dataManager, IPluginLog log)
    {
        var result = new Dictionary<uint, ActionStats>();

        try
        {
            var sheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>(ClientLanguage.English);
            if (sheet is null)
            {
                log.Warning("[JobGuideRU] Лист Action недоступен (English) - доп. статы умений (иконка/каст/дальность и т.п.) показываться не будут.");
                return result;
            }

            foreach (var row in sheet)
            {
                var jobs = row.ClassJobCategory.RowId == 0
                    ? System.Array.Empty<string>()
                    : JobActionDump.AllJobAbbreviations
                        .Where(code => JobActionDump.CategoryIncludesJob(row.ClassJobCategory.Value, code))
                        .ToArray();

                result[row.RowId] = new ActionStats(
                    row.Icon,
                    row.Cast100ms,
                    row.Recast100ms,
                    row.Range,
                    row.EffectRange,
                    row.ClassJobLevel,
                    jobs);
            }
        }
        catch (System.Exception ex)
        {
            log.Warning(ex, "[JobGuideRU] Ошибка при чтении доп. статов из листа Action.");
        }

        return result;
    }
}

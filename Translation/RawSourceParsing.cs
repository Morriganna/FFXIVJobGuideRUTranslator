using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FFXIVJobGuideRUTranslator.Translation;

/// <summary>
/// Разбор "сырых" JSON-файлов из DB/*.json репозитория FFXIVJobGuideRU.
/// Схема там неоднородная: обычно поля name/classification/content — простые строки,
/// но для умений, общих для нескольких классов (например MIN/BTN на роли сборщика),
/// те же поля приходят объектом вида {"MIN": "...", "BTN": "..."}.
/// Здесь оба варианта раскрываются в плоский список <see cref="TranslationEntry"/> (ещё без ActionId).
/// </summary>
public static class RawSourceParsing
{
    // <br>, <br/>, <br /> -> перенос строки; остальные теги (если вдруг появятся) просто вырезаются.
    private static readonly Regex BrTagRegex = new("<br\\s*/?>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AnyTagRegex = new("<[^>]+>", RegexOptions.Compiled);

    public static IEnumerable<TranslationEntry> ParseFile(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            yield break;

        foreach (var entryProperty in doc.RootElement.EnumerateObject())
        {
            var entry = entryProperty.Value;
            if (entry.ValueKind != JsonValueKind.Object)
                continue;

            var hasName = entry.TryGetProperty("name", out var nameEl);
            if (!hasName)
                continue;

            entry.TryGetProperty("classification", out var classificationEl);
            entry.TryGetProperty("content", out var contentEl);
            // "id" - настоящий ActionId/CraftActionId игры, если он уже есть в этой ветке
            // источника (пока не во всех - см. TranslationEntry.SourceActionId). Такой же
            // возможный "простое значение или объект по job-коду" формы, как у остальных полей.
            entry.TryGetProperty("id", out var idEl);

            if (nameEl.ValueKind == JsonValueKind.String)
            {
                // Обычная запись: name/classification/content - простые строки (или отсутствуют).
                var name = nameEl.GetString();
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                yield return new TranslationEntry
                {
                    EnglishName = name.Trim(),
                    JobCode = null,
                    Classification = AsString(classificationEl),
                    Content = CleanContent(AsString(contentEl)),
                    SourceActionId = AsUInt(idEl),
                };
            }
            else if (nameEl.ValueKind == JsonValueKind.Object)
            {
                // Умение, общее для нескольких классов (гл. образом сбор): по записи на каждый job-код.
                foreach (var jobProp in nameEl.EnumerateObject())
                {
                    var jobCode = jobProp.Name;
                    var name = jobProp.Value.ValueKind == JsonValueKind.String ? jobProp.Value.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    yield return new TranslationEntry
                    {
                        EnglishName = name.Trim(),
                        JobCode = jobCode,
                        Classification = AsStringForJob(classificationEl, jobCode),
                        Content = CleanContent(AsStringForJob(contentEl, jobCode)),
                        SourceActionId = AsUIntForJob(idEl, jobCode),
                    };
                }
            }
        }
    }

    private static string? AsString(JsonElement el)
        => el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static string? AsStringForJob(JsonElement el, string jobCode)
    {
        if (el.ValueKind == JsonValueKind.String)
            return el.GetString(); // одно значение на все job-коды
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(jobCode, out var byJob) && byJob.ValueKind == JsonValueKind.String)
            return byJob.GetString();
        return null;
    }

    private static uint? AsUInt(JsonElement el)
        => el.ValueKind == JsonValueKind.Number && el.TryGetUInt32(out var value) ? value : null;

    private static uint? AsUIntForJob(JsonElement el, string jobCode)
    {
        if (el.ValueKind == JsonValueKind.Number)
            return AsUInt(el); // одно значение на все job-коды
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(jobCode, out var byJob))
            return AsUInt(byJob);
        return null;
    }

    private static string? CleanContent(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return raw;

        var withNewlines = BrTagRegex.Replace(raw, "\n");
        var withoutTags = AnyTagRegex.Replace(withNewlines, string.Empty);
        return withoutTags
            .Replace("&nbsp;", " ")
            .Replace("&amp;", "&")
            .Replace("&quot;", "\"")
            .Replace("&#39;", "'")
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Trim();
    }
}

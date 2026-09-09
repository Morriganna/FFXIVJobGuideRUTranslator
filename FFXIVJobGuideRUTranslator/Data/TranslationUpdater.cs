using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace FFXIVJobGuideRUTranslator.Data;

/// <summary>
/// Скачивает свежие переводы из DB/*.json репозитория Murakumo-JP/FFXIVJobGuideRU на GitHub
/// и сохраняет их в конфиг-папку плагина. Вызывается только вручную (команда "/jgru update"),
/// плагин никогда не обращается в сеть сам по себе во время игры.
/// </summary>
public static class TranslationUpdater
{
    private const string Owner = "Murakumo-JP";
    private const string Repo = "FFXIVJobGuideRU";
    private const string Branch = "main";
    private const string TreeApiUrl = $"https://api.github.com/repos/{Owner}/{Repo}/git/trees/{Branch}?recursive=1";
    private const string RawBaseUrl = $"https://raw.githubusercontent.com/{Owner}/{Repo}/{Branch}/";

    public static async Task<int> UpdateAsync(string targetDirectory, IPluginLog log, CancellationToken ct = default)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("FFXIVJobGuideRUTranslator-DalamudPlugin/1.0 (+https://github.com/goatcorp/Dalamud)");

        log.Information("[JobGuideRU] Запрашиваю список файлов с GitHub...");
        var treeJson = await http.GetStringAsync(TreeApiUrl, ct).ConfigureAwait(false);

        var jsonPaths = new List<string>();
        using (var doc = JsonDocument.Parse(treeJson))
        {
            if (doc.RootElement.TryGetProperty("truncated", out var truncatedEl) && truncatedEl.ValueKind == JsonValueKind.True)
                log.Warning("[JobGuideRU] Ответ GitHub API помечен как truncated - часть файлов могла не попасть в список.");

            foreach (var item in doc.RootElement.GetProperty("tree").EnumerateArray())
            {
                if (!item.TryGetProperty("type", out var typeEl) || typeEl.GetString() != "blob")
                    continue;
                if (!item.TryGetProperty("path", out var pathEl))
                    continue;

                var path = pathEl.GetString();
                if (path is null)
                    continue;
                if (!path.StartsWith("DB/", StringComparison.Ordinal) || !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (path.EndsWith("Menu.json", StringComparison.OrdinalIgnoreCase))
                    continue; // навигация сайта, не тексты умений

                jsonPaths.Add(path);
            }
        }

        if (jsonPaths.Count == 0)
        {
            log.Warning("[JobGuideRU] Не нашёл ни одного файла DB/*.json - обновление отменено, старые данные не тронуты.");
            return 0;
        }

        // Скачиваем во временную папку и только при полном успехе подменяем боевую -
        // чтобы неудачное обновление (обрыв сети и т.п.) не оставило перевод в наполовину скачанном состоянии.
        var tempDirectory = targetDirectory + ".tmp";
        if (Directory.Exists(tempDirectory))
            Directory.Delete(tempDirectory, recursive: true);
        Directory.CreateDirectory(tempDirectory);

        var downloaded = 0;
        foreach (var path in jsonPaths)
        {
            var relative = path.Substring("DB/".Length);
            var destination = Path.Combine(tempDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            var url = RawBaseUrl + path;
            var content = await http.GetStringAsync(url, ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(destination, content, ct).ConfigureAwait(false);
            downloaded++;
        }

        if (Directory.Exists(targetDirectory))
            Directory.Delete(targetDirectory, recursive: true);
        Directory.Move(tempDirectory, targetDirectory);

        log.Information($"[JobGuideRU] Обновление завершено: скачано {downloaded} файлов.");
        return downloaded;
    }
}

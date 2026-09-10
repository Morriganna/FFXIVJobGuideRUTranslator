using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVJobGuideRUTranslator.Translation;
using FFXIVJobGuideRUTranslator.Services;
using FFXIVJobGuideRUTranslator.UI;

namespace FFXIVJobGuideRUTranslator;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!; // иконки умений в TranslationOverlay

    private const string CommandName = "/jgru";

    public Configuration Configuration { get; }
    public TranslationRepository Repository { get; }

    public bool IsUpdating { get; private set; }
    public string LastUpdateStatus { get; private set; } = string.Empty;

    public readonly WindowSystem WindowSystem = new("FFXIVJobGuideRUTranslator");
    private ConfigWindow ConfigWindow { get; }

    private AbilityHoverWatcher? hoverWatcher;
    private readonly string overrideDirectory;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        CleanUpTargetAddonNames();

        overrideDirectory = Path.Combine(PluginInterface.ConfigDirectory.FullName, "translations");

        Repository = new TranslationRepository(DataManager, Log, overrideDirectory);
        Repository.Reload();

        hoverWatcher = new AbilityHoverWatcher(AddonLifecycle, GameGui, Log, Configuration, Repository);

        ConfigWindow = new ConfigWindow(this);
        WindowSystem.AddWindow(ConfigWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Открывает настройки перевода умений. \"/jgru update\" - обновить перевод с GitHub.",
        });

        PluginInterface.UiBuilder.Draw += OnDraw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;

        // Первый запуск - в конфиг-папке ещё ничего не скачано, а в сборку JSON больше не вшит
        // (см. csproj) - без этого плагин молча ничего бы не переводил, пока пользователь сам не
        // догадается нажать "Обновить перевод с GitHub".
        if (Repository.TotalParsed == 0)
            UpdateTranslationsAsync();

        Log.Information("[JobGuideRU] Плагин загружен.");
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= OnDraw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;

        WindowSystem.RemoveAllWindows();
        ConfigWindow.Dispose();

        hoverWatcher?.Dispose();
        hoverWatcher = null;

        CommandManager.RemoveHandler(CommandName);
    }

    private void OnDraw()
    {
        WindowSystem.Draw();

        hoverWatcher?.RefreshIfCursorStable(ImGui.GetIO().MousePos);
        TranslationOverlay.Draw(hoverWatcher?.Current, Repository, TextureProvider, Configuration.ShowActionId);
    }

    /// <summary>Дамп умений текущей работы персонажа для вкладки "Debug" окна настроек - см. JobActionDump.</summary>
    public List<JobActionDumpRow> BuildJobActionDump(out string? jobAbbreviation)
        => JobActionDump.BuildForCurrentJob(DataManager, PlayerState, Repository, Log, out jobAbbreviation);

    /// <summary>
    /// Однократно чистит список аддонов от пустых/пробельных и повторяющихся (без учёта
    /// регистра) записей, которые могли накопиться в уже сохранённом на диске конфиге
    /// (например, из-за незатримленного ввода в старой версии окна настроек).
    /// </summary>
    private void CleanUpTargetAddonNames()
    {
        var cleaned = Configuration.TargetAddonNames
            .Select(n => n?.Trim())
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (cleaned.Count == Configuration.TargetAddonNames.Count &&
            cleaned.SequenceEqual(Configuration.TargetAddonNames, StringComparer.Ordinal))
        {
            return;
        }

        Configuration.TargetAddonNames = cleaned;
        Configuration.Save();
    }

    public void UpdateTranslationsAsync()
    {
        if (IsUpdating)
            return;

        IsUpdating = true;
        LastUpdateStatus = string.Empty;

        Task.Run(async () =>
        {
            try
            {
                var count = await TranslationUpdater.UpdateAsync(overrideDirectory, Log).ConfigureAwait(false);
                Configuration.LastUpdateUtc = DateTime.UtcNow;
                Configuration.Save();
                Repository.Reload();
                LastUpdateStatus = $"Готово: скачано {count} файлов, сопоставлено {Repository.TotalResolved} из {Repository.TotalParsed} записей.";
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[JobGuideRU] Не удалось обновить перевод.");
                LastUpdateStatus = $"Ошибка обновления: {ex.Message}";
            }
            finally
            {
                IsUpdating = false;
            }
        });
    }

    private void OnCommand(string command, string args)
    {
        var trimmed = args.Trim();
        if (string.Equals(trimmed, "update", StringComparison.OrdinalIgnoreCase))
        {
            UpdateTranslationsAsync();
            return;
        }

        ToggleConfigUi();
    }

    public void ToggleConfigUi() => ConfigWindow.Toggle();
}

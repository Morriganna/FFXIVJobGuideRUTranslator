using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVJobGuideRUTranslator.Data;
using FFXIVJobGuideRUTranslator.Hooks;
using FFXIVJobGuideRUTranslator.Windows;
using KamiToolKit.UiOverlay;

namespace FFXIVJobGuideRUTranslator;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    // IClientState.LocalPlayer признан устаревшим начиная с API 14 (см. changelog Dalamud v14) -
    // для чтения атрибутов текущего персонажа (в т.ч. работы) правильный сервис теперь IPlayerState.
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;

    private const string CommandName = "/jgru";

    public Configuration Configuration { get; }
    public TranslationRepository Repository { get; }

    public bool IsUpdating { get; private set; }
    public string LastUpdateStatus { get; private set; } = string.Empty;

    public readonly WindowSystem WindowSystem = new("FFXIVJobGuideRUTranslator");
    private ConfigWindow ConfigWindow { get; }

    private AbilityHoverWatcher? hoverWatcher;
    private readonly string overrideDirectory;

    // Экспериментальный нативный оверлей (см. Configuration.UseNativeTranslationWindow) - создаётся
    // всегда (недорого, пока не видим), но реально показывается только пока тумблер включён;
    // AddNode/RemoveAllNodes должны звонить строго из главного потока игры, как и конструктор
    // Plugin - см. доки KamiToolKit.
    private readonly OverlayController overlayController;
    private readonly NativeTranslationOverlayNode nativeOverlayNode;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        CleanUpTargetAddonNames();

        overrideDirectory = Path.Combine(PluginInterface.ConfigDirectory.FullName, "translations");

        Repository = new TranslationRepository(DataManager, Log, overrideDirectory);
        Repository.Reload();

        hoverWatcher = new AbilityHoverWatcher(AddonLifecycle, GameGui, Log, Configuration, Repository);

        overlayController = new OverlayController();
        nativeOverlayNode = new NativeTranslationOverlayNode(() => hoverWatcher?.Current, () => Configuration.UseNativeTranslationWindow);
        overlayController.AddNode(nativeOverlayNode);

        ConfigWindow = new ConfigWindow(this);
        WindowSystem.AddWindow(ConfigWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Открывает настройки перевода умений. \"/jgru update\" - обновить перевод с GitHub.",
        });

        PluginInterface.UiBuilder.Draw += OnDraw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;

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

        overlayController.Dispose();

        CommandManager.RemoveHandler(CommandName);
    }

    private void OnDraw()
    {
        WindowSystem.Draw();

        // Оверлей с переводом рисуется отдельно поверх экрана - см. AbilityHoverWatcher и один из
        // двух вариантов оверлея:
        //  - обычный (по умолчанию) - TranslationOverlay, ImGui-окно, приближающее вид родной
        //    подсказки;
        //  - экспериментальный (Configuration.UseNativeTranslationWindow) - NativeTranslationOverlayNode,
        //    настоящие ноды игры через KamiToolKit; сам обновляет себя через OverlayController,
        //    здесь ничего дополнительно дёргать не нужно - только не рисовать одновременно оба.
        // CurrentEntry живёт ровно один кадр: если ни один отслеживаемый аддон не "подсветил" его
        // заново на этом кадре (подсказка игры уже не показана), ResetForNextFrame его погасит.
        if (!Configuration.UseNativeTranslationWindow)
            TranslationOverlay.Draw(hoverWatcher?.Current);

        hoverWatcher?.ResetForNextFrame();
    }

    public void ApplyAddonRegistrations() => hoverWatcher?.ApplyRegistrations();

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

    public void ReloadBundledTranslations()
    {
        // Удаляем скачанные файлы, чтобы репозиторий вернулся к бандлу, встроенному в сборку.
        try
        {
            if (Directory.Exists(overrideDirectory))
                Directory.Delete(overrideDirectory, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[JobGuideRU] Не удалось удалить папку со скачанным переводом.");
        }

        Configuration.LastUpdateUtc = null;
        Configuration.Save();
        Repository.Reload();
        LastUpdateStatus = "Возвращён встроенный в плагин перевод.";
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

        if (string.Equals(trimmed, "reload", StringComparison.OrdinalIgnoreCase))
        {
            ReloadBundledTranslations();
            return;
        }

        ToggleConfigUi();
    }

    public void ToggleConfigUi() => ConfigWindow.Toggle();
}

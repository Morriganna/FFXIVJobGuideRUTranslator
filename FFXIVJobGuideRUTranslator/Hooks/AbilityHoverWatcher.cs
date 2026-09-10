using System;
using System.Collections.Generic;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Gui;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVJobGuideRUTranslator.Data;

namespace FFXIVJobGuideRUTranslator.Hooks;

/// <summary>
/// Следит, какое (если вообще какое-то) переведённое умение сейчас показано в одном из окон из
/// Configuration.TargetAddonNames, и прячет родную подсказку, если для неё есть перевод - сам
/// перевод рисует TranslationOverlay отдельным окном ImGui. Ноды игры не меняет.
/// </summary>
public sealed unsafe class AbilityHoverWatcher : IDisposable
{
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;
    private readonly Configuration configuration;
    private readonly TranslationRepository repository;

    private readonly HashSet<string> registeredAddonNames = new();

    public readonly record struct HoverInfo(TranslationEntry Entry);

    /// <summary>Умение с переводом, наведённое прямо сейчас, или null.</summary>
    public HoverInfo? Current
    {
        get
        {
            var value = currentValue;
            if (value is null)
                return null;

            return Environment.TickCount64 - lastSeenTicksMs <= StaleAfterMs ? value : null;
        }
    }

    private HoverInfo? currentValue;
    private long lastSeenTicksMs;

    // Родной аддон не перерисовывается каждый кадр, пока его содержимое не меняется - при
    // слишком коротком окне "свежести" подсказка гасла сама по себе, пока курсор ещё стоял на
    // умении. См. также RefreshIfStillHovering.
    private const long StaleAfterMs = 500;

    public AbilityHoverWatcher(
        IAddonLifecycle addonLifecycle,
        IGameGui gameGui,
        IPluginLog log,
        Configuration configuration,
        TranslationRepository repository)
    {
        this.addonLifecycle = addonLifecycle;
        this.gameGui = gameGui;
        this.log = log;
        this.configuration = configuration;
        this.repository = repository;

        ApplyRegistrations();
    }

    /// <summary>Вызывать каждый кадр из Plugin.OnDraw - продлевает свежесть Current, пока IGameGui.HoveredAction всё ещё указывает на то же умение.</summary>
    public void RefreshIfStillHovering()
    {
        if (currentValue is not { } current)
            return;

        var hovered = gameGui.HoveredAction;
        if (hovered.ActionId == 0 ||
            (hovered.DetailKind != DetailKind.Action && hovered.DetailKind != DetailKind.CraftingAction))
        {
            return;
        }

        if (!repository.TryGetByActionId(hovered.ActionId, out var byId) || byId.Count == 0)
            return;

        if (ReferenceEquals(byId[0], current.Entry))
            lastSeenTicksMs = Environment.TickCount64;
    }

    /// <summary>Перерегистрирует слушатели под текущий Configuration.TargetAddonNames - вызывать после изменения настроек.</summary>
    public void ApplyRegistrations()
    {
        foreach (var name in registeredAddonNames)
            addonLifecycle.UnregisterListener(AddonEvent.PreDraw, name, OnAddonPreDraw);
        registeredAddonNames.Clear();

        foreach (var rawName in configuration.TargetAddonNames)
        {
            var name = rawName?.Trim();
            if (string.IsNullOrEmpty(name) || !registeredAddonNames.Add(name))
                continue;

            addonLifecycle.RegisterListener(AddonEvent.PreDraw, name, OnAddonPreDraw);
        }

        log.Information($"[JobGuideRU] Слушаю аддоны: {string.Join(", ", registeredAddonNames)}");
    }

    // PreDraw, а не PostDraw - чтобы IsVisible = false долетало до рендера этого же кадра, а не
    // следующего (иначе окно на кадр мигало видимым).
    private void OnAddonPreDraw(AddonEvent type, AddonArgs args)
    {
        if (!configuration.Enabled)
            return;

        ProcessAddon(args);
    }

    private void ProcessAddon(AddonArgs args)
    {
        try
        {
            var addon = (AtkUnitBase*)args.Addon.Address;
            if (addon is null || !addon->IsVisible)
                return;

            TranslationEntry? entry = null;

            // DetailKind обязателен: у системных команд (Materia Extraction, роулетки и т.п.)
            // ActionId живёт в своём пространстве ID и может случайно совпасть с ID умения.
            var hovered = gameGui.HoveredAction;
            if (hovered.ActionId != 0 &&
                (hovered.DetailKind == DetailKind.Action || hovered.DetailKind == DetailKind.CraftingAction) &&
                repository.TryGetByActionId(hovered.ActionId, out var byId) && byId.Count > 0)
            {
                entry = byId[0];
            }

            if (entry is null)
            {
                // Панель вроде "Actions & Traits" - ищем ноду с известным названием умения.
                foreach (var text in EnumerateTextNodes(addon))
                {
                    if (string.IsNullOrWhiteSpace(text))
                        continue;
                    if (!repository.TryGetByEnglishName(text, out var byName) || byName.Count == 0)
                        continue;

                    entry = byName[0];
                    break;
                }
            }

            if (entry is not null && !string.IsNullOrEmpty(entry.Content))
            {
                currentValue = new HoverInfo(entry);
                lastSeenTicksMs = Environment.TickCount64;

                // Прячем только когда для умения есть перевод; в остальных случаях (Materia
                // Extraction, Repair и т.п. - тот же переиспользуемый попап) не трогаем - игра
                // сама выставит IsVisible = true, когда окно снова понадобится ей самой.
                addon->IsVisible = false;
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, $"[JobGuideRU] Ошибка при обработке аддона {args.AddonName}");
        }
    }

    private static IEnumerable<string?> EnumerateTextNodes(AtkUnitBase* addon)
    {
        var results = new List<string?>();
        Collect((AtkResNode*)addon->RootNode, results);
        for (var i = 0; i < addon->UldManager.NodeListCount; i++)
            Collect(addon->UldManager.NodeList[i], results);
        return results;

        static void Collect(AtkResNode* node, List<string?> into)
        {
            while (node is not null)
            {
                if (node->Type == NodeType.Text)
                    into.Add(((AtkTextNode*)node)->NodeText.ToString());
                else if (node->Type >= NodeType.Component)
                {
                    var component = ((AtkComponentNode*)node)->Component;
                    if (component is not null && component->UldManager.NodeList is not null)
                    {
                        for (var i = 0; i < component->UldManager.NodeListCount; i++)
                            Collect(component->UldManager.NodeList[i], into);
                    }
                }

                if (node->ChildNode is not null)
                    Collect(node->ChildNode, into);

                node = node->PrevSiblingNode;
            }
        }
    }

    public void Dispose()
    {
        foreach (var name in registeredAddonNames)
            addonLifecycle.UnregisterListener(AddonEvent.PreDraw, name, OnAddonPreDraw);
        registeredAddonNames.Clear();
    }
}

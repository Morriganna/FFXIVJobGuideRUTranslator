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

    // Умения, для которых плагин намеренно не показывает свой перевод (и не трогает родную
    // подсказку) - по английскому названию, как оно в TranslationEntry.EnglishName. Добавить ещё
    // одно исключение - просто дописать сюда его EN-название.
    private static readonly HashSet<string> ExcludedAbilityNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Sprint",
    };

    public readonly record struct HoverInfo(TranslationEntry Entry);

    // Для панели вроде "Actions & Traits" - там нет HoveredAction, поэтому умение опознаётся по
    // тексту ноды в PreDraw и хранится недолго (аддон не перерисовывается каждый кадр, см.
    // PanelStaleAfterMs). Для хотбара кэш не нужен вообще - см. Current.
    private TranslationEntry? panelEntry;
    private long panelSeenTicksMs;
    private const long PanelStaleAfterMs = 500;

    /// <summary>
    /// Умение с переводом, наведённое прямо сейчас, или null. Для хотбара каждый раз читает
    /// живое состояние игры (IGameGui.HoveredAction) - никакого кэша/протухания, поэтому исчезает
    /// ровно в тот кадр, когда курсор реально уходит с умения.
    /// </summary>
    public HoverInfo? Current
    {
        get
        {
            if (TryGetHoveredEntry(out var hoveredEntry))
                return new HoverInfo(hoveredEntry);

            if (panelEntry is not null && Environment.TickCount64 - panelSeenTicksMs <= PanelStaleAfterMs)
                return new HoverInfo(panelEntry);

            return null;
        }
    }

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

    /// <summary>true и переведённая запись, если сейчас реально наведено на боевое/ремесленное умение на хотбаре - только DetailKind Action/CraftingAction, иначе (Materia Extraction и т.п.) ActionId живёт в чужом пространстве ID.</summary>
    private bool TryGetHoveredEntry(out TranslationEntry entry)
    {
        entry = null!;

        var hovered = gameGui.HoveredAction;
        if (hovered.ActionId == 0 ||
            (hovered.DetailKind != DetailKind.Action && hovered.DetailKind != DetailKind.CraftingAction))
        {
            return false;
        }

        if (!repository.TryGetByActionId(hovered.ActionId, out var byId) || byId.Count == 0 || string.IsNullOrEmpty(byId[0].Content))
            return false;

        if (ExcludedAbilityNames.Contains(byId[0].EnglishName))
            return false;

        entry = byId[0];
        return true;
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

            // Хотбар - прямой сигнал уже есть (TryGetHoveredEntry), тут только прячем родное окно.
            if (TryGetHoveredEntry(out _))
            {
                addon->IsVisible = false;
                return;
            }

            // Панель вроде "Actions & Traits" - ищем ноду с известным названием умения.
            foreach (var text in EnumerateTextNodes(addon))
            {
                if (string.IsNullOrWhiteSpace(text))
                    continue;
                if (!repository.TryGetByEnglishName(text, out var byName) || byName.Count == 0 || string.IsNullOrEmpty(byName[0].Content))
                    continue;
                if (ExcludedAbilityNames.Contains(byName[0].EnglishName))
                    continue;

                panelEntry = byName[0];
                panelSeenTicksMs = Environment.TickCount64;
                addon->IsVisible = false;
                return;
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

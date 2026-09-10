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
/// НЕ трогает никакие ноды игры. Пять предыдущих попыток вписать перевод прямо в нативную
/// подсказку (растянуть плашку, сдвинуть соседей, уменьшить шрифт) одна за другой ломались,
/// потому что ActionDetail - общий попап игры, переиспользуемый на тех же адресах нод под
/// множество структурно разных вещей (умения, Materia Extraction, Repair, Ignore Target,
/// роулетки...), и любое вмешательство в его память рано или поздно "протекало" в контент,
/// который мы вообще не должны были трогать. А уменьшение шрифта не спасает по-настоящему
/// длинные описания (Technical Step и подобные) - становится нечитаемо.
///
/// Вместо этого этот класс только СМОТРИТ, какое (если вообще какое-то) переведённое умение
/// сейчас показано в одном из окон из Configuration.TargetAddonNames, и выставляет
/// <see cref="CurrentEntry"/>. Подсказку/панель самой игры это не меняет и не может сломать -
/// перевод рисуется отдельным всплывающим окном ImGui поверх экрана (см. TranslationOverlay),
/// которое само разворачивается под любой объём текста.
///
/// Как опознаётся умение:
///  1. Если сейчас реально наведено на боевое/ремесленное умение на хотбаре - точный ActionId
///     через IGameGui.HoveredAction, но ТОЛЬКО когда HoveredAction.DetailKind - это Action или
///     CraftingAction. Другие виды (DetailKind.GeneralAction и т.п. - Materia Extraction,
///     Ignore Target, роулетки...) не переведены и не должны попадать в выборку - их ActionId
///     живёт в отдельном пространстве идентификаторов и может случайно совпасть с ID умения.
///  2. Иначе (например, панель в "Actions & Traits") ищем текстовую ноду с названием умения
///     среди известных нам названий - только ЧИТАЕМ текст, ничего не меняем.
/// </summary>
public sealed unsafe class AbilityHoverWatcher : IDisposable
{
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;
    private readonly Configuration configuration;
    private readonly TranslationRepository repository;

    private readonly HashSet<string> registeredAddonNames = new();

    /// <summary>Умение, показанное прямо сейчас в одном из отслеживаемых окон, или null. Живёт один кадр (см. <see cref="ResetForNextFrame"/>).</summary>
    public TranslationEntry? CurrentEntry { get; private set; }

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

    /// <summary>Перерегистрирует слушатели под текущий список Configuration.TargetAddonNames (вызывать после изменения настроек).</summary>
    public void ApplyRegistrations()
    {
        foreach (var name in registeredAddonNames)
            addonLifecycle.UnregisterListener(AddonEvent.PostDraw, name, OnAddonPostDraw);
        registeredAddonNames.Clear();

        foreach (var rawName in configuration.TargetAddonNames)
        {
            var name = rawName?.Trim();
            if (string.IsNullOrEmpty(name))
                continue;

            // HashSet.Add возвращает false, если имя уже добавлено - так регистрируем каждый
            // аддон максимум один раз, даже если в конфиге он по ошибке продублирован.
            if (!registeredAddonNames.Add(name))
                continue;

            addonLifecycle.RegisterListener(AddonEvent.PostDraw, name, OnAddonPostDraw);
        }

        log.Information($"[JobGuideRU] Слушаю аддоны: {string.Join(", ", registeredAddonNames)}");
    }

    /// <summary>
    /// Вызывать из UiBuilder.Draw ПОСЛЕ того, как оверлей с переводом (если он был) уже
    /// нарисован за этот кадр. Сбрасывает CurrentEntry, чтобы на следующем кадре оверлей
    /// показался снова, только если хотя бы один из отслеживаемых аддонов реально ещё виден
    /// и сам вызовет OnAddonPostDraw заново - иначе (подсказка исчезла/навели на другое место)
    /// оверлей естественным образом пропадёт, без ручного отслеживания состояния "видимо/нет".
    /// </summary>
    public void ResetForNextFrame() => CurrentEntry = null;

    private void OnAddonPostDraw(AddonEvent type, AddonArgs args)
    {
        if (!configuration.Enabled)
            return;

        try
        {
            // args.Addon - это AtkUnitBasePtr (обёртка без прямой зависимости от ClientStructs),
            // берём голый адрес через .Address и приводим его к настоящему указателю.
            var addon = (AtkUnitBase*)args.Addon.Address;
            if (addon is null || !addon->IsVisible)
                return;

            TranslationEntry? entry = null;

            // DetailKind обязателен: у системных команд (Materia Extraction, Ignore Target,
            // роулетки и т.п. - DetailKind.GeneralAction и другие) ActionId живёт в своём,
            // отдельном пространстве ID и может случайно совпасть с ID переведённого умения.
            var hovered = gameGui.HoveredAction;
            if (hovered.ActionId != 0 &&
                (hovered.DetailKind == DetailKind.Action || hovered.DetailKind == DetailKind.CraftingAction) &&
                repository.TryGetByActionId(hovered.ActionId, out var byId) && byId.Count > 0)
            {
                entry = byId[0];
            }

            if (entry is null)
            {
                // Окно вроде "Actions & Traits": ищем ноду с названием умения среди известных
                // нам названий. Только читаем текст нод, ничего не меняем и не сохраняем адреса.
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
                CurrentEntry = entry;
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
            addonLifecycle.UnregisterListener(AddonEvent.PostDraw, name, OnAddonPostDraw);
        registeredAddonNames.Clear();
    }
}

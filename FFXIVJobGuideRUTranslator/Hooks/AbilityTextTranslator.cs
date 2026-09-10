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
/// Подменяет ТОЛЬКО текст описания умения (поле content из перевода) в тех нативных окнах игры,
/// что перечислены в Configuration.TargetAddonNames (по умолчанию - ActionDetail).
///
/// Намеренно НЕ трогает позицию/размер нод (никакого авто-подгона плашки под более длинный
/// русский текст) - несколько попыток это сделать показали, что ActionDetail это ОБЩИЙ попап
/// игры, переиспользуемый на тех же адресах нод под структурно разные раскладки (не только
/// умения, но и Materia Extraction, Repair, Dye, Ignore Target и другие системные команды из
/// меню "Extras", у каждой из которых своя, отличная от умения, естественная высота). Любая
/// попытка запомнить/восстановить "исходный" размер по адресу ноды в итоге ломала совершенно
/// не связанные с переводом окна, которые случайно используют тот же адрес. Поэтому вместо
/// подгонки размера - только SetText: если перевод длиннее английского оригинала, он может
/// вылезти за нижний край плашки (чисто визуально), но само окно и все его соседние поля
/// остаются такими же, какими их построила игра.
///
/// Как опознаётся умение:
///  1. Если сейчас реально наведено на что-то на хотбаре - берём точный ActionId через
///     IGameGui.HoveredAction, но ТОЛЬКО когда HoveredAction.DetailKind - это Action или
///     CraftingAction (то есть боевое или ремесленное/собирательское умение). Другие виды
///     (DetailKind.GeneralAction и т.п. - Materia Extraction, Ignore Target, роулетки...) не
///     переведены в исходных данных и не должны попадать в ActionId-выборку - их ActionId может
///     случайно совпасть с ID переведённого умения из другого пространства идентификаторов.
///  2. Иначе (например, панель в "Actions & Traits") ищем текстовую ноду с названием умения
///     среди известных нам названий.
///  3. Найдя умение - ищем "ноду описания": либо ноду, чей текст дословно совпадает с английским
///     описанием этого умения в данных игры (Lumina), либо (резерв) самую длинную текстовую ноду.
///  4. Подменяем текст этой ноды на русский перевод - и только текст, больше ничего.
/// </summary>
public sealed unsafe class AbilityTextTranslator : IDisposable
{
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;
    private readonly Configuration configuration;
    private readonly TranslationRepository repository;

    private readonly HashSet<string> registeredAddonNames = new();

    private readonly record struct NodeInfo(nint Address, string? Text);

    public AbilityTextTranslator(
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
            // аддон максимум один раз, даже если в конфиге он по ошибке продублирован
            // (иначе PostDraw срабатывал бы по несколько раз за кадр на один и тот же аддон).
            if (!registeredAddonNames.Add(name))
                continue;

            addonLifecycle.RegisterListener(AddonEvent.PostDraw, name, OnAddonPostDraw);
        }

        log.Information($"[JobGuideRU] Слушаю аддоны: {string.Join(", ", registeredAddonNames)}");
    }

    private void OnAddonPostDraw(AddonEvent type, AddonArgs args)
    {
        if (!configuration.Enabled)
            return;

        try
        {
            // args.Addon - это AtkUnitBasePtr (обёртка без прямой зависимости от ClientStructs),
            // берём голый адрес через .Address и приводим его к настоящему указателю.
            var addon = (AtkUnitBase*)args.Addon.Address;
            if (addon is null)
                return;

            // PostDraw срабатывает и для окон, которые технически существуют, но сейчас реально
            // не показаны игроку (например, ActionDetail не открыт, но продолжает рендериться в
            // фоне) - без этой проверки перевод "утекает" на экран без рамки окна поверх всего остального.
            if (!addon->IsVisible)
                return;

            var allNodes = new List<NodeInfo>();
            CollectNodes((AtkResNode*)addon->RootNode, allNodes);
            // Некоторые окна держат часть контента в списке аддона напрямую (не только через RootNode) -
            // на всякий случай проходим и по верхнему списку нод окна тоже.
            for (var i = 0; i < addon->UldManager.NodeListCount; i++)
                CollectNodes(addon->UldManager.NodeList[i], allNodes);

            TranslationEntry? entry = null;

            // Если сейчас реально наведено на боевое/ремесленное умение на хотбаре - берём точный
            // ActionId через Dalamud, без поиска по тексту. DetailKind обязателен: у системных
            // команд (Materia Extraction, Ignore Target, роулетки и т.п. - DetailKind.GeneralAction
            // и другие) ActionId живёт в своём, отдельном пространстве ID и может случайно
            // совпасть с ID переведённого умения - без проверки типа получаем чужой перевод.
            var hovered = gameGui.HoveredAction;
            if (hovered.ActionId != 0 &&
                (hovered.DetailKind == DetailKind.Action || hovered.DetailKind == DetailKind.CraftingAction) &&
                repository.TryGetByActionId(hovered.ActionId, out var byId) && byId.Count > 0)
            {
                entry = byId[0];
            }

            if (entry is null)
            {
                // Окно вроде "Actions & Traits": ищем ноду с названием умения среди известных нам названий.
                foreach (var info in allNodes)
                {
                    if (string.IsNullOrWhiteSpace(info.Text))
                        continue;
                    if (!repository.TryGetByEnglishName(info.Text, out var byName) || byName.Count == 0)
                        continue;

                    entry = byName[0];
                    break;
                }
            }

            if (entry is null || string.IsNullOrEmpty(entry.Content))
                return;

            ReplaceDescriptionNode(allNodes, entry);
        }
        catch (Exception ex)
        {
            log.Error(ex, $"[JobGuideRU] Ошибка при обработке аддона {args.AddonName}");
        }
    }

    private void ReplaceDescriptionNode(List<NodeInfo> allNodes, TranslationEntry entry)
    {
        // 1) Точное совпадение с английским описанием из данных игры - самый надёжный вариант.
        nint targetAddress = 0;
        if (!string.IsNullOrEmpty(entry.GameEnglishDescription))
        {
            foreach (var info in allNodes)
            {
                if (info.Text is not null && string.Equals(info.Text.Trim(), entry.GameEnglishDescription!.Trim(), StringComparison.Ordinal))
                {
                    targetAddress = info.Address;
                    break;
                }
            }
        }

        // 2) Уже переведено на предыдущем кадре - ничего делать не нужно.
        if (targetAddress == 0)
        {
            foreach (var info in allNodes)
            {
                if (info.Text is not null && string.Equals(info.Text.Trim(), entry.Content!.Trim(), StringComparison.Ordinal))
                    return;
            }
        }

        // 3) Резерв: самая длинная текстовая нода в этом окне (описание почти всегда самый длинный текст).
        if (targetAddress == 0)
        {
            var bestLength = -1;
            foreach (var info in allNodes)
            {
                if (info.Text is null)
                    continue;
                var trimmed = info.Text.Trim();
                if (trimmed.Length <= 3)
                    continue;
                if (string.Equals(trimmed, entry.EnglishName.Trim(), StringComparison.OrdinalIgnoreCase))
                    continue; // это нода названия, не описания
                if (trimmed.Length > bestLength)
                {
                    bestLength = trimmed.Length;
                    targetAddress = info.Address;
                }
            }
        }

        if (targetAddress == 0)
            return;

        var node = (AtkTextNode*)targetAddress;
        var current = node->NodeText.ToString();
        if (string.Equals(current.Trim(), entry.Content!.Trim(), StringComparison.Ordinal))
            return;

        node->SetText(entry.Content!);
    }

    private static void CollectNodes(AtkResNode* node, List<NodeInfo> results)
    {
        while (node is not null)
        {
            if (node->Type == NodeType.Text)
            {
                var textNode = (AtkTextNode*)node;
                results.Add(new NodeInfo((nint)textNode, textNode->NodeText.ToString()));
            }
            else if (node->Type >= NodeType.Component)
            {
                // Компонентные ноды (кнопки, списки и т.п.) хранят свои дочерние ноды в собственном UldManager.
                var component = ((AtkComponentNode*)node)->Component;
                if (component is not null && component->UldManager.NodeList is not null)
                {
                    for (var i = 0; i < component->UldManager.NodeListCount; i++)
                        CollectNodes(component->UldManager.NodeList[i], results);
                }
            }

            if (node->ChildNode is not null)
                CollectNodes(node->ChildNode, results);

            node = node->PrevSiblingNode;
        }
    }

    public void Dispose()
    {
        foreach (var name in registeredAddonNames)
            addonLifecycle.UnregisterListener(AddonEvent.PostDraw, name, OnAddonPostDraw);
        registeredAddonNames.Clear();
    }
}

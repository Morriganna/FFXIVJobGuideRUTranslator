using System;
using System.Collections.Generic;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVJobGuideRUTranslator.Data;

namespace FFXIVJobGuideRUTranslator.Hooks;

/// <summary>
/// Подменяет ТОЛЬКО текст описания умения (поле content из перевода) в тех нативных окнах игры,
/// что перечислены в Configuration.TargetAddonNames (по умолчанию: всплывающая подсказка умения
/// на хотбаре и панель описания в "Actions &amp; Traits"). Название умения нигде не трогается.
///
/// Как это работает (без завязки на конкретные ID текстовых нод, которые могут отличаться
/// между версиями игры):
///  1. Проходим по всем текстовым нодам окна и ищем ноду, чей текст совпадает с английским
///     названием какого-то умения из перевода - это "нода названия" (не изменяется).
///  2. Как только умение опознано, ищем "ноду описания": либо ноду, чей текст дословно совпадает
///     с английским описанием этого умения в данных игры (Lumina), либо (если не нашли - например,
///     из-за подставленных числовых значений) самую длинную текстовую ноду в этом же окне.
///  3. Подменяем найденную ноду описания на русский перевод.
///
/// Для всплывающей подсказки на хотбаре дополнительно используется IGameGui.HoveredAction -
/// это даёт точный ActionId без поиска по тексту и работает надёжнее.
/// </summary>
public sealed unsafe class AbilityTextTranslator : IDisposable
{
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;
    private readonly Configuration configuration;
    private readonly TranslationRepository repository;

    private readonly HashSet<string> registeredAddonNames = new();

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

        foreach (var name in configuration.TargetAddonNames)
        {
            if (string.IsNullOrWhiteSpace(name))
                continue;
            addonLifecycle.RegisterListener(AddonEvent.PostDraw, name, OnAddonPostDraw);
            registeredAddonNames.Add(name);
        }

        log.Information($"[JobGuideRU] Слушаю аддоны: {string.Join(", ", registeredAddonNames)}");
    }

    private void OnAddonPostDraw(AddonEvent type, AddonArgs args)
    {
        if (!configuration.Enabled)
            return;

        var isTooltip = string.Equals(args.AddonName, "Tooltip", StringComparison.Ordinal);
        if (isTooltip && !configuration.TranslateHoverTooltip)
            return;
        if (!isTooltip && !configuration.TranslateActionMenu)
            return;

        try
        {
            // args.Addon оборачивает адрес окна (AtkUnitBasePtr). Если эта строка не компилируется в вашей
            // версии Dalamud - замените на (AtkUnitBase*)args.Addon.Address или (AtkUnitBase*)(nint)args.Addon.
            var addon = (AtkUnitBase*)args.Addon;
            if (addon is null)
                return;

            TranslationEntry? entry = null;

            // Для всплывающей подсказки на хотбаре: точный ActionId через Dalamud, без поиска по тексту.
            if (isTooltip)
            {
                var hovered = gameGui.HoveredAction;
                if (hovered.ActionId != 0 && repository.TryGetByActionId(hovered.ActionId, out var byId) && byId.Count > 0)
                    entry = byId[0];
            }

            var textNodes = new List<(nint Address, string Text)>();
            CollectTextNodes((AtkResNode*)addon->RootNode, textNodes);
            // Некоторые окна держат часть контента в списке аддона напрямую (не только через RootNode) -
            // на всякий случай проходим и по верхнему списку нод окна тоже.
            for (var i = 0; i < addon->UldManager.NodeListCount; i++)
                CollectTextNodes(addon->UldManager.NodeList[i], textNodes);

            if (entry is null)
            {
                // Окно вроде "Actions & Traits": ищем ноду с названием умения среди известных нам названий.
                foreach (var (_, text) in textNodes)
                {
                    if (string.IsNullOrWhiteSpace(text))
                        continue;
                    if (!repository.TryGetByEnglishName(text, out var byName) || byName.Count == 0)
                        continue;

                    entry = byName[0];
                    break;
                }
            }

            if (entry is null || string.IsNullOrEmpty(entry.Content))
                return;

            ReplaceDescriptionNode(addon, textNodes, entry);
        }
        catch (Exception ex)
        {
            log.Error(ex, $"[JobGuideRU] Ошибка при обработке аддона {args.AddonName}");
        }
    }

    private void ReplaceDescriptionNode(AtkUnitBase* addon, List<(nint Address, string Text)> textNodes, TranslationEntry entry)
    {
        // 1) Точное совпадение с английским описанием из данных игры - самый надёжный вариант.
        nint targetAddress = 0;
        if (!string.IsNullOrEmpty(entry.GameEnglishDescription))
        {
            foreach (var (address, text) in textNodes)
            {
                if (string.Equals(text.Trim(), entry.GameEnglishDescription!.Trim(), StringComparison.Ordinal))
                {
                    targetAddress = address;
                    break;
                }
            }
        }

        // 2) Уже подменяли раньше - текст теперь совпадает с русским переводом, ничего делать не нужно.
        if (targetAddress == 0)
        {
            foreach (var (address, text) in textNodes)
            {
                if (string.Equals(text.Trim(), entry.Content!.Trim(), StringComparison.Ordinal))
                    return;
            }
        }

        // 3) Резерв: самая длинная текстовая нода в этом окне (описание почти всегда самый длинный текст).
        if (targetAddress == 0)
        {
            var bestLength = -1;
            foreach (var (address, text) in textNodes)
            {
                var trimmed = text.Trim();
                if (trimmed.Length <= 3)
                    continue;
                if (string.Equals(trimmed, entry.EnglishName.Trim(), StringComparison.OrdinalIgnoreCase))
                    continue; // это нода названия, не описания
                if (trimmed.Length > bestLength)
                {
                    bestLength = trimmed.Length;
                    targetAddress = address;
                }
            }
        }

        if (targetAddress == 0)
            return;

        var node = (AtkTextNode*)targetAddress;
        var current = node->NodeText.ToString();
        if (string.Equals(current.Trim(), entry.Content!.Trim(), StringComparison.Ordinal))
            return;

        node->SetText(entry.Content);
    }

    private static void CollectTextNodes(AtkResNode* node, List<(nint, string)> results)
    {
        while (node is not null)
        {
            if (node->Type == NodeType.Text)
            {
                var textNode = (AtkTextNode*)node;
                results.Add(((nint)textNode, textNode->NodeText.ToString()));
            }
            else if (node->Type >= NodeType.Component)
            {
                // Компонентные ноды (кнопки, списки и т.п.) хранят свои дочерние ноды в собственном UldManager.
                var component = ((AtkComponentNode*)node)->Component;
                if (component is not null && component->UldManager.NodeList is not null)
                {
                    for (var i = 0; i < component->UldManager.NodeListCount; i++)
                        CollectTextNodes(component->UldManager.NodeList[i], results);
                }
            }

            if (node->ChildNode is not null)
                CollectTextNodes(node->ChildNode, results);

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

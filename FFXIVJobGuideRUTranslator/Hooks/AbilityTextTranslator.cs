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
/// что перечислены в Configuration.TargetAddonNames (по умолчанию - ActionDetail, он же и
/// всплывающая подсказка умения на хотбаре, и панель описания в "Actions &amp; Traits").
/// Название умения нигде не трогается.
///
/// Как это работает (без завязки на конкретные ID текстовых нод, которые могут отличаться
/// между версиями игры):
///  1. Проходим по всем нодам окна и ищем текстовую ноду, чьё содержимое совпадает с английским
///     названием какого-то умения из перевода - это "нода названия" (не изменяется).
///  2. Как только умение опознано, ищем "ноду описания": либо ноду, чей текст дословно совпадает
///     с английским описанием этого умения в данных игры (Lumina), либо (если не нашли - например,
///     из-за подставленных числовых значений) самую длинную текстовую ноду в этом же окне.
///  3. Подменяем найденную ноду описания на русский перевод и пересчитываем её размер под новый
///     текст (ResizeNodeForCurrentText) - русский текст почти всегда длиннее английского и не
///     помещается в исходную высоту. Всё, что было расположено ниже описания (Acquired/Affinity
///     и т.п.), а также фон/рамку окна сдвигаем/растягиваем на ту же разницу в высоте, иначе
///     текст просто вылезает за плашку.
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

    // X/Y на AtkResNode - float (позиция с учётом дробного скейла), а не short.
    private readonly record struct NodeInfo(nint Address, float X, float Y, ushort Width, ushort Height, string? Text);

    /// <summary>
    /// Адрес ноды описания -> английское название умения, для которого мы её последний раз
    /// подменяли и пересчитывали размер. Нужно, чтобы не выполнять resize/сдвиг соседних нод
    /// повторно на КАЖДЫЙ кадр (PostDraw срабатывает десятки-сотни раз в секунду): сравнивать
    /// напрямую текст ноды с уже применённым переводом ненадёжно - игра может слегка
    /// перенормализовать текст при отображении, из-за чего строковое сравнение никогда бы не
    /// совпадало, и правки (в т.ч. сдвиг координат) применялись бы заново каждый кадр, раздувая
    /// окно до бесконечности.
    /// </summary>
    private readonly Dictionary<nint, string> lastAppliedEnglishName = new();

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

            TranslationEntry? entry = null;

            // Если сейчас реально наведено на умение на хотбаре - берём точный ActionId через
            // Dalamud, без поиска по тексту. Если нет (например, это панель в "Actions & Traits",
            // где ничего не "наведено" в смысле хотбара) - тихо переходим к поиску по названию ниже.
            var hovered = gameGui.HoveredAction;
            if (hovered.ActionId != 0 && repository.TryGetByActionId(hovered.ActionId, out var byId) && byId.Count > 0)
                entry = byId[0];

            var allNodes = new List<NodeInfo>();
            CollectNodes((AtkResNode*)addon->RootNode, allNodes);
            // Некоторые окна держат часть контента в списке аддона напрямую (не только через RootNode) -
            // на всякий случай проходим и по верхнему списку нод окна тоже.
            for (var i = 0; i < addon->UldManager.NodeListCount; i++)
                CollectNodes(addon->UldManager.NodeList[i], allNodes);

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
        //    Работает только пока нода ещё не переведена (текст на английском).
        NodeInfo? target = null;
        if (!string.IsNullOrEmpty(entry.GameEnglishDescription))
        {
            foreach (var info in allNodes)
            {
                if (info.Text is not null && string.Equals(info.Text.Trim(), entry.GameEnglishDescription!.Trim(), StringComparison.Ordinal))
                {
                    target = info;
                    break;
                }
            }
        }

        // 2) Резерв: самая длинная текстовая нода в этом окне (описание почти всегда самый длинный
        //    текст) - работает и до, и после перевода, не зависит от языка текста.
        if (target is null)
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
                    target = info;
                }
            }
        }

        if (target is null)
            return;

        var address = target.Value.Address;

        // Уже применяли перевод именно этого умения к этой же ноде - выходим сразу, ничего не
        // трогая (ни SetText, ни resize, ни сдвиг соседей). Именно эта проверка отсутствовала
        // раньше и приводила к тому, что окно раздувалось на каждом кадре без остановки.
        if (lastAppliedEnglishName.TryGetValue(address, out var appliedTo) &&
            string.Equals(appliedTo, entry.EnglishName, StringComparison.Ordinal))
        {
            return;
        }

        var node = (AtkTextNode*)address;
        var oldY = target.Value.Y;
        var oldHeight = target.Value.Height;
        var oldBottom = oldY + oldHeight;

        node->SetText(entry.Content!); // не null - проверено в OnAddonPostDraw перед вызовом этого метода
        // Пересчитывает Width/Height ноды под уже установленный текст (перенос строк по ширине сохраняется).
        node->ResizeNodeForCurrentText();
        lastAppliedEnglishName[address] = entry.EnglishName;

        var resizedNode = (AtkResNode*)node;
        var delta = resizedNode->Height - oldHeight;

        // Защита на случай, если ResizeNodeForCurrentText() в конкретной версии игры повёл себя
        // не так, как ожидалось (например, здесь же меряется контейнер, а не сама текстовая нода) -
        // не позволяем одному кадру раздуть окно на неадекватную величину.
        if (delta is 0 or < -2000 or > 2000)
            return;

        // Русский перевод почти всегда длиннее английского оригинала и не помещается в исходную
        // высоту плашки - растягиваем самый большой узел, который визуально накрывает описание
        // (скорее всего фон/рамка окна), и сдвигаем вниз всё, что было расположено ниже описания
        // (Acquired/Affinity и т.п.), чтобы не наезжало на новый, более высокий текст.
        NodeInfo? background = null;
        foreach (var info in allNodes)
        {
            if (info.Address == target.Value.Address)
                continue;
            if (info.Y <= oldY && info.Y + info.Height >= oldBottom
                && (background is null || info.Height > background.Value.Height))
            {
                background = info;
            }
        }

        if (background is not null)
        {
            var backgroundNode = (AtkResNode*)background.Value.Address;
            backgroundNode->Height = (ushort)(backgroundNode->Height + delta);
        }

        foreach (var info in allNodes)
        {
            if (info.Address == target.Value.Address)
                continue;
            if (background is not null && info.Address == background.Value.Address)
                continue;
            // Небольшой допуск (2px), чтобы не задеть ноды, чей верх совпадает с низом описания случайно.
            if (info.Y + 2 >= oldBottom)
            {
                var otherNode = (AtkResNode*)info.Address;
                otherNode->Y += delta;
            }
        }
    }

    private static void CollectNodes(AtkResNode* node, List<NodeInfo> results)
    {
        while (node is not null)
        {
            string? text = null;
            if (node->Type == NodeType.Text)
                text = ((AtkTextNode*)node)->NodeText.ToString();

            results.Add(new NodeInfo((nint)node, node->X, node->Y, node->Width, node->Height, text));

            if (node->Type >= NodeType.Component)
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

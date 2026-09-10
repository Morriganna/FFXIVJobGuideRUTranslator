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
/// Важно: ActionDetail - это ОБЩИЙ попап игры, переиспользуемый далеко не только для умений
/// (он же всплывает для Materia Extraction, Repair, Dye, Ignore Target и других системных команд
/// из меню "Extras"). Поэтому здесь нельзя ничего трогать "навсегда" - на каждый вызов сначала
/// возвращаем геометрию всех ранее подвинутых нами нод к исходной, и только потом, если текущее
/// содержимое действительно относится к переведённому умению, применяем перевод заново с чистого
/// листа. Без этого сдвиг/растяжение, сделанные под ОДНО умение, протекали в следующий (уже
/// не наш) попап, который переиспользует те же ноды - отсюда и "поехавшие" нетронутые окна.
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
///  4. Подменяем текст и пересчитываем размер ноды под новый текст (ResizeNodeForCurrentText) -
///     русский текст почти всегда длиннее английского. Всё, что было расположено ниже описания
///     (Acquired/Affinity и т.п.), и фон/рамку окна сдвигаем/растягиваем на ту же разницу.
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
    /// Адрес ноды -> её геометрия ДО первого вмешательства плагина. Заполняется один раз, при
    /// первом же изменении конкретной ноды, и используется, чтобы перед каждой новой попыткой
    /// перевода откатить окно к тому, каким его исходно построила игра - независимо от того, что
    /// именно (и для какого умения) мы делали с ним на предыдущем кадре/наведении.
    /// </summary>
    private readonly Dictionary<nint, (float Y, ushort Height)> originalGeometry = new();

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

            // Сначала откатываем ВСЁ, что мы когда-либо сдвигали/растягивали в этом окне, к
            // исходному состоянию - раз ActionDetail переиспользуется под разный контент, нельзя
            // полагаться на то, что игра сама уберёт за нами правки от предыдущего показа.
            RestoreOriginalGeometry(allNodes);

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

    /// <summary>Возвращает все ранее изменённые ноды этого окна к их исходной геометрии.</summary>
    private void RestoreOriginalGeometry(List<NodeInfo> allNodes)
    {
        for (var i = 0; i < allNodes.Count; i++)
        {
            var info = allNodes[i];
            if (!originalGeometry.TryGetValue(info.Address, out var original))
                continue;
            if (info.Y == original.Y && info.Height == original.Height)
                continue; // уже в исходном состоянии - ничего делать не нужно

            var resNode = (AtkResNode*)info.Address;
            resNode->Y = original.Y;
            resNode->Height = original.Height;
            allNodes[i] = info with { Y = original.Y, Height = original.Height };
        }
    }

    /// <summary>Запоминает исходную геометрию ноды, если ещё не запомнена (вызывать ДО первого изменения).</summary>
    private void RememberOriginalGeometry(NodeInfo info)
    {
        if (!originalGeometry.ContainsKey(info.Address))
            originalGeometry[info.Address] = (info.Y, info.Height);
    }

    private void ReplaceDescriptionNode(List<NodeInfo> allNodes, TranslationEntry entry)
    {
        // 1) Точное совпадение с английским описанием из данных игры - самый надёжный вариант.
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

        // 2) Резерв: самая длинная текстовая нода в этом окне (описание почти всегда самый длинный текст).
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
        RememberOriginalGeometry(target.Value);

        var node = (AtkTextNode*)address;
        var oldY = target.Value.Y;
        var oldHeight = target.Value.Height;
        var oldBottom = oldY + oldHeight;

        node->SetText(entry.Content!); // не null - проверено в OnAddonPostDraw перед вызовом этого метода
        // Пересчитывает Width/Height ноды под уже установленный текст (перенос строк по ширине сохраняется).
        node->ResizeNodeForCurrentText();

        var resizedNode = (AtkResNode*)node;
        var delta = resizedNode->Height - oldHeight;

        // Защита на случай, если ResizeNodeForCurrentText() в конкретной версии игры повёл себя
        // не так, как ожидалось (например, здесь же меряется контейнер, а не сама текстовая нода) -
        // не позволяем одному кадру раздуть окно на неадекватную величину.
        if (delta is 0 or < -2000 or > 2000)
            return;

        // Растягиваем самый большой узел, который визуально накрывает описание (скорее всего
        // фон/рамка окна), и сдвигаем вниз всё, что было расположено ниже описания
        // (Acquired/Affinity и т.п.), чтобы не наезжало на новый, более высокий текст.
        NodeInfo? background = null;
        foreach (var info in allNodes)
        {
            if (info.Address == address)
                continue;
            if (info.Y <= oldY && info.Y + info.Height >= oldBottom
                && (background is null || info.Height > background.Value.Height))
            {
                background = info;
            }
        }

        if (background is not null)
        {
            RememberOriginalGeometry(background.Value);
            var backgroundNode = (AtkResNode*)background.Value.Address;
            // Height - ushort (беззнаковый!). Если delta отрицательна и по модулю больше текущей
            // высоты, backgroundNode->Height + delta уходит в минус, а приведение к ushort не
            // даёт отрицательное число - оно переполняется в огромное (например, -30 -> 65506),
            // из-за чего контент внутри окна обрезается по этой чудовищной высоте и пропадает.
            // Считаем в int и жёстко ограничиваем снизу, прежде чем приводить обратно к ushort.
            var newBackgroundHeight = Math.Clamp(backgroundNode->Height + delta, 1, ushort.MaxValue);
            backgroundNode->Height = (ushort)newBackgroundHeight;
        }

        foreach (var info in allNodes)
        {
            if (info.Address == address)
                continue;
            if (background is not null && info.Address == background.Value.Address)
                continue;
            // Небольшой допуск (2px), чтобы не задеть ноды, чей верх совпадает с низом описания случайно.
            if (info.Y + 2 >= oldBottom)
            {
                RememberOriginalGeometry(info);
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

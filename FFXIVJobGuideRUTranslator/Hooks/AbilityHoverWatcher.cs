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
/// <see cref="Current"/> (само умение + экранные координаты родного окна). Подсказку/панель
/// самой игры это не меняет и не может сломать -
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

    /// <summary>
    /// Умение + экранные координаты и размер РОДНОГО окна, в котором оно показано - только чтение,
    /// координаты нужны, чтобы поставить перевод рядом, а не там, где сейчас курсор мыши (иначе в
    /// списках/панелях перевод оказывается где попало и перекрывает контент). AddonId - для
    /// AtkStage.Instance()->TooltipManager (см. Windows/NativeTooltipOverlay.cs): нативные
    /// подсказки привязываются к id окна-владельца, а не к координатам.
    /// </summary>
    public readonly record struct HoverInfo(TranslationEntry Entry, float X, float Y, float Width, float Height, ushort AddonId);

    /// <summary>
    /// Что показано прямо сейчас в одном из отслеживаемых окон, или null.
    ///
    /// Раньше это жило "один кадр" по схеме "выставили в OnAddonPostDraw -> погасили в конце
    /// Plugin.OnDraw (ResetForNextFrame)". Это ломалось для NativeTranslationOverlayNode:
    /// OverlayController из KamiToolKit обновляет свои ноды по СВОЕМУ циклу, а не по событию
    /// UiBuilder.Draw (которым и был Plugin.OnDraw) - относительный порядок "PostDraw родного
    /// аддона / сброс в конце ImGui-кадра / OnUpdate оверлея" непредсказуем между разными
    /// подсистемами, и на практике OnUpdate почти всегда читал уже сброшенное значение (см. лог:
    /// оверлей стабильно видел "нет наведённого умения", хотя родная подсказка при этом гасла -
    /// то есть OnAddonPostDraw точно отрабатывал и находил перевод).
    ///
    /// Вместо привязки к конкретному событию-кадру теперь значение просто "свежее" в течение
    /// небольшого окна времени после последней установки (см. StaleAfterMs) - не имеет значения,
    /// кто и когда его читает, ImGui-оверлей или обновление KamiToolKit.
    /// </summary>
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

    // Оказалось (проверено на живом клиенте), что сам аддон ActionDetail НЕ перерисовывается
    // каждый игровой кадр, пока его содержимое не меняется - PostDraw/PreDraw срабатывают
    // заметно реже, чем 60 раз в секунду (похоже на dirty-flag оптимизацию движка), даже пока
    // курсор всё это время неподвижно стоит на умении. При 150мс окно "свежести" оказывалось
    // короче типичного промежутка между такими перерисовками - подсказка успевала "протухнуть"
    // и погаснуть, хотя курсор с умения никто не убирал. Увеличено с запасом; для случая наведения
    // прямо на хотбар это не единственная защита - см. RefreshIfStillHovering, который держит
    // Current свежим на каждом кадре ImGui независимо от того, срабатывал ли в этом кадре PostDraw.
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

    /// <summary>
    /// Вызывать каждый кадр из Plugin.OnDraw (ImGui-кадр, срабатывает исправно каждый раз, в
    /// отличие от PostDraw/PreDraw аддона - см. StaleAfterMs). Если IGameGui.HoveredAction прямо
    /// сейчас всё ещё указывает на то же самое умение, что уже показано в Current, продлевает его
    /// свежесть - тогда подсказка не гаснет из-за паузы в перерисовке родного аддона, пока курсор
    /// реально остаётся на месте, и всё равно гаснет достаточно быстро (в пределах StaleAfterMs),
    /// как только курсор реально уходит. Не помогает для случая "Actions & Traits" (там нет
    /// HoveredAction, умение опознаётся по тексту в ноде) - для него единственная защита от
    /// протухания - сам StaleAfterMs.
    /// </summary>
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

    /// <summary>Перерегистрирует слушатели под текущий список Configuration.TargetAddonNames (вызывать после изменения настроек).</summary>
    public void ApplyRegistrations()
    {
        foreach (var name in registeredAddonNames)
            addonLifecycle.UnregisterListener(AddonEvent.PreDraw, name, OnAddonPreDraw);
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

            addonLifecycle.RegisterListener(AddonEvent.PreDraw, name, OnAddonPreDraw);
        }

        log.Information($"[JobGuideRU] Слушаю аддоны: {string.Join(", ", registeredAddonNames)}");
    }

    /// <summary>
    /// Прячет родную подсказку ДО отрисовки этого же кадра, а не постфактум (в PostDraw) - раньше
    /// (см. историю правок) прятанье делалось в PostDraw, на кадр ПОЗЖЕ момента, когда игра уже
    /// нарисовала окно видимым, и если игра сама периодически заново выставляет IsVisible = true,
    /// пока курсор ещё наведён (судя по логу - именно так и происходит), окно успевает мигнуть
    /// видимым каждый такой раз. PreDraw срабатывает непосредственно перед отрисовкой ЭТОГО кадра -
    /// выставленное здесь IsVisible = false гарантированно долетает до рендера. Прячем ВСЕГДА, как
    /// только для умения есть перевод - независимо от Configuration.UseNativeTranslationWindow: в
    /// обычном режиме вместо неё показывается TranslationOverlay (ImGui), в экспериментальном -
    /// NativeTooltipOverlay (TooltipManager); в обоих случаях родная английская подсказка не нужна.
    /// </summary>
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
                // Текстовые ноды уже заполнены игрой к этому моменту (данные обновляются в Update,
                // до Draw) - работает одинаково что в PreDraw, что в PostDraw.
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
                // Только читаем координаты окна - X/Y/масштаб самой игры, ничего не меняем.
                var width = addon->GetScaledWidth(true);
                var height = addon->GetScaledHeight(true);

                currentValue = new HoverInfo(entry, addon->X, addon->Y, width, height, addon->Id);
                lastSeenTicksMs = Environment.TickCount64;

                // Прячем РОДНУЮ подсказку только на кадрах, где для неё точно есть перевод - её
                // ноды при этом не трогаются вообще, только IsVisible всего окна. В любой другой
                // момент (другое использование этого же попапа - Materia Extraction и т.п., или
                // умение без перевода) addon->IsVisible никак не меняем - родное окно ведёт себя
                // как обычно. См. доккомментарий OnAddonPreDraw - прячем всегда, оба варианта
                // оверлея показывают перевод вместо неё, а не рядом с ней.
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

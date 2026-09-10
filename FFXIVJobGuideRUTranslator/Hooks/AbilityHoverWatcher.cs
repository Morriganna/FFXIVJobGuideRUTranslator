using System;
using System.Collections.Generic;
using System.Numerics;
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

    // IGameGui.HoveredAction, вопреки документации Dalamud, на практике НЕ сбрасывается в 0, когда
    // курсор уходит в пустое место - держит последнее значение, пока не наведёшь на что-то новое
    // (проверено на живом клиенте), поэтому сам по себе для "наведено прямо сейчас" не годится.
    // Прячем родную подсказку через альфу (см. ProcessAddon), а не addon->IsVisible - это важно и
    // для скрытия, и для слежения: если бы мы сами гасили IsVisible, то заодно обрубили бы и
    // PreDraw для этого аддона на все следующие кадры (похоже, движок не утруждается обрабатывать
    // невидимое окно) - вместе с ним потеряли бы единственный надёжный сигнал "курсор всё ещё тут".
    // С альфой IsVisible остаётся настоящим, каким его держит сама игра - PreDraw продолжает
    // исправно вызываться, пока реально наведено. RefreshIfCursorStable - дополнительная подстраховка
    // на случай пауз между перерисовками самого аддона (см. StaleAfterMs).
    private HoverInfo? currentValue;
    private long lastSeenTicksMs;
    private TranslationEntry? cursorTrackedEntry;
    private Vector2 lastMousePos;

    // Сам аддон не перерисовывается каждый кадр, даже пока курсор неподвижно стоит на умении -
    // окно на этот случай, чтобы подсказка не гасла между такими перерисовками/паузами PreDraw.
    private const long StaleAfterMs = 600;

    // Курсор считается "тем же местом" в пределах этого радиуса (px) - небольшой допуск на
    // дрожание руки/мыши, а не на реальное перемещение к другой иконке.
    private const float MouseStableRadius = 6f;

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

    /// <summary>
    /// Вызывать каждый кадр из Plugin.OnDraw с текущей позицией курсора (ImGui.GetIO().MousePos).
    /// Пока курсор остаётся примерно на месте (в пределах MouseStableRadius) относительно того, где
    /// он был, когда умение последний раз подтвердилось - продлевает Current, даже если PreDraw
    /// аддона всё это время молчит (см. комментарий у currentValue). Как только курсор сдвинулся
    /// заметно - просто перестаёт продлевать, и подсказка гаснет сама в пределах StaleAfterMs.
    /// </summary>
    public void RefreshIfCursorStable(Vector2 mousePos)
    {
        if (currentValue is not { } current)
        {
            cursorTrackedEntry = null;
            return;
        }

        if (!ReferenceEquals(current.Entry, cursorTrackedEntry))
        {
            // Новое умение (или первый кадр после того, как его нашёл ProcessAddon) - просто
            // запоминаем точку отсчёта, повторно продлевать не нужно, ProcessAddon это уже сделал.
            cursorTrackedEntry = current.Entry;
            lastMousePos = mousePos;
            return;
        }

        // lastMousePos НЕ обновляем на каждый вызов - иначе точка отсчёта "ползла" бы вслед за
        // курсором (сдвиг между соседними кадрами почти всегда меньше радиуса), и любое медленное
        // движение засчитывалось бы как "стабильно" даже после того, как курсор давно ушёл с
        // умения. Точка отсчёта фиксируется один раз - когда началось отслеживание этой записи.
        if (Vector2.DistanceSquared(mousePos, lastMousePos) <= MouseStableRadius * MouseStableRadius)
            lastSeenTicksMs = Environment.TickCount64;
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

    // PreDraw, а не PostDraw - чтобы скрытие долетало до рендера этого же кадра, а не следующего
    // (иначе окно на кадр мигало видимым).
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

            // Хотбар - точный ActionId через IGameGui.HoveredAction.
            if (TryGetHoveredEntry(out var hoveredEntry))
                entry = hoveredEntry;

            if (entry is null)
            {
                // Панель вроде "Actions & Traits" - ищем ноду с известным названием умения.
                foreach (var text in EnumerateTextNodes(addon))
                {
                    if (string.IsNullOrWhiteSpace(text))
                        continue;
                    if (!repository.TryGetByEnglishName(text, out var byName) || byName.Count == 0 || string.IsNullOrEmpty(byName[0].Content))
                        continue;
                    if (ExcludedAbilityNames.Contains(byName[0].EnglishName))
                        continue;

                    entry = byName[0];
                    break;
                }
            }

            if (entry is not null)
            {
                currentValue = new HoverInfo(entry);
                lastSeenTicksMs = Environment.TickCount64;

                // Alpha, а не IsVisible - выставив IsVisible = false, мы бы сами обрубили PreDraw
                // для этого аддона на все следующие кадры (движок, похоже, не утруждается
                // обрабатывать невидимое окно) - вместе с ним потеряли бы и единственный надёжный
                // сигнал "курсор всё ещё тут" (см. комментарий у currentValue). С альфой IsVisible
                // остаётся настоящим, каким его держит сама игра - PreDraw продолжает исправно
                // вызываться, пока реально наведено, и гаснет ровно тогда, когда гаснет он.
                if (addon->RootNode is not null)
                    ((AtkResNode*)addon->RootNode)->Color.A = 0;
            }
            else if (addon->RootNode is not null && ((AtkResNode*)addon->RootNode)->Color.A == 0)
            {
                // Другое использование того же переиспользуемого попапа (Materia Extraction,
                // Repair и т.п.) - обязательно возвращаем альфу, иначе оно останется прозрачным.
                ((AtkResNode*)addon->RootNode)->Color.A = 255;
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

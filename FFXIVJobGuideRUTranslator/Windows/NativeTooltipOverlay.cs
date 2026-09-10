using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVJobGuideRUTranslator.Data;
using FFXIVJobGuideRUTranslator.Hooks;
using Lumina.Text;

namespace FFXIVJobGuideRUTranslator.Windows;

/// <summary>
/// Замена NativeTranslationOverlayNode (см. историю правок в git - собственное дерево нод через
/// KamiToolKit.UiOverlay.OverlayController не заработало: перенос текста не встал куда нужно, а
/// видимость окна мигала). Вместо постройки своей плашки из нод используем ГОТОВЫЙ нативный
/// менеджер простых текстовых подсказок игры - AtkStage.Instance()->TooltipManager - тот же
/// механизм, которым игра сама показывает подсказки у иконок валют, ссылок в чате и т.п. Он сам
/// переносит текст, сам считает размер плашки и сам позиционирует её у курсора - выглядит
/// гарантированно нативно, раз это и есть нативный компонент игры, а не наша имитация.
///
/// В Dalamud нет собственной обёртки над этим менеджером (проверено: ни в IGameGui, ни в других
/// сервисах Dalamud.Plugin.Services ничего подобного нет) - обращаемся к FFXIVClientStructs
/// напрямую, как это делает сам автор KamiToolKit в своём плагине VanillaPlus:
/// https://github.com/MidoriKami/VanillaPlus/blob/master/VanillaPlus/Features/ChatPlayerTooltip/ChatPlayerTooltip.cs
/// (ChatPlayerTooltip.ShowTooltip/HideTooltip - структура этого класса скопирована оттуда же).
///
/// Минус по сравнению с попыткой построить свою плашку под рамку ActionDetail: выглядит как
/// обычная жёлтая подсказка-пузырь (как у иконок валют), а не один-в-один как окно ActionDetail
/// (иконка+статы+рамка). Само родное окно умения по-прежнему прячется отдельно, в
/// AbilityHoverWatcher (addon->IsVisible = false) - этот класс только показывает перевод взамен.
/// KamiToolKit этому классу не нужен вообще - обычный PackageReference убран из csproj.
/// </summary>
public static unsafe class NativeTooltipOverlay
{
    private static bool isShown;
    private static ushort shownAddonId;
    private static TranslationEntry? shownEntry;

    /// <summary>Вызывать из UiBuilder.Draw (Plugin.OnDraw) каждый кадр, независимо от режима - сама решает, показывать что-то или прятать.</summary>
    public static void Draw(AbilityHoverWatcher.HoverInfo? hover, bool enabled, IPluginLog log)
    {
        if (!enabled || hover is null || string.IsNullOrEmpty(hover.Value.Entry.Content))
        {
            HideIfShown(log);
            return;
        }

        var info = hover.Value;

        // Уже показываем перевод именно этого умения в этом же родном окне - повторно звать
        // ShowTooltip каждый кадр не нужно: нативная подсказка сама остаётся на экране, пока её
        // явно не спрятать (HideTooltip) - см. HideIfShown. ReferenceEquals по TranslationEntry
        // (не по ActionId) - тот же самый объект отдаёт TranslationRepository на каждый повторный
        // хит одного и того же умения, так что сравнение ссылок здесь корректно и дешевле поиска.
        if (isShown && shownAddonId == info.AddonId && ReferenceEquals(shownEntry, info.Entry))
            return;

        try
        {
            // Content - уже подготовленный текст (см. RawSourceParsing.CleanContent): без HTML-
            // тегов, переносы строк как \n. ShowTooltip принимает ReadOnlySeString напрямую
            // (сгенерированная перегрузка FFXIVClientStructs, [GenerateStringOverloads]) - строим
            // её так же, как раньше строили TextNode.String.
            var tooltipString = new SeStringBuilder().Append(info.Entry.Content!).ToReadOnlySeString();

            // null вторым аргументом (targetNode) - как и в примере VanillaPlus: подсказка сама
            // позиционируется у курсора, нам не нужно указывать конкретную ноду родного окна.
            AtkStage.Instance()->TooltipManager.ShowTooltip(info.AddonId, null, tooltipString);

            isShown = true;
            shownAddonId = info.AddonId;
            shownEntry = info.Entry;
        }
        catch (Exception ex)
        {
            log.Error(ex, "[JobGuideRU] Не удалось показать нативную подсказку (TooltipManager).");
            isShown = false;
            shownEntry = null;
        }
    }

    /// <summary>Прячет подсказку, если она сейчас показана. Вызывать также при выгрузке/перезагрузке плагина (Plugin.Dispose), чтобы не оставить подсказку висеть на экране.</summary>
    public static void HideIfShown(IPluginLog log)
    {
        if (!isShown)
            return;

        try
        {
            AtkStage.Instance()->TooltipManager.HideTooltip(shownAddonId);
        }
        catch (Exception ex)
        {
            log.Error(ex, "[JobGuideRU] Не удалось спрятать нативную подсказку (TooltipManager).");
        }
        finally
        {
            isShown = false;
            shownEntry = null;
        }
    }
}

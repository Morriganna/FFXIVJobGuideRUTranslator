using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVJobGuideRUTranslator.Hooks;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using KamiToolKit.UiOverlay;
using Lumina.Text;

namespace FFXIVJobGuideRUTranslator.Windows;

/// <summary>
/// Экспериментальная замена TranslationOverlay (см. Configuration.UseNativeTranslationWindow):
/// вместо приближения ImGui-окном рисует перевод НАСТОЯЩИМИ нодами игры через KamiToolKit -
/// фон в виде стандартной оконной текстуры (WindowBackgroundTextureNode, тот же "ui/uld/WindowA_Bg",
/// которым нарисовано большинство окон игры) и обычная AtkTextNode для текста.
///
/// В отличие от AbilityHoverWatcher (который только читает родные ноды), этот класс создаёт
/// СВОЁ отдельное дерево нод через OverlayController - ни один узел родного попапа ActionDetail
/// не трогается и не переиспользуется. Единственное вмешательство в родное окно - выключение
/// его видимости на кадрах, где показывается наш перевод (см. AbilityHoverWatcher), сам попап
/// при этом продолжает существовать и работать как обычно.
///
/// Не проверялось на живом клиенте - структура OverlayNode/WindowBackgroundTextureNode/TextNode
/// восстановлена по исходникам KamiToolKit (2.2.39), а не по документации (у библиотеки её
/// толком нет) - если что-то не компилируется или выглядит не так, смотрите актуальные исходники
/// https://github.com/MidoriKami/KamiToolKit.
/// </summary>
public sealed unsafe class NativeTranslationOverlayNode : OverlayNode
{
    // Above user interface - "above most windows but below certain popup windows like battle
    // text" (см. KamiToolKit.Enums.OverlayLayer) - должно быть выше хотбара/обычных окон, но не
    // настолько агрессивно, как Foreground ("above everything, use with caution").
    public override OverlayLayer OverlayLayer => OverlayLayer.AboveUserInterface;

    private const float PaddingX = 14f;
    private const float PaddingY = 10f;
    private const float TextWrapWidth = 260f;
    private const float Margin = 8f;
    private const float Gap = 6f; // расстояние между родным окном и нашим

    private readonly Func<AbilityHoverWatcher.HoverInfo?> hoverProvider;
    private readonly Func<bool> isEnabledProvider;
    private readonly IPluginLog log;
    private readonly WindowBackgroundTextureNode background;
    private readonly TextNode textNode;

    // Логируем только при СМЕНЕ состояния (не каждый кадр - иначе log-файл раздувается за
    // секунды, пока курсор наведён) - тот же приём, что в AbilityHoverWatcher.TryGetBackgroundNineGrid.
    private string? lastLoggedState;

    public NativeTranslationOverlayNode(Func<AbilityHoverWatcher.HoverInfo?> hoverProvider, Func<bool> isEnabledProvider, IPluginLog log)
    {
        this.hoverProvider = hoverProvider;
        this.isEnabledProvider = isEnabledProvider;
        this.log = log;

        // false = обычный (не "выбранный/подсвеченный") вариант текстуры окна - см. исходник
        // WindowBackgroundTextureNode, второй аргумент selectedPath управляет именно этим.
        background = new WindowBackgroundTextureNode(false)
        {
            IsVisible = true,
        };
        background.AttachNode(this);

        textNode = new TextNode
        {
            Width = TextWrapWidth,
            IsVisible = true,
        };
        textNode.AttachNode(this);

        // По умолчанию AtkTextNode переноса по словам не делает вообще (проверено на живом
        // клиенте - см. историю правок: GetTextDrawSize() возвращал однострочную ширину в
        // несколько раз больше TextWrapWidth, то есть перенос не применялся). WordWrap+MultiLine
        // включают перенос в пределах уже выставленной выше ширины (Width), AutoAdjustNodeSize
        // заставляет игру саму пересчитать Height ноды под получившееся число строк - этим же
        // приёмом (только для однострочного авто-размера по ширине) пользуется сам автор
        // KamiToolKit в VanillaPlus (CurrencyTooltipNode); примера именно с WordWrap в его коде
        // не нашлось - поведение выведено из названий флагов и семантики AtkTextNode
        // (FFXIVClientStructs.FFXIV.Component.GUI.TextFlags). Если после теста высота/ширина
        // ноды в логе выглядят не так - см. актуальные исходники FFXIVClientStructs.
        textNode.TextFlags = TextFlags.WordWrap | TextFlags.MultiLine | TextFlags.AutoAdjustNodeSize;

        IsVisible = false;

        LogStateChange("создана, ждёт первого кадра");
    }

    protected override void OnUpdate()
    {
        try
        {
            UpdateCore();
        }
        catch (Exception ex)
        {
            // OverlayController зовёт OnUpdate каждый кадр вне обычного try/catch, которым обёрнуты
            // колбэки IAddonLifecycle (см. AbilityHoverWatcher) - ловим сами, чтобы не уронить
            // обновление вообще всех нод оверлея (не только нашей) необработанным исключением.
            IsVisible = false;
            LogStateChange($"ошибка в OnUpdate: {ex}");
        }
    }

    private void UpdateCore()
    {
        if (!isEnabledProvider())
        {
            IsVisible = false;
            LogStateChange("скрыто (тумблер \"нативное окно\" выключен)");
            return;
        }

        var hover = hoverProvider();
        if (hover is null || string.IsNullOrEmpty(hover.Value.Entry.Content))
        {
            IsVisible = false;
            LogStateChange("скрыто (нет наведённого умения с переводом в этом кадре)");
            return;
        }

        var info = hover.Value;

        // Content - уже подготовленный текст (см. RawSourceParsing.CleanContent): без HTML-тегов,
        // переносы строк как \n. TextNode.String принимает ReadOnlySeString - строим её явно через
        // SeStringBuilder (задокументированный на dalamud.dev способ), а не понадеявшись на неявное
        // преобразование из string, которое не удалось подтвердить в исходниках.
        textNode.String = new SeStringBuilder().Append(info.Entry.Content!).ToReadOnlySeString();

        // GetTextDrawSize() тут намеренно НЕ используется - это "естественная" однострочная
        // ширина текста без учёта переноса (см. исходник TextNode.GetTextDrawSize -> нативный
        // Node->GetTextDrawSize), она не отражает реальный перенесённый по словам текст.
        // TextFlags.AutoAdjustNodeSize (выставлен в конструкторе вместе с WordWrap/MultiLine)
        // заставляет саму игру пересчитать Height ноды сразу по SetText - читаем его напрямую.
        var textHeight = textNode.Height;
        textNode.Position = new Vector2(PaddingX / 2f, PaddingY / 2f);

        var contentSize = new Vector2(TextWrapWidth, textHeight) + new Vector2(PaddingX, PaddingY);
        background.Size = contentSize;
        Size = contentSize;

        // Позиционирование - та же логика, что раньше была в TranslationOverlay.Draw (ImGui-
        // версии): по умолчанию справа от родного окна, с откатом влево и прижатием к краям
        // экрана. Настоящие ноды игры сами учитывают масштаб UI игры, поэтому в отличие от
        // ImGui-версии никакой ручной подгонки под разрешение экрана (GetScale) тут не нужно.
        var display = ImGui.GetIO().DisplaySize;
        var pos = new Vector2(info.X + info.Width + Gap, info.Y);

        if (pos.X + contentSize.X + Margin > display.X)
            pos.X = info.X - contentSize.X - Gap;

        pos.X = Math.Clamp(pos.X, Margin, Math.Max(Margin, display.X - contentSize.X - Margin));
        pos.Y = Math.Clamp(pos.Y, Margin, Math.Max(Margin, display.Y - contentSize.Y - Margin));

        Position = pos;
        IsVisible = true;

        LogStateChange($"показано: pos={pos} size={contentSize} textNode.Width={textNode.Width} textNode.Height={textHeight} background.IsVisible={background.IsVisible} textNode.IsVisible={textNode.IsVisible}");
    }

    private void LogStateChange(string state)
    {
        if (state == lastLoggedState)
            return;

        lastLoggedState = state;
        log.Information($"[JobGuideRU] [нативный оверлей] {state}");
    }
}

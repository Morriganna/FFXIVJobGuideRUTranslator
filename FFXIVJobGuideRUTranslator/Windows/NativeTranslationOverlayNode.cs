using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
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
    private readonly WindowBackgroundTextureNode background;
    private readonly TextNode textNode;

    public NativeTranslationOverlayNode(Func<AbilityHoverWatcher.HoverInfo?> hoverProvider, Func<bool> isEnabledProvider)
    {
        this.hoverProvider = hoverProvider;
        this.isEnabledProvider = isEnabledProvider;

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

        IsVisible = false;
    }

    protected override void OnUpdate()
    {
        if (!isEnabledProvider())
        {
            IsVisible = false;
            return;
        }

        var hover = hoverProvider();
        if (hover is null || string.IsNullOrEmpty(hover.Value.Entry.Content))
        {
            IsVisible = false;
            return;
        }

        var info = hover.Value;

        // Content - уже подготовленный текст (см. RawSourceParsing.CleanContent): без HTML-тегов,
        // переносы строк как \n. TextNode.String принимает ReadOnlySeString - строим её явно через
        // SeStringBuilder (задокументированный на dalamud.dev способ), а не понадеявшись на неявное
        // преобразование из string, которое не удалось подтвердить в исходниках.
        textNode.String = new SeStringBuilder().Append(info.Entry.Content!).ToReadOnlySeString();

        // Ширина уже выставлена (TextWrapWidth) в конструкторе - GetTextDrawSize должен посчитать
        // высоту с учётом переноса по этой ширине. Не проверено на живом клиенте: если перенос
        // не работает (текст вылезает за TextWrapWidth по горизонтали) - в IntelliSense поищите
        // свойство переноса (WrapMode/TextFlags) у TextNode и выставьте его явно.
        var textSize = textNode.GetTextDrawSize();
        textNode.Size = new Vector2(TextWrapWidth, textSize.Y);
        textNode.Position = new Vector2(PaddingX / 2f, PaddingY / 2f);

        var contentSize = new Vector2(TextWrapWidth, textSize.Y) + new Vector2(PaddingX, PaddingY);
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
    }
}

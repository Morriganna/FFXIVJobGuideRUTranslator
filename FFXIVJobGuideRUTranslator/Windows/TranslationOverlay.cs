using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using FFXIVJobGuideRUTranslator.Data;

namespace FFXIVJobGuideRUTranslator.Windows;

/// <summary>
/// Рисует перевод описания умения отдельным всплывающим окном ImGui рядом с курсором, поверх
/// экрана - вместо того чтобы пытаться вписать его в родную подсказку игры (см. историю правок
/// в AbilityHoverWatcher про то, почему это не сработало). Окно ImGui само разворачивается под
/// любой объём текста, ничего в памяти игры не трогает и в принципе не может сломать её UI.
/// </summary>
public static class TranslationOverlay
{
    private const float WrapWidth = 340f;
    private const float Margin = 8f;

    // Размер окна с ПРЕДЫДУЩЕГО кадра - используется, чтобы решить, куда его поместить сейчас
    // (ImGui не знает размер AlwaysAutoResize-окна заранее, до отрисовки). Отставание на один
    // кадр незаметно глазу, а окну курсора это не мешает - false-положительный "прыжок" возможен
    // разве что в самый первый кадр показа, пока используется значение по умолчанию.
    private static Vector2 lastSize = new(340, 90);

    /// <summary>Рисует оверлей, если entry не null. Вызывать из UiBuilder.Draw.</summary>
    public static void Draw(TranslationEntry? entry)
    {
        if (entry is null || string.IsNullOrEmpty(entry.Content))
            return;

        var mouse = ImGui.GetMousePos();
        var display = ImGui.GetIO().DisplaySize;

        var pos = mouse + new Vector2(28, 28);

        // Не помещается по высоте ниже курсора (курсор у нижнего края экрана, как на хотбаре) -
        // показываем окно НАД курсором вместо того, чтобы дать ему вылезти за пределы экрана.
        if (pos.Y + lastSize.Y + Margin > display.Y)
            pos.Y = mouse.Y - lastSize.Y - 12;

        // Аналогично по ширине - не даём вылезти за правый край.
        if (pos.X + lastSize.X + Margin > display.X)
            pos.X = display.X - lastSize.X - Margin;

        pos.X = Math.Max(pos.X, Margin);
        pos.Y = Math.Max(pos.Y, Margin);

        ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.95f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10, 8));

        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar
                                        | ImGuiWindowFlags.NoResize
                                        | ImGuiWindowFlags.NoMove
                                        | ImGuiWindowFlags.NoNav
                                        | ImGuiWindowFlags.NoFocusOnAppearing
                                        | ImGuiWindowFlags.NoInputs
                                        | ImGuiWindowFlags.AlwaysAutoResize;

        if (ImGui.Begin("###JobGuideRUTranslationOverlay", flags))
        {
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + WrapWidth);
            ImGui.TextColored(new Vector4(0.55f, 0.85f, 1f, 1f), "Перевод (ff14jobguide.ru):");
            ImGui.TextWrapped(entry.Content);
            ImGui.PopTextWrapPos();

            lastSize = ImGui.GetWindowSize();
        }

        ImGui.End();
        ImGui.PopStyleVar();
    }
}

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

    /// <summary>Рисует оверлей, если entry не null. Вызывать из UiBuilder.Draw.</summary>
    public static void Draw(TranslationEntry? entry)
    {
        if (entry is null || string.IsNullOrEmpty(entry.Content))
            return;

        var mouse = ImGui.GetMousePos();
        // Небольшой отступ от курсора, чтобы не перекрывать саму родную подсказку/курсор.
        ImGui.SetNextWindowPos(mouse + new Vector2(28, 28), ImGuiCond.Always);
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
        }

        ImGui.End();
        ImGui.PopStyleVar();
    }
}

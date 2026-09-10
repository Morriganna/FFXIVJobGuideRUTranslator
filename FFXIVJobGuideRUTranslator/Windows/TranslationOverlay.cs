using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using FFXIVJobGuideRUTranslator.Hooks;

namespace FFXIVJobGuideRUTranslator.Windows;

/// <summary>
/// Рисует перевод описания умения отдельным всплывающим окном ImGui рядом с родным окном игры
/// (по его экранным координатам, а не по курсору мыши - см. историю правок: у курсора перевод
/// иногда перекрывал совсем другой контент, например список умений в "Actions & Traits"), поверх
/// экрана - вместо того чтобы пытаться вписать его в родную подсказку игры (см. историю правок
/// в AbilityHoverWatcher про то, почему это не сработало). Окно ImGui само разворачивается под
/// любой объём текста, ничего в памяти игры не трогает и в принципе не может сломать её UI.
///
/// По внешнему виду специально старается быть визуальным "двойником" родной подсказки: тот же
/// шрифт (настоящий игровой Axis через Dalamud GameFontStyle, тот же небольшой размер, что и в
/// самой подсказке), тот же тёмный фон, тонкая рамка с почти прямыми углами и компактные отступы,
/// та же подсветка меток - "Duration:" зелёным, "Additional Effect:" золотым. Никакой собственной
/// подписи/шапки не рисует - просто текст описания, как если бы это была ещё одна такая же
/// плашка, только на русском.
/// </summary>
public static class TranslationOverlay
{
    private const float WrapWidth = 260f;
    private const float Margin = 8f;
    private const float Gap = 6f; // расстояние между родным окном и нашим

    // Цвета подобраны на глаз под то, как их красит родная подсказка игры (см. скриншоты в истории
    // правок) - точных hex-кодов из клиента у меня нет, так что это приближение, не единственно
    // возможное. Порядок важен: более длинные/специфичные варианты раньше более общих.
    private static readonly (string Prefix, Vector4 Color)[] HighlightedPrefixes =
    {
        ("Продолжительность", new Vector4(0.62f, 0.85f, 0.55f, 1f)),      // как "Duration:" в оригинале - зелёный
        ("Длительность", new Vector4(0.62f, 0.85f, 0.55f, 1f)),
        ("Дополнительные эффекты", new Vector4(0.85f, 0.72f, 0.40f, 1f)), // как "Additional Effect:" - золотой
        ("Дополнительный эффект", new Vector4(0.85f, 0.72f, 0.40f, 1f)),
        ("Комбо умение", new Vector4(0.85f, 0.72f, 0.40f, 1f)),
        ("Комбо-действие", new Vector4(0.85f, 0.72f, 0.40f, 1f)),
        ("Комбо бонус", new Vector4(0.85f, 0.72f, 0.40f, 1f)),
        ("Бонус комбо", new Vector4(0.85f, 0.72f, 0.40f, 1f)),
        // "Cure Potency:"/"Attack Potency:" и т.п. в оригинале - голубоватый, отдельно от золотого.
        ("Сила лечения", new Vector4(0.45f, 0.75f, 0.90f, 1f)),
        ("Сила исцеления", new Vector4(0.45f, 0.75f, 0.90f, 1f)),
        ("Сила атаки", new Vector4(0.45f, 0.75f, 0.90f, 1f)),
        ("Сила урона", new Vector4(0.45f, 0.75f, 0.90f, 1f)),
        ("Сила в комбо", new Vector4(0.45f, 0.75f, 0.90f, 1f)),
        ("Сила комбо", new Vector4(0.45f, 0.75f, 0.90f, 1f)),
    };

    private static readonly Vector4 BodyColor = new(0.90f, 0.90f, 0.92f, 1f);
    private static readonly Vector4 SeparatorColor = new(0.5f, 0.5f, 0.54f, 0.45f);

    // Размер окна с ПРЕДЫДУЩЕГО кадра - используется, чтобы решить, куда его поместить сейчас
    // (ImGui не знает размер AlwaysAutoResize-окна заранее, до отрисовки). Отставание на один
    // кадр незаметно глазу.
    private static Vector2 lastSize = new(260, 80);

    private static IFontHandle? bodyFontHandle;

    /// <summary>
    /// Настоящий игровой шрифт (Axis, 12pt - как основной текст описания в родной подсказке)
    /// вместо системного шрифта ImGui. Создаётся один раз и держится на весь сеанс игры.
    /// </summary>
    private static IFontHandle GetBodyFont()
    {
        return bodyFontHandle ??= Plugin.PluginInterface.UiBuilder.FontAtlas.NewGameFontHandle(
            new GameFontStyle(GameFontFamilyAndSize.Axis12));
    }

    /// <summary>Рисует оверлей, если hover не null. Вызывать из UiBuilder.Draw.</summary>
    public static void Draw(AbilityHoverWatcher.HoverInfo? hover)
    {
        if (hover is null || string.IsNullOrEmpty(hover.Value.Entry.Content))
            return;

        var info = hover.Value;
        var display = ImGui.GetIO().DisplaySize;

        // По умолчанию - справа от родного окна, на той же высоте, что и его верх.
        var pos = new Vector2(info.X + info.Width + Gap, info.Y);

        // Не помещается справа - показываем слева от родного окна вместо того, чтобы вылезти
        // за правый край экрана.
        if (pos.X + lastSize.X + Margin > display.X)
            pos.X = info.X - lastSize.X - Gap;

        // Всё ещё за пределами (окно само у самого края) - прижимаем к соответствующему краю экрана.
        pos.X = Math.Clamp(pos.X, Margin, Math.Max(Margin, display.X - lastSize.X - Margin));

        // Не помещается по высоте ниже верхней границы родного окна - сдвигаем вверх так, чтобы
        // остаться в пределах экрана.
        pos.Y = Math.Clamp(pos.Y, Margin, Math.Max(Margin, display.Y - lastSize.Y - Margin));

        ImGui.SetNextWindowPos(pos, ImGuiCond.Always);

        // Тёмный, почти чёрный (с лёгким тёплым оттенком) фон и тонкая рамка практически без
        // скругления углов - под стиль родной подсказки умения. Отступы и интервалы компактные,
        // под стать плотной вёрстке родной плашки (не просторный "воздух" по умолчанию у ImGui).
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.085f, 0.078f, 0.070f, 0.97f));
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.5f, 0.48f, 0.44f, 0.5f));
        ImGui.PushStyleColor(ImGuiCol.Separator, SeparatorColor);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(7, 5));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(2, 2));

        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar
                                        | ImGuiWindowFlags.NoResize
                                        | ImGuiWindowFlags.NoMove
                                        | ImGuiWindowFlags.NoNav
                                        | ImGuiWindowFlags.NoFocusOnAppearing
                                        | ImGuiWindowFlags.NoInputs
                                        | ImGuiWindowFlags.AlwaysAutoResize;

        using var fontPush = GetBodyFont().Push();

        if (ImGui.Begin("###JobGuideRUTranslationOverlay", flags))
        {
            foreach (var line in info.Entry.Content!.Split('\n'))
                DrawLine(line);

            lastSize = ImGui.GetWindowSize();
        }

        ImGui.End();
        ImGui.PopStyleVar(4);
        ImGui.PopStyleColor(3);
    }

    /// <summary>
    /// Красит известную метку в начале строки ("Продолжительность:", "Дополнительный эффект:" и
    /// т.п.) под цвет оригинала. Короткое значение остаётся на той же строке, что и метка (как в
    /// игре); длинное - переносится на отдельную строку с переносом от левого края, а не от места
    /// окончания метки - иначе ImGui "залипает" на этом отступе для всех последующих строк абзаца.
    /// </summary>
    private static void DrawLine(string line)
    {
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + WrapWidth);

        foreach (var (prefix, color) in HighlightedPrefixes)
        {
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            var afterPrefix = line.Length > prefix.Length ? line[prefix.Length] : '\0';
            if (afterPrefix != ':')
                continue;

            var rest = line[(prefix.Length + 1)..].TrimStart();
            var labelText = prefix + ":";
            ImGui.TextColored(color, labelText);

            if (rest.Length > 0)
            {
                var availableForRest = WrapWidth - ImGui.CalcTextSize(labelText).X - 6;
                if (ImGui.CalcTextSize(rest).X <= availableForRest)
                    ImGui.SameLine(0, 6);

                ImGui.TextColored(BodyColor, rest);
            }

            ImGui.PopTextWrapPos();
            return;
        }

        ImGui.TextColored(BodyColor, line);
        ImGui.PopTextWrapPos();
    }
}

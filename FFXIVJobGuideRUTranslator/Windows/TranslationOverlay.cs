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
///
/// Внешний вид подогнан под стилистику родной подсказки умения: тёмный полупрозрачный фон,
/// тонкая рамка, золотая разделительная линия под заголовком (частый приём в интерфейсе FFXIV),
/// и подсветка тех же меток, что игра красит цветом в оригинале - "Duration:" (зелёный) и
/// "Additional Effect:" (золотой). Сама вёрстка (иконка/Range/Radius/Cast/Recast и т.п.) не
/// воспроизводится - это по-прежнему отдельное окно, а не копия родного.
/// </summary>
public static class TranslationOverlay
{
    private const float WrapWidth = 340f;
    private const float Margin = 8f;

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
    };

    private static readonly Vector4 BodyColor = new(0.90f, 0.90f, 0.92f, 1f);
    private static readonly Vector4 GoldAccent = new(0.75f, 0.62f, 0.32f, 0.9f); // общий золотой акцент интерфейса FFXIV

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

        // Тёмный полупрозрачный фон и тонкая рамка - под стиль родной подсказки умения.
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.055f, 0.055f, 0.065f, 0.95f));
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.42f, 0.44f, 0.50f, 0.6f));
        ImGui.PushStyleColor(ImGuiCol.Separator, GoldAccent);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(11, 9));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 3f);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(4, 5));

        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar
                                        | ImGuiWindowFlags.NoResize
                                        | ImGuiWindowFlags.NoMove
                                        | ImGuiWindowFlags.NoNav
                                        | ImGuiWindowFlags.NoFocusOnAppearing
                                        | ImGuiWindowFlags.NoInputs
                                        | ImGuiWindowFlags.AlwaysAutoResize;

        if (ImGui.Begin("###JobGuideRUTranslationOverlay", flags))
        {
            ImGui.SetWindowFontScale(0.82f);
            ImGui.TextColored(GoldAccent, "ПЕРЕВОД · FF14JOBGUIDE.RU");
            ImGui.SetWindowFontScale(1f);

            // Золотая разделительная линия под заголовком - как в самой игре под названиями разделов.
            ImGui.Separator();
            ImGui.Spacing();

            foreach (var line in entry.Content.Split('\n'))
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

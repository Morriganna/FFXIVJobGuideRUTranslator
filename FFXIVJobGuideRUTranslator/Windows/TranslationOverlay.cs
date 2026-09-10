using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using FFXIVJobGuideRUTranslator.Data;
using FFXIVJobGuideRUTranslator.Hooks;

namespace FFXIVJobGuideRUTranslator.Windows;

/// <summary>
/// Рисует перевод описания умения отдельным всплывающим окном ImGui рядом с родным окном игры
/// (по его экранным координатам, а не по курсору мыши - см. историю правок), поверх экрана -
/// вместо того чтобы пытаться вписать его в родную подсказку игры (см. AbilityHoverWatcher про
/// то, почему это не сработало). Окно ImGui само разворачивается под любой объём текста, ничего
/// в памяти игры не трогает и в принципе не может сломать её UI.
///
/// По внешнему виду специально старается быть визуальным "двойником" родной подсказки: тот же
/// шрифт (настоящий игровой Axis через Dalamud GameFontStyle), тёмный фон, тонкая рамка с почти
/// прямыми углами и компактные отступы, та же подсветка меток ("Duration:" зелёным, "Additional
/// Effect:"/"Cure Potency:" золотым/голубым) И названий умений/статусов ПРЯМО ВНУТРИ предложения
/// (они остаются на английском в переводе - оригинал их не переводит, как и мы) - так же, как это
/// делает сама игра. Перенос строк реализован вручную, по словам (а не через PushTextWrapPos) -
/// иначе разноцветные куски одного абзаца "залипают" на отступе первого куска при переносе
/// (см. историю правок).
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
    // Названия умений/статусов, упомянутые внутри предложения (остаются на английском) -
    // тот же тёплый оттенок, что и "Additional Effect:".
    private static readonly Vector4 NameHighlightColor = new(0.90f, 0.62f, 0.32f, 1f);

    // Подряд идущие слова с большой буквы (латиница) - кандидаты на "это название умения/статуса".
    private static readonly Regex CapitalizedRunRegex =
        new(@"\b[A-Z][a-zA-Z']*(?:\s+[A-Z][a-zA-Z']*){0,3}\b", RegexOptions.Compiled);
    private static readonly Regex WhitespaceSplitRegex = new(@"(\s+)", RegexOptions.Compiled);

    // Размер окна с ПРЕДЫДУЩЕГО кадра - используется, чтобы решить, куда его поместить сейчас
    // (ImGui не знает размер AlwaysAutoResize-окна заранее, до отрисовки). Отставание на один
    // кадр незаметно глазу.
    private static Vector2 lastSize = new(260, 80);

    private static IFontHandle? bodyFontHandle;

    private readonly record struct Token(string Text, Vector4 Color);

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
    public static void Draw(AbilityHoverWatcher.HoverInfo? hover, TranslationRepository repository)
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
                DrawLine(line, repository);

            lastSize = ImGui.GetWindowSize();
        }

        ImGui.End();
        ImGui.PopStyleVar(4);
        ImGui.PopStyleColor(3);
    }

    /// <summary>
    /// Рисует одну строку описания: известную метку в начале ("Продолжительность:" и т.п.) - цветом
    /// оригинала, названия умений/статусов внутри предложения (остаются на английском) - тем же
    /// тёплым акцентом, что и в игре, остальное - обычным цветом. Перенос строк - вручную, по
    /// словам (см. класс) - иначе ImGui "залипает" на отступе первого разноцветного куска абзаца.
    /// </summary>
    private static void DrawLine(string line, TranslationRepository repository)
    {
        var body = line;
        Token? label = null;

        foreach (var (prefix, color) in HighlightedPrefixes)
        {
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            var afterPrefix = line.Length > prefix.Length ? line[prefix.Length] : '\0';
            if (afterPrefix != ':')
                continue;

            label = new Token(prefix + ":", color);
            body = line[(prefix.Length + 1)..].TrimStart();
            break;
        }

        var tokens = Tokenize(body, repository);

        var cursorX = 0f;
        var atLineStart = true;

        void Place(Token token)
        {
            // Пробел, оказавшийся в начале строки (после переноса) - пропускаем, иначе перенесённая
            // строка начинается с заметного отступа.
            if (atLineStart && string.IsNullOrWhiteSpace(token.Text))
                return;

            var width = ImGui.CalcTextSize(token.Text).X;
            // Перенос строки: НЕ вызываем ImGui.NewLine() явно - обычный TextColored сам переводит
            // курсор на новую строку, если следующий вызов не предварён SameLine(). Явный NewLine()
            // здесь добавлял бы ВТОРОЙ перевод строки поверх автоматического - отсюда были двойные
            // интервалы между строками. Просто не зовём SameLine() для этого токена.
            var wraps = !atLineStart && cursorX + width > WrapWidth;
            if (wraps)
            {
                cursorX = 0f;
                atLineStart = true;

                if (string.IsNullOrWhiteSpace(token.Text))
                    return; // тот же пробел, теперь уже в начале новой строки - тоже не рисуем
            }

            if (!atLineStart)
                ImGui.SameLine(0, 0);

            ImGui.TextColored(token.Color, token.Text);
            cursorX += width;
            atLineStart = false;
        }

        if (label is { } l)
        {
            Place(l);
            Place(new Token(" ", BodyColor));
        }

        foreach (var token in tokens)
            Place(token);

        if (atLineStart)
            ImGui.NewLine(); // строка не дала ни одного видимого токена (пустая строка в оригинале) - просто переходим дальше
    }

    /// <summary>Разбивает текст на токены (слова/пробелы), подсвечивая упомянутые в нём известные названия умений/статусов.</summary>
    private static List<Token> Tokenize(string text, TranslationRepository repository)
    {
        var tokens = new List<Token>();
        var pos = 0;

        foreach (Match match in CapitalizedRunRegex.Matches(text))
        {
            if (match.Index > pos)
                AppendPlainWords(text[pos..match.Index], tokens);

            var words = match.Value.Split(' ');
            var matchedWordCount = 0;
            for (var take = words.Length; take >= 1; take--)
            {
                var candidate = string.Join(' ', words, 0, take);
                if (repository.TryGetByEnglishName(candidate, out _))
                {
                    matchedWordCount = take;
                    break;
                }
            }

            if (matchedWordCount > 0)
            {
                var matchedText = string.Join(' ', words, 0, matchedWordCount);
                tokens.Add(new Token(matchedText, NameHighlightColor));
                pos = match.Index + matchedText.Length;
            }
            else
            {
                AppendPlainWords(match.Value, tokens);
                pos = match.Index + match.Length;
            }
        }

        if (pos < text.Length)
            AppendPlainWords(text[pos..], tokens);

        return tokens;
    }

    private static void AppendPlainWords(string text, List<Token> tokens)
    {
        foreach (var part in WhitespaceSplitRegex.Split(text))
        {
            if (part.Length > 0)
                tokens.Add(new Token(part, BodyColor));
        }
    }
}

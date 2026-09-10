using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;
using FFXIVJobGuideRUTranslator.Data;
using FFXIVJobGuideRUTranslator.Hooks;

namespace FFXIVJobGuideRUTranslator.Windows;

/// <summary>
/// Рисует перевод умения в отдельном окне ImGui рядом с курсором, поверх экрана - не трогает
/// родную подсказку игры (см. AbilityHoverWatcher), просто разворачивается под любой текст.
/// Оформление - по макету "Ability Tooltip ImGui" (Claude Design): иконка+имя+классификация+
/// дальность/радиус сверху, Каст/Восстановление строкой, разделитель, текст перевода (метки вроде
/// "Продолжительность:" красятся зелёным прямо в потоке), разделитель, "Получено"/"Работы" снизу.
/// </summary>
public static class TranslationOverlay
{
    // Базовые размеры под монитор 1920x1080 (Scale=1, см. GetScale) - на более высоком разрешении
    // пропорционально растут, иначе окно нечитаемо мелкое рядом с игровым UI.
    private const float BaseWrapWidth = 280f;
    private const float BaseMargin = 8f;
    private const float BaseGap = 6f;
    private const float BaseWindowPaddingX = 14f;
    private const float BaseWindowPaddingY = 12f;
    private const float BaseItemSpacing = 4f;
    private const float IconSize = 40f;

    private const float MinScale = 1f;
    private const float MaxScale = 2.5f;

    private static Vector4 Hex(int r, int g, int b, float a = 1f) => new(r / 255f, g / 255f, b / 255f, a);

    // Цвета - из макета "Ability Tooltip ImGui".
    private static readonly Vector4 WindowBgColor = Hex(0x0a, 0x0c, 0x10, 0.94f);
    private static readonly Vector4 BorderColor = Hex(0x6a, 0x75, 0x80);
    private static readonly Vector4 SeparatorColor = Hex(0x4c, 0x55, 0x5f);
    private static readonly Vector4 TextColor = Hex(0xf0, 0xf0, 0xf0);
    private static readonly Vector4 LabelColor = Hex(0xa7, 0xae, 0xb5);
    private static readonly Vector4 AccentGold = Hex(0xe8, 0xc9, 0x8a); // названия умений/статусов внутри текста
    private static readonly Vector4 LineLabelGreen = Hex(0x8f, 0xd4, 0x8a); // "Продолжительность:"/"Стоимость:", "Ур. N"

    // Метки, у которых зелёным красится всё до двоеточия включительно, остальное - обычным текстом.
    private static readonly string[] LabelOnlyAccentPrefixes =
    {
        "Продолжительность",
        "Длительность",
        "Сила",
        "Стоимость",
        "Дополнительные эффекты",
        "Дополнительный эффект",
        "Комбо умение",
        "Комбо-действие",
        "Комбо бонус",
        "Бонус комбо",
        "Первое использование",
        "Второе использование",
        "Третье использование",
        "Четвёртое использование",
    };

    // "Эффект <Название статуса>:" - составная метка с заранее не известным названием, той же категории.
    private static readonly Regex EffectOfNamedStatusRegex =
        new(@"^Эффект\s+[A-Z][a-zA-Z']*(?:\s+[A-Z][a-zA-Z']*)*:", RegexOptions.Compiled);

    // Названия умений/статусов внутри предложения - остаются на английском, красятся золотым.
    private static readonly Regex CapitalizedRunRegex =
        new(@"\b[A-Z][a-zA-Z']*(?:\s+[A-Z][a-zA-Z']*){0,3}\b", RegexOptions.Compiled);
    private static readonly Regex WhitespaceSplitRegex = new(@"(\s+)", RegexOptions.Compiled);

    private static readonly HashSet<string> ExcludedAbbreviations = new(StringComparer.Ordinal)
    {
        "HP", "MP", "TP", "GP", "CP",
    };

    // Размер окна с прошлого кадра - для позиционирования (ImGui не знает размер AlwaysAutoResize-окна заранее).
    private static Vector2 lastSize = new(280, 80);

    private static IFontHandle? bodyFontHandle;

    private readonly record struct Token(string Text, Vector4 Color);

    /// <summary>Игровой шрифт (Axis 9.6pt) вместо системного - создаётся один раз на сеанс.</summary>
    private static IFontHandle GetBodyFont()
    {
        return bodyFontHandle ??= Plugin.PluginInterface.UiBuilder.FontAtlas.NewGameFontHandle(
            new GameFontStyle(GameFontFamilyAndSize.Axis96));
    }

    /// <summary>Множитель размера под текущее разрешение экрана, относительно базового 1920x1080.</summary>
    private static float GetScale(Vector2 display)
    {
        const float baselineHeight = 1080f;
        if (display.Y <= 0)
            return MinScale;

        return Math.Clamp(display.Y / baselineHeight, MinScale, MaxScale);
    }

    /// <summary>Рисует оверлей, если hover не null. Вызывать из UiBuilder.Draw.</summary>
    public static void Draw(AbilityHoverWatcher.HoverInfo? hover, TranslationRepository repository, ITextureProvider textureProvider)
    {
        if (hover is null || string.IsNullOrEmpty(hover.Value.Entry.Content))
            return;

        var info = hover.Value;
        // Есть, только если ActionId принадлежит листу Action (боевое умение) - для крафта/сбора
        // или неразрешённых записей null, тогда иконка/статы/подвал просто не рисуются.
        var hasStats = repository.ActionStatsById.TryGetValue(info.Entry.ActionId, out var stats);

        var display = ImGui.GetIO().DisplaySize;
        var scale = GetScale(display);

        var margin = BaseMargin * scale;
        var gap = BaseGap * scale;

        // Позиционируем от курсора мыши, а не от родного окна - оно после скрытия перестаёт
        // обновлять свои координаты (см. AbilityHoverWatcher), курсор всегда актуален.
        var mouse = ImGui.GetIO().MousePos;
        var pos = mouse + new Vector2(gap, gap);

        if (pos.X + lastSize.X + margin > display.X)
            pos.X = mouse.X - lastSize.X - gap;
        if (pos.Y + lastSize.Y + margin > display.Y)
            pos.Y = mouse.Y - lastSize.Y - gap;

        pos.X = Math.Clamp(pos.X, margin, Math.Max(margin, display.X - lastSize.X - margin));
        pos.Y = Math.Clamp(pos.Y, margin, Math.Max(margin, display.Y - lastSize.Y - margin));

        ImGui.SetNextWindowPos(pos, ImGuiCond.Always);

        ImGui.PushStyleColor(ImGuiCol.WindowBg, WindowBgColor);
        ImGui.PushStyleColor(ImGuiCol.Border, BorderColor);
        ImGui.PushStyleColor(ImGuiCol.Separator, SeparatorColor);
        ImGui.PushStyleColor(ImGuiCol.Text, TextColor);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(BaseWindowPaddingX * scale, BaseWindowPaddingY * scale));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 5f * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(BaseItemSpacing * scale, BaseItemSpacing * scale));

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
            // До любого текста/CalcTextSize в этом окне - иначе перенос строк посчитает по
            // немасштабированному размеру шрифта.
            ImGui.SetWindowFontScale(scale);

            DrawHeader(info.Entry, hasStats ? stats : null, textureProvider, scale);

            if (hasStats)
                DrawCastRecastRow(stats, scale);

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            foreach (var line in info.Entry.Content!.Split('\n'))
                DrawLine(line, scale);

            if (hasStats)
            {
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();
                DrawFooter(stats, scale);
            }

            lastSize = ImGui.GetWindowSize();
        }

        ImGui.End();
        ImGui.PopStyleVar(4);
        ImGui.PopStyleColor(4);
    }

    /// <summary>Иконка + имя + классификация (слева) и Дальность/Радиус (справа) под именем.</summary>
    private static void DrawHeader(TranslationEntry entry, ActionStats? stats, ITextureProvider textureProvider, float scale)
    {
        if (stats is { } s)
        {
            var iconSize = IconSize * scale;
            var wrap = textureProvider.GetFromGameIcon(new GameIconLookup { IconId = s.IconId }).GetWrapOrEmpty();
            ImGui.Image(wrap.Handle, new Vector2(iconSize, iconSize));
            ImGui.SameLine();
        }

        ImGui.BeginGroup();

        ImGui.SetWindowFontScale(scale * 1.3f);
        ImGui.TextUnformatted(entry.EnglishName);
        ImGui.SetWindowFontScale(scale);

        var rangeText = stats is { } s2 ? $"Дальность {FormatYalms(s2.Range)} · Радиус {FormatYalms(s2.Radius)}" : null;
        var hasClassification = !string.IsNullOrEmpty(entry.Classification);
        if (hasClassification)
            ImGui.TextColored(LabelColor, entry.Classification);

        if (rangeText is not null)
        {
            var rangeWidth = ImGui.CalcTextSize(rangeText).X;
            var targetX = Math.Max(ImGui.GetCursorPosX(), BaseWrapWidth * scale - rangeWidth);
            if (hasClassification)
                ImGui.SameLine(targetX); // продолжаем строку классификации
            else
                ImGui.SetCursorPosX(targetX); // классификации нет - курсор и так уже на новой строке под именем
            ImGui.TextColored(LabelColor, rangeText);
        }

        ImGui.EndGroup();
    }

    /// <summary>"Каст Мгновенная    Восстановление 5.00 сек." одной строкой.</summary>
    private static void DrawCastRecastRow(ActionStats stats, float scale)
    {
        ImGui.TextColored(LabelColor, "Каст");
        ImGui.SameLine();
        ImGui.TextUnformatted(stats.CastHundredMs == 0 ? "Мгновенная" : FormatSeconds(stats.CastHundredMs));

        ImGui.SameLine(150f * scale);
        ImGui.TextColored(LabelColor, "Восстановление");
        ImGui.SameLine();
        ImGui.TextUnformatted(FormatSeconds(stats.RecastHundredMs));
    }

    /// <summary>"Получено: RPR Ур. N" / "Работы: RPR SAM ...".</summary>
    private static void DrawFooter(ActionStats stats, float scale)
    {
        ImGui.TextColored(LabelColor, "Получено:");
        ImGui.SameLine();
        ImGui.TextUnformatted(stats.Jobs.Count > 0 ? stats.Jobs[0] : "-");
        ImGui.SameLine();
        ImGui.TextColored(LineLabelGreen, $"Ур. {stats.Level}");

        if (stats.Jobs.Count > 0)
        {
            ImGui.TextColored(LabelColor, "Работы:");
            ImGui.SameLine();

            // По словам, а не одним слитным токеном - иначе длинный список работ не переносится
            // по ширине окна, а просто раздувает его вширь.
            var jobTokens = new List<Token>();
            AppendPlainWords(string.Join("  ", stats.Jobs), jobTokens, TextColor);
            RenderLineTokens(jobTokens, BaseWrapWidth * scale);
        }
    }

    private static string FormatSeconds(int hundredMs) => (hundredMs / 10f).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " сек.";

    private static string FormatYalms(int range) => range < 0 ? "-" : range + "y";

    /// <summary>Рисует одну строку описания - метка (см. LabelOnlyAccentPrefixes) зелёным, остальное обычным текстом с золотыми названиями.</summary>
    private static void DrawLine(string line, float scale)
    {
        var tokens = ComputeLineTokens(line);
        RenderLineTokens(tokens, BaseWrapWidth * scale);
    }

    private static List<Token> ComputeLineTokens(string line)
    {
        var labelLength = FindGreenLabelLength(line);
        if (labelLength > 0)
        {
            var tokens = new List<Token> { new(line[..labelLength], LineLabelGreen) };
            tokens.AddRange(Tokenize(line[labelLength..], TextColor));
            return tokens;
        }

        return Tokenize(line, TextColor);
    }

    /// <summary>Длина зелёной части строки (до включённого двоеточия), или 0.</summary>
    private static int FindGreenLabelLength(string line)
    {
        foreach (var prefix in LabelOnlyAccentPrefixes)
        {
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            var colonIndex = line.IndexOf(':');
            // Двоеточие должно быть недалеко от метки - иначе это не "Метка:", а случайное
            // двоеточие где-то дальше в предложении, начинающемся с того же слова.
            if (colonIndex >= 0 && colonIndex - prefix.Length <= 40)
                return colonIndex + 1;
        }

        var effectMatch = EffectOfNamedStatusRegex.Match(line);
        return effectMatch.Success ? effectMatch.Length : 0;
    }

    // Перенос строк вручную, по словам (не PushTextWrapPos) - иначе разноцветные куски одного
    // абзаца залипают на отступе первого куска при переносе.
    private static void RenderLineTokens(List<Token> tokens, float wrapWidth)
    {
        var cursorX = 0f;
        var atLineStart = true;

        void Place(string text, Vector4 color, float glueWidth)
        {
            if (atLineStart && string.IsNullOrWhiteSpace(text))
                return;

            var width = ImGui.CalcTextSize(text).X;
            var wraps = !atLineStart && cursorX + width + glueWidth > wrapWidth;
            if (wraps)
            {
                cursorX = 0f;
                atLineStart = true;

                if (string.IsNullOrWhiteSpace(text))
                    return;
            }

            if (!atLineStart)
                ImGui.SameLine(0, 0);

            ImGui.TextColored(color, text);
            cursorX += width;
            atLineStart = false;
        }

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            // "(" без пробела перед следующим словом - переносим их вместе, как единое целое.
            var glueWidth = token.Text == "(" && i + 1 < tokens.Count
                ? ImGui.CalcTextSize(tokens[i + 1].Text).X
                : 0f;
            Place(token.Text, token.Color, glueWidth);
        }

        if (atLineStart)
            ImGui.NewLine();
    }

    /// <summary>Разбивает текст на токены, подсвечивая названия умений/статусов золотым - кроме коротких сокращений вроде HP/MP.</summary>
    private static List<Token> Tokenize(string text, Vector4 defaultColor)
    {
        var tokens = new List<Token>();
        var pos = 0;

        foreach (Match match in CapitalizedRunRegex.Matches(text))
        {
            if (match.Index > pos)
                AppendPlainWords(text[pos..match.Index], tokens, defaultColor);

            if (ExcludedAbbreviations.Contains(match.Value))
                AppendPlainWords(match.Value, tokens, defaultColor);
            else
                tokens.Add(new Token(match.Value, AccentGold));

            pos = match.Index + match.Length;
        }

        if (pos < text.Length)
            AppendPlainWords(text[pos..], tokens, defaultColor);

        return tokens;
    }

    private static void AppendPlainWords(string text, List<Token> tokens, Vector4 color)
    {
        foreach (var part in WhitespaceSplitRegex.Split(text))
        {
            if (part.Length > 0)
                tokens.Add(new Token(part, color));
        }
    }
}

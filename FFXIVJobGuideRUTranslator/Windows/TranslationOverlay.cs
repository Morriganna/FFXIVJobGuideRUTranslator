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
/// Рисует перевод описания умения отдельным всплывающим окном ImGui рядом с родным окном игры
/// (по его экранным координатам, а не по курсору мыши - см. историю правок), поверх экрана -
/// вместо того чтобы пытаться вписать его в родную подсказку игры (см. AbilityHoverWatcher про
/// то, почему это не сработало). Окно ImGui само разворачивается под любой объём текста, ничего
/// в памяти игры не трогает и в принципе не может сломать её UI.
///
/// Оформление - по третьей версии макета "Ability Tooltip ImGui" (Claude Design, см. историю
/// правок - первые две не прижились: заголовок-инспектор был избыточным, карточки-строки с
/// цветными полосками - лишней сложностью). Эта версия проще: иконка+имя+классификация+
/// дальность/радиус сверху, Каст/Восстановление одной строкой, разделитель, текст перевода (где
/// "структурные" метки вроде "Продолжительность:"/"Стоимость:" красятся зелёным прямо в потоке
/// текста, без отдельных карточек), ещё разделитель, "Получено"/"Работы" снизу.
/// </summary>
public static class TranslationOverlay
{
    // Базовые размеры подобраны под условный монитор 1920x1080 - на нём Scale (см. GetScale)
    // равен 1. На более высоком разрешении окно целиком (шрифт, отступы, перенос строк)
    // пропорционально увеличивается, иначе на 1440p/4K оно выглядит нечитаемо мелким рядом с
    // родной подсказкой игры, которая масштабируется вместе с игровым UI.
    private const float BaseWrapWidth = 280f;
    private const float BaseMargin = 8f;
    private const float BaseGap = 6f; // расстояние между родным окном и нашим
    private const float BaseWindowPaddingX = 14f;
    private const float BaseWindowPaddingY = 12f;
    private const float BaseItemSpacing = 4f;
    private const float IconSize = 40f;

    // Не даём окну ужаться мельче исходного расчёта (на совсем маленьких/низких разрешениях) и
    // не даём ему раздуться бесконечно на сверхширокоформатных мониторах - разумный потолок.
    private const float MinScale = 1f;
    private const float MaxScale = 2.5f;

    private static Vector4 Hex(int r, int g, int b, float a = 1f) => new(r / 255f, g / 255f, b / 255f, a);

    // Цвета - один в один из hex-значений макета "Ability Tooltip ImGui" (см. доккомментарий класса).
    private static readonly Vector4 WindowBgColor = Hex(0x0a, 0x0c, 0x10, 0.94f);
    private static readonly Vector4 BorderColor = Hex(0x6a, 0x75, 0x80);
    private static readonly Vector4 SeparatorColor = Hex(0x4c, 0x55, 0x5f);
    private static readonly Vector4 TextColor = Hex(0xf0, 0xf0, 0xf0); // имя, значения Каст/Восст., тело текста
    private static readonly Vector4 LabelColor = Hex(0xa7, 0xae, 0xb5); // классификация/дальность-радиус/подписи
    private static readonly Vector4 AccentGold = Hex(0xe8, 0xc9, 0x8a); // названия умений/статусов внутри текста
    private static readonly Vector4 LineLabelGreen = Hex(0x8f, 0xd4, 0x8a); // "Продолжительность:"/"Стоимость:" и т.п., "Ур. N"

    // Все эти строки в оригинале красятся одинаково: зелёное всё ДО ПЕРВОГО двоеточия включительно
    // (сама метка, плюс переменное название статуса/шкалы, если оно есть, например "Стоимость
    // Angler's Art:", "Сила под эффектом Divine Might:" - оно тоже зелёное, без отдельного
    // золотого вырезания, раз это часть составной метки, а не самостоятельно упомянутая вещь).
    // Всё, что ПОСЛЕ двоеточия - описание эффекта, число, время и т.п. - обычным текстом; конкретное
    // название умения/статуса внутри этой части (если есть) всё равно вырезается и красится
    // отдельным золотым цветом - см. Tokenize.
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
        // "First Use:"/"Second Use:"/"Third Use:" (шкала эффектов повторного использования, как
        // у Modest Lure и подобных DoL-умений) - та же категория "метка зелёная, значение обычное".
        "Первое использование",
        "Второе использование",
        "Третье использование",
        "Четвёртое использование",
    };

    // Составные метки вида "Эффект <Название статуса>:" (например "Эффект Knight's Resolve:") -
    // конкретное название заранее не известно (это не отдельное умение, а статус, произведённый от
    // него), но сама метка (включая название) целиком зелёная - как и у LabelOnlyAccentPrefixes.
    // Важно: после "Эффект" сразу должно идти капитализированное английское название И двоеточие -
    // иначе это просто обычное предложение ("Эффект заканчивается после..."), не метка.
    private static readonly Regex EffectOfNamedStatusRegex =
        new(@"^Эффект\s+[A-Z][a-zA-Z']*(?:\s+[A-Z][a-zA-Z']*)*:", RegexOptions.Compiled);

    // Подряд идущие слова с большой буквы (латиница) - кандидаты на "это название умения/статуса",
    // упомянутое прямо внутри предложения (остаётся на английском в переводе, как и в оригинале).
    private static readonly Regex CapitalizedRunRegex =
        new(@"\b[A-Z][a-zA-Z']*(?:\s+[A-Z][a-zA-Z']*){0,3}\b", RegexOptions.Compiled);
    private static readonly Regex WhitespaceSplitRegex = new(@"(\s+)", RegexOptions.Compiled);

    // Короткие служебные сокращения характеристик - их игра не подсвечивает как ссылку на
    // умение/статус, в отличие от почти любого другого захваченного куска с большой буквы.
    private static readonly HashSet<string> ExcludedAbbreviations = new(StringComparer.Ordinal)
    {
        "HP", "MP", "TP", "GP", "CP",
    };

    // Размер окна с ПРЕДЫДУЩЕГО кадра - используется, чтобы решить, куда его поместить сейчас
    // (ImGui не знает размер AlwaysAutoResize-окна заранее, до отрисовки). Отставание на один
    // кадр незаметно глазу.
    private static Vector2 lastSize = new(280, 80);

    private static IFontHandle? bodyFontHandle;

    private readonly record struct Token(string Text, Vector4 Color);

    /// <summary>
    /// Настоящий игровой шрифт (Axis, 9.6pt - меньше и компактнее 12pt, ближе к тому, каким
    /// размером в родной подсказке набрано само описание) вместо системного шрифта ImGui.
    /// Создаётся один раз и держится на весь сеанс игры.
    /// </summary>
    private static IFontHandle GetBodyFont()
    {
        return bodyFontHandle ??= Plugin.PluginInterface.UiBuilder.FontAtlas.NewGameFontHandle(
            new GameFontStyle(GameFontFamilyAndSize.Axis96));
    }

    /// <summary>
    /// Множитель размера окна под текущее разрешение экрана (DisplaySize), относительно базового
    /// расчёта под 1920x1080 (см. константы Base*). Считаем по высоте - она меньше "плавает" от
    /// ультраширокоформатных мониторов, чем ширина. Зажато между MinScale и MaxScale.
    /// </summary>
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
        // Есть, только если ActionId точно принадлежит листу Action (боевое умение) - см.
        // TranslationRepository.ActionStatsById/ActionStatsLookup. Для крафта/сбора (CraftAction)
        // или неразрешённых записей - null, тогда просто не рисуем иконку/статы/подвал.
        var hasStats = repository.ActionStatsById.TryGetValue(info.Entry.ActionId, out var stats);

        var display = ImGui.GetIO().DisplaySize;
        var scale = GetScale(display);

        var margin = BaseMargin * scale;
        var gap = BaseGap * scale;

        // По умолчанию - справа от родного окна, на той же высоте, что и его верх.
        var pos = new Vector2(info.X + info.Width + gap, info.Y);

        // Не помещается справа - показываем слева от родного окна вместо того, чтобы вылезти
        // за правый край экрана.
        if (pos.X + lastSize.X + margin > display.X)
            pos.X = info.X - lastSize.X - gap;

        // Всё ещё за пределами (окно само у самого края) - прижимаем к соответствующему краю экрана.
        pos.X = Math.Clamp(pos.X, margin, Math.Max(margin, display.X - lastSize.X - margin));

        // Не помещается по высоте ниже верхней границы родного окна - сдвигаем вверх так, чтобы
        // остаться в пределах экрана.
        pos.Y = Math.Clamp(pos.Y, margin, Math.Max(margin, display.Y - lastSize.Y - margin));

        ImGui.SetNextWindowPos(pos, ImGuiCond.Always);

        ImGui.PushStyleColor(ImGuiCol.WindowBg, WindowBgColor);
        ImGui.PushStyleColor(ImGuiCol.Border, BorderColor);
        ImGui.PushStyleColor(ImGuiCol.Separator, SeparatorColor);
        ImGui.PushStyleColor(ImGuiCol.Text, TextColor);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(BaseWindowPaddingX * scale, BaseWindowPaddingY * scale));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 5f * scale); // как border-radius:5px в макете
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
            // Масштабирует весь текст этого окна (и то, что CalcTextSize/перенос строк ниже
            // считают в WrapWidth*scale) под разрешение монитора - см. GetScale. Обязательно
            // ДО любого текста/CalcTextSize в этом окне, иначе перенос строк посчитает по
            // немасштабированному размеру шрифта и разъедется с реальной шириной глифов.
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

    /// <summary>Иконка + название + классификация (слева) и Дальность/Радиус (справа) на одной строке под именем.</summary>
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
        if (!string.IsNullOrEmpty(entry.Classification))
            ImGui.TextColored(LabelColor, entry.Classification);
        if (rangeText is not null)
        {
            if (!string.IsNullOrEmpty(entry.Classification))
                ImGui.SameLine();
            var rangeWidth = ImGui.CalcTextSize(rangeText).X;
            ImGui.SameLine(Math.Max(ImGui.GetCursorPosX(), BaseWrapWidth * scale - rangeWidth));
            ImGui.TextColored(LabelColor, rangeText);
        }

        ImGui.EndGroup();
    }

    /// <summary>"Каст Мгновенная    Восстановление 5.00 сек." одной строкой - см. ActionStats.</summary>
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

    /// <summary>"Получено: RPR Ур. N" / "Работы: RPR SAM ..." - см. ActionStats.</summary>
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
            RenderLineTokens(new List<Token> { new(string.Join("  ", stats.Jobs), TextColor) }, BaseWrapWidth * scale);
        }
    }

    private static string FormatSeconds(int hundredMs) => (hundredMs / 10f).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " сек.";

    private static string FormatYalms(int range) => range < 0 ? "-" : range + "y";

    /// <summary>
    /// Рисует одну строку описания. Если строка начинается с одной из "структурных" меток
    /// (Продолжительность/Дополнительный эффект/Комбо/Сила/Стоимость/"Эффект Х:") - зелёным
    /// красится только сама метка, до двоеточия включительно (туда же попадает и переменное
    /// название статуса, если оно есть); всё, что после двоеточия, и любые прочие строки - обычным
    /// текстом. В любом случае конкретные названия умений/статусов внутри строки (остаются на
    /// английском) вырезаются и красятся золотым - см. Tokenize. Перенос строк - вручную, по
    /// словам (а не через PushTextWrapPos) - иначе разноцветные куски одного абзаца "залипают" на
    /// отступе первого куска при переносе.
    /// </summary>
    private static void DrawLine(string line, float scale)
    {
        var tokens = ComputeLineTokens(line);
        RenderLineTokens(tokens, BaseWrapWidth * scale);
    }

    /// <summary>Решает, как разбить и раскрасить строку - см. комментарии у LabelOnlyAccentPrefixes/EffectOfNamedStatusRegex.</summary>
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

    /// <summary>Длина зелёной части строки (от начала до включённого двоеточия), или 0, если строка не относится ни к одной "зелёной" категории.</summary>
    private static int FindGreenLabelLength(string line)
    {
        foreach (var prefix in LabelOnlyAccentPrefixes)
        {
            if (!line.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            var colonIndex = line.IndexOf(':');
            if (colonIndex >= 0)
                return colonIndex + 1;
        }

        var effectMatch = EffectOfNamedStatusRegex.Match(line);
        return effectMatch.Success ? effectMatch.Length : 0;
    }

    private static void RenderLineTokens(List<Token> tokens, float wrapWidth)
    {
        var cursorX = 0f;
        var atLineStart = true;

        void Place(string text, Vector4 color, float glueWidth)
        {
            // Пробел, оказавшийся в начале строки (после переноса) - пропускаем, иначе перенесённая
            // строка начинается с заметного отступа.
            if (atLineStart && string.IsNullOrWhiteSpace(text))
                return;

            var width = ImGui.CalcTextSize(text).X;
            var wraps = !atLineStart && cursorX + width + glueWidth > wrapWidth;
            if (wraps)
            {
                cursorX = 0f;
                atLineStart = true;

                if (string.IsNullOrWhiteSpace(text))
                    return; // тот же пробел, теперь уже в начале новой строки - тоже не рисуем
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
            var glueWidth = token.Text == "(" && i + 1 < tokens.Count
                ? ImGui.CalcTextSize(tokens[i + 1].Text).X
                : 0f;
            Place(token.Text, token.Color, glueWidth);
        }

        if (atLineStart)
            ImGui.NewLine(); // строка не дала ни одного видимого токена (пустая строка в оригинале) - просто переходим дальше
    }

    /// <summary>
    /// Разбивает текст на токены (слова/пробелы), подсвечивая упомянутые в нём названия умений/
    /// статусов золотым (см. AccentGold). Поскольку почти любой захваченный кусок текста с большой
    /// буквы в описании умения и так является ссылкой на другое умение/статус (случайных
    /// капитализированных английских слов в русском переводе не бывает), подсвечиваются ВСЕ такие
    /// куски одинаково, кроме короткой служебки вроде "HP"/"MP" (её игра просто не выделяет).
    /// </summary>
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

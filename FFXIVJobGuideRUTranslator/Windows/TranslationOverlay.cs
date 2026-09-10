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
/// в памяти игры не трогает и в принципе не может сломать её UI. Плагин переводит ТОЛЬКО текст
/// описания - иконка, статы (Cast/Recast/Range/Radius), Acquired/Affinity остаются в родном окне
/// как есть, это окно их не дублирует и не подменяет, а просто становится рядом.
///
/// Оформление - по макету "Ability Inspector" (Claude Design, см. историю правок): НЕ попытка
/// притвориться родным окном игры (более ранний вариант пробовал красть настоящую текстуру фона
/// через AbilityHoverWatcher, но для ActionDetail подходящей ноды-текстуры почти никогда не
/// находилось), а осознанно "инструмент разработчика" - почти чёрный фон, тонкая серая рамка,
/// острые углы без скругления, настоящий заголовок окна (название умения) на цветной плашке -
/// сразу видно, что это окно плагина, а не часть родного UI. Настоящий игровой шрифт (Axis через
/// Dalamud GameFontStyle) и подсветка меток ("Duration:" зелёным, "Additional Effect:"/"Cure
/// Potency:" золотым/голубым) И названий умений/статусов ПРЯМО ВНУТРИ предложения (они остаются на
/// английском в переводе - оригинал их не переводит, как и мы) - часть самого текста, не оформления
/// окна. Перенос строк реализован вручную, по словам (а не через PushTextWrapPos) - иначе
/// разноцветные куски одного абзаца "залипают" на отступе первого куска при переносе (см. историю
/// правок).
/// </summary>
public static class TranslationOverlay
{
    // Базовые размеры подобраны под условный монитор 1920x1080 - на нём Scale (см. GetScale)
    // равен 1. На более высоком разрешении окно целиком (шрифт, отступы, перенос строк)
    // пропорционально увеличивается, иначе на 1440p/4K оно выглядит нечитаемо мелким рядом с
    // родной подсказкой игры, которая масштабируется вместе с игровым UI.
    private const float BaseWrapWidth = 260f;
    private const float BaseMargin = 8f;
    private const float BaseGap = 6f; // расстояние между родным окном и нашим
    private const float BaseWindowPaddingX = 7f;
    private const float BaseWindowPaddingY = 5f;
    private const float BaseItemSpacing = 2f;

    // Не даём окну ужаться мельче исходного расчёта (на совсем маленьких/низких разрешениях) и
    // не даём ему раздуться бесконечно на сверхширокоформатных мониторах - разумный потолок.
    private const float MinScale = 1f;
    private const float MaxScale = 2.5f;

    // Цвета подобраны на глаз под то, как их красит родная подсказка игры (см. скриншоты в истории
    // правок) - точных hex-кодов из клиента у меня нет, так что это приближение, не единственно
    // возможное. Порядок важен: более длинные/специфичные варианты раньше более общих.
    // Один акцентный цвет на всё (Duration, Additional Effect, названия умений/статусов) вместо
    // двух-трёх разных - по отзыву слишком много одновременных цветов тяжело воспринимается,
    // даже если каждый по отдельности более-менее совпадает с оригиналом. Обычный белый текст
    // остаётся единственным "фоновым" цветом, акцент - для всего структурно важного разом.
    private static readonly Vector4 AccentColor = new(0.62f, 0.85f, 0.55f, 1f); // зелёный, как "Duration:" в оригинале

    // Все эти строки в оригинале красятся одинаково: зелёное всё ДО ПЕРВОГО двоеточия включительно
    // (сама метка, плюс переменное название статуса/шкалы, если оно есть, например "Стоимость
    // Angler's Art:", "Сила под эффектом Divine Might:" - оно тоже зелёное, без отдельного
    // оранжевого вырезания, раз это часть составной метки, а не самостоятельно упомянутая вещь).
    // Всё, что ПОСЛЕ двоеточия - описание эффекта, число, время и т.п. - обычным белым текстом;
    // конкретное название умения/статуса внутри этой части (если есть) всё равно вырезается и
    // красится отдельным оранжевым цветом - см. Tokenize. Раньше "Дополнительный эффект"/"Комбо..."
    // ошибочно считались "зелёными целиком, включая описание" - оказалось, что и они устроены так же.
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
        // у Modest Lure и подобных DoL-умений) - та же категория "метка зелёная, значение белое".
        "Первое использование",
        "Второе использование",
        "Третье использование",
        "Четвёртое использование",
    };

    // Составные метки вида "Эффект <Название статуса>:" (например "Эффект Knight's Resolve:",
    // "Эффект Knight's Benediction:") - конкретное название заранее не известно (это же не
    // отдельное умение, а статус, произведённый от него), но сама метка (включая название) целиком
    // зелёная - как и у остальных LabelOnlyAccentPrefixes, дальше по строке (описание эффекта) уже
    // обычным белым. Важно: после "Эффект" сразу должно идти капитализированное английское
    // название И двоеточие - иначе это просто обычное предложение, начинающееся со слова "Эффект"
    // ("Эффект заканчивается после..."), которое подсвечивать не нужно.
    private static readonly Regex EffectOfNamedStatusRegex =
        new(@"^Эффект\s+[A-Z][a-zA-Z']*(?:\s+[A-Z][a-zA-Z']*)*:", RegexOptions.Compiled);

    private static readonly Vector4 BodyColor = new(0.90f, 0.90f, 0.92f, 1f);

    // Оформление окна - по макету "Ability Inspector" (см. доккомментарий класса).
    private static readonly Vector4 WindowBgColor = new(0.059f, 0.059f, 0.059f, 0.96f); // #0f0f0f
    private static readonly Vector4 BorderColor = new(0.239f, 0.239f, 0.239f, 1f); // #3d3d3d
    private static readonly Vector4 SeparatorColor = new(0.227f, 0.227f, 0.227f, 1f); // #3a3a3a
    private static readonly Vector4 TitleBgColor = new(0.082f, 0.227f, 0.388f, 1f); // #153a63
    private static readonly Vector4 LabelColor = new(0.616f, 0.616f, 0.616f, 1f); // #9d9d9d, классификация
    private static readonly Vector4 FooterColor = new(0.435f, 0.435f, 0.435f, 1f); // #6f6f6f, подпись внизу
    private const string FooterTag = "JobGuideRU · перевод";
    // Названия умений/статусов, упомянутые внутри предложения ("Holy Spirit", "Confiteor" и т.п.) -
    // в оригинале это ДРУГОЙ цвет, оранжевый, отдельно от зелёных меток (Duration/Additional Effect).
    private static readonly Vector4 NameHighlightColor = new(0.90f, 0.62f, 0.32f, 1f);

    // Подряд идущие слова с большой буквы (латиница) - кандидаты на "это название умения/статуса".
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
    private static Vector2 lastSize = new(260, 80);

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
        // или неразрешённых записей - null, тогда просто не рисуем этот блок вообще.
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
        ImGui.PushStyleColor(ImGuiCol.TitleBg, TitleBgColor);
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, TitleBgColor);
        ImGui.PushStyleColor(ImGuiCol.TitleBgCollapsed, TitleBgColor);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(BaseWindowPaddingX * scale, BaseWindowPaddingY * scale));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f); // острые углы, как у "инструмента разработчика"
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(BaseItemSpacing * scale, BaseItemSpacing * scale));

        // Настоящий заголовок окна ImGui (не нарисованная вручную имитация) - NoInputs всё равно
        // не даёт его таскать/закрывать, но сам заголовок (название умения на цветной плашке)
        // рисуется штатными средствами ImGui. "###..." после текста - фиксированный ID окна для
        // ImGui, не зависящий от того, какое умение показано сейчас (иначе окно "прыгало" бы
        // между разными позициями/размерами в собственной памяти ImGui при смене умения).
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoResize
                                        | ImGuiWindowFlags.NoMove
                                        | ImGuiWindowFlags.NoNav
                                        | ImGuiWindowFlags.NoFocusOnAppearing
                                        | ImGuiWindowFlags.NoInputs
                                        | ImGuiWindowFlags.NoCollapse
                                        | ImGuiWindowFlags.AlwaysAutoResize;

        using var fontPush = GetBodyFont().Push();

        if (ImGui.Begin($"{info.Entry.EnglishName}###JobGuideRUTranslationOverlay", flags))
        {
            // Масштабирует весь текст этого окна (и то, что CalcTextSize/перенос строк ниже
            // считают в WrapWidth*scale) под разрешение монитора - см. GetScale. Обязательно
            // ДО любого текста/CalcTextSize в этом окне, иначе перенос строк посчитает по
            // немасштабированному размеру шрифта и разъедется с реальной шириной глифов.
            ImGui.SetWindowFontScale(scale);

            if (hasStats)
                DrawStatsHeader(stats, info.Entry.Classification, textureProvider, scale);
            else if (!string.IsNullOrEmpty(info.Entry.Classification))
                ImGui.TextColored(LabelColor, info.Entry.Classification);

            ImGui.Separator();

            foreach (var line in info.Entry.Content!.Split('\n'))
                DrawLine(line, scale);

            if (hasStats)
                DrawAcquiredAndAffinity(stats, scale);

            ImGui.Spacing();
            ImGui.SetWindowFontScale(scale * 0.78f);
            ImGui.TextColored(FooterColor, FooterTag);
            ImGui.SetWindowFontScale(scale);

            lastSize = ImGui.GetWindowSize();
        }

        ImGui.End();
        ImGui.PopStyleVar(4);
        ImGui.PopStyleColor(6);
    }

    private const float IconSize = 32f;

    /// <summary>
    /// Иконка умения + классификация ("Способность"/"Боевой навык"...) и Cast/Recast/Range/Radius -
    /// всё это НЕ переводится (числа и коды, языко-независимы), просто дублируется в нашем окне из
    /// листа Action (см. ActionStatsLookup), чтобы не заставлять смотреть в родное окно и в наше
    /// одновременно.
    /// </summary>
    private static void DrawStatsHeader(ActionStats stats, string? classification, ITextureProvider textureProvider, float scale)
    {
        var iconSize = IconSize * scale;
        var wrap = textureProvider.GetFromGameIcon(new GameIconLookup { IconId = stats.IconId }).GetWrapOrEmpty();
        ImGui.Image(wrap.Handle, new Vector2(iconSize, iconSize));

        ImGui.SameLine();
        ImGui.BeginGroup();

        if (!string.IsNullOrEmpty(classification))
            ImGui.TextColored(LabelColor, classification);

        ImGui.TextColored(LabelColor, $"Дальность {FormatYalms(stats.Range)}   Радиус {FormatYalms(stats.Radius)}");
        ImGui.EndGroup();

        ImGui.Spacing();

        // Cast/Recast - те же слова, что уже используются в тексте перевода (см.
        // LabelOnlyAccentPrefixes: "Продолжительность"/"Стоимость" и т.п. там же про "сек.").
        ImGui.TextColored(LabelColor, "Каст");
        ImGui.SameLine(120f * scale);
        ImGui.TextColored(LabelColor, "Восстановление");

        ImGui.TextColored(BodyColor, stats.CastHundredMs == 0 ? "Мгновенная" : FormatSeconds(stats.CastHundredMs));
        ImGui.SameLine(120f * scale);
        ImGui.TextColored(BodyColor, FormatSeconds(stats.RecastHundredMs));

        ImGui.Spacing();
    }

    /// <summary>Уровень получения + список работ, которым доступно умение (Affinity) - см. ActionStatsLookup.</summary>
    private static void DrawAcquiredAndAffinity(ActionStats stats, float scale)
    {
        ImGui.Spacing();

        ImGui.TextColored(LabelColor, "Получено");
        ImGui.SameLine();
        ImGui.TextColored(AccentColor, $"Ур. {stats.Level}");

        if (stats.Jobs.Count > 0)
        {
            ImGui.TextColored(LabelColor, "Работы");
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + BaseWrapWidth * scale);
            ImGui.TextColored(AccentColor, string.Join("  ", stats.Jobs));
            ImGui.PopTextWrapPos();
        }
    }

    private static string FormatSeconds(int hundredMs) => (hundredMs / 10f).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " сек.";

    private static string FormatYalms(int range) => range < 0 ? "-" : range + "y";

    /// <summary>
    /// Рисует одну строку описания. Если строка начинается с одной из "структурных" меток
    /// (Duration/Additional Effect/Комбо/Сила/Стоимость/"Эффект Х:") - зелёным красится только
    /// сама метка, до двоеточия включительно (туда же попадает и переменное название статуса,
    /// если оно есть); всё, что после двоеточия, и любые прочие строки - обычным белым текстом.
    /// В любом случае конкретные названия умений/статусов внутри строки (остаются на английском)
    /// вырезаются и красятся отдельным, оранжевым цветом поверх - см. Tokenize. Перенос строк -
    /// вручную, по словам (см. класс) - иначе ImGui "залипает" на отступе первого разноцветного
    /// куска абзаца.
    /// </summary>
    private static void DrawLine(string line, float scale)
    {
        var tokens = ComputeLineTokens(line);
        RenderLineTokens(tokens, BaseWrapWidth * scale);
    }

    /// <summary>Решает, как разбить и раскрасить строку - см. комментарии у LabelOnlyAccentPrefixes/EffectOfNamedStatusRegex.</summary>
    private static List<Token> ComputeLineTokens(string line)
    {
        // Зелёное - всё до конца метки включительно (двоеточие); дальше по строке (описание
        // эффекта, число, время и т.п.) - обычным белым текстом (см. LabelOnlyAccentPrefixes).
        var labelLength = FindGreenLabelLength(line);
        if (labelLength > 0)
        {
            var tokens = new List<Token> { new(line[..labelLength], AccentColor) };
            tokens.AddRange(Tokenize(line[labelLength..], BodyColor));
            return tokens;
        }

        return Tokenize(line, BodyColor);
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

        // "Эффект <Название статуса>:" - составная метка, название заранее не известно, поэтому
        // тут не префикс, а регулярное выражение (см. EffectOfNamedStatusRegex), которое само
        // заканчивается на двоеточие - длина совпадения и есть длина зелёной части.
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
            // glueWidth - ширина следующего токена, если этот токен - открывающая скобка "(" без
            // пробела перед содержимым: без неё "(" сама по себе маленькая и легко помещается в
            // конец строки, а вот следующее слово (название умения в скобках и т.п.) уже не
            // помещается и переносится само по себе, отделяясь от открывающей скобки. Складывая
            // сюда ширину следующего токена, переносим их вместе, как единое целое.
            var wraps = !atLineStart && cursorX + width + glueWidth > wrapWidth;
            // Перенос строки: НЕ вызываем ImGui.NewLine() явно - обычный TextColored сам переводит
            // курсор на новую строку, если следующий вызов не предварён SameLine(). Явный NewLine()
            // здесь добавлял бы ВТОРОЙ перевод строки поверх автоматического - отсюда были двойные
            // интервалы между строками. Просто не зовём SameLine() для этого токена.
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
            // Одинокая открывающая скобка (без пробела после - "(Limit Break)") - считаем её
            // приклеенной к следующему токену для целей переноса, чтобы они не разъезжались
            // по разным строкам, даже раскрашенные в разные цвета (сама скобка рисуется отдельным
            // вызовом TextColored, просто перенос решается для обоих сразу).
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
    /// статусов. Изначально подсвечивались только точные совпадения с базой переведённых умений,
    /// но составные статус-эффекты (умение + суффикс вроде "Ready"/"Attunement", например
    /// "Confiteor Ready" или "Knight's Benediction") в базу не попадают - там только умения, не
    /// статусы. Поскольку почти любой захваченный кусок текста с большой буквы в описании умения
    /// и так является ссылкой на другое умение/статус (случайных капитализированных английских
    /// слов в русском переводе не бывает), теперь подсвечиваются ВСЕ такие куски одинаково,
    /// кроме короткой служебки вроде "HP"/"MP" (её игра просто не выделяет).
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
                tokens.Add(new Token(match.Value, NameHighlightColor));

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

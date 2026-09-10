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
/// Оформление - по макету "Ability Tooltip ImGui" (Claude Design, вторая версия, см. историю
/// правок): плашка-эйбрау (цветная риска + классификация + код работы), иконка+имя+уровень,
/// сетка из 4 статов (Каст/Восст./Дальность/Радиус - см. ActionStats), дальше текст перевода, а
/// строки вида "Метка: значение" (Продолжительность/Дополнительный эффект/Стоимость/Комбо...)
/// вынесены из абзаца в отдельные карточки с цветной полоской слева, по категориям (см.
/// RowCategories) - вместо инлайн-подсветки внутри сплошного текста, как раньше. Часть элементов
/// макета (градиенты, тени, отдельный "лёгкий" шрифт) переданы приближённо - настолько, насколько
/// это возможно средствами ImGui (см. заголовок задачи в истории правок).
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
    private const float BaseWindowPaddingX = 10f;
    private const float BaseWindowPaddingY = 8f;
    private const float BaseItemSpacing = 3f;
    private const float IconSize = 40f;

    // Не даём окну ужаться мельче исходного расчёта (на совсем маленьких/низких разрешениях) и
    // не даём ему раздуться бесконечно на сверхширокоформатных мониторах - разумный потолок.
    private const float MinScale = 1f;
    private const float MaxScale = 2.5f;

    private static Vector4 Hex(int r, int g, int b, float a = 1f) => new(r / 255f, g / 255f, b / 255f, a);

    // Цвета - один в один из hex-значений макета "Ability Tooltip ImGui" (см. доккомментарий класса).
    private static readonly Vector4 WindowBgColor = Hex(0x10, 0x12, 0x15, 0.97f);
    private static readonly Vector4 BorderColor = Hex(0x2c, 0x32, 0x38);
    private static readonly Vector4 HeaderTopColor = Hex(0x1b, 0x28, 0x36);
    private static readonly Vector4 HeaderBottomColor = Hex(0x14, 0x1c, 0x26);
    private static readonly Vector4 EyebrowColor = Hex(0xcf, 0xd6, 0xde);
    private static readonly Vector4 LabelColor = Hex(0x8c, 0x95, 0x9e); // классификация/job/статы/метки строк
    private static readonly Vector4 TitleColor = Hex(0xe8, 0xea, 0xed);
    private static readonly Vector4 BodyColor = Hex(0xcd, 0xd3, 0xd9);
    private static readonly Vector4 SeparatorColor = Hex(0x23, 0x29, 0x2f);
    private static readonly Vector4 RowBgColor = Hex(0x16, 0x1b, 0x21);
    private static readonly Vector4 FooterColor = Hex(0x6d, 0x76, 0x81);

    // Акцент по умолчанию из макета (гербовая риска в шапке + подсветка названий умений/статусов
    // прямо в тексте) - плюс три доп. цвета карточек-строк (см. RowCategories), тоже из макета.
    private static readonly Vector4 AccentGold = Hex(0xd8, 0xa9, 0x5c);
    private static readonly Vector4 RowGreen = Hex(0x5f, 0x8f, 0x5c);
    private static readonly Vector4 RowPurple = Hex(0x7a, 0x5f, 0x8f);
    private static readonly Vector4 RowBlue = Hex(0x6f, 0xa8, 0xdc);

    // Какие "структурные" метки описания выносятся из абзаца в отдельную карточку-строку, и каким
    // цветом красится полоска слева у каждой категории. Раньше (до макета) это была одна плоская
    // подсветка внутри сплошного текста - см. историю правок.
    private static readonly (string[] Prefixes, Vector4 Bar)[] RowCategories =
    {
        (new[] { "Продолжительность", "Длительность" }, RowGreen),
        (new[] { "Дополнительные эффекты", "Дополнительный эффект" }, AccentGold),
        (new[] { "Стоимость" }, RowPurple),
        (new[] { "Комбо умение", "Комбо-действие", "Комбо бонус", "Бонус комбо" }, RowBlue),
        (new[] { "Сила", "Первое использование", "Второе использование", "Третье использование", "Четвёртое использование" }, RowGreen),
    };

    // Составные метки вида "Эффект <Название статуса>:" (например "Эффект Knight's Resolve:") -
    // конкретное название заранее не известно, поэтому не префикс, а регулярное выражение; той же
    // категории, что "Дополнительный эффект" (золотая полоска).
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
    // кадр незаметно глазу. Та же идея - для фона каждой карточки-строки (см. DrawRow):
    // высота её содержимого (перенос по словам) тоже не известна заранее.
    private static Vector2 lastSize = new(260, 80);
    private static readonly Dictionary<string, float> lastRowHeights = new();

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
        // или неразрешённых записей - null, тогда просто не рисуем иконку/статы/сетку.
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
        ImGui.PushStyleColor(ImGuiCol.TableBorderLight, SeparatorColor);
        ImGui.PushStyleColor(ImGuiCol.TableBorderStrong, SeparatorColor);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(BaseWindowPaddingX * scale, BaseWindowPaddingY * scale));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 3f * scale); // как border-radius:3px в макете
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(BaseItemSpacing * scale, BaseItemSpacing * scale));
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(8f * scale, 6f * scale));

        // NoTitleBar - в этой версии макета заголовок ("ABILITY"/эйбрау-плашка) нарисован вручную
        // внутри тела окна, а не через штатный заголовок ImGui (в отличие от предыдущей версии).
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

            var jobAbbreviation = hasStats && stats.Jobs.Count == 1 ? stats.Jobs[0] : null;
            DrawEyebrowBar(info.Entry.Classification, jobAbbreviation, scale);

            ImGui.Spacing();
            DrawIdentityRow(info.Entry.EnglishName, hasStats ? stats : null, textureProvider, scale);

            if (hasStats)
            {
                ImGui.Spacing();
                ImGui.Separator();
                DrawStatsGrid(stats, scale);
            }

            ImGui.Separator();
            ImGui.Spacing();

            // Строки-метки ("Продолжительность:"/"Стоимость:" и т.п.) выносятся из потока в
            // отдельные карточки (см. RowCategories) - остальные строки остаются сплошным абзацем.
            var rows = new List<(string Label, string Value, Vector4 Bar)>();
            foreach (var line in info.Entry.Content!.Split('\n'))
            {
                if (TryGetRow(line, out var label, out var value, out var bar))
                    rows.Add((label, value, bar));
                else
                    DrawLine(line, scale);
            }

            if (rows.Count > 0)
            {
                ImGui.Spacing();
                foreach (var row in rows)
                    DrawRow(row.Label, row.Value, row.Bar, scale);
            }

            ImGui.Spacing();
            ImGui.SetWindowFontScale(scale * 0.78f);
            ImGui.TextColored(FooterColor, "JobGuideRU · перевод");
            ImGui.SetWindowFontScale(scale);

            lastSize = ImGui.GetWindowSize();
        }

        ImGui.End();
        ImGui.PopStyleVar(5);
        ImGui.PopStyleColor(5);
    }

    /// <summary>
    /// Цветная риска + классификация ("Способность"/"Боевой навык"...) слева, код работы (если
    /// умение принадлежит ровно одной) - справа. Фон - вертикальный градиент, как в макете
    /// (AddRectFilledMultiColor), нарисован вручную поверх обычного фона окна.
    /// </summary>
    private static void DrawEyebrowBar(string? classification, string? jobAbbreviation, float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        var padX = 8f * scale;
        var padY = 5f * scale;
        var avail = ImGui.GetContentRegionAvail().X;
        var barHeight = ImGui.GetTextLineHeight() + padY * 2f;
        var pos = ImGui.GetCursorScreenPos();

        drawList.AddRectFilledMultiColor(
            pos, pos + new Vector2(avail, barHeight),
            ImGui.ColorConvertFloat4ToU32(HeaderTopColor), ImGui.ColorConvertFloat4ToU32(HeaderTopColor),
            ImGui.ColorConvertFloat4ToU32(HeaderBottomColor), ImGui.ColorConvertFloat4ToU32(HeaderBottomColor));

        var tickWidth = 3f * scale;
        drawList.AddRectFilled(
            pos + new Vector2(padX * 0.5f, padY * 0.6f), pos + new Vector2(padX * 0.5f + tickWidth, barHeight - padY * 0.6f),
            ImGui.ColorConvertFloat4ToU32(AccentGold));

        ImGui.SetCursorScreenPos(pos + new Vector2(padX * 0.5f + tickWidth + padX, padY));
        ImGui.TextColored(EyebrowColor, (classification ?? "Умение").ToUpperInvariant());

        if (!string.IsNullOrEmpty(jobAbbreviation))
        {
            var jobWidth = ImGui.CalcTextSize(jobAbbreviation).X;
            ImGui.SetCursorScreenPos(pos + new Vector2(avail - padX - jobWidth, padY));
            ImGui.TextColored(LabelColor, jobAbbreviation);
        }

        ImGui.SetCursorScreenPos(pos + new Vector2(0, barHeight));
    }

    /// <summary>Иконка умения (если известна) + название + уровень получения.</summary>
    private static void DrawIdentityRow(string englishName, ActionStats? stats, ITextureProvider textureProvider, float scale)
    {
        if (stats is { } s)
        {
            var iconSize = IconSize * scale;
            var wrap = textureProvider.GetFromGameIcon(new GameIconLookup { IconId = s.IconId }).GetWrapOrEmpty();
            ImGui.Image(wrap.Handle, new Vector2(iconSize, iconSize));
            ImGui.SameLine();
        }

        ImGui.BeginGroup();
        ImGui.SetWindowFontScale(scale * 1.45f);
        ImGui.TextColored(TitleColor, englishName);
        ImGui.SetWindowFontScale(scale);

        if (stats is { } s2)
            ImGui.TextColored(LabelColor, $"Ур. {s2.Level}");

        ImGui.EndGroup();
    }

    /// <summary>Сетка из 4 колонок - Каст/Восст./Дальность/Радиус (см. ActionStats) - языко-независимые числа, не переводятся.</summary>
    private static void DrawStatsGrid(ActionStats stats, float scale)
    {
        if (!ImGui.BeginTable("##jgru_stats_grid", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchSame))
            return;

        ImGui.TableNextRow();
        DrawStatCell("КАСТ", stats.CastHundredMs == 0 ? "Мгнов." : FormatSeconds(stats.CastHundredMs));
        DrawStatCell("ВОССТ.", FormatSeconds(stats.RecastHundredMs));
        DrawStatCell("ДАЛЬНОСТЬ", FormatYalms(stats.Range));
        DrawStatCell("РАДИУС", FormatYalms(stats.Radius));

        ImGui.EndTable();

        void DrawStatCell(string label, string value)
        {
            ImGui.TableNextColumn();
            ImGui.SetWindowFontScale(scale * 0.82f);
            ImGui.TextColored(LabelColor, label);
            ImGui.SetWindowFontScale(scale);
            ImGui.TextColored(BodyColor, value);
        }
    }

    /// <summary>
    /// Карточка-строка "метка / значение" с цветной полоской слева (см. RowCategories). Фон и
    /// высота полоски берутся с ПРЕДЫДУЩЕГО кадра для этой же метки (см. lastRowHeights) - точная
    /// высота содержимого (перенос значения по словам) известна только постфактум, тот же приём,
    /// что и lastSize для всего окна.
    /// </summary>
    private static void DrawRow(string label, string value, Vector4 bar, float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        var padX = 8f * scale;
        var padY = 4f * scale;
        var barWidth = 2.5f * scale;
        var avail = ImGui.GetContentRegionAvail().X;
        var startScreenPos = ImGui.GetCursorScreenPos();

        var estimatedHeight = lastRowHeights.TryGetValue(label, out var h) ? h : ImGui.GetTextLineHeightWithSpacing() * 2f;

        drawList.AddRectFilled(startScreenPos, startScreenPos + new Vector2(avail, estimatedHeight), ImGui.ColorConvertFloat4ToU32(RowBgColor));
        drawList.AddRectFilled(startScreenPos, startScreenPos + new Vector2(barWidth, estimatedHeight), ImGui.ColorConvertFloat4ToU32(bar));

        ImGui.SetCursorScreenPos(startScreenPos + new Vector2(barWidth + padX, padY));
        ImGui.BeginGroup();
        ImGui.TextColored(LabelColor, label);
        RenderLineTokens(Tokenize(value, BodyColor), BaseWrapWidth * scale - barWidth - padX);
        ImGui.EndGroup();

        var contentHeight = ImGui.GetItemRectMax().Y - startScreenPos.Y + padY;
        lastRowHeights[label] = contentHeight;

        ImGui.SetCursorScreenPos(startScreenPos + new Vector2(0, Math.Max(estimatedHeight, contentHeight)));
        ImGui.Dummy(new Vector2(avail, 1f * scale)); // маленький зазор перед следующей карточкой
    }

    private static string FormatSeconds(int hundredMs) => (hundredMs / 10f).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " сек.";

    private static string FormatYalms(int range) => range < 0 ? "-" : range + "y";

    /// <summary>Строка вида "Метка: значение" из известной категории (см. RowCategories/EffectOfNamedStatusRegex) - или false, если строка обычный текст абзаца.</summary>
    private static bool TryGetRow(string line, out string label, out string value, out Vector4 bar)
    {
        foreach (var (prefixes, color) in RowCategories)
        {
            foreach (var prefix in prefixes)
            {
                if (!line.StartsWith(prefix, StringComparison.Ordinal))
                    continue;

                var colonIndex = line.IndexOf(':');
                if (colonIndex < 0)
                    continue;

                label = line[..colonIndex].Trim();
                value = line[(colonIndex + 1)..].Trim();
                bar = color;
                return true;
            }
        }

        var effectMatch = EffectOfNamedStatusRegex.Match(line);
        if (effectMatch.Success)
        {
            var colonIndex = effectMatch.Length - 1; // сам regex заканчивается на ':'
            label = line[..colonIndex].Trim();
            value = line[(colonIndex + 1)..].Trim();
            bar = AccentGold;
            return true;
        }

        label = string.Empty;
        value = string.Empty;
        bar = default;
        return false;
    }

    /// <summary>Рисует одну строку абзаца (не карточку) - перенос вручную, по словам (см. класс), с подсветкой упомянутых внутри названий умений/статусов.</summary>
    private static void DrawLine(string line, float scale)
        => RenderLineTokens(Tokenize(line, BodyColor), BaseWrapWidth * scale);

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
    /// статусов золотым акцентом (см. AccentGold) - тем же, что и цветная риска в шапке. Изначально
    /// подсвечивались только точные совпадения с базой переведённых умений, но составные статус-
    /// эффекты (умение + суффикс вроде "Ready"/"Attunement", например "Confiteor Ready") в базу не
    /// попадают - там только умения, не статусы. Поскольку почти любой захваченный кусок текста с
    /// большой буквы в описании умения и так является ссылкой на другое умение/статус (случайных
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

using System;
using System.Collections.Generic;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Gui;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVJobGuideRUTranslator.Data;

namespace FFXIVJobGuideRUTranslator.Hooks;

/// <summary>
/// НЕ трогает никакие ноды игры. Пять предыдущих попыток вписать перевод прямо в нативную
/// подсказку (растянуть плашку, сдвинуть соседей, уменьшить шрифт) одна за другой ломались,
/// потому что ActionDetail - общий попап игры, переиспользуемый на тех же адресах нод под
/// множество структурно разных вещей (умения, Materia Extraction, Repair, Ignore Target,
/// роулетки...), и любое вмешательство в его память рано или поздно "протекало" в контент,
/// который мы вообще не должны были трогать. А уменьшение шрифта не спасает по-настоящему
/// длинные описания (Technical Step и подобные) - становится нечитаемо.
///
/// Вместо этого этот класс только СМОТРИТ, какое (если вообще какое-то) переведённое умение
/// сейчас показано в одном из окон из Configuration.TargetAddonNames, и выставляет
/// <see cref="Current"/> (само умение + экранные координаты родного окна). Подсказку/панель
/// самой игры это не меняет и не может сломать -
/// перевод рисуется отдельным всплывающим окном ImGui поверх экрана (см. TranslationOverlay),
/// которое само разворачивается под любой объём текста.
///
/// Как опознаётся умение:
///  1. Если сейчас реально наведено на боевое/ремесленное умение на хотбаре - точный ActionId
///     через IGameGui.HoveredAction, но ТОЛЬКО когда HoveredAction.DetailKind - это Action или
///     CraftingAction. Другие виды (DetailKind.GeneralAction и т.п. - Materia Extraction,
///     Ignore Target, роулетки...) не переведены и не должны попадать в выборку - их ActionId
///     живёт в отдельном пространстве идентификаторов и может случайно совпасть с ID умения.
///  2. Иначе (например, панель в "Actions & Traits") ищем текстовую ноду с названием умения
///     среди известных нам названий - только ЧИТАЕМ текст, ничего не меняем.
/// </summary>
public sealed unsafe class AbilityHoverWatcher : IDisposable
{
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;
    private readonly Configuration configuration;
    private readonly TranslationRepository repository;

    private readonly HashSet<string> registeredAddonNames = new();

    /// <summary>Умение + экранные координаты и размер РОДНОГО окна, в котором оно показано - только чтение, координаты нужны, чтобы поставить перевод рядом, а не там, где сейчас курсор мыши (иначе в списках/панелях перевод оказывается где попало и перекрывает контент).</summary>
    public readonly record struct HoverInfo(TranslationEntry Entry, float X, float Y, float Width, float Height, NineGridInfo? Background);

    /// <summary>
    /// Ссылка на РЕАЛЬНУЮ текстуру фона родного окна (та же самая, что уже загружена и
    /// используется игрой прямо сейчас) вместе с геометрией девятислайса - чтобы нарисовать
    /// перевод в плашке, которая один-в-один так же выглядит, как родная, а не в приближении
    /// цветом. Только чтение - сам объект текстуры/ноды не модифицируется никак.
    /// </summary>
    public readonly record struct NineGridInfo(
        nint TextureId, float TextureWidth, float TextureHeight,
        float U, float V, float SpriteWidth, float SpriteHeight,
        float TopOffset, float BottomOffset, float LeftOffset, float RightOffset);

    /// <summary>Что показано прямо сейчас в одном из отслеживаемых окон, или null. Живёт один кадр (см. <see cref="ResetForNextFrame"/>).</summary>
    public HoverInfo? Current { get; private set; }

    public AbilityHoverWatcher(
        IAddonLifecycle addonLifecycle,
        IGameGui gameGui,
        IPluginLog log,
        Configuration configuration,
        TranslationRepository repository)
    {
        this.addonLifecycle = addonLifecycle;
        this.gameGui = gameGui;
        this.log = log;
        this.configuration = configuration;
        this.repository = repository;

        ApplyRegistrations();
    }

    /// <summary>Перерегистрирует слушатели под текущий список Configuration.TargetAddonNames (вызывать после изменения настроек).</summary>
    public void ApplyRegistrations()
    {
        foreach (var name in registeredAddonNames)
            addonLifecycle.UnregisterListener(AddonEvent.PostDraw, name, OnAddonPostDraw);
        registeredAddonNames.Clear();

        foreach (var rawName in configuration.TargetAddonNames)
        {
            var name = rawName?.Trim();
            if (string.IsNullOrEmpty(name))
                continue;

            // HashSet.Add возвращает false, если имя уже добавлено - так регистрируем каждый
            // аддон максимум один раз, даже если в конфиге он по ошибке продублирован.
            if (!registeredAddonNames.Add(name))
                continue;

            addonLifecycle.RegisterListener(AddonEvent.PostDraw, name, OnAddonPostDraw);
        }

        log.Information($"[JobGuideRU] Слушаю аддоны: {string.Join(", ", registeredAddonNames)}");
    }

    /// <summary>
    /// Вызывать из UiBuilder.Draw ПОСЛЕ того, как оверлей с переводом (если он был) уже
    /// нарисован за этот кадр. Сбрасывает Current, чтобы на следующем кадре оверлей показался
    /// снова, только если хотя бы один из отслеживаемых аддонов реально ещё виден и сам вызовет
    /// OnAddonPostDraw заново - иначе (подсказка исчезла/навели на другое место) оверлей
    /// естественным образом пропадёт, без ручного отслеживания состояния "видимо/нет".
    /// </summary>
    public void ResetForNextFrame() => Current = null;

    private void OnAddonPostDraw(AddonEvent type, AddonArgs args)
    {
        if (!configuration.Enabled)
            return;

        try
        {
            // args.Addon - это AtkUnitBasePtr (обёртка без прямой зависимости от ClientStructs),
            // берём голый адрес через .Address и приводим его к настоящему указателю.
            var addon = (AtkUnitBase*)args.Addon.Address;
            if (addon is null || !addon->IsVisible)
                return;

            TranslationEntry? entry = null;

            // DetailKind обязателен: у системных команд (Materia Extraction, Ignore Target,
            // роулетки и т.п. - DetailKind.GeneralAction и другие) ActionId живёт в своём,
            // отдельном пространстве ID и может случайно совпасть с ID переведённого умения.
            var hovered = gameGui.HoveredAction;
            if (hovered.ActionId != 0 &&
                (hovered.DetailKind == DetailKind.Action || hovered.DetailKind == DetailKind.CraftingAction) &&
                repository.TryGetByActionId(hovered.ActionId, out var byId) && byId.Count > 0)
            {
                entry = byId[0];
            }

            if (entry is null)
            {
                // Окно вроде "Actions & Traits": ищем ноду с названием умения среди известных
                // нам названий. Только читаем текст нод, ничего не меняем и не сохраняем адреса.
                foreach (var text in EnumerateTextNodes(addon))
                {
                    if (string.IsNullOrWhiteSpace(text))
                        continue;
                    if (!repository.TryGetByEnglishName(text, out var byName) || byName.Count == 0)
                        continue;

                    entry = byName[0];
                    break;
                }
            }

            if (entry is not null && !string.IsNullOrEmpty(entry.Content))
            {
                // Только читаем координаты окна - X/Y/масштаб самой игры, ничего не меняем.
                var width = addon->GetScaledWidth(true);
                var height = addon->GetScaledHeight(true);
                var background = TryGetBackgroundNineGrid(addon, out var nineGrid, log) ? nineGrid : (NineGridInfo?)null;
                Current = new HoverInfo(entry, addon->X, addon->Y, width, height, background);
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, $"[JobGuideRU] Ошибка при обработке аддона {args.AddonName}");
        }
    }

    /// <summary>
    /// Ищет самую большую по площади NineGrid-ноду в окне (кандидат на "это и есть фон/рамка")
    /// и достаёт из неё ссылку на уже загруженную игрой текстуру + геометрию 9-слайса. Только
    /// чтение: адрес ноды/её полей не изменяется, берём копию нужных значений.
    /// Раскладка полей (TopOffset/BottomOffset/LeftOffset/RightOffset как толщина каждой из
    /// четырёх кромок в пикселях спрайта, а не абсолютные координаты среза) - предположение по
    /// имени полей в FFXIVClientStructs, не проверено на живом клиенте: если углы плашки при
    /// растяжении выглядят перекошенными, вероятно, нужно поменять интерпретацию этих четырёх
    /// значений в TranslationOverlay.DrawNineSlice.
    /// </summary>
    // Логируем причину неудачи только при СМЕНЕ причины (не каждый кадр - иначе log-файл
    // раздувается за секунды, пока курсор наведён), чтобы разово увидеть в /xllog, на каком
    // именно шаге ломается извлечение текстуры, не гадая вслепую по скриншотам.
    private static string? lastLoggedReason;

    private static bool TryGetBackgroundNineGrid(AtkUnitBase* addon, out NineGridInfo info, IPluginLog log)
    {
        info = default;

        // Кандидат в фон может оказаться и NineGrid-нодой (с честным 9-слайсом), и обычной
        // Image-нодой (одна текстура без разбиения на края - растягиваем целиком). Первая
        // проверка (только NineGrid) нашла крошечную декоративную полоску 32x4 - явно не фон -
        // поэтому теперь смотрим на обе разновидности и берём наибольшую по площади из ЛЮБОЙ.
        AtkResNode* best = null;
        var bestArea = 0f;
        var nineGridCount = 0;
        var imageCount = 0;

        Scan((AtkResNode*)addon->RootNode);
        for (var i = 0; i < addon->UldManager.NodeListCount; i++)
            Scan(addon->UldManager.NodeList[i]);
        // WindowNode - отдельное поле на самом AtkUnitBase (рамка/фон "стандартного" окна),
        // не входит в обычное дерево RootNode/UldManager.NodeList - предыдущий поиск нашёл
        // только тонкую декоративную полоску 32x4, значит настоящий фон, если он вообще
        // текстура, а не просто заливка цветом, стоит поискать и здесь.
        if (addon->WindowNode is not null)
            Scan((AtkResNode*)addon->WindowNode);

        void Scan(AtkResNode* node)
        {
            while (node is not null)
            {
                if (node->Type == NodeType.NineGrid || node->Type == NodeType.Image)
                {
                    if (node->Type == NodeType.NineGrid) nineGridCount++;
                    else imageCount++;

                    var area = (float)node->Width * node->Height;
                    if (area > bestArea)
                    {
                        bestArea = area;
                        best = node;
                    }
                }
                else if (node->Type >= NodeType.Component)
                {
                    var component = ((AtkComponentNode*)node)->Component;
                    if (component is not null && component->UldManager.NodeList is not null)
                    {
                        for (var i = 0; i < component->UldManager.NodeListCount; i++)
                            Scan(component->UldManager.NodeList[i]);
                    }
                }

                if (node->ChildNode is not null)
                    Scan(node->ChildNode);

                node = node->PrevSiblingNode;
            }
        }

        if (best is null)
            return Fail($"ни одной NineGrid/Image-ноды в окне не найдено (NineGrid={nineGridCount}, Image={imageCount})");

        // Проверено на живом клиенте (45 кандидатов, включая WindowNode): в ActionDetail нет
        // ноды, которая правдоподобно выглядела бы как основной фон - самая большая раз за разом
        // оказывается декоративной полоской 32x4 (area=1384), явно не фон. Похоже, фон здесь -
        // обычная заливка цветом на уровне ниже нод, до которого не достучаться. Порог отсекает
        // любые такие мелкие декоративные элементы: пока не найдётся что-то ощутимо крупнее
        // иконки умения, используем проверенное приближение цветом вместо случайной картинки.
        const float minPlausibleBackgroundArea = 6000f; // с запасом больше типичной иконки (~40x40=1600)
        if (bestArea < minPlausibleBackgroundArea)
            return Fail($"крупнейший кандидат ({best->Type}, area={bestArea}) слишком мал, чтобы быть фоном - похоже, реального фона-текстуры тут нет");

        // AtkImageNode и AtkNineGridNode держат PartsList/PartId на одних и тех же полях (хоть у
        // AtkNineGridNode PartId - uint, а у AtkImageNode - ushort), поэтому оба разбираем через
        // общий указатель на AtkNineGridNode - для Image-ноды поля TopOffset и т.п. дальше по
        // структуре, но мы их просто не используем (offsets остаются 0 => без 9-слайса, просто
        // растягиваем целиком).
        var isNineGrid = best->Type == NodeType.NineGrid;
        var asNineGrid = (AtkNineGridNode*)best;
        var partsList = asNineGrid->PartsList;
        var partId = isNineGrid ? asNineGrid->PartId : ((AtkImageNode*)best)->PartId;

        if (partsList is null || partId >= partsList->PartCount)
            return Fail($"у выбранной ноды ({best->Type}, area={bestArea}) нет валидного PartsList/PartId");

        var part = &partsList->Parts[partId];
        var asset = part->UldAsset;
        if (asset is null)
            return Fail("у части (Part) нет UldAsset");
        if (!asset->AtkTexture.IsTextureReady())
            return Fail("текстура найдена, но IsTextureReady() == false");

        var texture = asset->AtkTexture.GetKernelTexture();
        if (texture is null)
            return Fail("GetKernelTexture() вернул null");
        if (texture->D3D11ShaderResourceView is null)
            return Fail("у текстуры нет D3D11ShaderResourceView");

        info = new NineGridInfo(
            (nint)texture->D3D11ShaderResourceView,
            texture->ActualWidth,
            texture->ActualHeight,
            part->U, part->V, part->Width, part->Height,
            isNineGrid ? asNineGrid->TopOffset : 0,
            isNineGrid ? asNineGrid->BottomOffset : 0,
            isNineGrid ? asNineGrid->LeftOffset : 0,
            isNineGrid ? asNineGrid->RightOffset : 0);

        var successReason = $"OK: nodeType={best->Type} area={bestArea} (candidates NineGrid={nineGridCount} Image={imageCount}) texture {texture->ActualWidth}x{texture->ActualHeight}, " +
                             $"sprite U={part->U} V={part->V} W={part->Width} H={part->Height}, " +
                             $"offsets T={(isNineGrid ? asNineGrid->TopOffset : 0)} B={(isNineGrid ? asNineGrid->BottomOffset : 0)} " +
                             $"L={(isNineGrid ? asNineGrid->LeftOffset : 0)} R={(isNineGrid ? asNineGrid->RightOffset : 0)}";
        if (successReason != lastLoggedReason)
        {
            lastLoggedReason = successReason;
            log.Information($"[JobGuideRU] Фон найден: {successReason}");
        }

        return true;

        bool Fail(string reason)
        {
            if (reason != lastLoggedReason)
            {
                lastLoggedReason = reason;
                log.Information($"[JobGuideRU] Фон НЕ найден: {reason}");
            }

            return false;
        }
    }

    private static IEnumerable<string?> EnumerateTextNodes(AtkUnitBase* addon)
    {
        var results = new List<string?>();
        Collect((AtkResNode*)addon->RootNode, results);
        for (var i = 0; i < addon->UldManager.NodeListCount; i++)
            Collect(addon->UldManager.NodeList[i], results);
        return results;

        static void Collect(AtkResNode* node, List<string?> into)
        {
            while (node is not null)
            {
                if (node->Type == NodeType.Text)
                    into.Add(((AtkTextNode*)node)->NodeText.ToString());
                else if (node->Type >= NodeType.Component)
                {
                    var component = ((AtkComponentNode*)node)->Component;
                    if (component is not null && component->UldManager.NodeList is not null)
                    {
                        for (var i = 0; i < component->UldManager.NodeListCount; i++)
                            Collect(component->UldManager.NodeList[i], into);
                    }
                }

                if (node->ChildNode is not null)
                    Collect(node->ChildNode, into);

                node = node->PrevSiblingNode;
            }
        }
    }

    public void Dispose()
    {
        foreach (var name in registeredAddonNames)
            addonLifecycle.UnregisterListener(AddonEvent.PostDraw, name, OnAddonPostDraw);
        registeredAddonNames.Clear();
    }
}

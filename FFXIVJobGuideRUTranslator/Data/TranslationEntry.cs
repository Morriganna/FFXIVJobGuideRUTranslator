namespace FFXIVJobGuideRUTranslator.Data;

/// <summary>
/// Один переведённый кусок текста умения, уже приведённый к плоскому виду и
/// (по возможности) сопоставленный с реальным ActionId/CraftActionId игры.
/// Имя умения (EnglishName) НЕ отображается игроку и НЕ подменяется — оно используется
/// только как ключ для сопоставления с данными игры и для поиска нужного текстового узла.
/// </summary>
public sealed class TranslationEntry
{
    /// <summary>Английское название умения, как в исходном JSON (и как в игре на английском клиенте).</summary>
    public required string EnglishName { get; init; }

    /// <summary>
    /// Код класса/job (например "MIN", "BTN"), если запись из разряда тех, где одно и то же место
    /// умения называется по-разному в зависимости от активного класса (см. Gathering.json). Иначе null.
    /// </summary>
    public string? JobCode { get; init; }

    /// <summary>Переведённый тип умения ("Способность", "Боевой навык", ...) или null, если не задан.</summary>
    public string? Classification { get; init; }

    /// <summary>Переведённое описание умения (уже без HTML-тегов, переносы строк как \n) или null.</summary>
    public string? Content { get; init; }

    /// <summary>
    /// Английское описание умения из данных игры (Lumina), заполняется при разрешении ID.
    /// Используется, чтобы находить нужный текстовый узел по совпадению текста, без завязки на ID нод.
    /// </summary>
    public string? GameEnglishDescription { get; set; }

    /// <summary>Разрешённый числовой ID умения (Action/CraftAction) или 0, если не удалось сопоставить.</summary>
    public uint ActionId { get; set; }

    /// <summary>Имя листа Lumina, из которого разрешился ID ("Action", "CraftAction") или null.</summary>
    public string? ResolvedSheet { get; set; }

    public bool IsResolved => ActionId != 0;
}

using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace FFXIVJobGuideRUTranslator;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    /// <summary>Глобальный выключатель — если false, плагин не трогает никакой текст.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Имена нативных аддонов (окон), в которых плагину разрешено подменять текст.
    /// По факту (проверено на живом клиенте) один и тот же аддон ActionDetail рисует и
    /// всплывающую подсказку умения на хотбаре, и панель описания в "Actions &amp; Traits" -
    /// отдельный аддон "Tooltip" добавлять НЕ нужно, он дублирует тот же текст поверх экрана
    /// без рамки окна. Проверить/поправить имена под свою версию игры: /xldata -> Addon Inspector.
    /// Это единственное место, которое ограничивает, где плагин вообще может что-то менять —
    /// в остальных окнах он не активен.
    /// </summary>
    public List<string> TargetAddonNames { get; set; } = new()
    {
        "ActionDetail",
    };

    /// <summary>UTC-время последнего успешного обновления перевода с GitHub. Null = используется только бандл.</summary>
    public DateTime? LastUpdateUtc { get; set; }

    /// <summary>
    /// Экспериментально: вместо отдельного ImGui-окна (TranslationOverlay) прячет родную подсказку
    /// умения (только когда для неё есть перевод) и рисует свою через настоящие ноды игры
    /// (KamiToolKit) - см. Windows/NativeTranslationOverlayNode.cs. По умолчанию выключено:
    /// это 6-я попытка интеграции с нативным UI в этом проекте (см. AbilityHoverWatcher.cs) -
    /// предыдущие 5 (менявшие текст/размер прямо в родном попапе) ломали другие окна, которые
    /// повторно используют тот же попап (Materia Extraction, Repair и т.п.). Этот вариант не
    /// трогает ноды родного попапа вообще - только переключает его видимость и рисует полностью
    /// отдельное окно, - но всё равно не проверялся на живом клиенте, поэтому выключатель есть.
    /// </summary>
    public bool UseNativeTranslationWindow { get; set; } = false;

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}

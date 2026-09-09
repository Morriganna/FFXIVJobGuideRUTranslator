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

    /// <summary>Заменять текст в панели описания окна "Actions &amp; Traits" (ActionMenu / ActionDetail).</summary>
    public bool TranslateActionMenu { get; set; } = true;

    /// <summary>Заменять текст во всплывающей подсказке умения на хотбаре.</summary>
    public bool TranslateHoverTooltip { get; set; } = true;

    /// <summary>
    /// Имена нативных аддонов (окон), в которых плагину разрешено подменять текст.
    /// Проверьте актуальные имена через /xldata -> Addon Inspector (см. README) и поправьте список,
    /// если в вашей версии игры название другое. Это единственное место, которое ограничивает,
    /// где плагин вообще может что-то менять — в остальных окнах он не активен.
    /// </summary>
    public List<string> TargetAddonNames { get; set; } = new()
    {
        "ActionDetail",
        "Tooltip",
    };

    /// <summary>UTC-время последнего успешного обновления перевода с GitHub. Null = используется только бандл.</summary>
    public DateTime? LastUpdateUtc { get; set; }

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}

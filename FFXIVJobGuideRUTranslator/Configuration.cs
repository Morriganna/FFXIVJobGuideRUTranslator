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

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
